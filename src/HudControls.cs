using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
namespace PgrVoice;

// 冷灰白主题的绘制原语：全部用几何绘制，不含任何游戏素材；不使用 Effect，也没有循环动画。
internal static class Hud
{
    /// <summary>减少动画：关闭封面淡入等过渡（偏好「减少动画」或系统关闭客户区动画时）。</summary>
    public static bool ReduceMotion { get; set; }
    public static event Action? MotionChanged;
    public static void SetReduceMotion(bool value)
    {
        bool next = value || !SystemParameters.ClientAreaAnimation;
        if (next == ReduceMotion) return;
        ReduceMotion = next; MotionChanged?.Invoke();
    }
    public static Brush Brush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); b.Freeze(); return b;
    }
    public static SolidColorBrush Solid(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static Pen Pen(Brush brush, double thickness, double opacity = 1)
    {
        var b = opacity >= 1 ? brush : Fade(brush, opacity);
        var p = new Pen(b, thickness) { LineJoin = PenLineJoin.Miter }; p.Freeze(); return p;
    }
    public static Brush Fade(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush s) { var c = s.Color; var f = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B)); f.Freeze(); return f; }
        var clone = brush.CloneCurrentValue(); clone.Opacity *= opacity; clone.Freeze(); return clone;
    }
    public static StreamGeometry Chamfer(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Top), true, false); if (tr > 0) ctx.LineTo(new Point(r.Right, r.Top + tr), true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), true, false); if (br > 0) ctx.LineTo(new Point(r.Right - br, r.Bottom), true, false);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom), true, false); if (bl > 0) ctx.LineTo(new Point(r.Left, r.Bottom - bl), true, false);
            ctx.LineTo(new Point(r.Left, r.Top + tl), true, false);
        }
        g.Freeze(); return g;
    }
    public static FormattedText Text(Visual visual, string text, string fontKey, double size, Brush brush, FontWeight? weight = null)
    {
        var family = Application.Current?.TryFindResource(fontKey) as FontFamily ?? new FontFamily("Segoe UI");
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.SemiBold, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
    }
}

// 线性图标（12×12 网格，描边绘制）。
public static class Icons
{
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    public static readonly Geometry Previous = G("M10,2 L4,6 L10,10 M2.5,2 V10");
    public static readonly Geometry Replay = G("M2.6,7 A3.6,3.6 0 1 0 3.6,3.2 M3.6,0.8 V3.4 H1");
    public static readonly Geometry Next = G("M2,2 L8,6 L2,10 M9.5,2 V10");
    public static readonly Geometry Pause = G("M4,2 V10 M8,2 V10");
    public static readonly Geometry Original = G("M1.5,4.5 H4 L7,2 V10 L4,7.5 H1.5 Z M9.2,4 Q10.8,6 9.2,8");
    public static readonly Geometry People = G("M1.5,10.5 Q1.5,7 4.5,7 Q7.5,7 7.5,10.5 M3,4.2 a1.5,1.5 0 1 0 3,0 a1.5,1.5 0 1 0 -3,0 M8.5,4 L10.5,6 L8.5,8");
    public static readonly Geometry List = G("M4,3 H10.5 M4,6 H10.5 M4,9 H8 M1.5,3 H2 M1.5,6 H2 M1.5,9 H2");
    public static readonly Geometry History = G("M2.2,6 A3.8,3.8 0 1 0 3.3,3.3 M6,4 V6.2 L7.8,7.3 M1.2,1.8 L3.3,3.3 L1.7,5");
    public static readonly Geometry Undo = G("M4,2.5 L1.5,5 L4,7.5 M1.5,5 H7.8 A2.6,2.6 0 0 1 7.8,10.2 H5");
    public static readonly Geometry Bookmark = G("M3,1.5 H9 V10.5 L6,8 L3,10.5 Z");
    public static readonly Geometry Locate = G("M6,0.8 V3 M6,9 V11.2 M0.8,6 H3 M9,6 H11.2 M4.4,6 a1.6,1.6 0 1 0 3.2,0 a1.6,1.6 0 1 0 -3.2,0");
    public static readonly Geometry Book = G("M1.5,2.5 H5.5 Q6,2.5 6,3.5 V10.5 Q6,9.5 5.5,9.5 H1.5 Z M10.5,2.5 H6.5 Q6,2.5 6,3.5 M6,10.5 Q6,9.5 6.5,9.5 H10.5 V2.5");
    public static readonly Geometry Feedback = G("M1.5,2 H10.5 V8 H6 L3.5,10.5 V8 H1.5 Z");
    public static readonly Geometry Search = G("M1.5,5 a3.5,3.5 0 1 0 7,0 a3.5,3.5 0 1 0 -7,0 M7.6,7.6 L10.5,10.5");
}

/// <summary>模板用附加属性：图标、状态文字与色调（teal / red / off）。</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.RegisterAttached("StateText", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetStateText(DependencyObject d) => (string)d.GetValue(StateTextProperty);
    public static void SetStateText(DependencyObject d, string value) => d.SetValue(StateTextProperty, value);
    /// <summary>节点按钮名称牌的英文副标（PREV / REPLAY / NEXT）。</summary>
    public static readonly DependencyProperty SublabelProperty = DependencyProperty.RegisterAttached("Sublabel", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetSublabel(DependencyObject d) => (string)d.GetValue(SublabelProperty);
    public static void SetSublabel(DependencyObject d, string value) => d.SetValue(SublabelProperty, value);
    /// <summary>节点名称牌密度：full（中文 + 英文）/ dense（只中文）/ none（只留菱形，名称见提示）。由 PlaybackDeck 按可用宽度设置。</summary>
    public static readonly DependencyProperty CaptionModeProperty = DependencyProperty.RegisterAttached("CaptionMode", typeof(string), typeof(Ui), new FrameworkPropertyMetadata("full"));
    public static string GetCaptionMode(DependencyObject d) => (string)d.GetValue(CaptionModeProperty);
    public static void SetCaptionMode(DependencyObject d, string value) => d.SetValue(CaptionModeProperty, value);
    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached("Tone", typeof(string), typeof(Ui), new FrameworkPropertyMetadata("off"));
    public static string GetTone(DependencyObject d) => (string)d.GetValue(ToneProperty);
    public static void SetTone(DependencyObject d, string value) => d.SetValue(ToneProperty, value);
}

/// <summary>工具条芯片：把「历史 F2」拆成文字与按键角标；没有按键时角标为空。</summary>
public sealed class ChipPartConverter : IValueConverter
{
    static readonly Regex Pattern = new(@"^(?<label>.*\S)\s+(?<key>F\d{1,2}|(?:Ctrl|Alt|Shift|Win)\+\S+|Esc|Enter|Tab|Space|[A-Z0-9]{1,3})$");
    public static (string Label, string Key) Split(string? text)
    {
        string value = (text ?? "").Trim();
        var m = Pattern.Match(value);
        return m.Success ? (m.Groups["label"].Value, m.Groups["key"].Value) : (value, "");
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (label, key) = Split(value as string ?? value?.ToString());
        return parameter as string == "key" ? key : label;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
/// <summary>按钮显示用：把用于对齐的长串空格压缩为两个空格（原文 Content 不变）。</summary>
public sealed class CompactTextConverter : IValueConverter
{
    static readonly Regex Spaces = new(@"\s{2,}");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Spaces.Replace((value?.ToString() ?? "").Trim(), "  ");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class OddConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i && i % 2 == 1;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
/// <summary>说话人色条：同名稳定映射到点缀色之一；旁白与空名为中性灰。</summary>
public sealed class SpeakerToneConverter : IValueConverter
{
    static readonly Brush[] Tones = { Hud.Solid("#A3283A"), Hud.Solid("#4F6B8A"), Hud.Solid("#3E7C7A"), Hud.Solid("#B08A3E"), Hud.Solid("#6F7C8A"), Hud.Solid("#8A5A4F") };
    static readonly Brush Neutral = Hud.Solid("#B9C0C8");
    public static Brush For(string? speaker)
    {
        string name = CharacterArt.SanitizeName(speaker);
        if (name.Length == 0 || name.StartsWith("旁白", StringComparison.Ordinal)) return Neutral;
        uint hash = 2166136261; foreach (char c in name) { hash ^= c; hash *= 16777619; }
        return Tones[hash % (uint)Tones.Length];
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>头像图片来源：由主窗口提供（只返回已缓存图片，未缓存时在后台加载，完成后通知刷新）。</summary>
internal static class PortraitImages
{
    public static Func<string, ImageSource?>? Resolver { get; set; }
    public static bool Enabled { get; set; } = true;
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();
}

/// <summary>说话人头像：左上 / 右下切角框 + 本地图片或字标；播放中为暗红细框（一层淡红描边）；Auto 时自行按说话人取图。</summary>
public sealed class SpeakerPortrait : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty SpeakerProperty = DependencyProperty.Register(nameof(Speaker), typeof(string), typeof(SpeakerPortrait), Render(""));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(SpeakerPortrait), Render(null));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(SpeakerPortrait), Render(false));
    public static readonly DependencyProperty ShowSerialProperty = DependencyProperty.Register(nameof(ShowSerial), typeof(bool), typeof(SpeakerPortrait), Render(true));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SpeakerPortrait), Render(null));
    public static readonly DependencyProperty AutoProperty = DependencyProperty.Register(nameof(Auto), typeof(bool), typeof(SpeakerPortrait), Render(false));
    public string Speaker { get => (string)GetValue(SpeakerProperty); set => SetValue(SpeakerProperty, value); }
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    /// <summary>保留以兼容旧代码；冷灰白主题不再显示编号。</summary>
    public bool ShowSerial { get => (bool)GetValue(ShowSerialProperty); set => SetValue(ShowSerialProperty, value); }
    /// <summary>非空时覆盖描边色（例如分支黄铜、小队高亮）。</summary>
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    /// <summary>为真且未设置 Source 时，通过 PortraitImages 自行查找头像（列表行、小队条等）。</summary>
    public bool Auto { get => (bool)GetValue(AutoProperty); set => SetValue(AutoProperty, value); }

    public SpeakerPortrait()
    {
        IsHitTestVisible = false; SnapsToDevicePixels = true;
        Loaded += (_, _) => { if (Auto) PortraitImages.Changed += Refresh; };
        Unloaded += (_, _) => PortraitImages.Changed -= Refresh;
    }
    void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 8 || h < 8) return;
        double c = Math.Round(Math.Min(w, h) * 0.18);
        var rect = new Rect(0, 0, w, h);
        var shape = Hud.Chamfer(rect, c, 0, c, 0);
        var plate = Hud.Brush("Plate", "#2A3139");
        dc.DrawGeometry(Hud.Brush("SlateFill", "#E3E9F0"), null, shape);
        var image = Source ?? (Auto && PortraitImages.Enabled ? PortraitImages.Resolver?.Invoke(Speaker) : null);
        dc.PushClip(shape);
        if (image is { Width: > 0, Height: > 0 })
        {
            // 头像多为上半身：按高宽填满，水平居中、顶部对齐。
            double scale = Math.Max(w / image.Width, h / image.Height), iw = image.Width * scale, ih = image.Height * scale;
            dc.DrawImage(image, new Rect((w - iw) / 2, 0, iw, ih));
        }
        else
        {
            if (Application.Current?.TryFindResource("StripeTexture") is Brush stripes) dc.DrawRectangle(stripes, null, rect);
            var glyph = Hud.Text(this, CharacterArt.Monogram(Speaker), "UiFont", Math.Max(9, h * 0.46), plate);
            dc.DrawText(glyph, new Point((w - glyph.Width) / 2, (h - glyph.Height) / 2));
        }
        dc.Pop();
        var accent = Hud.Brush("Accent", "#A3283A");
        var stroke = Accent ?? (IsPlaying ? accent : Hud.Brush("StrokeStrong", "#A6AFB9"));
        if (IsPlaying) dc.DrawGeometry(null, Hud.Pen(accent, 4, 0.16), shape);
        dc.DrawGeometry(null, Hud.Pen(stroke, IsPlaying || Accent != null ? 1.5 : 1), Hud.Chamfer(new Rect(0.5, 0.5, w - 1, h - 1), c, 0, c, 0));
        // 右上角暗红三角标记（PGR 名牌角标）
        if (w >= 28)
        {
            double t = Math.Min(7, w / 6); var mark = new StreamGeometry();
            using (var ctx = mark.Open()) { ctx.BeginFigure(new Point(w - t, 0), true, true); ctx.LineTo(new Point(w, 0), true, false); ctx.LineTo(new Point(w, t), true, false); }
            mark.Freeze(); dc.DrawGeometry(IsPlaying || Accent == null ? accent : Accent, null, mark);
        }
    }
}

/// <summary>原创徽记（几何，不复制任何官方标志）：六边框 + 暗红羽翼三角 + 雾蓝菱形。</summary>
public sealed class Emblem : FrameworkElement
{
    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(nameof(FrameBrush), typeof(Brush), typeof(Emblem), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>六边框颜色；为空时用名牌深色（深色底上可设为浅色）。</summary>
    public Brush? FrameBrush { get => (Brush?)GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }
    public Emblem() { IsHitTestVisible = false; Width = 16; Height = 16; }
    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(RenderSize.Width, RenderSize.Height); if (s < 6) return;
        double cx = RenderSize.Width / 2, cy = RenderSize.Height / 2, r = s / 2 - 0.75;
        var hex = new StreamGeometry();
        using (var ctx = hex.Open())
        {
            for (int i = 0; i < 6; i++)
            {
                double a = Math.PI / 3 * i - Math.PI / 2; var p = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                if (i == 0) ctx.BeginFigure(p, false, true); else ctx.LineTo(p, true, false);
            }
        }
        hex.Freeze();
        dc.DrawGeometry(null, Hud.Pen(FrameBrush ?? Hud.Brush("Plate", "#2A3139"), 1, 0.9), hex);
        var wing = new StreamGeometry();
        using (var ctx = wing.Open())
        {
            ctx.BeginFigure(new Point(cx - r * 0.62, cy - r * 0.28), true, true);
            ctx.LineTo(new Point(cx + r * 0.62, cy - r * 0.28), true, false);
            ctx.LineTo(new Point(cx, cy + r * 0.62), true, false);
            ctx.LineTo(new Point(cx, cy + r * 0.12), true, false);
        }
        wing.Freeze(); dc.DrawGeometry(Hud.Brush("Accent", "#A3283A"), null, wing);
        double d = r * 0.2; var diamond = new StreamGeometry();
        using (var ctx = diamond.Open()) { ctx.BeginFigure(new Point(cx, cy - r * 0.62 - d), true, true); ctx.LineTo(new Point(cx + d, cy - r * 0.62), true, false); ctx.LineTo(new Point(cx, cy - r * 0.62 + d), true, false); ctx.LineTo(new Point(cx - d, cy - r * 0.62), true, false); }
        diamond.Freeze(); dc.DrawGeometry(Hud.Brush("Slate", "#4F6B8A"), null, diamond);
    }
}

/// <summary>大号描边文字（章节编号“01”）：只描边不填充；几何按文字与尺寸缓存。</summary>
public sealed class OutlinedText : FrameworkElement
{
    static FrameworkPropertyMetadata Layout(object value) => new(value, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText), Layout(""));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedText), Layout(72.0));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlinedText), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedText), new FrameworkPropertyMetadata(1.2, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(OutlinedText), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>可选填充（默认只描边）。</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public OutlinedText() { IsHitTestVisible = false; }
    FormattedText? Format() => string.IsNullOrEmpty(Text) ? null : Hud.Text(this, Text, "NumberFont", FontSize, Brushes.Black, FontWeights.Bold);
    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format(); if (text == null) return new Size(0, 0);
        return new Size(text.WidthIncludingTrailingWhitespace + StrokeThickness * 2, text.Height * 0.86);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var text = Format(); if (text == null) return;
        var geometry = text.BuildGeometry(new Point(StrokeThickness, -text.Height * 0.07));
        dc.DrawGeometry(Fill, Hud.Pen(Stroke ?? Hud.Brush("Plate", "#2A3139"), StrokeThickness), geometry);
    }
}

/// <summary>帕尼希亚病毒侵蚀角标：一个角上的少量暗红碎片（静态几何，只用一处）。</summary>
public sealed class CorruptionMark : FrameworkElement
{
    public CorruptionMark() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 8 || h < 8) return;
        var dark = Hud.Solid("#8C7A1E2C"); var mid = Hud.Solid("#59A3283A"); var light = Hud.Solid("#33A3283A");
        var random = new Random(23);
        // 从左上角向内的锯齿碎裂块
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true, true);
            for (double x = 0; x <= w; x += 6 + random.NextDouble() * 9)
                ctx.LineTo(new Point(x, Math.Max(0, h * 0.45 * (1 - x / w) + random.NextDouble() * h * 0.18 * (1 - x / w))), true, false);
            ctx.LineTo(new Point(w, 0), true, false);
        }
        body.Freeze(); dc.DrawGeometry(light, null, body);
        var core = new StreamGeometry();
        using (var ctx = core.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true, true);
            for (double y = 0; y <= h; y += 5 + random.NextDouble() * 8)
                ctx.LineTo(new Point(Math.Max(0, w * 0.22 * (1 - y / h) + random.NextDouble() * w * 0.08 * (1 - y / h)), y), true, false);
            ctx.LineTo(new Point(0, h), true, false);
        }
        core.Freeze(); dc.DrawGeometry(mid, null, core);
        for (int i = 0; i < 14; i++)
        {
            double x = random.NextDouble() * w * 0.7, y = random.NextDouble() * h * 0.7;
            if (x / w + y / h > 0.75) continue;
            double s = random.Next(2, 5);
            dc.DrawRectangle(i % 3 == 0 ? dark : mid, null, new Rect(Math.Round(x), Math.Round(y), s, s));
        }
    }
}

/// <summary>
/// 播放节点排布：仍是 UniformGrid（按标签页显隐的代码按类型查找它），但按“节点轨道”排布：
/// 前两个子元素在中央确认铭牌左侧，第三个在右侧（与铭牌同一行，铭牌由上一行用负边距叠放），其余为下一行左对齐的次级开关；
/// 次级开关右侧的空位由音量行占用（SecondRowChanged 通知主窗口对齐）。
/// </summary>
public sealed class PlaybackDeck : UniformGrid
{
    public static readonly DependencyProperty TrackHeightProperty = DependencyProperty.Register(nameof(TrackHeight), typeof(double), typeof(PlaybackDeck), new FrameworkPropertyMetadata(66.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty CenterGapProperty = DependencyProperty.Register(nameof(CenterGap), typeof(double), typeof(PlaybackDeck), new FrameworkPropertyMetadata(224.0, FrameworkPropertyMetadataOptions.AffectsArrange));
    public double TrackHeight { get => (double)GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }
    public double CenterGap { get => (double)GetValue(CenterGapProperty); set => SetValue(CenterGapProperty, value); }
    /// <summary>第二行（次级开关）占用的宽度与高度；变化时触发 SecondRowChanged（在布局完成后异步通知）。</summary>
    public double SecondRowWidth { get; private set; }
    public double SecondRowHeight { get; private set; }
    public event Action? SecondRowChanged;
    const double RowGap = 4, ToggleGap = 8;
    protected override Size MeasureOverride(Size constraint)
    {
        double row2 = 0, width = 0;
        var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i]; child.Measure(any);
            if (i >= 3) { row2 = Math.Max(row2, child.DesiredSize.Height); width += child.DesiredSize.Width + ToggleGap; }
        }
        double w = double.IsInfinity(constraint.Width) ? Math.Max(width, 420) : constraint.Width;
        return new Size(w, TrackHeight + (row2 > 0 ? RowGap + row2 : 0));
    }
    protected override Size ArrangeOverride(Size size)
    {
        double w = size.Width, gap = Math.Min(CenterGap, w * 0.5), side = Math.Max(0, (w - gap) / 2), track = TrackHeight;
        var kids = InternalChildren;
        // 名称牌密度只由宽度决定（不看名称牌自身尺寸，避免布局来回）：左侧两节点间距放得下「配音上一句 PREV」+「重播 REPLAY」才显示英文，
        // 放得下两块中文牌才显示中文，否则只留菱形（与紧凑高度一致，名称见提示）。名称牌之间、与确认铭牌都不重叠。
        // 名称牌整块在铭牌下沿之下，只需彼此不碰：左侧两节点放在 0.22 / 0.82·side（间距 0.6·side）。
        double spacing = side * 0.6;
        string mode = spacing >= 98 ? "full" : spacing >= 62 ? "dense" : "none";
        for (int i = 0; i < Math.Min(3, kids.Count); i++) if (Ui.GetCaptionMode(kids[i]) != mode) Ui.SetCaptionMode(kids[i], mode);
        void Place(UIElement e, double center, double top, double height)
        {
            // 不再把节点压窄到 side 以下（会裁掉名称牌）；居中于节点、夹在面板内。
            double cw = Math.Min(Math.Max(e.DesiredSize.Width, 64), Math.Max(64, w));
            e.Arrange(new Rect(Math.Max(0, Math.Min(w - cw, center - cw / 2)), top, cw, height));
        }
        if (kids.Count > 0) Place(kids[0], side * 0.22, 0, track);
        if (kids.Count > 1) Place(kids[1], side * 0.82, 0, track);
        if (kids.Count > 2) Place(kids[2], w - side * 0.5, 0, track);
        double x = 0, row2 = 0;
        for (int i = 3; i < kids.Count; i++) if (kids[i].Visibility != Visibility.Collapsed) row2 = Math.Max(row2, kids[i].DesiredSize.Height);
        for (int i = 3; i < kids.Count; i++)
        {
            if (kids[i].Visibility == Visibility.Collapsed) { kids[i].Arrange(new Rect()); continue; }
            kids[i].Arrange(new Rect(x, track + RowGap, kids[i].DesiredSize.Width, row2)); x += kids[i].DesiredSize.Width + ToggleGap;
        }
        double used = Math.Max(0, x - ToggleGap);
        if (Math.Abs(used - SecondRowWidth) > 0.5 || Math.Abs(row2 - SecondRowHeight) > 0.5)
        {
            SecondRowWidth = used; SecondRowHeight = row2;
            Dispatcher.BeginInvoke(() => SecondRowChanged?.Invoke());
        }
        return size;
    }
}

/// <summary>选区角标：四个暗红 L 形细角（替代旧的青色瞄准框）；Cross 为真时中心加一个小菱形点，标出点击位置。</summary>
public sealed class CornerMarks : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(CornerMarks), Render(null));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CornerMarks), Render(1.5));
    public static readonly DependencyProperty LengthProperty = DependencyProperty.Register(nameof(Length), typeof(double), typeof(CornerMarks), Render(8.0));
    public static readonly DependencyProperty CrossProperty = DependencyProperty.Register(nameof(Cross), typeof(bool), typeof(CornerMarks), Render(false));
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Length { get => (double)GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public bool Cross { get => (bool)GetValue(CrossProperty); set => SetValue(CrossProperty, value); }
    public CornerMarks() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height, t = StrokeThickness, l = Math.Min(Length, Math.Min(w, h) / 2.5);
        if (w < 4 || h < 4) return;
        var brush = Stroke ?? Hud.Brush("Accent", "#A3283A"); var pen = Hud.Pen(brush, t); double o = t / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            void L(Point a, Point b, Point c) { ctx.BeginFigure(a, false, false); ctx.LineTo(b, true, false); ctx.LineTo(c, true, false); }
            L(new Point(o, l), new Point(o, o), new Point(l, o));
            L(new Point(w - l, o), new Point(w - o, o), new Point(w - o, l));
            L(new Point(w - o, h - l), new Point(w - o, h - o), new Point(w - l, h - o));
            L(new Point(l, h - o), new Point(o, h - o), new Point(o, h - l));
        }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
        if (!Cross) return;
        double cx = w / 2, cy = h / 2, d = Math.Max(2.5, t * 2);
        var dot = new StreamGeometry();
        using (var ctx = dot.Open()) { ctx.BeginFigure(new Point(cx, cy - d), true, true); ctx.LineTo(new Point(cx + d, cy), true, false); ctx.LineTo(new Point(cx, cy + d), true, false); ctx.LineTo(new Point(cx - d, cy), true, false); }
        dot.Freeze(); dc.DrawGeometry(brush, null, dot);
    }
}
