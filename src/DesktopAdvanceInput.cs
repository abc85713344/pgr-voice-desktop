using System;
using System.Runtime.InteropServices;

namespace PgrVoice;

// 仅发送用户开启的下一句热区点击；不激活后台窗口、不重试失败的点击。
internal static class DesktopAdvanceInput
{
    internal const uint InputMarker = 0x50475241;
    [StructLayout(LayoutKind.Sequential)] internal struct PixelRect : IEquatable<PixelRect>
    {
        public int Left, Top, Right, Bottom;
        public bool Equals(PixelRect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out PixelRect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out PixelRect rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    internal static bool ClientBounds(IntPtr window, out PixelRect rect)
    {
        rect = default;
        if (!Native.IsWindow(window) || Native.IsIconic(window) || !Native.IsWindowVisible(window) || !GetClientRect(window, out rect)) return false;
        var point = new Point();
        if (!ClientToScreen(window, ref point) || rect.Right <= 0 || rect.Bottom <= 0) return false;
        rect.Left = point.X; rect.Top = point.Y; rect.Right += point.X; rect.Bottom += point.Y;
        return true;
    }
    internal static bool IsUnobscured(IntPtr window, int x, int y) => GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) == window;
    internal static bool TryClick(IntPtr window, PixelRect expected, int x, int y, string configuredExecutable, out string reason)
    {
        reason = "游戏窗口或热区发生变化，请重新定位后开启。";
        if (!Native.IsGameWindow(window, configuredExecutable) || Native.GetForegroundWindow() != window || !ClientBounds(window, out var current) || !current.Equals(expected) || !current.Contains(x, y)) return false;
        if (!IsUnobscured(window, x, y)) { reason = "下一句热区被其他窗口遮住，已暂停自动播放。"; return false; }
        // 用户正按住鼠标或任意键时不能把自动点击与该输入叠加。
        for (int key = 1; key < 255; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) { reason = "检测到手动操作，已暂停自动播放，请核对当前句。"; return false; }
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1) return false;
        Input Event(uint flags, int dx = 0, int dy = 0) => new() { Type = 0, Value = new() { Mouse = new() { X = dx, Y = dy, Flags = flags, Extra = new UIntPtr(InputMarker) } } };
        var inputs = new[] { Event(0x8000 | 0x4000 | 0x0001, (int)((long)(x-left)*65535/(width-1)), (int)((long)(y-top)*65535/(height-1))), Event(0x0002), Event(0x0004) };
        if (!Native.IsGameWindow(window, configuredExecutable) || Native.GetForegroundWindow() != window) return false;
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            // 部分发送时只释放可能已按下的左键，绝不补发第二次按下。
            if (sent > 1) SendInput(1, new[] { Event(0x0004) }, Marshal.SizeOf<Input>());
            reason = "游戏未接受完整点击，已暂停。请检查窗口权限与热区位置。"; return false;
        }
        reason = ""; return true;
    }
}
