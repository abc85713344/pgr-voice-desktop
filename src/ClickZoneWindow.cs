using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

// 屏幕上的「下一句」点击热区（64 × 64，ClickZone 01 徽记）。
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

    readonly ClickZoneGlyph glyph = new();

    public bool Draggable { get; private set; }

    public ClickZoneWindow()
    {
        // ClickZone 01（HANDOFF 10-CZ）：64×64 矢量徽记，空中花园轨道 + 鸦羽 V 标 + 红潮线；拖动态换青灰描边、四角与十字。
        Width = 64;
        Height = 64;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        // 细线与 0.5 像素坐标按设计稿绘制，不对齐像素网格。
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        Content = glyph;
        SetDraggableVisuals(false);

        MouseLeftButtonDown += (_, _) => { if (Draggable) { try { DragMove(); } catch { } Moved?.Invoke(); } };
    }

    public event Action? Moved;

    void SetDraggableVisuals(bool draggable)
    {
        glyph.Drag = draggable;
        Cursor = draggable ? Cursors.SizeAll : Cursors.Arrow;
        ToolTip = draggable
            ? "正在调整位置：拖到游戏里“点击继续”的位置，然后点击完成或收起播放器面板。"
            : "在游戏里点这个框会请求配音跟随。点击照常传给游戏；在设置里选择调整位置后可拖动。";
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
        SetDraggableVisuals(draggable);
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

/// <summary>ClickZone 01 徽记（64×64 视框，原创矢量）：底板 → 空中花园轨道与站点 → 鸦羽 V 标 / 拖动十字 → 文字 → 红潮线 → 描边与拖动角标。</summary>
public sealed class ClickZoneGlyph : FrameworkElement
{
    public static readonly DependencyProperty DragProperty = DependencyProperty.Register(nameof(Drag), typeof(bool), typeof(ClickZoneGlyph), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Drag { get => (bool)GetValue(DragProperty); set => SetValue(DragProperty, value); }
    public ClickZoneGlyph() { SnapsToDevicePixels = false; UseLayoutRounding = false; }

    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    static readonly Geometry Plate = G("M0.5,0.5 H53.5 L63.5,10.5 V63.5 H10.5 L0.5,53.5 Z");
    static readonly Geometry Station = G("M32,4.6 l2.1,2.6 l-2.1,2.6 l-2.1,-2.6 z");
    static readonly Geometry Spars = G("M27.5,7.2 h-3.4 M36.5,7.2 h3.4");
    static readonly Geometry FeatherL = G("M21,21 L24.2,21 L32,32.8 L32,38 Z");
    static readonly Geometry FeatherR = G("M43,21 L39.8,21 L32,32.8 L32,38 Z");
    static readonly Geometry Notches = G("M22.7,26.3 l2.2,-0.9 M25.45,30.55 l2.2,-0.9 M41.3,26.3 l-2.2,-0.9 M38.55,30.55 l-2.2,-0.9");
    static readonly Geometry InnerV = G("M26.28,16 L32,27 L37.72,16");
    static readonly Geometry Cross = G("M23,30 H41 M32,21 V39");
    static readonly Geometry Arrows = G("M32,18 l-2,3 h4 z M32,42 l-2,-3 h4 z M20,30 l3,-2 v4 z M44,30 l-3,-2 v4 z");
    static readonly Geometry DragCorners = G("M4,9 V4 H9 M55,4 H60 V9 M4,55 V60 H9 M55,60 H60 V55");
    static readonly Geometry Tide = TideLine.Crest(10, 54, 59, 6, 2.6);
    static Pen Pen(string hex, double w, double opacity = 1, DashStyle? dash = null)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex); c.A = (byte)(c.A * opacity);
        var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Miter, DashStyle = dash }; p.Freeze(); return p;
    }
    static Brush Fill(string hex) => Hud.Solid(hex);
    static readonly Pen Orbit = Pen("#C19A52", 0.8, 0.95, new DashStyle(new[] { 3.2 / 0.8, 1.6 / 0.8 }, 0));
    static readonly Pen Ink = Pen("#DFE7EE", 0.8);
    static readonly Pen NotchPen = Pen("#C71A2028", 0.9);
    static readonly Pen VPen = Pen("#B23A4A", 1.4);
    static readonly Pen CrossPen = Pen("#8FD3CF", 0.75, 0.9);

    protected override void OnRender(DrawingContext dc)
    {
        double k = Math.Min(RenderSize.Width, RenderSize.Height) / 64; if (k <= 0) return;
        dc.PushTransform(new ScaleTransform(k, k));
        bool drag = Drag;
        dc.DrawGeometry(Fill(drag ? "#C71A2028" : "#A81A2028"), null, Plate);
        dc.DrawEllipse(null, Orbit, new Point(32, 11), 17, 3.6);
        dc.DrawGeometry(null, Ink, Station); dc.DrawGeometry(null, Ink, Spars);
        dc.DrawEllipse(Fill("#C19A52"), null, new Point(48.6, 12.2), 1, 1);
        if (drag)
        {
            dc.DrawGeometry(null, CrossPen, Cross);
            dc.PushOpacity(0.9); dc.DrawGeometry(Fill("#8FD3CF"), null, Arrows); dc.Pop();
        }
        else
        {
            dc.DrawGeometry(Fill("#DFE7EE"), null, FeatherL); dc.DrawGeometry(Fill("#DFE7EE"), null, FeatherR);
            dc.DrawGeometry(null, NotchPen, Notches);
            dc.DrawGeometry(null, VPen, InnerV);
        }
        var text = new FormattedText(drag ? "拖动" : "下一句", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Theme.Font("UiFont"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 8.6,
            Fill(drag ? "#E3F1F0" : "#B4C0CB"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(32 - text.WidthIncludingTrailingWhitespace / 2, 51 - text.Baseline));
        dc.DrawGeometry(null, Pen(drag ? "#8CB23A4A" : "#B23A4A", 0.9), Tide);
        dc.PushOpacity(0.95);
        dc.DrawGeometry(null, new Pen(drag ? Theme.Brush("DragAccent") : Fill("#8FA6BF"), drag ? 2 : 1) { LineJoin = PenLineJoin.Miter }, Plate);
        dc.Pop();
        if (drag) dc.DrawGeometry(null, new Pen(Theme.Brush("DragAccentLight"), 1) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter }, DragCorners);
        dc.Pop();
    }
}
