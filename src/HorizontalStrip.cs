using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>
/// 横向滑动条（页签 / 芯片条溢出时用）：左右切角箭头 + 边缘渐隐；滚轮、Shift+滚轮、触控板横扫、触摸平移都横向滚动。
/// 内容放得下时箭头收起、无渐隐，与原来完全一样；获得键盘 / 手柄焦点的项自动滚入视野。
/// </summary>
internal sealed class HorizontalStrip : Grid
{
    const int WmMouseHWheel = 0x020E;
    const double FadeWidth = 26, RevealPadding = 30;
    internal ScrollViewer Viewer { get; }
    internal StripChevron LeftButton { get; }
    internal StripChevron RightButton { get; }
    HwndSource? source;
    bool chevronsShown;

    internal HorizontalStrip(UIElement content, bool dark = true)
    {
        ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Viewer = new ScrollViewer
        {
            Content = content, Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, PanningMode = PanningMode.HorizontalOnly, PanningRatio = 1,
        };
        SetColumn(Viewer, 1); Children.Add(Viewer);
        LeftButton = new StripChevron(left: true, dark) { Visibility = Visibility.Collapsed, ToolTip = "向左滚动" };
        RightButton = new StripChevron(left: false, dark) { Visibility = Visibility.Collapsed, ToolTip = "向右滚动" };
        SetColumn(RightButton, 2); Children.Add(LeftButton); Children.Add(RightButton);
        LeftButton.Click += (_, _) => ScrollBy(-Step);
        RightButton.Click += (_, _) => ScrollBy(Step);
        Viewer.PreviewMouseWheel += (_, e) => { if (!CanScroll) return; ScrollBy(-e.Delta); e.Handled = true; };
        Viewer.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        Viewer.ScrollChanged += (_, _) => Update();
        Viewer.SizeChanged += (_, _) => Update();
        Viewer.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => { if (e.NewFocus is FrameworkElement f) Reveal(f); }), true);
        Loaded += (_, _) => { source = PresentationSource.FromVisual(this) as HwndSource; source?.AddHook(Hook); Update(); };
        Unloaded += (_, _) => { source?.RemoveHook(Hook); source = null; };
    }

    /// <summary>true：内容按宽度换行（不滚动、无箭头）；false：单行横向滑动。</summary>
    internal bool Wrap
    {
        get => Viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled;
        set
        {
            if (value == Wrap) return;
            Viewer.ScrollToHorizontalOffset(0);
            Viewer.HorizontalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            Viewer.PanningMode = value ? PanningMode.None : PanningMode.HorizontalOnly;
            Update();
        }
    }

    double Step => Math.Max(120, Viewer.ViewportWidth * .6);
    internal bool CanScroll => Viewer.ScrollableWidth > .5;
    internal bool CanScrollLeft => Viewer.HorizontalOffset > .5;
    internal bool CanScrollRight => Viewer.HorizontalOffset < Viewer.ScrollableWidth - .5;
    internal void ScrollBy(double delta) => Viewer.ScrollToHorizontalOffset(Math.Clamp(Viewer.HorizontalOffset + delta, 0, Viewer.ScrollableWidth));

    /// <summary>把元素滚入可见区，左右各留出渐隐 / 箭头的余量；比可见区还宽的元素（如整个列表）不处理。</summary>
    internal void Reveal(FrameworkElement? element)
    {
        if (element == null || !element.IsVisible || ReferenceEquals(element, Viewer) || !Viewer.IsAncestorOf(element)) return;
        if (element.ActualWidth <= 0 || element.ActualWidth >= Viewer.ViewportWidth) return;
        var box = element.TransformToAncestor(Viewer).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        double pad = Math.Min(RevealPadding, Math.Max(0, (Viewer.ViewportWidth - box.Width) / 2));
        if (box.Left < pad) ScrollBy(box.Left - pad);
        else if (box.Right > Viewer.ViewportWidth - pad) ScrollBy(box.Right - (Viewer.ViewportWidth - pad));
    }

    // WPF 不把 WM_MOUSEHWHEEL（触控板横扫 / 滚轮左右拨）转成事件，这里只在指针位于本条上方时接管。
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel || !IsVisible || !IsMouseOver || !CanScroll) return IntPtr.Zero;
        int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        ScrollBy(delta); handled = true; return IntPtr.Zero;
    }

    void Update()
    {
        double chevrons = chevronsShown ? LeftButton.ActualWidth + RightButton.ActualWidth : 0;
        bool overflow = Viewer.ExtentWidth > Viewer.ViewportWidth + chevrons + .5;
        if (overflow != chevronsShown)
        {
            chevronsShown = overflow;
            LeftButton.Visibility = RightButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        }
        bool left = overflow && CanScrollLeft, right = overflow && CanScrollRight;
        LeftButton.IsEnabled = left; RightButton.IsEnabled = right;
        double width = Viewer.ActualWidth;
        if (!(left || right) || width <= 0) { Viewer.OpacityMask = null; return; }
        double f = Math.Min(.3, FadeWidth / width);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, left ? f : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, right ? 1 - f : 1));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze(); Viewer.OpacityMask = mask;
    }
}

/// <summary>滑动条两端的箭头：深石板（或浅灰）切角块 + 内侧发丝线 + 黄铜底刻；悬停顶部暗红刻线；不可滚动时变暗。不抢键盘焦点。</summary>
internal sealed class StripChevron : RepeatButton
{
    readonly bool left, dark;
    internal StripChevron(bool left, bool dark)
    {
        this.left = left; this.dark = dark;
        Template = null; Width = 24; Focusable = false; IsTabStop = false; Cursor = Cursors.Hand;
        Delay = 380; Interval = 110; Background = Brushes.Transparent; SnapsToDevicePixels = true;
    }
    protected override Size MeasureOverride(Size constraint) => new(Width, 0);
    protected override Size ArrangeOverride(Size arrangeBounds) => arrangeBounds;
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w <= 0 || h <= 0) return;
        bool on = IsEnabled, hot = on && IsMouseOver, pressed = on && IsPressed;
        string fill = dark ? (pressed ? "#3A4550" : hot ? "#303943" : "#232A31") : (pressed ? "#D3D9DF" : hot ? "#DDE2E7" : "#E6EAEE");
        var rect = new Rect(0, 0, w, h);
        // 外侧切角：左箭头切左下角，右箭头切右上角（与页签的右上切角同向）
        dc.DrawGeometry(Hud.Solid(fill), null, Hud.Chamfer(rect, 0, left ? 0 : 7, 0, left ? 7 : 0));
        var hair = Hud.Pen(Hud.Solid(dark ? "#4A545F" : "#C9D0D7"), 1);
        double x = left ? w - .5 : .5; dc.DrawLine(hair, new Point(x, 6), new Point(x, h - 6));
        if (on) dc.DrawRectangle(Theme.Brush("Brass"), null, new Rect(left ? w - 9 : 1, h - 2, 8, 2));
        if (hot) dc.DrawRectangle(Theme.Brush("Accent"), null, new Rect(left ? 3 : 1, 0, w - 4, 2));
        var ink = Hud.Solid(on ? (dark ? (hot ? "#FFFFFF" : "#DDE3E9") : "#36404A") : (dark ? "#55606B" : "#B5BDC5"));
        var pen = new Pen(ink, 1.6) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
        double cx = w / 2, cy = h / 2, d = left ? 2 : -2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx + d, cy - 4.5), false, false);
            c.LineTo(new Point(cx - d, cy), true, false);
            c.LineTo(new Point(cx + d, cy + 4.5), true, false);
        }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
    }
}
