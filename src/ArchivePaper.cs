using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>
/// A3 强度「档案纸面」（SPEC 通用规则）：放在大面积白色内容面板下面，所有装饰都画在内容之下、
/// 只占右侧 / 底部 / 角落；正文列前加一层 .55–.62 白色衬底，保证文字对比度不受影响。
/// 层次：底色渐变 + 角落辉光 → 4px 扫描线 + 28px 网格 → 轨道弧 → 红潮带 → F1b 徽记 → 描边大号水印 →
/// 侵蚀角块 → 右侧刻度轨 → 三处角标 → 正文衬底。
/// </summary>
public sealed class ArchivePaper : Decorator
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    static FrameworkPropertyMetadata Layout(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(nameof(Watermark), typeof(string), typeof(ArchivePaper), Render(""));
    public static readonly DependencyProperty WatermarkSizeProperty = DependencyProperty.Register(nameof(WatermarkSize), typeof(double), typeof(ArchivePaper), Render(250.0));
    public static readonly DependencyProperty WatermarkRightProperty = DependencyProperty.Register(nameof(WatermarkRight), typeof(double), typeof(ArchivePaper), Render(40.0));
    public static readonly DependencyProperty EmblemSizeProperty = DependencyProperty.Register(nameof(EmblemSize), typeof(double), typeof(ArchivePaper), Render(300.0));
    public static readonly DependencyProperty EmblemRightProperty = DependencyProperty.Register(nameof(EmblemRight), typeof(double), typeof(ArchivePaper), Render(-70.0));
    public static readonly DependencyProperty EmblemBottomProperty = DependencyProperty.Register(nameof(EmblemBottom), typeof(double), typeof(ArchivePaper), Render(-62.0));
    public static readonly DependencyProperty EmblemTopProperty = DependencyProperty.Register(nameof(EmblemTop), typeof(double), typeof(ArchivePaper), Render(double.NaN));
    public static readonly DependencyProperty TideHeightProperty = DependencyProperty.Register(nameof(TideHeight), typeof(double), typeof(ArchivePaper), Render(64.0));
    public static readonly DependencyProperty ShowRailProperty = DependencyProperty.Register(nameof(ShowRail), typeof(bool), typeof(ArchivePaper), Layout(true));
    public static readonly DependencyProperty RailLabelProperty = DependencyProperty.Register(nameof(RailLabel), typeof(string), typeof(ArchivePaper), Render("ARCHIVE"));
    public static readonly DependencyProperty RailMarksProperty = DependencyProperty.Register(nameof(RailMarks), typeof(string[]), typeof(ArchivePaper), Render(null));
    /// <summary>减淡水印（04-A6：实底 .06 / 描边 .15，默认 A3 为 .10 / .22）。</summary>
    public static readonly DependencyProperty WatermarkFaintProperty = DependencyProperty.Register(nameof(WatermarkFaint), typeof(bool), typeof(ArchivePaper), Render(false));
    public bool WatermarkFaint { get => (bool)GetValue(WatermarkFaintProperty); set => SetValue(WatermarkFaintProperty, value); }
    public static readonly DependencyProperty DecorTopProperty = DependencyProperty.Register(nameof(DecorTop), typeof(double), typeof(ArchivePaper), Render(0.0));
    public static readonly DependencyProperty VeilProperty = DependencyProperty.Register(nameof(Veil), typeof(bool), typeof(ArchivePaper), Render(true));
    public static readonly DependencyProperty ShowOrbitsProperty = DependencyProperty.Register(nameof(ShowOrbits), typeof(bool), typeof(ArchivePaper), Render(true));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(ArchivePaper), Layout(new Thickness(0)));
    /// <summary>描边大号水印（章节号、页码等）。空串不画。</summary>
    public string Watermark { get => (string)GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }
    public double WatermarkSize { get => (double)GetValue(WatermarkSizeProperty); set => SetValue(WatermarkSizeProperty, value); }
    public double WatermarkRight { get => (double)GetValue(WatermarkRightProperty); set => SetValue(WatermarkRightProperty, value); }
    /// <summary>F1b 徽记宽度；0 不画。</summary>
    public double EmblemSize { get => (double)GetValue(EmblemSizeProperty); set => SetValue(EmblemSizeProperty, value); }
    public double EmblemRight { get => (double)GetValue(EmblemRightProperty); set => SetValue(EmblemRightProperty, value); }
    public double EmblemBottom { get => (double)GetValue(EmblemBottomProperty); set => SetValue(EmblemBottomProperty, value); }
    /// <summary>设置后徽记改为从顶部定位（04 参考窗：卡片顶部 34px 起）。</summary>
    public double EmblemTop { get => (double)GetValue(EmblemTopProperty); set => SetValue(EmblemTopProperty, value); }
    public double TideHeight { get => (double)GetValue(TideHeightProperty); set => SetValue(TideHeightProperty, value); }
    public bool ShowRail { get => (bool)GetValue(ShowRailProperty); set => SetValue(ShowRailProperty, value); }
    public string RailLabel { get => (string)GetValue(RailLabelProperty); set => SetValue(RailLabelProperty, value); }
    /// <summary>刻度轨上的小号节点标记（如最近记录编号），最后一项画暗红「当前」刻线。</summary>
    public string[]? RailMarks { get => (string[]?)GetValue(RailMarksProperty); set => SetValue(RailMarksProperty, value); }
    /// <summary>顶部留给档案条的高度（装饰从这里往下排）。</summary>
    public double DecorTop { get => (double)GetValue(DecorTopProperty); set => SetValue(DecorTopProperty, value); }
    public bool Veil { get => (bool)GetValue(VeilProperty); set => SetValue(VeilProperty, value); }
    public bool ShowOrbits { get => (bool)GetValue(ShowOrbitsProperty); set => SetValue(ShowOrbitsProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public const double RailWidth = 26;

    public ArchivePaper() { ClipToBounds = true; SnapsToDevicePixels = true; }

    Thickness Inner => new(Padding.Left, Padding.Top, Padding.Right + (ShowRail ? RailWidth : 0), Padding.Bottom);
    protected override Size MeasureOverride(Size constraint)
    {
        var p = Inner; double hw = p.Left + p.Right, hh = p.Top + p.Bottom;
        if (Child == null) return new Size(hw, hh);
        Child.Measure(new Size(Math.Max(0, constraint.Width - hw), Math.Max(0, constraint.Height - hh)));
        return new Size(Child.DesiredSize.Width + hw, Child.DesiredSize.Height + hh);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var p = Inner;
        Child?.Arrange(new Rect(p.Left, p.Top, Math.Max(0, size.Width - p.Left - p.Right), Math.Max(0, size.Height - p.Top - p.Bottom)));
        return size;
    }

    static Brush? paper, tiles, glowBr, glowTl;
    static Color Mist(double a) => Color.FromArgb((byte)(255 * a), 0x4F, 0x6B, 0x8A);
    static Brush Freeze(Brush b) { b.Freeze(); return b; }
    static Pen P(Color c, double w, params double[] dash)
    {
        var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Miter };
        if (dash.Length > 0) p.DashStyle = new DashStyle(dash.Select(d => d / w), 0);
        p.Freeze(); return p;
    }
    static Brush Paper => paper ??= Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromRgb(0xFB, 0xFC, 0xFD), 0), new GradientStop(Color.FromRgb(0xF1, 0xF3, 0xF6), .6), new GradientStop(Color.FromRgb(0xE6, 0xEA, 0xEF), 1),
    }, new Point(.4, 0), new Point(.6, 1)));
    /// <summary>4px 扫描线 + 28px 网格（单块 28×28 平铺）。</summary>
    internal static Brush Tiles => tiles ??= BuildTiles();
    static Brush BuildTiles()
    {
        var g = new DrawingGroup();
        var scan = new SolidColorBrush(Mist(.05)); var grid = new SolidColorBrush(Mist(.07));
        for (int y = 0; y < 28; y += 4) g.Children.Add(new GeometryDrawing(scan, null, new RectangleGeometry(new Rect(0, y, 28, 1))));
        g.Children.Add(new GeometryDrawing(grid, null, new RectangleGeometry(new Rect(0, 0, 1, 28))));
        g.Children.Add(new GeometryDrawing(grid, null, new RectangleGeometry(new Rect(1, 0, 27, 1))));
        return Freeze(new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 28, 28), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 28, 28), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None });
    }
    static Brush GlowBr => glowBr ??= Freeze(new RadialGradientBrush { Center = new Point(1, 1), GradientOrigin = new Point(1, 1), RadiusX = 1.2, RadiusY = .9, GradientStops = { new GradientStop(Mist(.20), 0), new GradientStop(Mist(0), .55) } });
    static Brush GlowTl => glowTl ??= Freeze(new RadialGradientBrush { Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = .6, RadiusY = .5, GradientStops = { new GradientStop(Color.FromArgb(31, 0x3E, 0x7C, 0x7A), 0), new GradientStop(Color.FromArgb(0, 0x3E, 0x7C, 0x7A), .7) } });

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 20 || h < 20) return;
        var full = new Rect(0, 0, w, h); double top = DecorTop, rail = ShowRail ? RailWidth : 0;
        dc.DrawRectangle(Paper, null, full);
        dc.DrawRectangle(GlowBr, null, full); dc.DrawRectangle(GlowTl, null, full);
        dc.DrawRectangle(Tiles, null, full);
        if (ShowOrbits) DrawOrbits(dc, w - rail, h);
        if (TideHeight > 0) DrawTide(dc, w, h, TideHeight);
        if (EmblemSize > 0)
        {
            double ew = EmblemSize, eh = ew * EmblemArt.ViewHeight / EmblemArt.ViewWidth;
            double left = w - rail - EmblemRight - ew;
            double y = double.IsNaN(EmblemTop) ? h - EmblemBottom - eh : top + EmblemTop;
            ArchiveEmblem.Draw(dc, left, y, ew);
        }
        if (!string.IsNullOrEmpty(Watermark)) DrawWatermark(dc, w - rail - WatermarkRight, top + 14, Watermark, WatermarkSize, WatermarkFaint ? .06 : .10, WatermarkFaint ? .15 : .22, this);
        DrawCorrosion(dc, new Rect(w - rail - 28 - 60, top, 60, 32));
        if (ShowRail) DrawRail(dc, w, h, top);
        // 角标：左上绯红 3px、右下黄铜 3px、左下雾蓝 2px
        Corner(dc, new Point(8, top + 8), 20, 3, Color.FromRgb(0xA3, 0x28, 0x3A), 1, 1);
        Corner(dc, new Point(w - rail - 8, h - 8), 20, 3, Color.FromRgb(0xB0, 0x8A, 0x3E), -1, -1);
        Corner(dc, new Point(8, h - 8), 12, 2, Color.FromRgb(0x4F, 0x6B, 0x8A), 1, -1);
        if (Veil && Child is FrameworkElement c && c.ActualWidth > 0)
        {
            var r = new Rect(Inner.Left, Inner.Top, Math.Max(0, w - Inner.Left - Inner.Right), Math.Max(0, h - Inner.Top - Inner.Bottom));
            dc.DrawRectangle(VeilBrush, null, r);
        }
    }
    static Brush? veil;
    static Brush VeilBrush => veil ??= Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(158, 255, 255, 255), 0), new GradientStop(Color.FromArgb(140, 255, 255, 255), .62), new GradientStop(Color.FromArgb(0, 255, 255, 255), .86),
    }, 0));

    internal static void Corner(DrawingContext dc, Point at, double len, double t, Color c, int dx, int dy)
    {
        var b = new SolidColorBrush(c); b.Freeze();
        dc.DrawRectangle(b, null, new Rect(dx > 0 ? at.X : at.X - t, dy > 0 ? at.Y : at.Y - len, t, len));
        dc.DrawRectangle(b, null, new Rect(dx > 0 ? at.X : at.X - len, dy > 0 ? at.Y : at.Y - t, len, t));
    }
    internal static void DrawWatermark(DrawingContext dc, double right, double top, string text, double size, double fill, double stroke, Visual visual)
    {
        var ft = Hud.Text(visual, text, "NumberFont", size, Brushes.Black, FontWeights.Bold);
        var g = ft.BuildGeometry(new Point(right - ft.WidthIncludingTrailingWhitespace, top - ft.Height * .08));
        dc.DrawGeometry(new SolidColorBrush(Mist(fill)), P(Mist(stroke), 1.6), g);
    }
    static void DrawOrbits(DrawingContext dc, double w, double h)
    {
        // 预览 orbits()：600×300 视框、不等比拉伸，占底部 62%
        double oh = h * .62, oy = h - oh, sx = w / 600, sy = oh / 300;
        Point T(double x, double y) => new(x * sx, oy + y * sy);
        Geometry C(double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(T(x0, y0), false, false); ctx.BezierTo(T(c1x, c1y), T(c2x, c2y), T(x1, y1), true, false); }
            g.Freeze(); return g;
        }
        dc.DrawGeometry(null, P(Color.FromArgb(140, 0xB0, 0x8A, 0x3E), 1.4), C(-40, 250, 120, 120, 420, 60, 640, 110));
        dc.DrawGeometry(null, P(Color.FromArgb(82, 0xB0, 0x8A, 0x3E), 1, 6, 5), C(-40, 280, 160, 170, 430, 120, 640, 170));
        dc.DrawGeometry(null, P(Mist(.30), 1), C(-40, 215, 140, 110, 380, 70, 640, 70));
        var node = new SolidColorBrush(Color.FromArgb(204, 0xB0, 0x8A, 0x3E)); node.Freeze();
        dc.DrawEllipse(node, null, T(452, 86), 3, 3);
    }
    internal static void DrawTide(DrawingContext dc, double w, double h, double th)
    {
        // 预览 tide3()：雾蓝波 + #8E1E30 尖波；600 视框横向拉伸，纵向按高度缩放
        double k = th / 64, y0 = h - th; const double vw = 600;
        double sx = w / vw;
        Geometry Wave(double y, double a, double p, double ph)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, y0 + th), true, true);
                for (double x = 0; x <= vw; x += 4) ctx.LineTo(new Point(x * sx, y0 + (y + Math.Sin((x + ph) / p) * a) * k), true, true);
                ctx.LineTo(new Point(w, y0 + th), true, false);
            }
            g.Freeze(); return g;
        }
        var crest = new StreamGeometry();
        int[] up = { 3, 10, 4, 16, 5, 8, 3 }, dn = { 2, 1, 3, 1, 2, 1, 2 };
        using (var ctx = crest.Open())
        {
            ctx.BeginFigure(new Point(0, y0 + th), true, true); ctx.LineTo(new Point(0, y0 + 40 * k), true, false);
            for (int x = 0; x <= vw; x += 12)
            {
                int i = (x / 12) % 7;
                ctx.LineTo(new Point((x + 4) * sx, y0 + (40 - up[i]) * k), true, false);
                ctx.LineTo(new Point((x + 9) * sx, y0 + (40 + dn[i]) * k), true, false);
                ctx.LineTo(new Point((x + 12) * sx, y0 + 40 * k), true, false);
            }
            ctx.LineTo(new Point(w, y0 + th), true, false);
        }
        crest.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Mist(.10)), P(Mist(.40), 1), Wave(26, 6, 46, 0));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(26, 0x8E, 0x1E, 0x30)), P(Color.FromArgb(128, 0x8E, 0x1E, 0x30), 1.1), crest);
        dc.DrawGeometry(new SolidColorBrush(Mist(.16)), P(Mist(.50), 1), Wave(50, 4, 30, 20));
    }
    internal static void DrawCorrosion(DrawingContext dc, Rect r)
    {
        // 预览 corBlock()：46×24 视框的侵蚀碎片
        var g = Geometry.Parse("M46 0H10l4 4-6 3 8 2-3 4 9 1-2 5 8-1 2 6 5-3 4 4 2-6 5 3V0z");
        dc.PushTransform(new MatrixTransform(r.Width / 46, 0, 0, r.Height / 24, r.X, r.Y));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(77, 0x7D, 0x20, 0x30)), null, g);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(102, 0xA3, 0x28, 0x3A)), null, new Rect(4, 12, 2, 2));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(77, 0xA3, 0x28, 0x3A)), null, new Rect(12, 19, 2, 2));
        dc.Pop();
    }
    void DrawRail(DrawingContext dc, double w, double h, double top)
    {
        double x = w - RailWidth;
        var bg = new LinearGradientBrush(Color.FromArgb(18, 0x2A, 0x31, 0x39), Color.FromArgb(5, 0x2A, 0x31, 0x39), 90); bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(x, top, RailWidth, h - top));
        dc.DrawRectangle(new SolidColorBrush(Mist(.45)), null, new Rect(x, top, 1, h - top));
        var tick = new SolidColorBrush(Mist(.55)); tick.Freeze();
        for (int i = 0; top + i * 10 < h; i++) { double tw = i % 5 == 0 ? 12 : 6; dc.DrawRectangle(tick, null, new Rect(w - tw, top + i * 10, tw, 1)); }
        var marks = RailMarks;
        if (marks is { Length: > 0 })
        {
            var ink = new SolidColorBrush(Mist(.85)); ink.Freeze();
            for (int i = 0; i < marks.Length; i++)
            {
                var ft = Hud.Text(this, marks[i], "NumberFont", 8.5, ink);
                double y = top + 40 + i * 28;
                dc.DrawText(ft, new Point(w - 3 - ft.Width, y - ft.Height * 1.1));
            }
            dc.DrawRectangle(Hud.Brush("Accent", "#A3283A"), null, new Rect(x, top + 40 + (marks.Length - 1) * 28 - 1, RailWidth, 3));
        }
        if (!string.IsNullOrEmpty(RailLabel))
        {
            var label = Hud.Text(this, Spaced(RailLabel), "NumberFont", 8.5, new SolidColorBrush(Mist(.75)));
            double lx = w - 7 - label.Height / 2, ly = h - 64 - label.Width;
            if (ly > top + 40 + (marks?.Length ?? 0) * 28)
            {
                dc.PushTransform(new RotateTransform(90, lx, ly)); dc.DrawText(label, new Point(lx, ly - label.Height / 2)); dc.Pop();
            }
        }
    }
    internal static string Spaced(string s) => string.Join("\u200A", s.ToUpperInvariant().ToCharArray());
}
