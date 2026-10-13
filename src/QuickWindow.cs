using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace PgrVoice;

/// <summary>
/// 0.4.7 桌面悬浮快捷窗（设计稿 17–19）。两种外形：
/// · 展开「配音快捷」面板（宽 364）：深色标题栏（握把 + 置顶 / 打开主窗口 / 收起），正文由主窗口按状态填充；
/// · 悬浮球右侧的扇出（B）：主按钮 + 两个次要按钮 + 说明，无标题栏。
/// 不抢焦点（WS_EX_NOACTIVATE + MA_NOACTIVATE，与分支菜单窗相同），点完焦点仍在游戏；只有窗口自身的矩形接收鼠标（不画外阴影）。
/// 只能从标题栏握把拖动；距屏幕工作区或游戏窗口边 16px 内吸附；位置由主窗口按显示器记住。全部坐标用物理像素（Per-Monitor DPI）。
/// </summary>
public sealed class QuickWindow : Window
{
    public enum Shape { Panel, Fan }
    public Shape Form { get; private set; } = Shape.Panel;
    public readonly StackPanel Body = new() { Margin = new Thickness(10, 9, 10, 11) };
    public readonly StackPanel FanBody = new();
    public Action? OpenMain, Minimize, PinChanged, Moved;
    public Func<IntPtr>? GameHandle;
    public bool Pinned { get; private set; } = true;
    public bool Dragging => dragStart != null;
    readonly CutFrame frame;
    readonly Grid panelRoot = new();
    readonly Border header;
    readonly Button pinButton;
    readonly CutFrame pinFrame;
    readonly Border fanRoot;
    Point? dragStart; RECT dragRect;

    public QuickWindow()
    {
        Title = "配音快捷"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true; SnapsToDevicePixels = true;
        FontFamily = Theme.Font("UiFont"); Foreground = Theme.Brush("Fg");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // 标题栏（.qh）：石板深色 30px，握把点阵 + 「配音快捷」+ QUICK；右侧三个工具；左下 54×2 红条。
        var grip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        var dots = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Rows = 3, Width = 8, Height = 12, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false };
        for (int i = 0; i < 6; i++) dots.Children.Add(new Ellipse { Width = 2.2, Height = 2.2, Fill = Theme.Brush("PlateSub") });
        grip.Children.Add(dots);
        grip.Children.Add(new TextBlock { Text = "配音快捷", FontSize = 11.5, Foreground = Theme.Brush("PlateFg"), VerticalAlignment = VerticalAlignment.Center });
        grip.Children.Add(new TextBlock { Text = "QUICK", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Theme.Brush("PlateSub"), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        (pinButton, pinFrame) = Tool("pin", "置顶（默认开启）", () => { Pinned = !Pinned; ApplyPin(); PinChanged?.Invoke(); });
        tools.Children.Add(pinButton);
        tools.Children.Add(Tool("open", "打开主窗口（F6）", () => OpenMain?.Invoke()).Item1);
        tools.Children.Add(Tool("min", "收起回台词条 / 悬浮球（Esc）", () => Minimize?.Invoke()).Item1);
        var headGrid = new Grid { Height = 30 };
        headGrid.Children.Add(grip); headGrid.Children.Add(tools);
        headGrid.Children.Add(new Rectangle { Width = 54, Height = 2, Fill = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-12, 0, 0, 0), IsHitTestVisible = false });
        header = new Border { Background = Theme.Brush("Plate"), Padding = new Thickness(12, 0, 6, 0), Child = headGrid, Cursor = Cursors.SizeAll, ToolTip = "按住标题栏拖动；靠近屏幕或游戏窗口边缘 16px 内自动吸附" };
        header.MouseLeftButtonDown += HeaderDown; header.MouseMove += HeaderMove; header.MouseLeftButtonUp += HeaderUp; header.LostMouseCapture += (_, _) => EndDrag();

        panelRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panelRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelRoot.Children.Add(header); Grid.SetRow(Body, 1); panelRoot.Children.Add(Body);
        // 外框（.qw + frameSvg）：左上 / 右下 12px 切角，1px 钢灰描边，右下角 40px 红色收边。
        frame = new CutFrame { Background = Theme.Brush("Bg"), BorderBrush = Theme.Brush("StrokeStrong"), Cut = 12, Corners = CutCorners.Diagonal, ClipContent = true, Child = panelRoot, Width = 364 };
        var accent = new Path { Stroke = Theme.Brush("Accent"), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Data = Geometry.Parse("M12,0 V40 L0,52"), Margin = new Thickness(0, 0, 0.5, 0.5) };
        var outer = new Grid { Children = { frame, accent }, Margin = new Thickness(1) };
        // 1px 深色外缘：只在框外一圈，帮助浅色面板压在游戏画面上；不做外阴影，避免透明阴影区拦截点击。
        var halo = new CutFrame { BorderBrush = Hud.Solid("#66000000"), Cut = 13, Corners = CutCorners.Diagonal, IsHitTestVisible = false };
        var panelHost = new Grid { Children = { halo, outer } };

        fanRoot = new Border { Child = FanBody, Background = Brushes.Transparent };
        Content = new Grid { Children = { panelHost, fanRoot } };
        SetForm(Shape.Panel);
        ApplyPin();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80);
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        };
    }

    (Button, CutFrame) Tool(string icon, string tip, Action click)
    {
        var box = new CutFrame { Width = 26, Height = 22, Cut = 0, Corners = CutCorners.None, Margin = new Thickness(2, 0, 0, 0), Child = QuickUi.Icon(icon, Theme.Brush("PlateFg"), 13) };
        var button = QuickUi.Plate(box, tip, click);
        button.Cursor = Cursors.Hand;
        QuickUi.Hover(button, box, Brushes.Transparent, Hud.Solid("#22FFFFFF"), Hud.Solid("#33FFFFFF"));
        return (button, box);
    }
    void ApplyPin()
    {
        Topmost = Pinned;
        pinFrame.BorderBrush = Pinned ? Hud.Solid("#8CE2C27E") : null;
        pinFrame.Child = QuickUi.Icon("pin", Pinned ? Hud.Solid("#E2C27E") : Theme.Brush("PlateFg"), 13);
        pinButton.ToolTip = Pinned ? "置顶：已开启（点按关闭）" : "置顶：已关闭（点按开启）";
    }
    public void SetPinned(bool pinned) { Pinned = pinned; ApplyPin(); }

    public void SetForm(Shape form)
    {
        Form = form;
        ((Grid)Content).Children[0].Visibility = form == Shape.Panel ? Visibility.Visible : Visibility.Collapsed;
        fanRoot.Visibility = form == Shape.Fan ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>对齐成功等短提示的淡出：3 秒后 0.4 秒淡出，然后回调。</summary>
    public void FadeOut(UIElement element, Action done)
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromSeconds(3) };
        fade.Completed += (_, _) => done();
        element.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 不激活 ----------------
    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        return IntPtr.Zero;
    }

    // ---------------- 拖动与吸附（物理像素） ----------------
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public RECT PixelRect { get { GetWindowRect(Handle, out var r); return r; } }
    public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element != null && element != header; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.ClickCount > 1) return;
        GetCursorPos(out var p); dragStart = new Point(p.X, p.Y); dragRect = PixelRect;
        header.CaptureMouse(); e.Handled = true;
    }
    void HeaderMove(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var p);
        int width = dragRect.Right - dragRect.Left, height = dragRect.Bottom - dragRect.Top;
        int x = dragRect.Left + (int)(p.X - start.X), y = dragRect.Top + (int)(p.Y - start.Y);
        (x, y) = Snap(x, y, width, height, new POINT { X = p.X, Y = p.Y });
        MoveTo(x, y);
    }
    void HeaderUp(object sender, MouseButtonEventArgs e) { if (dragStart != null) { header.ReleaseMouseCapture(); EndDrag(); } }
    void EndDrag() { if (dragStart == null) return; dragStart = null; Moved?.Invoke(); }

    /// <summary>距屏幕工作区边或游戏窗口边 16px（按 DPI 换算）内吸附；游戏窗口内外两侧都吸。</summary>
    internal (int, int) Snap(int x, int y, int width, int height, POINT cursor)
    {
        int threshold = (int)Math.Round(16 * Scale);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var xs = new System.Collections.Generic.List<int> { screen.Left, screen.Right - width };
        var ys = new System.Collections.Generic.List<int> { screen.Top, screen.Bottom - height };
        var game = GameHandle?.Invoke() ?? IntPtr.Zero;
        if (game != IntPtr.Zero && IsWindowVisible(game) && !IsIconic(game) && GetWindowRect(game, out var g) && g.Right > g.Left)
        {
            xs.AddRange(new[] { g.Left, g.Right - width, g.Right, g.Left - width });
            ys.AddRange(new[] { g.Top, g.Bottom - height, g.Bottom, g.Top - height });
        }
        int Pick(int value, System.Collections.Generic.List<int> edges)
        {
            int best = value, distance = threshold + 1;
            foreach (var edge in edges) { int d = Math.Abs(edge - value); if (d <= threshold && d < distance) { best = edge; distance = d; } }
            return best;
        }
        return (Pick(x, xs), Pick(y, ys));
    }
    public void MoveTo(int x, int y) => SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
