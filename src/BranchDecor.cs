using System.Windows;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>
/// 分支浮层装饰（10-A2 / 11-B / 12-B）：只画在页眉、右侧刻度轨、底部潮汐带与四角，选项行与按钮上不压装饰。
/// 页眉：网格 + 扫描线 + 描边节点号水印 + F1b 徽记出血 + 黄铜轨道；右侧 24px 刻度轨（竖排 ROUTE // BRANCH · GRAY RAVEN）；
/// 底部潮汐带（12-B 另加刻度尺）；左上绯红 / 右下黄铜 3px 角括号。
/// </summary>
public sealed class BranchDecor : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty HeaderBottomProperty = DependencyProperty.Register(nameof(HeaderBottom), typeof(double), typeof(BranchDecor), Render(120.0));
    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(nameof(Watermark), typeof(string), typeof(BranchDecor), Render(""));
    public static readonly DependencyProperty WatermarkSizeProperty = DependencyProperty.Register(nameof(WatermarkSize), typeof(double), typeof(BranchDecor), Render(128.0));
    public static readonly DependencyProperty EmblemSizeProperty = DependencyProperty.Register(nameof(EmblemSize), typeof(double), typeof(BranchDecor), Render(118.0));
    public static readonly DependencyProperty ShowRailProperty = DependencyProperty.Register(nameof(ShowRail), typeof(bool), typeof(BranchDecor), Render(true));
    public static readonly DependencyProperty TideHeightProperty = DependencyProperty.Register(nameof(TideHeight), typeof(double), typeof(BranchDecor), Render(44.0));
    public static readonly DependencyProperty RulerProperty = DependencyProperty.Register(nameof(Ruler), typeof(bool), typeof(BranchDecor), Render(false));
    public static readonly DependencyProperty RailMarksProperty = DependencyProperty.Register(nameof(RailMarks), typeof(string[]), typeof(BranchDecor), Render(null));
    public double HeaderBottom { get => (double)GetValue(HeaderBottomProperty); set => SetValue(HeaderBottomProperty, value); }
    public string Watermark { get => (string)GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }
    public double WatermarkSize { get => (double)GetValue(WatermarkSizeProperty); set => SetValue(WatermarkSizeProperty, value); }
    public double EmblemSize { get => (double)GetValue(EmblemSizeProperty); set => SetValue(EmblemSizeProperty, value); }
    public bool ShowRail { get => (bool)GetValue(ShowRailProperty); set => SetValue(ShowRailProperty, value); }
    public double TideHeight { get => (double)GetValue(TideHeightProperty); set => SetValue(TideHeightProperty, value); }
    public bool Ruler { get => (bool)GetValue(RulerProperty); set => SetValue(RulerProperty, value); }
    public string[]? RailMarks { get => (string[]?)GetValue(RailMarksProperty); set => SetValue(RailMarksProperty, value); }
    public const double RailWidth = 24;
    public BranchDecor() { IsHitTestVisible = false; Focusable = false; ClipToBounds = true; }

    static Color Mist(double a) => Color.FromArgb((byte)(255 * a), 0x4F, 0x6B, 0x8A);
    static Brush S(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 60 || h < 60) return;
        double rail = ShowRail ? RailWidth : 0, hb = Math.Min(HeaderBottom, h);
        // 页眉档案底纹（网格 + 扫描线），向下淡出
        var fade = new LinearGradientBrush(Color.FromArgb(255, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90); fade.Freeze();
        dc.PushOpacityMask(fade); dc.DrawRectangle(ArchivePaper.Tiles, null, new Rect(0, 0, w - rail, hb)); dc.Pop();
        // 黄铜轨道弧（页眉右半）
        var orbit = new StreamGeometry();
        using (var ctx = orbit.Open())
        {
            ctx.BeginFigure(new Point(w * .30, hb * .92), false, false);
            ctx.BezierTo(new Point(w * .55, hb * .25), new Point(w * .82, hb * .05), new Point(w + 20, hb * .18), true, false);
        }
        orbit.Freeze();
        dc.DrawGeometry(null, new Pen(S(Color.FromArgb(128, 0xB0, 0x8A, 0x3E)), 1) { DashStyle = new DashStyle(new[] { 5.0, 3.0 }, 0) }, orbit);
        dc.DrawEllipse(S(Color.FromArgb(204, 0xB0, 0x8A, 0x3E)), null, new Point(w * .74, hb * .13), 2.4, 2.4);
        if (!string.IsNullOrEmpty(Watermark)) ArchivePaper.DrawWatermark(dc, w - rail - 8, -6, Watermark, WatermarkSize, .06, .16, this);
        if (EmblemSize > 0)
        {
            dc.PushOpacity(.85);
            ArchiveEmblem.Draw(dc, w - rail - EmblemSize * .72, -EmblemSize * .30, EmblemSize);
            dc.Pop();
        }
        if (TideHeight > 0)
        {
            dc.PushOpacity(.8); ArchivePaper.DrawTide(dc, w - rail, h, TideHeight); dc.Pop();
        }
        if (Ruler)
        {
            var tick = S(Mist(.5));
            double y = h - TideHeight - 1;
            dc.DrawRectangle(tick, null, new Rect(16, y, w - rail - 32, 1));
            for (int i = 0; 16 + i * 8 < w - rail - 16; i++) dc.DrawRectangle(tick, null, new Rect(16 + i * 8, y - (i % 5 == 0 ? 6 : 3), 1, i % 5 == 0 ? 6 : 3));
        }
        if (ShowRail) DrawRail(dc, w, h);
        ArchivePaper.Corner(dc, new Point(3, 3), 14, 3, Color.FromRgb(0xA3, 0x28, 0x3A), 1, 1);
        ArchivePaper.Corner(dc, new Point(w - 3, h - 3), 14, 3, Color.FromRgb(0xB0, 0x8A, 0x3E), -1, -1);
    }
    void DrawRail(DrawingContext dc, double w, double h)
    {
        double x = w - RailWidth, top = 14, bottom = h - 10;
        var bg = new LinearGradientBrush(Color.FromArgb(18, 0x2A, 0x31, 0x39), Color.FromArgb(5, 0x2A, 0x31, 0x39), 90); bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(x, top, RailWidth, bottom - top));
        dc.DrawRectangle(S(Mist(.45)), null, new Rect(x, top, 1, bottom - top));
        var tick = S(Mist(.55));
        for (int i = 0; top + i * 10 < bottom; i++) { double tw = i % 5 == 0 ? 10 : 5; dc.DrawRectangle(tick, null, new Rect(w - 3 - tw, top + i * 10, tw, 1)); }
        var marks = RailMarks;
        double markEnd = top + 30;
        if (marks is { Length: > 0 })
        {
            var ink = S(Mist(.85));
            for (int i = 0; i < marks.Length; i++)
            {
                var ft = Hud.Text(this, marks[i], "NumberFont", 8, ink);
                double y = top + 36 + i * 22;
                dc.DrawText(ft, new Point(w - 3 - ft.Width, y - ft.Height));
                markEnd = y + 6;
            }
            dc.DrawRectangle(Hud.Brush("Accent", "#A3283A"), null, new Rect(x, top + 36 + (marks.Length - 1) * 22, RailWidth, 2));
        }
        var label = Hud.Text(this, ArchivePaper.Spaced("ROUTE // BRANCH · GRAY RAVEN"), "NumberFont", 8, S(Mist(.75)));
        double lx = w - RailWidth / 2 - label.Height / 2 + label.Height / 2, ly = Math.Max(markEnd + 12, bottom - 40 - label.Width);
        if (ly + label.Width < bottom - 4)
        {
            dc.PushTransform(new RotateTransform(90, lx, ly)); dc.DrawText(label, new Point(lx, ly - label.Height / 2)); dc.Pop();
        }
    }
}
