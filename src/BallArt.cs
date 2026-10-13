using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace PgrVoice;

public enum BallIcon { Text, Note, Pause }

/// <summary>
/// 悬浮球 14-02A「轨道弧」：64×64 坐标系内的全部线稿（轨道弧、空间站菱形节点、鸦羽冠、红潮、微铭文、标签、草稿签、角标）。
/// 外形与底色、描边仍由外层 CutFrame（Cut 13 · Diagonal）负责；本控件不参与命中测试，单击 / 拖动行为不变。
/// 只有 Playing 时运行红潮振幅 / 行进动画；减少动画时停在静态振幅 1.0。
/// </summary>
public sealed class BallArt : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(BallArt), Render("声"));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(BallIcon), typeof(BallArt), Render(BallIcon.Text));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(BallArt), Render("待开始"));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(bool), typeof(BallArt), Render(false));
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(BallArt), Render(false));
    public static readonly DependencyProperty OriginalProperty = DependencyProperty.Register(nameof(Original), typeof(bool), typeof(BallArt), Render(false));
    public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(nameof(Draft), typeof(bool), typeof(BallArt), Render(false));
    public static readonly DependencyProperty WavyProperty = DependencyProperty.Register(nameof(Wavy), typeof(bool), typeof(BallArt), Render(false));
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(nameof(Playing), typeof(bool), typeof(BallArt),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BallArt)d).UpdateMotion()));
    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(nameof(Amplitude), typeof(double), typeof(BallArt), Render(1.0));
    public static readonly DependencyProperty DriftProperty = DependencyProperty.Register(nameof(Drift), typeof(double), typeof(BallArt), Render(0.0));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public BallIcon Icon { get => (BallIcon)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>分支待选 / 路线台词 / 待手动续接：线条换黄铜，文字换 BranchAccent。</summary>
    public bool Highlight { get => (bool)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    /// <summary>分支待选：外层为 BranchFill 底，节点填色随之改变。</summary>
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public bool Original { get => (bool)GetValue(OriginalProperty); set => SetValue(OriginalProperty, value); }
    public bool Draft { get => (bool)GetValue(DraftProperty); set => SetValue(DraftProperty, value); }
    /// <summary>红潮为起伏（播放、分支、待续接、原声）；否则为平线。</summary>
    public bool Wavy { get => (bool)GetValue(WavyProperty); set => SetValue(WavyProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }
    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public double Drift { get => (double)GetValue(DriftProperty); set => SetValue(DriftProperty, value); }

    public BallArt()
    {
        IsHitTestVisible = false; Focusable = false; Width = 64; Height = 64;
        IsVisibleChanged += (_, _) => UpdateMotion();
        Loaded += (_, _) => { Hud.MotionChanged += UpdateMotion; UpdateMotion(); };
        Unloaded += (_, _) => { Hud.MotionChanged -= UpdateMotion; Stop(); };
    }
    bool animating;
    void Stop() { animating = false; BeginAnimation(AmplitudeProperty, null); BeginAnimation(DriftProperty, null); Amplitude = 1; Drift = 0; }
    void UpdateMotion()
    {
        bool run = Playing && IsVisible && !Hud.ReduceMotion;
        if (run == animating) return;
        if (!run) { Stop(); return; }
        animating = true;
        BeginAnimation(AmplitudeProperty, new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        BeginAnimation(DriftProperty, new DoubleAnimation(0, -Step, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    const double Step = 4.5, X0 = 14, X1 = 50, TideY = 38.6;
    static readonly Brush Paper = Hud.Solid("#FFFFFF"), TideShadow = Hud.Solid("#8FA6BF");
    static Brush K(string key, string hex) => Hud.Brush(key, hex);

    protected override void OnRender(DrawingContext dc)
    {
        bool hl = Highlight;
        Brush red = K("Accent", "#A3283A"), brass = K("Brass", "#B08A3E"), brassText = K("BranchAccent", "#7F6223");
        Brush line = hl ? brass : K("Slate", "#4F6B8A"), mark = hl ? brass : red;
        Brush glyph = hl ? brassText : Original ? red : K("NormalAccent", "#3F5A78");

        // 顶部握把 18×1.6；左下 / 右上角标（右上在高亮时为黄铜）。
        dc.DrawRectangle(red, null, new Rect(23, 2.7, 18, 1.6));
        dc.DrawGeometry(null, Hud.Pen(red, 1), Geometry.Parse("M3.5,56.5 V60.5 H7.5"));
        dc.DrawGeometry(null, Hud.Pen(mark, 1), Geometry.Parse("M60.5,7.5 V3.5 H56.5"));

        // 巴比伦塔轨道弧：虚线椭圆弧 + 两端黄铜节点。
        var orbit = new Pen(hl ? brass : TideShadow, 0.5) { DashStyle = new DashStyle(new[] { 4.4, 2.8 }, 0) }; orbit.Freeze();
        dc.DrawGeometry(null, orbit, Geometry.Parse("M6.5,30 A27,22 0 0 1 57.5,30"));
        dc.DrawEllipse(brass, null, new Point(6.5, 30), 0.9, 0.9);
        dc.DrawEllipse(brass, null, new Point(57.5, 30), 0.9, 0.9);
        // 空间站菱形节点：待机在左，播放时移到右侧（草稿态不移，避让 DRAFT 签）。
        double sx = Playing && !Draft ? 51.2 : 12.8, sy = 15.6;
        dc.DrawGeometry(Filled ? K("BranchFill", "#F3ECDC") : Paper, Hud.Pen(mark, 0.7), Diamond(sx, sy, 1.8, 2.1));

        // 鸦羽冠：两侧各 4 道二次曲线 + 冠顶实心菱。
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < 4; i++)
            {
                double x = 32 + side * 7.5, y = 22 + i * .2;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x, y), false, false);
                    c.QuadraticBezierTo(new Point(x + side * (5 + i * 2.4), y - 3.8 + i * 1.9), new Point(x + side * (9.6 + i * 2.6), y - 6.4 + i * 3.4), true, true);
                }
                g.Freeze(); dc.DrawGeometry(null, Hud.Pen(line, .75 - i * .1, .95 - i * .15), g);
            }
        dc.DrawGeometry(mark, null, Diamond(32, 11.4, 1.5, 1.8));

        // 字形：基线 31；♫ / Ⅱ 为矢量。
        switch (Icon)
        {
            case BallIcon.Note:
                dc.DrawGeometry(null, Hud.Pen(glyph, 1.5), Geometry.Parse("M28.2,29.6 V19 L37.2,16.6 V27.4"));
                Note(dc, glyph, 26.6, 29.6); Note(dc, glyph, 35.6, 27.4);
                break;
            case BallIcon.Pause:
                dc.DrawRectangle(glyph, null, new Rect(27.4, 18, 2.6, 12)); dc.DrawRectangle(glyph, null, new Rect(34, 18, 2.6, 12));
                break;
            default:
                Centered(dc, Glyph == "?" ? "？" : Glyph, "UiFont", 15.5, glyph, FontWeights.SemiBold, 31);
                break;
        }

        // 红潮：平线（待机 / 暂停 / 跟随未播放）或尖浪；播放时振幅与行进由动画驱动。
        if (!Wavy && !Playing)
        {
            dc.DrawLine(Hud.Pen(mark, .8, .75), new Point(X0, TideY), new Point(X1, TideY));
            var dash = new Pen(TideShadow, .5) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) }; dash.Freeze();
            dc.DrawLine(dash, new Point(X0, TideY + 2.1), new Point(X1, TideY + 2.1));
        }
        else
        {
            double amp = Playing ? 4.4 * Amplitude : 2.6, drift = Playing ? Drift : 0;
            dc.PushClip(new RectangleGeometry(new Rect(X0, TideY - 6, X1 - X0, 10)));
            dc.DrawGeometry(null, Hud.Pen(mark, .85), Tide(X0, TideY + 1, amp, drift));
            dc.DrawGeometry(null, Hud.Pen(TideShadow, .5), Tide(X0 + 2, TideY + 2.7, 1.4 * (Playing ? Amplitude : 1), drift, 32.0 / 8));
            dc.Pop();
        }

        // 微铭文（装饰）+ 状态标签（8.6px，AA）。
        Spaced(dc, "PGR · VOICE", 3.8, hl ? brassText : K("Slate", "#4F6B8A"), 46.4, 0.55, 0.9);
        Centered(dc, Label ?? "", "UiFont", 8.6, hl ? brassText : K("MutedText", "#4B5560"), FontWeights.Medium, 56);

        if (Draft)
        {
            var tag = new Pen(brass, .6) { DashStyle = new DashStyle(new[] { 2.67, 1.67 }, 0) }; tag.Freeze();
            dc.DrawRectangle(null, tag, new Rect(44, 14, 14, 6));
            Spaced(dc, "DRAFT", 4.6, brassText, 18.6, 0.3, 1, 51, FontWeights.Bold);
        }
    }

    static Geometry Diamond(double x, double y, double hw, double hh)
    { var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(new Point(x, y - hh), true, true); c.LineTo(new Point(x + hw, y), true, false); c.LineTo(new Point(x, y + hh), true, false); c.LineTo(new Point(x - hw, y), true, false); } g.Freeze(); return g; }
    static void Note(DrawingContext dc, Brush b, double x, double y)
    { dc.PushTransform(new RotateTransform(-20, x, y)); dc.DrawEllipse(b, null, new Point(x, y), 2.6, 2.0); dc.Pop(); }
    /// <summary>尖峰包络固定在空间位置上，行进一个波长后无缝衔接。</summary>
    static Geometry Tide(double x0, double y, double amp, double drift, double step = Step)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double start = x0 + drift - step; c.BeginFigure(new Point(start, y), false, false);
            for (int i = 0; i < 11; i++)
            {
                double a = start + i * step, crest = a + step * .78;
                double k = amp * (.55 + .45 * Math.Sin((crest - x0 - step * .78) / step * 1.7 + .6));
                c.QuadraticBezierTo(new Point(a + step * .55, y), new Point(crest, y - k), true, false);
                c.LineTo(new Point(a + step, y), true, false);
            }
        }
        g.Freeze(); return g;
    }
    FormattedText Fmt(string text, string font, double size, Brush brush, FontWeight weight) => Hud.Text(this, text, font, size, brush, weight);
    void Centered(DrawingContext dc, string text, string font, double size, Brush brush, FontWeight weight, double baseline)
    { var t = Fmt(text, font, size, brush, weight); dc.DrawText(t, new Point(32 - t.Width / 2, baseline - t.Baseline)); }
    void Spaced(DrawingContext dc, string text, double size, Brush brush, double baseline, double spacing, double opacity, double center = 32, FontWeight? weight = null)
    {
        var parts = new FormattedText[text.Length]; double total = 0;
        for (int i = 0; i < text.Length; i++) { parts[i] = Fmt(text[i].ToString(), "LabelFont", size, brush, weight ?? FontWeights.SemiBold); total += parts[i].WidthIncludingTrailingWhitespace + (i > 0 ? spacing : 0); }
        double x = center - total / 2; if (opacity < 1) dc.PushOpacity(opacity);
        foreach (var p in parts) { dc.DrawText(p, new Point(x, baseline - p.Baseline)); x += p.WidthIncludingTrailingWhitespace + spacing; }
        if (opacity < 1) dc.Pop();
    }
}
