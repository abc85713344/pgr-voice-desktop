using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
namespace PgrVoice;

// 灰鸦冷灰白设计系统：颜色、字体、切角几何集中在 Themes/Tactical.xaml，这里只提供代码侧入口与绘制原语。
internal static class Theme
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    // 中文主标题配英文小标签；只用于装饰，不改变控件 Header / Content 的原文。
    static readonly (string Prefix, string English)[] Sublabels =
    {
        ("游戏配音", "GAME VOICE"), ("台词", "LINES"), ("听书", "LISTENING"), ("辅助定位与诊断", "DIAGNOSTICS"), ("辅助定位", "LOCATE"),
        ("设置", "SETTINGS"), ("历史", "HISTORY"), ("手柄", "GAMEPAD"),
        ("游戏与配音包", "GAME & PACKS"), ("角色与封面", "CHARACTERS & COVERS"), ("角色头像与立绘", "CHARACTER ART"), ("章节封面", "CHAPTER COVERS"), ("图片文件夹", "IMAGE FOLDERS"), ("动画", "MOTION"), ("按键与点击位置", "INPUT"), ("外观与声音", "DISPLAY & AUDIO"),
        ("收听设置与书签", "OPTIONS & BOOKMARKS"), ("查看识别到的文字", "OCR TEXT"), ("手动连接", "MANUAL LINK"),
        ("3D 营地", "3D SCENES"), ("连接说明", "HOW IT WORKS"), ("可选：检查对白画面", "DIALOGUE CHECK"),
        ("选择章节", "CHAPTER SELECT"), ("选择小节", "SECTION SELECT"), ("自定义按键", "KEY BINDING"),
        ("选取对白正文", "DIALOGUE REGION"), ("反馈这一句", "LINE FEEDBACK"), ("角色音量", "VOICE MIX"),
        ("搜索全部配音章节", "LIBRARY SEARCH"), ("内置剧情文本", "STORY ARCHIVE"), ("三步上手", "QUICK START"),
        ("剧情配音", "STORY VOICE"), ("战双配音", "STORY VOICE"),
        ("文字跟随", "TEXT"), ("键盘 / 手柄", "KEY / PAD"), ("点按跟随", "TAP"), ("顺序播放", "SEQUENCE"),
    };
    public static string English(string? chinese)
    {
        if (string.IsNullOrEmpty(chinese)) return "";
        foreach (var (prefix, english) in Sublabels)
            if (chinese.StartsWith(prefix, StringComparison.Ordinal)) return english;
        return "";
    }

    // 普通对话框：浅色系统标题栏 + 冷灰白外框模板。内容对象保持原样，测试与业务代码仍直接访问 Content。
    static System.Windows.Media.ImageSource? appIcon;
    /// <summary>应用图标「逆潮金鸦」（r6-2b，多尺寸 ICO），所有窗口标题栏 / 任务栏共用。</summary>
    public static System.Windows.Media.ImageSource AppIcon => appIcon ??= System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/PgrVoice.ico"));

    public static void ApplyDialog(Window window)
    {
        window.Icon ??= AppIcon;
        window.Style = Style("PgrDialogWindow");
        window.SourceInitialized += (_, _) => LightChrome(window);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    // 系统标题栏与内容同色：关闭深色模式，Win11 下标题栏为冷灰白、边框为细线色。
    public static void LightChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == IntPtr.Zero) return;
            int off = 0; DwmSetWindowAttribute(hwnd, 20, ref off, sizeof(int));
            int caption = ColorRef("HeaderColor"), border = ColorRef("StrokeColor"), text = ColorRef("FgColor");
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // Win11 标题栏底色
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        }
        catch (Exception) { /* 旧系统没有这些属性时保持系统外框 */ }
    }
    public static void DarkChrome(Window window) => LightChrome(window);
    static int ColorRef(string key)
    {
        var c = (Color)Application.Current.FindResource(key);
        return c.R | c.G << 8 | c.B << 16;
    }

    // 区块标题（PGR 区头）：暗红三角 + 中文 + 英文压缩小标签 + 细分隔线（起首一段暗红）。
    public static FrameworkElement Heading(string text, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new());
        var marker = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Brush("Accent"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, Style = Style("HudHeadingText") };
        var english = new TextBlock { Text = English(text), Style = Style("SublabelText"), FontSize = 9.5, Margin = new Thickness(8, 1, 0, 0) };
        var rule = new Border { Height = 1, Background = Brush("Stroke"), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(14, 0, 0, 0), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); Grid.SetColumn(english, 2); Grid.SetColumn(rule, 3);
        grid.Children.Add(marker); grid.Children.Add(label); grid.Children.Add(english); grid.Children.Add(rule);
        return grid;
    }

    // 对话框正文的大标题：徽记 + 暗红短条 + 英文压缩小标签，下为中文标题。
    public static FrameworkElement Title(string chinese, string english, Thickness margin, double size = 20)
    {
        var stack = new StackPanel { Margin = margin };
        var eyebrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        eyebrow.Children.Add(new Emblem { Width = 12, Height = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        eyebrow.Children.Add(new Border { Width = 14, Height = 3, Background = Brush("Accent"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        eyebrow.Children.Add(new TextBlock { Text = english, Style = Style("SublabelText"), Foreground = Brush("NormalAccent") });
        stack.Children.Add(eyebrow);
        stack.Children.Add(new TextBlock { Text = chinese, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        return stack;
    }
}

[Flags] public enum CutCorners { None = 0, TopLeft = 1, TopRight = 2, BottomRight = 4, BottomLeft = 8, Diagonal = TopLeft | BottomRight, All = 15 }

// 切角面板：PGR 风格的斜切边框。保持 Decorator 语义（Child / Background / BorderBrush / Padding），
// 因此原来对 Border 设置背景、描边或读取 Child 的代码可以不变。
public sealed class CutFrame : System.Windows.Controls.Decorator
{
    static FrameworkPropertyMetadata Render(object value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(nameof(OutlineBrush), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CutFrame), Render(1.0));
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(nameof(Cut), typeof(double), typeof(CutFrame), Render(8.0));
    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(nameof(Corners), typeof(CutCorners), typeof(CutFrame), Render(CutCorners.Diagonal));
    public static readonly DependencyProperty EdgeWidthProperty = DependencyProperty.Register(nameof(EdgeWidth), typeof(double), typeof(CutFrame), Render(0.0));
    public static readonly DependencyProperty BracketBrushProperty = DependencyProperty.Register(nameof(BracketBrush), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty BracketLengthProperty = DependencyProperty.Register(nameof(BracketLength), typeof(double), typeof(CutFrame), Render(0.0));
    public static readonly DependencyProperty CornerMarkProperty = DependencyProperty.Register(nameof(CornerMark), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty GlowBrushProperty = DependencyProperty.Register(nameof(GlowBrush), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty NotchBrushProperty = DependencyProperty.Register(nameof(NotchBrush), typeof(Brush), typeof(CutFrame), Render(null!));
    public static readonly DependencyProperty NotchLengthProperty = DependencyProperty.Register(nameof(NotchLength), typeof(double), typeof(CutFrame), Render(0.0));
    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(nameof(ClipContent), typeof(bool), typeof(CutFrame), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(CutFrame),
        new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    /// <summary>无侧边条时作外框颜色；EdgeWidth &gt; 0 时作左侧状态条颜色（与原来 2px 左边框的语义一致）。</summary>
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    /// <summary>EdgeWidth &gt; 0 时的外框颜色。</summary>
    public Brush? OutlineBrush { get => (Brush?)GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public CutCorners Corners { get => (CutCorners)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public double EdgeWidth { get => (double)GetValue(EdgeWidthProperty); set => SetValue(EdgeWidthProperty, value); }
    public Brush? BracketBrush { get => (Brush?)GetValue(BracketBrushProperty); set => SetValue(BracketBrushProperty, value); }
    public double BracketLength { get => (double)GetValue(BracketLengthProperty); set => SetValue(BracketLengthProperty, value); }
    public Brush? CornerMark { get => (Brush?)GetValue(CornerMarkProperty); set => SetValue(CornerMarkProperty, value); }
    /// <summary>外框“发光”：用两层半透明宽描边模拟，不使用 Effect（大面板上 Effect 代价高）。</summary>
    public Brush? GlowBrush { get => (Brush?)GetValue(GlowBrushProperty); set => SetValue(GlowBrushProperty, value); }
    /// <summary>上边右侧的梯形缺口标记（PGR 面板的“卡口”）。</summary>
    public Brush? NotchBrush { get => (Brush?)GetValue(NotchBrushProperty); set => SetValue(NotchBrushProperty, value); }
    public double NotchLength { get => (double)GetValue(NotchLengthProperty); set => SetValue(NotchLengthProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding; double w = p.Left + p.Right, h = p.Top + p.Bottom;
        if (Child == null) return new Size(w, h);
        Child.Measure(new Size(Math.Max(0, constraint.Width - w), Math.Max(0, constraint.Height - h)));
        return new Size(Child.DesiredSize.Width + w, Child.DesiredSize.Height + h);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top, Math.Max(0, size.Width - p.Left - p.Right), Math.Max(0, size.Height - p.Top - p.Bottom)));
        Clip = ClipContent ? Outline(new Rect(size), 0) : null;
        return size;
    }

    double EffectiveCut(Size size) => Math.Max(0, Math.Min(Cut, Math.Min(size.Width, size.Height) / 2));
    StreamGeometry Outline(Rect r, double inset)
    {
        double c = Math.Max(0, EffectiveCut(r.Size) - inset * 0.4);
        r = new Rect(r.X + inset, r.Y + inset, Math.Max(0, r.Width - 2 * inset), Math.Max(0, r.Height - 2 * inset));
        var k = Corners; var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool tl = k.HasFlag(CutCorners.TopLeft), tr = k.HasFlag(CutCorners.TopRight), br = k.HasFlag(CutCorners.BottomRight), bl = k.HasFlag(CutCorners.BottomLeft);
            ctx.BeginFigure(new Point(r.Left + (tl ? c : 0), r.Top), true, true);
            ctx.LineTo(new Point(r.Right - (tr ? c : 0), r.Top), true, false);
            if (tr) ctx.LineTo(new Point(r.Right, r.Top + c), true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - (br ? c : 0)), true, false);
            if (br) ctx.LineTo(new Point(r.Right - c, r.Bottom), true, false);
            ctx.LineTo(new Point(r.Left + (bl ? c : 0), r.Bottom), true, false);
            if (bl) ctx.LineTo(new Point(r.Left, r.Bottom - c), true, false);
            ctx.LineTo(new Point(r.Left, r.Top + (tl ? c : 0)), true, false);
        }
        g.Freeze(); return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize; if (size.Width <= 0 || size.Height <= 0) return;
        double t = StrokeThickness, c = EffectiveCut(size);
        var outline = EdgeWidth > 0 ? OutlineBrush : BorderBrush;
        if (Background != null) dc.DrawGeometry(Background, null, Outline(new Rect(size), 0));
        if (CornerMark != null && Cut > 0)
        {
            // 右上角的小三角标记，取代圆角上的“装饰点”。
            double m = Math.Min(10, Math.Min(size.Width, size.Height) / 3);
            var mark = new StreamGeometry();
            using (var ctx = mark.Open()) { ctx.BeginFigure(new Point(size.Width - m, 0), true, true); ctx.LineTo(new Point(size.Width, 0), true, false); ctx.LineTo(new Point(size.Width, m), true, false); }
            mark.Freeze(); dc.DrawGeometry(CornerMark, null, mark);
        }
        if (GlowBrush != null && GlowBrush is not SolidColorBrush { Color.A: 0 })
        {
            var glow = Outline(new Rect(size), t / 2);
            dc.DrawGeometry(null, Hud.Pen(GlowBrush, t + 6, 0.10), glow);
            dc.DrawGeometry(null, Hud.Pen(GlowBrush, t + 3, 0.22), glow);
        }
        if (outline != null && t > 0) dc.DrawGeometry(null, new Pen(outline, t), Outline(new Rect(size), t / 2));
        if (NotchBrush != null && NotchLength > 0 && size.Width > NotchLength + 2 * c + 40)
        {
            double right = size.Width - (Corners.HasFlag(CutCorners.TopRight) ? c : 0) - 24, left = right - NotchLength, d = 4;
            var notch = new StreamGeometry();
            using (var ctx = notch.Open()) { ctx.BeginFigure(new Point(left, 0), true, true); ctx.LineTo(new Point(right, 0), true, false); ctx.LineTo(new Point(right - d, d), true, false); ctx.LineTo(new Point(left + d, d), true, false); }
            notch.Freeze(); dc.DrawGeometry(NotchBrush, null, notch);
        }
        if (EdgeWidth > 0 && BorderBrush != null)
        {
            bool tl = Corners.HasFlag(CutCorners.TopLeft), bl = Corners.HasFlag(CutCorners.BottomLeft);
            double top = tl ? c : 0, bottom = size.Height - (bl ? c : 0);
            if (bottom > top) dc.DrawRectangle(BorderBrush, null, new Rect(0, top, Math.Min(EdgeWidth, size.Width), bottom - top));
        }
        if (BracketBrush != null && BracketLength > 0)
        {
            var pen = new Pen(BracketBrush, 1.5); double l = Math.Min(BracketLength, Math.Min(size.Width, size.Height) / 3), o = 0.75;
            void Corner(Point a, Point corner, Point b) { var f = new StreamGeometry(); using (var ctx = f.Open()) { ctx.BeginFigure(a, false, false); ctx.LineTo(corner, true, false); ctx.LineTo(b, true, false); } f.Freeze(); dc.DrawGeometry(null, pen, f); }
            double w = size.Width, h = size.Height;
            if (!Corners.HasFlag(CutCorners.TopLeft)) Corner(new(o, l), new(o, o), new(l, o));
            if (!Corners.HasFlag(CutCorners.TopRight)) Corner(new(w - l, o), new(w - o, o), new(w - o, l));
            if (!Corners.HasFlag(CutCorners.BottomRight)) Corner(new(w - o, h - l), new(w - o, h - o), new(w - l, h - o));
            if (!Corners.HasFlag(CutCorners.BottomLeft)) Corner(new(l, h - o), new(o, h - o), new(o, h - l));
        }
    }
}

// 选项序号：01、02……（AlternationIndex 从 0 开始）。
public sealed class OrdinalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? (i + 1).ToString(parameter as string ?? "00", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// 中文标题对应的英文小标签；没有对应时返回空串并由模板折叠。
public sealed class SublabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Theme.English(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
