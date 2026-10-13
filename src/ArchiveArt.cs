using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>F1b 档案徽记（300×380 视框）：从嵌入的线稿数据构建一次并冻结，所有尺寸共用。</summary>
internal static class EmblemArt
{
    public const double ViewWidth = 300, ViewHeight = 380;
    static DrawingGroup? f1b;
    public static DrawingGroup F1b => f1b ??= Build();
    static Brush? Color(string s)
    {
        if (s.Length == 0) return null;
        if (s == "KO")
        {
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(204, 0xF7, 0xF9, 0xFB), 0));
            g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(184, 0xF7, 0xF9, 0xFB), 0.86));
            g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 0xF7, 0xF9, 0xFB), 1));
            g.Freeze(); return g;
        }
        var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(s)); b.Freeze(); return b;
    }
    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    static Geometry Text(string spec)
    {
        // x,y,size,anchor,text（文字本身可能含逗号以外的任意字符）
        var parts = spec.Split(',', 5);
        double x = D(parts[0]), y = D(parts[1]), size = D(parts[2]); string anchor = parts[3], text = parts[4];
        var face = new Typeface(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var group = new GeometryGroup(); double pen = 0, baseline = 0;
        foreach (char c in text)
        {
            var ft = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
            baseline = ft.Baseline;
            group.Children.Add(ft.BuildGeometry(new Point(pen, 0)));
            pen += ft.WidthIncludingTrailingWhitespace + 1.2;   // letter-spacing 1.2
        }
        pen -= 1.2;
        double left = anchor == "middle" ? x - pen / 2 : anchor == "end" ? x - pen : x;
        group.Transform = new TranslateTransform(left, y - baseline);
        return group;
    }
    static DrawingGroup Build()
    {
        var root = new DrawingGroup();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PgrVoice.EmblemF1b.txt");
        if (stream == null) { root.Freeze(); return root; }
        using var reader = new StreamReader(stream);
        string? lastKey = null; GeometryGroup? current = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var f = line.Split('|', 6);
            if (f.Length < 6) continue;
            Geometry g = f[5][0] switch
            {
                'P' => Geometry.Parse("F1 " + f[5][1..]),
                'E' => Ellipse(f[5][1..]),
                _ => Text(f[5][1..]),
            };
            var m = f[4].Split(',').Select(D).ToArray();
            var wrapper = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = new MatrixTransform(m[0], m[1], m[2], m[3], m[4], m[5]) };
            wrapper.Children.Add(g);
            string key = string.Join('|', f, 0, 4);
            if (key != lastKey || current == null)
            {
                current = new GeometryGroup { FillRule = FillRule.Nonzero };
                Pen? pen = null;
                if (f[0].Length > 0)
                {
                    double w = D(f[1]);
                    pen = new Pen(Color(f[0]), w) { LineJoin = PenLineJoin.Miter };
                    if (f[2].Length > 0) pen.DashStyle = new DashStyle(f[2].Split(',').Select(v => D(v) / Math.Max(0.05, w)), 0);
                }
                root.Children.Add(new GeometryDrawing(Color(f[3]), pen, current));
                lastKey = key;
            }
            current.Children.Add(wrapper);
        }
        root.Freeze();
        return root;
    }
    static Geometry Ellipse(string spec)
    {
        var v = spec.Split(',').Select(D).ToArray();
        return new EllipseGeometry(new Point(v[0], v[1]), v[2], v[3]);
    }
}

/// <summary>F1b 档案徽记：按宽度等比缩放（300:380）；装饰件，不参与命中测试。</summary>
public sealed class ArchiveEmblem : FrameworkElement
{
    public ArchiveEmblem() { IsHitTestVisible = false; Focusable = false; }
    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsNaN(Width) ? (double.IsInfinity(available.Width) ? 140 : available.Width) : Width;
        return new Size(w, w * EmblemArt.ViewHeight / EmblemArt.ViewWidth);
    }
    protected override void OnRender(DrawingContext dc)
    {
        double k = Math.Min(RenderSize.Width / EmblemArt.ViewWidth, RenderSize.Height / EmblemArt.ViewHeight);
        if (k <= 0) return;
        dc.PushTransform(new ScaleTransform(k, k)); dc.DrawDrawing(EmblemArt.F1b); dc.Pop();
    }
    internal static void Draw(DrawingContext dc, double left, double top, double width)
    {
        double k = width / EmblemArt.ViewWidth;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, left, top)); dc.DrawDrawing(EmblemArt.F1b); dc.Pop();
    }
}

/// <summary>红潮线：等距尖峰（Q 曲线上升、直线回落），画在控件底部基线上。</summary>
public sealed class TideLine : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(int), typeof(TideLine), Render(6));
    public static readonly DependencyProperty PeakHeightProperty = DependencyProperty.Register(nameof(PeakHeight), typeof(double), typeof(TideLine), Render(2.6));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TideLine), Render(null));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(TideLine), Render(0.9));
    public int Peaks { get => (int)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double PeakHeight { get => (double)GetValue(PeakHeightProperty); set => SetValue(PeakHeightProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public TideLine() { IsHitTestVisible = false; Focusable = false; }
    internal static Geometry Crest(double x0, double x1, double y, int peaks, double amp)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0, y), false, false);
            double st = (x1 - x0) / Math.Max(1, peaks);
            for (int i = 0; i < peaks; i++)
            {
                double a = x0 + i * st;
                ctx.QuadraticBezierTo(new Point(a + st * .55, y), new Point(a + st * .78, y - amp), true, false);
                ctx.LineTo(new Point(a + st, y), true, false);
            }
        }
        g.Freeze(); return g;
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 4) return;
        double y = h - StrokeThickness / 2;
        dc.DrawGeometry(null, Hud.Pen(Stroke ?? Hud.Brush("Accent", "#A3283A"), StrokeThickness),
            Crest(0, w, y, Peaks > 0 ? Peaks : Math.Max(3, (int)(w / 18)), Math.Min(PeakHeight, Math.Max(1, h - StrokeThickness))));
    }
}

/// <summary>轨道弧：黄铜虚线椭圆弧 + 节点，铺满控件（低透明度装饰）。</summary>
public sealed class OrbitArc : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OrbitArc), Render(null));
    public static readonly DependencyProperty StartAngleProperty = DependencyProperty.Register(nameof(StartAngle), typeof(double), typeof(OrbitArc), Render(160.0));
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(OrbitArc), Render(200.0));
    public static readonly DependencyProperty TiltProperty = DependencyProperty.Register(nameof(Tilt), typeof(double), typeof(OrbitArc), Render(-14.0));
    public static readonly DependencyProperty DashedProperty = DependencyProperty.Register(nameof(Dashed), typeof(bool), typeof(OrbitArc), Render(true));
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StartAngle { get => (double)GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public double Tilt { get => (double)GetValue(TiltProperty); set => SetValue(TiltProperty, value); }
    public bool Dashed { get => (bool)GetValue(DashedProperty); set => SetValue(DashedProperty, value); }
    public OrbitArc() { IsHitTestVisible = false; Focusable = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 8 || h < 4) return;
        var brush = Stroke ?? Hud.Brush("Brass", "#B08A3E");
        var pen = new Pen(brush, 1) { DashStyle = Dashed ? new DashStyle(new[] { 5.0, 3.0 }, 0) : null }; pen.Freeze();
        Point P(double deg)
        {
            double a = deg * Math.PI / 180, t = Tilt * Math.PI / 180;
            double x = Math.Cos(a) * w * .48, y = Math.Sin(a) * h * .42;
            return new Point(w / 2 + x * Math.Cos(t) - y * Math.Sin(t), h / 2 + x * Math.Sin(t) + y * Math.Cos(t));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(StartAngle), false, false);
            for (double d = 4; d <= Sweep; d += 4) ctx.LineTo(P(StartAngle + d), true, true);
        }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
        var node = P(StartAngle + Sweep * .72); dc.DrawEllipse(brush, null, node, 2.2, 2.2);
    }
}
