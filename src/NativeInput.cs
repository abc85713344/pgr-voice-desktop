using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;

namespace PgrVoice;

public sealed class KeyboardListener : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct NativeMessage
    { public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc callback;
    readonly RawInputPressGate rawGate = new();
    readonly RawInputModifierState rawModifiers = new();
    readonly DedicatedMenuKeyGate menuGate = new();
    readonly CapturedMenuInputHistory capturedMenuInputs = new();
    readonly ConcurrentQueue<(Key Key, IntPtr Foreground, int Epoch)> menuEvents = new();
    readonly ManualResetEventSlim started = new(false);
    readonly Thread? hookThread;
    MenuCaptureSnapshot snapshot = MenuCaptureSnapshot.Disabled;
    IntPtr hook;
    uint threadId;
    int disposed, dispatching, hookStop;
    string? hookError;
    public bool HookAvailable => Volatile.Read(ref hook) != IntPtr.Zero;
    public string? HookError => Volatile.Read(ref hookError);
    // 只读查询保留旧验收代码用法；菜单状态必须由 UI 发布快照，不能在 Hook 上读取控件。
    public Func<Key, IntPtr, int> CaptureMenu => FindMenuEpoch;
    public event Action<Key, IntPtr, int>? MenuPressed;
    public event Action<ObservedKeyInput>? ObservedPressed;
    public event Action<Key, IntPtr>? Pressed;

    public KeyboardListener() : this(true) { }
    internal KeyboardListener(bool installMenuHook)
    {
        callback = OnKey;
        if (!installMenuHook) { started.Set(); return; }
        // 若播放器启动时修饰键已经按住，后续第一次普通键也必须识别为组合键。
        foreach (int key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) rawModifiers.Seed(key);
        hookThread = new Thread(HookLoop) { IsBackground = true, Name = "PgrVoice menu keyboard hook" };
        hookThread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(2)))
        {
            Volatile.Write(ref hookError, "菜单键监听线程启动超时；普通推进仍只使用 Raw Input。");
            // 菜单线程超时只停菜单 Hook；普通 Raw Input 仍可正常计数。
            Interlocked.Exchange(ref hookStop, 1);
            if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void PublishMenuSnapshot(MenuCaptureSnapshot state) => Volatile.Write(ref snapshot, state ?? MenuCaptureSnapshot.Disabled);
    int FindMenuEpoch(Key key, IntPtr foreground) => Volatile.Read(ref snapshot).FindEpoch(key, foreground);
    int FindMenuEpoch(Key key, IntPtr foreground, ModifierKeys modifiers) => Volatile.Read(ref snapshot).FindEpoch(key, foreground, modifiers);
    static ModifierKeys ReadMenuModifiers(uint flags)
    {
        // 此回调发生在“当前菜单键”的异步状态更新前；这里只读取此前已按住的修饰键。
        ModifierKeys result = (flags & 0x20) != 0 ? ModifierKeys.Alt : ModifierKeys.None;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) result |= ModifierKeys.Shift;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) result |= ModifierKeys.Control;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) result |= ModifierKeys.Alt;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) result |= ModifierKeys.Windows;
        return result;
    }

    void HookLoop()
    {
        try
        {
            threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // 建立线程消息队列，允许关闭时发送 WM_QUIT。
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref hookStop) != 0) return;
            var installed = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            Volatile.Write(ref hook, installed);
            if (installed == IntPtr.Zero)
                Volatile.Write(ref hookError, "菜单键拦截不可用：" + new Win32Exception(Marshal.GetLastWin32Error()).Message + "。请使用菜单鼠标按钮。");
            started.Set();
            if (installed == IntPtr.Zero) return;
            // 低层 Hook 在安装它的专用线程回调；消息泵不执行 UI、音频、文件 IO。
            // Hook 超时会延迟输入或被系统移除，不能据此断言游戏必然丢失按键。
            while (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref hookStop) == 0)
            {
                int result = GetMessage(out _, IntPtr.Zero, 0, 0);
                if (result <= 0) break;
            }
        }
        catch (Exception ex) { Volatile.Write(ref hookError, "菜单键监听未启动：" + ex.Message); }
        finally
        {
            var installed = Interlocked.Exchange(ref hook, IntPtr.Zero);
            if (installed != IntPtr.Zero) UnhookWindowsHookEx(installed);
            started.Set();
        }
    }

    IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || Volatile.Read(ref disposed) != 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
        var input = Marshal.PtrToStructure<KeyData>(data);
        // 普通按键在这里没有计数、日志或 UI 通知；空格立即交给后续链。
        if (input.VirtualKey is not (0x26 or 0x28 or 0x0D or 0x1B))
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        int msg = message.ToInt32();
        if (msg is not (0x100 or 0x104 or 0x101 or 0x105)) return CallNextHookEx(IntPtr.Zero, code, message, data);
        long received = Stopwatch.GetTimestamp();
        long timestamp = InputMessageClock.ToTimestamp(input.Time, unchecked((uint)Environment.TickCount), received);
        bool consumed = ProcessMenuKey((int)input.VirtualKey, msg is 0x100 or 0x104,
            Native.GetForegroundWindow(), (input.Flags & 0x10) != 0, timestamp, ReadMenuModifiers(input.Flags));
        return consumed ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    bool ProcessMenuKey(int virtualKey, bool down, IntPtr foreground, bool injected, long timestamp,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (virtualKey is not (0x26 or 0x28 or 0x0D or 0x1B)) return false;
        Key key = KeyInterop.KeyFromVirtualKey(virtualKey);
        int epoch = FindMenuEpoch(key, foreground, modifiers);
        var decision = menuGate.Update(virtualKey, down, epoch > 0, injected);
        if (decision.Swallow) capturedMenuInputs.Observe(virtualKey, down, timestamp);
        if (decision.Fire) QueueMenu(key, foreground, epoch);
        return decision.Swallow;
    }

    void QueueMenu(Key key, IntPtr foreground, int epoch)
    {
        menuEvents.Enqueue((key, foreground, epoch));
        if (Interlocked.CompareExchange(ref dispatching, 1, 0) == 0)
            ThreadPool.QueueUserWorkItem(_ => DrainMenuEvents());
    }

    void DrainMenuEvents()
    {
        do
        {
            while (menuEvents.TryDequeue(out var input))
            {
                if (Volatile.Read(ref disposed) != 0) continue;
                // 事件订阅者在工作线程被通知；主窗口应只投递 Dispatcher，且再次校验 Epoch。
                try { MenuPressed?.Invoke(input.Key, input.Foreground, input.Epoch); } catch { }
            }
            Interlocked.Exchange(ref dispatching, 0);
        }
        while (!menuEvents.IsEmpty && Interlocked.CompareExchange(ref dispatching, 1, 0) == 0);
    }

    public void OnRawKey(RawKeyInput input)
    {
        if (Volatile.Read(ref disposed) != 0 || input.VirtualKey is <= 0 or >= 255) return;
        var modifiers = rawModifiers.Update(input.Device, input.VirtualKey, input.IsDown);
        if (!rawGate.Update(input.Device, input.VirtualKey, input.IsDown)) return;
        if (capturedMenuInputs.Contains(input.VirtualKey, input.Timestamp)) return;
        Key key = KeyInterop.KeyFromVirtualKey(input.VirtualKey);
        int epoch = FindMenuEpoch(key, input.Foreground, modifiers);
        if (epoch > 0)
        {
            // 菜单 Hook 是菜单动作的唯一来源；安装失败时才显式降级到 Raw 观察。
            // 降级只能观察，不能阻止游戏收到 Enter/方向键，界面须提示使用鼠标菜单。
            if (!HookAvailable) QueueMenu(key, input.Foreground, epoch);
            return;
        }
        var observed = new ObservedKeyInput(key, input.Foreground, input.Timestamp, input.ReceivedTimestamp, modifiers, input.MessageTime);
        ObservedPressed?.Invoke(observed);
        // 兼容事件只表达单键动作；完整观察事件仍携带组合键，供自动播放停止等处理使用。
        if (modifiers == ModifierKeys.None) Pressed?.Invoke(key, input.Foreground);
    }

    public void OnRawKey(int virtualKey, bool down, IntPtr foreground)
    {
        long timestamp = Stopwatch.GetTimestamp();
        OnRawKey(new(virtualKey, down, foreground, timestamp, timestamp, IntPtr.Zero));
    }
    public void ForgetRawDevice(IntPtr device) { rawGate.RemoveDevice(device); rawModifiers.RemoveDevice(device); }

    // 仅测试输入路由，不向系统注入按键；菜单状态机与真实 Hook 共用。
    internal bool ProbeMenuKey(int key, bool down) => ProbeMenuKey(key, down, Native.GetForegroundWindow());
    internal bool ProbeMenuKey(int key, bool down, IntPtr foreground) =>
        ProcessMenuKey(key, down, foreground, false, Stopwatch.GetTimestamp(), rawModifiers.Current);
    internal bool ProbeMenuKey(int key, bool down, IntPtr foreground, ModifierKeys modifiers, bool injected = false) =>
        ProcessMenuKey(key, down, foreground, injected, Stopwatch.GetTimestamp(), modifiers);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        PublishMenuSnapshot(MenuCaptureSnapshot.Disabled);
        uint id = threadId;
        if (id != 0) PostThreadMessage(id, 0x12, IntPtr.Zero, IntPtr.Zero);
        if (hookThread != null && Thread.CurrentThread != hookThread) hookThread.Join(TimeSpan.FromSeconds(2));
        // started 可能还会被异常慢的线程 finally 设置，因此不提前 Dispose 该同步对象。
    }
}

// 普通键盘和鼠标的唯一观察来源，不通过 RIDEV_NOLEGACY 禁用游戏自己的输入。
public sealed class RawKeyboardListener : IDisposable
{
    const int WmInput = 0x00FF, WmInputDeviceChange = 0x00FE;
    const uint RidInput = 0x10000003, RidevInputSink = 0x100, RidevDeviceNotify = 0x2000, RidevRemove = 1;
    [StructLayout(LayoutKind.Sequential)] struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VirtualKey; public uint Message, ExtraInformation; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern int GetMessageTime();
    [DllImport("user32.dll")] static extern uint GetMessagePos();
    delegate void ForegroundEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, ForegroundEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    readonly HwndSource source;
    readonly Action<RawKeyInput> onKey;
    readonly MouseTapGate mouseGestures = new();
    readonly ForegroundEventProc foregroundCallback;
    IntPtr foregroundHook;
    bool disposed, mouseRegistered, reportedReadFailure;
    public event Action<ObservedMouseInput>? MouseObserved;
    public event Action<ObservedMouseGesture>? MouseGestureObserved;
    public event Action<IntPtr>? DeviceRemoved;
    public event Action<string>? ReadFailed;
    public Action<string, IntPtr>? MousePressed { get; set; }
    public bool KeyboardAvailable => !disposed;
    public bool MouseAvailable => !disposed && mouseRegistered;
    public bool MouseGestureAvailable => MouseAvailable && foregroundHook != IntPtr.Zero;
    public string? MouseError { get; }
    public string? MouseGestureError { get; }
    public RawKeyboardListener(IntPtr window, Action<int, bool, IntPtr> onKey)
        : this(window, input => onKey(input.VirtualKey, input.IsDown, input.Foreground)) { }
    public RawKeyboardListener(IntPtr window, Action<RawKeyInput> onKey)
    {
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("播放器窗口尚未就绪，无法接收输入");
        this.onKey = onKey;
        foregroundCallback = OnForegroundChanged;
        var keys = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink | RidevDeviceNotify, Target = window } };
        if (!RegisterRawInputDevices(keys, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw Input 键盘监听启动失败，键盘跟随不可用；请使用面板按钮。没有启用第二种计数来源。");
        var mouse = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevInputSink | RidevDeviceNotify, Target = window } };
        mouseRegistered = RegisterRawInputDevices(mouse, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        if (!mouseRegistered) MouseError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        if (mouseRegistered)
        {
            // 只观察前台切换，不拦截输入。OUTOFCONTEXT 回调由本线程消息泵交付。
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
            if (foregroundHook == IntPtr.Zero) MouseGestureError = "前台变化监听不可用，鼠标轻点跟随已停用；鼠标按下观察仍可用。";
        }
        source.AddHook(WndProc);
    }
    void OnForegroundChanged(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (disposed) return;
        long now = Stopwatch.GetTimestamp();
        mouseGestures.ObserveForeground(window, InputMessageClock.ToTimestamp(time, unchecked((uint)Environment.TickCount), now), time);
    }
    void ReportReadFailure()
    {
        mouseGestures.Invalidate();
        if (reportedReadFailure) return;
        reportedReadFailure = true;
        ReadFailed?.Invoke("读取 Raw Input 失败，跟随输入可能不完整，请暂停并重新连接播放器；未切换到其他计数来源。");
    }
    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if (message == WmInputDeviceChange && wParam.ToInt64() == 2) { mouseGestures.RemoveDevice(lParam); DeviceRemoved?.Invoke(lParam); return IntPtr.Zero; }
        if (message != WmInput) return IntPtr.Zero;
        long received = Stopwatch.GetTimestamp();
        uint messageTime = unchecked((uint)GetMessageTime());
        long timestamp = InputMessageClock.ToTimestamp(messageTime, unchecked((uint)Environment.TickCount), received);
        var position = InputMessageClock.DecodePosition(GetMessagePos());
        IntPtr foreground = Native.GetForegroundWindow();
        mouseGestures.ObserveForeground(foreground, timestamp, messageTime);
        uint size = 0, headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize)
        { ReportReadFailure(); return IntPtr.Zero; }
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) != size) { ReportReadFailure(); return IntPtr.Zero; }
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type == 1)
            {
                if (size < headerSize + (uint)Marshal.SizeOf<RawKeyboard>()) { ReportReadFailure(); return IntPtr.Zero; }
                var key = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(buffer, (int)headerSize));
                if (key.VirtualKey is not (0 or 255))
                    onKey(new(RawKeyboardKey.Normalize(key.VirtualKey, key.MakeCode, key.Flags), (key.Flags & 1) == 0, foreground, timestamp, received, header.Device, messageTime));
            }
            else if (header.Type == 0)
            {
                if (size < headerSize + 24) { ReportReadFailure(); return IntPtr.Zero; }
                uint extraInformation = unchecked((uint)Marshal.ReadInt32(buffer, (int)headerSize + 20));
                short buttons = Marshal.ReadInt16(buffer, (int)headerSize + 4);
                bool moved = (Marshal.ReadInt16(buffer, (int)headerSize) & 1) != 0 ||
                    Marshal.ReadInt32(buffer, (int)headerSize + 12) != 0 || Marshal.ReadInt32(buffer, (int)headerSize + 16) != 0;
                ObserveMousePacket(new("鼠标左键", foreground, position.X, position.Y, timestamp, received, header.Device, messageTime), buttons, moved, extraInformation);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return IntPtr.Zero; // 不设置 handled，保持系统默认的 Raw Input 清理与游戏输入。
    }
    internal void ObserveMousePacket(ObservedMouseInput point, short buttons, bool moved, uint extraInformation)
    {
        // 自身自动点击由专属标记排除；设备句柄为零并不代表无效输入。
        if (disposed || extraInformation == DesktopAdvanceInput.InputMarker) return;
        Notify(1, "鼠标左键"); Notify(4, "鼠标右键"); Notify(16, "鼠标中键");
        if (MouseGestureAvailable && (moved || buttons != 0))
        {
            if ((buttons & 1) != 0) mouseGestures.Begin(point);
            mouseGestures.Move(point);
            if ((buttons & 2) != 0 && mouseGestures.End(point) is { } gesture) MouseGestureObserved?.Invoke(gesture);
        }
        void Notify(short flag, string name)
        {
            if ((buttons & flag) == 0) return;
            MouseObserved?.Invoke(point with { Button = name });
            MousePressed?.Invoke(name, point.Foreground);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        mouseGestures.Clear();
        if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        source.RemoveHook(WndProc);
        var keys = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevRemove, Target = IntPtr.Zero } };
        RegisterRawInputDevices(keys, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        if (mouseRegistered)
        {
            var mouse = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevRemove, Target = IntPtr.Zero } };
            RegisterRawInputDevices(mouse, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            mouseRegistered = false;
        }
    }
}
