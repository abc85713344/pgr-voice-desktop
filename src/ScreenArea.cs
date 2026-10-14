using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

// SystemParameters.WorkArea 只代表主屏；副屏（含左侧 / 上方的负坐标副屏）要按所在显示器取工作区。
// 每显示器 DPI 下 WPF 的 Left/Top 以窗口当前所在屏的缩放为单位，跨不同缩放的屏幕后数值会变，
// 需要跨屏记忆或摆放的窗口改用物理像素。
static class ScreenArea
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>窗口自身单位下、其所在显示器的工作区（窗口自己的 Left/Top 换算一致时使用）。</summary>
    public static Rect WorkAreaAt(Visual visual, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return SystemParameters.WorkArea;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)Math.Round(x * dpi.DpiScaleX), (int)Math.Round(y * dpi.DpiScaleY)));
            var a = screen.WorkingArea;
            return new Rect(a.Left / dpi.DpiScaleX, a.Top / dpi.DpiScaleY, a.Width / dpi.DpiScaleX, a.Height / dpi.DpiScaleY);
        }
        catch (Exception) { return SystemParameters.WorkArea; }
    }

    public static Rect WorkAreaOf(Window window) =>
        WorkAreaAt(window, window.Left + (double.IsFinite(window.Width) ? window.Width / 2 : 0), window.Top + 20);

    /// <summary>把某个已显示窗口的 Left/Top 单位换成物理像素。</summary>
    public static Point ToPhysical(Visual reference, double x, double y)
    {
        var dpi = VisualTreeHelper.GetDpi(reference);
        return new Point(x * dpi.DpiScaleX, y * dpi.DpiScaleY);
    }

    /// <summary>离物理点最近的显示器：返回其物理工作区与缩放比例。</summary>
    public static double Monitor(Point physical, out System.Drawing.Rectangle work)
    {
        var p = new System.Drawing.Point((int)Math.Round(physical.X), (int)Math.Round(physical.Y));
        work = System.Windows.Forms.Screen.FromPoint(p).WorkingArea;
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (Exception) { }
        return 1.0;
    }

    public static Point PhysicalOrigin(Window window) =>
        DesktopAdvanceInput.GetWindowRect(new WindowInteropHelper(window).Handle, out var r) ? new Point(r.Left, r.Top) : new Point(double.NaN, double.NaN);

    public static void MoveTo(Window window, double x, double y)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }
}
