using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PgrVoice;

[Flags]
public enum GamepadButtons : uint
{
    None = 0, South = 1 << 0, East = 1 << 1, West = 1 << 2, North = 1 << 3,
    Back = 1 << 4, Start = 1 << 5, LeftShoulder = 1 << 6, RightShoulder = 1 << 7,
    LeftStick = 1 << 8, RightStick = 1 << 9, DPadUp = 1 << 10, DPadDown = 1 << 11,
    DPadLeft = 1 << 12, DPadRight = 1 << 13, LeftTrigger = 1 << 14, RightTrigger = 1 << 15
}
public enum GamepadFamily { Unknown, Xbox, PlayStation, Nintendo }
public sealed record GamepadDevice(uint Id, string Name, GamepadFamily Family, bool IsVirtualXbox = false);
public sealed record GamepadReading(GamepadDevice? Device, bool Connected, GamepadButtons Buttons,
    GamepadButtons Pressed, GamepadButtons Released, float LeftX, float LeftY, float RightX, float RightY,
    float LeftTrigger, float RightTrigger, long Timestamp)
{
    public bool IsNeutral => Buttons == GamepadButtons.None && LeftX == 0 && LeftY == 0 && RightX == 0 && RightY == 0 &&
        LeftTrigger <= .1f && RightTrigger <= .1f;
}

// 仅观察设备。事件在专用线程发出，界面必须 BeginInvoke；不应在回调中同步等待界面。
// SDL 3.4.16 / Windows x64；不创建 SDL 视频窗口，不初始化音频，不发送系统输入、震动或灯光命令。
public sealed class GamepadInput : IDisposable
{
    readonly Func<IGamepadBackend> createBackend;
    readonly ManualResetEventSlim stop = new(false);
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static int nativeOwner;
    int started, disposed;
    long selectedId = -1;
    string? error;
    GamepadReading? latestReading;
    GamepadDevice[] devices = Array.Empty<GamepadDevice>();
    public event Action<GamepadReading>? Reading;
    public event Action<GamepadDevice[]>? DevicesChanged;
    public string? Error => Volatile.Read(ref error);
    public Task Completion => completion.Task;
    public GamepadReading? LatestReading => Volatile.Read(ref latestReading);
    public GamepadDevice[] Devices => (GamepadDevice[])Volatile.Read(ref devices).Clone();
    public uint? SelectedDeviceId
    {
        get { long id = Interlocked.Read(ref selectedId); return id < 0 ? null : (uint)id; }
        set => Interlocked.Exchange(ref selectedId, value.HasValue ? value.Value : -1L);
    }
    public GamepadInput() : this(() => new SdlGamepadBackend()) { }
    internal GamepadInput(Func<IGamepadBackend> createBackend) => this.createBackend = createBackend;
    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
        try { new Thread(Run) { IsBackground = true, Name = "PgrVoice gamepad input" }.Start(); }
        catch (Exception ex) { Volatile.Write(ref error, "手柄监听未启动：" + ex.Message); stop.Dispose(); completion.TrySetResult(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stop.Set(); } catch (ObjectDisposedException) { } // 不 Join，不在 UI 等待驱动或 SDL 释放。
        if (Volatile.Read(ref started) == 0) { stop.Dispose(); completion.TrySetResult(); }
    }
    void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { Volatile.Write(ref error, "手柄通知处理失败：" + ex.Message); }
    }
    void PublishReading(GamepadReading value) { Volatile.Write(ref latestReading, value); Notify(Reading, value); }
    void PublishDevices(GamepadDevice[] next)
    {
        Volatile.Write(ref devices, next);
        Notify(DevicesChanged, (GamepadDevice[])next.Clone());
    }
    void Run()
    {
        IGamepadBackend? backend = null;
        var tracker = new GamepadReadingTracker();
        GamepadDevice? active = null;
        bool ownsNative = false;
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            backend = createBackend();
            if (backend is SdlGamepadBackend)
            {
                ownsNative = Interlocked.CompareExchange(ref nativeOwner, 1, 0) == 0;
                if (!ownsNative) throw new InvalidOperationException("已有一个手柄监听器正在运行，请等待其关闭后重试。");
            }
            backend.Initialize();
            GamepadDevice[] known = Array.Empty<GamepadDevice>();
            bool firstList = true;
            long nextList = 0, lastSelection = long.MinValue, nextFrame = Stopwatch.GetTimestamp();
            long frameTicks = Math.Max(1, Stopwatch.Frequency / 60);
            while (Volatile.Read(ref disposed) == 0)
            {
                backend.Pump();
                long now = Stopwatch.GetTimestamp(), selection = Interlocked.Read(ref selectedId);
                if (firstList || now >= nextList || selection != lastSelection || active != null && !backend.Connected)
                {
                    var current = backend.Enumerate();
                    if (firstList || !known.SequenceEqual(current)) { known = current; PublishDevices(current); firstList = false; }
                    nextList = now + Stopwatch.Frequency / 2;
                }
                if (Volatile.Read(ref disposed) != 0) break;
                var target = GamepadDeviceSelection.Choose(known, selection < 0 ? null : (uint)selection, active?.Id);
                if (target?.Id != active?.Id || active != null && !backend.Connected)
                {
                    if (active != null)
                    {
                        backend.Close();
                        if (tracker.Observe(active, false, default, now) is { } disconnected) PublishReading(disconnected);
                        active = null;
                    }
                    if (target != null)
                    {
                        if (backend.Open(target.Id)) { active = target; Volatile.Write(ref error, null); }
                        else { Volatile.Write(ref error, "无法读取所选手柄：" + backend.Error); known = Array.Empty<GamepadDevice>(); nextList = now + Stopwatch.Frequency / 2; }
                    }
                }
                lastSelection = selection;
                if (active != null && backend.Connected)
                {
                    if (tracker.Observe(active, true, backend.Read(), now) is { } reading) PublishReading(reading);
                }
                else if (tracker.Observe(active, false, default, now) is { } disconnected) PublishReading(disconnected);
                nextFrame += frameTicks;
                long remaining = nextFrame - Stopwatch.GetTimestamp();
                if (remaining <= 0) nextFrame = Stopwatch.GetTimestamp();
                else stop.Wait(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref error, "手柄监听不可用：" + ex.Message);
            PublishDevices(Array.Empty<GamepadDevice>());
        }
        finally
        {
            if (tracker.Observe(active, false, default, Stopwatch.GetTimestamp()) is { } disconnected) PublishReading(disconnected);
            try { backend?.Dispose(); } catch (Exception ex) { Volatile.Write(ref error, "手柄监听释放失败：" + ex.Message); }
            if (ownsNative) Interlocked.Exchange(ref nativeOwner, 0);
            stop.Dispose();
            completion.TrySetResult();
        }
    }
}

internal static class GamepadDeviceSelection
{
    static int Priority(GamepadDevice device) => device.IsVirtualXbox ? 0 : device.Family == GamepadFamily.Xbox ? 1 : 2;
    public static GamepadDevice? Choose(GamepadDevice[] devices, uint? selected, uint? current)
    {
        if (selected.HasValue) return devices.FirstOrDefault(device => device.Id == selected.Value);
        if (devices.Length == 0) return null;
        int priority = devices.Min(Priority);
        // 同优先级保留当前设备；绝不把多只手柄的按钮合并。
        return devices.FirstOrDefault(device => device.Id == current && Priority(device) == priority) ??
            devices.Where(device => Priority(device) == priority).OrderBy(device => device.Id).First();
    }
}
internal readonly record struct GamepadSample(GamepadButtons Buttons, float LeftX, float LeftY, float RightX, float RightY, float LeftTrigger, float RightTrigger);
internal static class GamepadAnalog
{
    public const float StickDeadZone = .22f, TriggerPress = .55f, TriggerRelease = .35f;
    static float Quantize(float value) => MathF.Round(value * 100f) / 100f;
    public static (float X, float Y) Stick(short rawX, short rawY)
    {
        float x = rawX / (rawX < 0 ? 32768f : 32767f), y = rawY / (rawY < 0 ? 32768f : 32767f);
        float length = MathF.Sqrt(x * x + y * y);
        if (length <= StickDeadZone) return (0, 0);
        float scale = Math.Min(1f, (length - StickDeadZone) / (1f - StickDeadZone)) / length;
        return (Quantize(x * scale), Quantize(y * scale));
    }
    public static float Trigger(short raw) => Quantize(Math.Clamp(raw / 32767f, 0, 1));
    public static bool TriggerDown(float value, bool previouslyDown) => value >= (previouslyDown ? TriggerRelease : TriggerPress);
}
internal sealed class GamepadReadingTracker
{
    GamepadReading? previous;
    public GamepadReading? Observe(GamepadDevice? device, bool connected, GamepadSample sample, long timestamp)
    {
        if (!connected) device ??= previous?.Device;
        bool continuous = connected && previous?.Connected == true && previous.Device?.Id == device?.Id;
        var priorButtons = continuous ? previous!.Buttons : GamepadButtons.None;
        var buttons = connected ? sample.Buttons : GamepadButtons.None;
        if (connected && GamepadAnalog.TriggerDown(sample.LeftTrigger, (priorButtons & GamepadButtons.LeftTrigger) != 0)) buttons |= GamepadButtons.LeftTrigger;
        if (connected && GamepadAnalog.TriggerDown(sample.RightTrigger, (priorButtons & GamepadButtons.RightTrigger) != 0)) buttons |= GamepadButtons.RightTrigger;
        if (!connected) sample = default;
        var next = new GamepadReading(device, connected, buttons, continuous ? buttons & ~priorButtons : GamepadButtons.None,
            continuous ? priorButtons & ~buttons : !connected ? previous?.Buttons ?? GamepadButtons.None : GamepadButtons.None,
            sample.LeftX, sample.LeftY, sample.RightX, sample.RightY, sample.LeftTrigger, sample.RightTrigger, timestamp);
        bool changed = previous == null || previous.Connected != next.Connected || previous.Device?.Id != next.Device?.Id || previous.Buttons != next.Buttons ||
            previous.LeftX != next.LeftX || previous.LeftY != next.LeftY || previous.RightX != next.RightX || previous.RightY != next.RightY ||
            previous.LeftTrigger != next.LeftTrigger || previous.RightTrigger != next.RightTrigger;
        previous = next;
        return changed ? next : null;
    }
}

internal interface IGamepadBackend : IDisposable
{
    string? Error { get; }
    bool Connected { get; }
    void Initialize();
    void Pump();
    GamepadDevice[] Enumerate();
    bool Open(uint id);
    GamepadSample Read();
    void Close();
}

internal sealed class SdlGamepadBackend : IGamepadBackend
{
    IntPtr gamepad, library;
    bool initialized, ownsSdl;
    public string? Error => Marshal.PtrToStringUTF8(Sdl.SDL_GetError());
    public bool Connected => gamepad != IntPtr.Zero && Sdl.SDL_GamepadConnected(gamepad);
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("当前手柄组件需要 Windows x64 版本。");
        string file = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        if (!File.Exists(file)) file = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "SDL3.dll");
        if (!File.Exists(file)) throw new FileNotFoundException("缺少 SDL3.dll，请使用包含手柄组件的完整播放器包。");
        library = NativeLibrary.Load(file);
        if (Sdl.SDL_WasInit(0) != 0) throw new InvalidOperationException("SDL 已被其他功能初始化，不能跨线程接管手柄事件。");
        ownsSdl = true;
        // 固定版本 SDL.c：首次 SetMainReady 记录当前线程；Events 初始化也记录它。
        // Windows 无 SDL video 时，这个专用线程就是 SDL_IsMainThread 认定的线程。
        Sdl.SDL_SetMainReady();
        Hint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        Hint("SDL_JOYSTICK_ENHANCED_REPORTS", "0");
        Hint("SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED", "0");
        Hint("SDL_JOYSTICK_HIDAPI_XBOX_360_PLAYER_LED", "0");
        // DirectInput 的 SDL 驱动使用 EXCLUSIVE|BACKGROUND，避免与游戏争设备。
        // 保留 HIDAPI 对 PS4/PS5 USB/蓝牙的官方支持，Xbox 可用 XInput/RawInput/WGI。
        Hint("SDL_JOYSTICK_DIRECTINPUT", "0");
        if (!Sdl.SDL_Init(0x00002000)) throw new InvalidOperationException(Error ?? "SDL 手柄初始化失败");
        initialized = true;
        if (!Sdl.SDL_IsMainThread() || (Sdl.SDL_WasInit(0) & (0x10u | 0x20u)) != 0)
            throw new InvalidOperationException("SDL 手柄线程或子系统状态不符合只观察输入的要求。");
        Sdl.SDL_SetGamepadEventsEnabled(false); Sdl.SDL_SetJoystickEventsEnabled(false);
    }
    static void Hint(string name, string value)
    {
        // Override 仅覆盖本进程 SDL hint，防止环境变量打开震动增强报告；不更改系统设置。
        if (!Sdl.SDL_SetHintWithPriority(name, value, 2)) throw new InvalidOperationException("无法设置手柄观察选项：" + name);
    }
    public void Pump()
    {
        Sdl.SDL_PumpEvents(); Sdl.SDL_UpdateGamepads();
        Sdl.SDL_FlushEvents(0, 0xffff); // 本线程独占 SDL；不积压禁用前生成的设备通知。
    }
    public GamepadDevice[] Enumerate()
    {
        IntPtr ids = Sdl.SDL_GetGamepads(out int count);
        if (ids == IntPtr.Zero) throw new InvalidOperationException(Error ?? "读取手柄列表失败");
        try
        {
            if (count < 0 || count > 256) throw new InvalidOperationException("手柄设备数量异常");
            var result = new List<GamepadDevice>(count);
            for (int i = 0; i < count; i++)
            {
                uint id = unchecked((uint)Marshal.ReadInt32(ids, i * 4));
                string name = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadNameForID(id)) ?? "未命名手柄";
                int type = Sdl.SDL_GetRealGamepadTypeForID(id);
                var family = type is 2 or 3 ? GamepadFamily.Xbox : type is 4 or 5 or 6 ? GamepadFamily.PlayStation : type is >= 7 and <= 11 ? GamepadFamily.Nintendo : GamepadFamily.Unknown;
                string path = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadPathForID(id)) ?? "";
                // 通用 Xbox 名称不能证明它是虚拟设备；只标注明确字符串证据。
                bool virtualXbox = family == GamepadFamily.Xbox && new[] { "virtual", "vigem", "ds4windows", "rewasd" }.Any(value => (name + " " + path).Contains(value, StringComparison.OrdinalIgnoreCase));
                result.Add(new(id, name, family, virtualXbox));
            }
            return result.OrderBy(device => device.Id).ToArray();
        }
        finally { Sdl.SDL_free(ids); }
    }
    public bool Open(uint id) { Close(); gamepad = Sdl.SDL_OpenGamepad(id); return gamepad != IntPtr.Zero; }
    public void Close() { if (gamepad == IntPtr.Zero) return; Sdl.SDL_CloseGamepad(gamepad); gamepad = IntPtr.Zero; }
    public GamepadSample Read()
    {
        GamepadButtons buttons = GamepadButtons.None;
        foreach (var (index, button) in ButtonMap) if (Sdl.SDL_GetGamepadButton(gamepad, index)) buttons |= button;
        var left = GamepadAnalog.Stick(Sdl.SDL_GetGamepadAxis(gamepad, 0), Sdl.SDL_GetGamepadAxis(gamepad, 1));
        var right = GamepadAnalog.Stick(Sdl.SDL_GetGamepadAxis(gamepad, 2), Sdl.SDL_GetGamepadAxis(gamepad, 3));
        return new(buttons, left.X, left.Y, right.X, right.Y, GamepadAnalog.Trigger(Sdl.SDL_GetGamepadAxis(gamepad, 4)), GamepadAnalog.Trigger(Sdl.SDL_GetGamepadAxis(gamepad, 5)));
    }
    static readonly (int, GamepadButtons)[] ButtonMap = {
        (0,GamepadButtons.South),(1,GamepadButtons.East),(2,GamepadButtons.West),(3,GamepadButtons.North),(4,GamepadButtons.Back),
        (6,GamepadButtons.Start),(7,GamepadButtons.LeftStick),(8,GamepadButtons.RightStick),(9,GamepadButtons.LeftShoulder),(10,GamepadButtons.RightShoulder),
        (11,GamepadButtons.DPadUp),(12,GamepadButtons.DPadDown),(13,GamepadButtons.DPadLeft),(14,GamepadButtons.DPadRight)
    };
    public void Dispose()
    {
        try { if (initialized) Close(); if (ownsSdl) Sdl.SDL_Quit(); }
        finally { initialized = ownsSdl = false; if (library != IntPtr.Zero) NativeLibrary.Free(library); library = IntPtr.Zero; }
    }
    // SDL bool 是 C 的 1 字节 bool；SDL_JoystickID 是 Uint32，轴是 Sint16，字符串 UTF-8。
    static class Sdl
    {
        const string Dll = "SDL3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetMainReady();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_Init(uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_Quit();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SDL_WasInit(uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_IsMainThread();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_SetHintWithPriority([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetGamepadEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetJoystickEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_PumpEvents();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_UpdateGamepads();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_FlushEvents(uint minimum, uint maximum);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetGamepads(out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetGamepadNameForID(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetGamepadPathForID(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetRealGamepadTypeForID(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_OpenGamepad(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_CloseGamepad(IntPtr gamepad);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_GamepadConnected(IntPtr gamepad);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetError();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_free(IntPtr memory);
    }
}
