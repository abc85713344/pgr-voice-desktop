using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

// 屏幕上的「下一句」点击热区，外观与播放器悬浮球同尺寸同形状（64 × 64，圆角 23）。
//
// 平时整块完全穿透鼠标（WS_EX_TRANSPARENT），所以它只是画个标记给玩家看：
// 点下去这一下照样落到游戏里，游戏照常推进；播放器通过 Raw Input 旁听到这次点击，
// 再用光标坐标判断“是不是点在框里”，是才跟着推进一步。
// 这样在游戏里点选项、点技能、点其他地方都不会误跟随。
//
// 只有播放器面板展开（此时游戏不在前台）才临时取消穿透，让玩家把框拖到目标位置。
public sealed class ClickZoneWindow : Window
{
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;   // 鼠标穿透
    const int WsExNoActivate = 0x08000000;    // 不抢焦点
    const int WsExToolWindow = 0x00000080;    // 不出现在 Alt+Tab

    readonly Border frame;
    readonly Border grip;
    readonly TextBlock glyph;
    readonly TextBlock hint;

    public bool Draggable { get; private set; }

    public ClickZoneWindow()
    {
        // 与悬浮球一致：64 × 64、圆角 23、1 像素描边，顶部一条小握把。
        Width = 64;
        Height = 64;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SnapsToDevicePixels = true;

        grip = new Border { Width = 18, Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) };
        glyph = new TextBlock { Text = "下", FontSize = 23, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        hint = new TextBlock { Text = "下一句", FontSize = 9, TextAlignment = TextAlignment.Center };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(glyph);
        stack.Children.Add(hint);
        var grid = new Grid();
        grid.Children.Add(grip);
        grid.Children.Add(stack);
        frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(23), Child = grid };
        Content = frame;
        ApplyTheme();

        MouseLeftButtonDown += (_, _) => { if (Draggable) { try { DragMove(); } catch { } Moved?.Invoke(); } };
    }

    public event Action? Moved;

    void ApplyTheme()
    {
        frame.BorderBrush = Theme.Brush("NormalAccent");
        grip.Background = Theme.Brush("Accent");
        glyph.Foreground = Theme.Brush("NormalAccent");
        hint.Foreground = Theme.Brush("MutedText");
        // 与悬浮球同色系，但做成半透明，免得挡住底下那格游戏画面。
        var ball = Theme.Brush("BallNormal") as SolidColorBrush;
        var color = ball?.Color ?? Color.FromRgb(20, 22, 26);
        frame.Background = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        ApplyClickThrough(!Draggable);
    }

    // 穿透时点不到这个窗口（正是我们要的）；不穿透时才能拖动。
    public void ApplyClickThrough(bool through)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExStyle);
        int wanted = through ? style | WsExTransparent : style & ~WsExTransparent;
        if (wanted != style) SetWindowLong(hwnd, GwlExStyle, wanted);
    }

    public void SetDraggable(bool draggable)
    {
        if (Draggable == draggable) return;
        Draggable = draggable;
        ApplyClickThrough(!draggable);
        frame.BorderThickness = new Thickness(draggable ? 2 : 1);
        frame.BorderBrush = draggable ? Theme.Brush("PlayingAccent") : Theme.Brush("NormalAccent");
        hint.Text = draggable ? "拖动" : "下一句";
        Cursor = draggable ? Cursors.SizeAll : Cursors.Arrow;
        ToolTip = draggable
            ? "正在调整位置：拖到游戏里“点击继续”的位置，然后点击完成或收起播放器面板。"
            : "在游戏里点这个框会请求配音跟随。点击照常传给游戏；在设置里选择调整位置后可拖动。";
    }

    public void PlaceAt(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        // 左侧或上方副屏的位置可以为负，只有初始哨兵 -1 表示尚未设置。
        double x = double.IsFinite(left) && left != -1 ? left : area.Left + (area.Width - Width) / 2;
        double y = double.IsFinite(top) && top != -1 ? top : area.Top + area.Height * 0.62;
        Left = Math.Clamp(x, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(y, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
    }

    // 当前是不是处在“鼠标穿透”状态（供诊断与自测使用）。
    public bool IsClickThrough
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            return (GetWindowLong(hwnd, GwlExStyle) & WsExTransparent) != 0;
        }
    }

    // 光标是否落在这个框里。用 Win32 的窗口矩形，和 GetCursorPos 同为物理像素，
    // 避免高 DPI 下的换算误差。
    public bool ContainsCursor(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
    }
}
