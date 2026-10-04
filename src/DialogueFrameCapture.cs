using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PgrVoice;

public sealed record CapturedDialogueFrame(DialogueFrameMask Mask, string Geometry, long Timestamp, double CaptureMs);

public static class DialogueFrameCapture
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    delegate bool EnumWindow(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    public static CapturedDialogueFrame Capture(IntPtr hwnd, DialogueRegion region)
    {
        if (!region.IsValid) throw new InvalidOperationException("请重新选取对白范围");
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("游戏不在前台");
        long started = Stopwatch.GetTimestamp();
        if (!GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("无法读取游戏窗口范围");
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("无法读取游戏窗口位置");
        int gw = rect.Right - rect.Left, gh = rect.Bottom - rect.Top;
        if (gw < 320 || gh < 180) throw new InvalidOperationException("游戏窗口太小");
        var area = new Rectangle(origin.X + (int)Math.Round(gw * region.Left), origin.Y + (int)Math.Round(gh * region.Top),
            (int)Math.Round(gw * region.Width), (int)Math.Round(gh * region.Height));
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(area)) throw new InvalidOperationException("对白范围超出屏幕");
        bool obscured = false;
        EnumWindows((other, _) =>
        {
            if (other == hwnd || !Native.IsWindowVisible(other) || Native.IsIconic(other)) return true;
            GetWindowThreadProcessId(other, out uint pid);
            if (pid == Environment.ProcessId && GetWindowRect(other, out var bounds) &&
                area.IntersectsWith(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)))
            { obscured = true; return false; }
            return true;
        }, IntPtr.Zero);
        if (obscured) throw new InvalidOperationException("播放器悬浮窗遮住对白，请移开悬浮窗或热区");
        using var original = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(original)) g.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);
        int width = Math.Min(640, area.Width), height = Math.Max(4, (int)Math.Round(area.Height * (double)width / area.Width));
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled)) { g.InterpolationMode = InterpolationMode.Bilinear; g.DrawImage(original, new Rectangle(0, 0, width, height)); }
        var data = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var pixels = new byte[width * height * 3];
        try { for (int row = 0; row < height; row++) Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * width * 3, width * 3); }
        finally { scaled.UnlockBits(data); }
        var afterOrigin = new Point();
        if (Native.GetForegroundWindow() != hwnd || Native.IsIconic(hwnd) || !GetClientRect(hwnd, out var afterRect) ||
            !ClientToScreen(hwnd, ref afterOrigin) || afterRect.Right != rect.Right || afterRect.Bottom != rect.Bottom ||
            afterOrigin.X != origin.X || afterOrigin.Y != origin.Y) throw new InvalidOperationException("截图期间窗口发生变化");
        return new(DialogueFrameAnalysis.CreateWhiteMask(width, height, width * 3, pixels),
            $"{origin.X},{origin.Y},{gw},{gh};{area}", started, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
