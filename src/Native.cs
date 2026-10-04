using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace PgrVoice;
public sealed record GameWindow(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => $"{Title}  [{ProcessName}]";
}
public static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr value, int length, out int returned);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    public static string? ExecutablePath(IntPtr window)
    {
        if (!IsWindow(window)) return null;
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768); int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
    public static bool? IsElevated(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return null;
            try
            {
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out int length); // TokenIntegrityLevel
                if (length < IntPtr.Size) return null;
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, length, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buffer);
                    var countPointer = GetSidSubAuthorityCount(sid);
                    if (countPointer == IntPtr.Zero) return null;
                    byte count = Marshal.ReadByte(countPointer);
                    if (count == 0) return null;
                    var rid = GetSidSubAuthority(sid, (uint)(count - 1));
                    return rid == IntPtr.Zero ? null : Marshal.ReadInt32(rid) >= 0x3000;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    public static bool IsGameWindow(IntPtr hwnd, string configuredExecutable)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return GameInstallation.MatchesWindowIdentity(configuredExecutable,
                configuredExecutable.Length > 0 ? ExecutablePath(hwnd) : null, process.ProcessName);
        }
        catch { return false; }
    }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    // 光标屏幕坐标（物理像素），用于判断点击是否落在“下一句”热区内。
    public static (int X, int Y) CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : (int.MinValue, int.MinValue);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    public static List<GameWindow> Windows()
    {
        var result = new List<GameWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = new StringBuilder(1024); GetWindowText(h, title, title.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId || title.Length == 0) return true;
            try { result.Add(new(h, title.ToString(), Process.GetProcessById((int)pid).ProcessName)); } catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static (string Path, int Width, int Height) Capture(IntPtr hwnd, string directory)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd)) throw new InvalidOperationException("游戏未打开或已最小化");
        // 截图取游戏客户区的屏幕像素。普通权限的播放器不一定能把管理员游戏
        // 强行切到前台；候选仍由玩家确认，不能因此禁止可见画面的截图。
        GetClientRect(hwnd, out var rect); var origin = new Point(); ClientToScreen(hwnd, ref origin);
        int width = rect.Right, height = rect.Bottom;
        if (width < 100 || height < 100) throw new InvalidOperationException("游戏窗口太小");
        Directory.CreateDirectory(directory);
        string file = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(origin.X, origin.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        bmp.Save(file, ImageFormat.Png);
        return (file, width, height);
    }
}
