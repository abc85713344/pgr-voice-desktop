using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace PgrVoice;

/// <summary>次级界面共用的小部件（SPEC 01–09 / 10–13）：档案条、斜纹标签、键帽、按键提示、页眉。都是纯展示件，不可聚焦。</summary>
internal static class Archive
{
    static Brush B(string hex) => Hud.Solid(hex);
    public static Brush MistBracket => Hud.Brush("MistBracket", "#6F8FB0");

    public static string CategoryEnglish(string category) => category switch
    {
        "主线" => "MAIN STORY", "外篇剧情" => "SIDE STORY", "间章剧情" => "INTERLUDE", "浮点纪实" => "FLOAT RECORD",
        "本我回廊" => "EGO CORRIDOR", "黄金之涡" => "GOLDEN VORTEX", "空中花园" => "BABYLONIA", "多维演绎" => "MULTIVERSE",
        "时宇漫纪" => "TIME CHRONICLE", "联动" => "COLLAB", "好感剧情" => "COMMUNICATION", "调色板战争" => "PALETTE WAR",
        "边界公约" => "BORDER PACT", _ => "STORY VOICE"
    };

    /// <summary>深色档案条（26px）：暗红斜印章 + 中文 + 英文 + 编号 + 条码 + 右端红黑斜纹。</summary>
    public static FrameworkElement Strip(string stamp, string label, string english, string number = "")
    {
        var grid = new Grid { Height = 26, IsHitTestVisible = false, ClipToBounds = true };
        grid.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x2A, 0x31, 0x39), 0), new GradientStop(Color.FromRgb(0x38, 0x41, 0x4C), .82), new GradientStop(Color.FromArgb(0xE0, 0x38, 0x41, 0x4C), 1),
        }, 0);
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(46) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Stamp(stamp));
        row.Children.Add(new TextBlock { Text = label, Foreground = B("#C3CBD4"), FontSize = 10.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (english.Length > 0) row.Children.Add(new TextBlock { Text = english, Foreground = B("#9AA5B1"), FontSize = 9, FontFamily = Theme.Font("LabelFont"), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (number.Length > 0)
        {
            row.Children.Add(new Border { Width = 1, Height = 12, Background = B("#56606C"), Margin = new Thickness(10, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = number, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Font("NumberFont"), VerticalAlignment = VerticalAlignment.Center });
        }
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 12, Opacity = .7, Margin = new Thickness(12, 0, 0, 0) };
        int[] widths = { 1, 2, 1, 3, 1, 1, 2 };
        for (int i = 0; i < 18; i++) bars.Children.Add(new Border { Width = widths[i % 7], Background = B("#C3CBD4"), Margin = new Thickness(0, 0, 1, 0) });
        row.Children.Add(bars);
        grid.Children.Add(row);
        var hazard = new Border { Background = Hazard(5), Opacity = .95, Clip = Geometry.Parse("M10,0 L46,0 46,26 0,26 Z") };
        Grid.SetColumn(hazard, 1); grid.Children.Add(hazard);
        return grid;
    }
    public static FrameworkElement Stamp(string text) => new Border
    {
        Background = Hud.Brush("Accent", "#A3283A"), Padding = new Thickness(6, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = B("#E9EDF1"), FontSize = 9, FontWeight = FontWeights.Bold, FontFamily = Theme.Font("LabelFont"), LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight },
    }.WithSlant();
    static Border WithSlant(this Border b)
    {
        b.SizeChanged += (_, e) => b.Clip = new PathGeometry(new[] { new PathFigure(new Point(0, 0), new PathSegment[] { new PolyLineSegment(new[] { new Point(e.NewSize.Width, 0), new Point(e.NewSize.Width - 4, e.NewSize.Height), new Point(0, e.NewSize.Height) }, false) }, true) });
        return b;
    }
    /// <summary>红黑 45° 斜纹（档案条尾、标签）。</summary>
    public static Brush Hazard(double band)
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(B("#2A3139"), null, new RectangleGeometry(new Rect(0, 0, band * 2, band * 2))));
        g.Children.Add(new GeometryDrawing(Hud.Brush("Accent", "#A3283A"), null, Geometry.Parse($"M0,0 L{band},0 L0,{band} Z M{band * 2},0 L{band * 2},{band} L{band},{band * 2} L0,{band * 2} Z")));
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, band * 2, band * 2), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }
    /// <summary>浅色斜纹（禁用按钮、同文卡片）。</summary>
    public static Brush Stripes(string a = "#EEF0F2", string b = "#E7EAED", double size = 5)
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(B(a), null, new RectangleGeometry(new Rect(0, 0, size, size))));
        g.Children.Add(new GeometryDrawing(B(b), null, Geometry.Parse($"M{size * .8},0 L{size},0 L{size},{size * .2} L{size * .2},{size} L0,{size} L0,{size * .8} Z")));
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }
    /// <summary>斜纹标签：小块红黑斜纹 + 雾蓝压缩英文（如 FIRST LINE // CONFIRM）。</summary>
    public static FrameworkElement StripedTag(string text, bool dark = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        row.Children.Add(new Border { Width = 18, Height = 10, Background = Hazard(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontFamily = Theme.Font("LabelFont"), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = dark ? B("#C3CBD4") : Hud.Brush("NormalAccent", "#3F5A78"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    /// <summary>雾蓝深底键帽（右下切角）。</summary>
    public static FrameworkElement Keycap(string key, bool light = false)
    {
        var text = new TextBlock { Text = key, FontFamily = Theme.Font("LabelFont"), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = light ? Hud.Brush("Plate", "#2A3139") : Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        return new CutFrame { Background = light ? B("#E3E9F0") : Hud.Brush("Plate", "#2A3139"), Cut = 3, Corners = CutCorners.BottomRight, Padding = new Thickness(4, 0, 4, 1), Child = text, VerticalAlignment = VerticalAlignment.Center, Height = 14, IsHitTestVisible = false };
    }
    /// <summary>页脚按键提示：键帽 + 说明，组之间留空；“A / B” 用圆形手柄键。</summary>
    public static FrameworkElement KeyHints(params (string Key, string Label)[] hints)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        foreach (var (key, label) in hints)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
            foreach (var k in key.Split('|'))
                item.Children.Add(k is "A" or "B" ? PadKey(k) : Keycap(k));
            item.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Hud.Brush("MutedText", "#4B5560"), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(item);
        }
        return row;
    }
    static FrameworkElement PadKey(string k) => new Border
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 1, 0),
        Background = k == "A" ? B("#3E7C7A") : Hud.Brush("Accent", "#A3283A"),
        Child = new TextBlock { Text = k, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Theme.Font("LabelFont") },
    };
    /// <summary>页眉：▶ 小标题 · 英文 → 大标题 → 宽字距英文路径；右侧描边大号水印由调用方另放。</summary>
    public static FrameworkElement Header(string kicker, string kickerEnglish, string title, string path, string extra = "")
    {
        var stack = new StackPanel { IsHitTestVisible = false };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(new Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Hud.Brush("Accent", "#A3283A"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(new TextBlock { Text = kicker, FontSize = 11.5, Foreground = Hud.Brush("Fg", "#1D232A") });
        top.Children.Add(new TextBlock { Text = kickerEnglish, FontSize = 9, FontFamily = Theme.Font("LabelFont"), Foreground = Hud.Brush("SubtleText", "#5A646E"), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (extra.Length > 0)
        {
            top.Children.Add(new Border { Width = 1, Height = 10, Background = Hud.Brush("Stroke", "#CBD1D7"), Margin = new Thickness(10, 0, 10, 0) });
            top.Children.Add(new TextBlock { Text = extra, FontSize = 11, Foreground = Hud.Brush("MutedText", "#4B5560"), TextTrimming = TextTrimming.CharacterEllipsis });
        }
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Foreground = Hud.Brush("Fg", "#1D232A") });
        if (path.Length > 0) stack.Children.Add(new TextBlock { Text = path, FontSize = 9.5, FontFamily = Theme.Font("LabelFont"), Foreground = Hud.Brush("SubtleText", "#5A646E"), Margin = new Thickness(0, 3, 0, 0) });
        return stack;
    }
    /// <summary>页眉右侧的描边大号编号（装饰，不占布局宽度）。</summary>
    public static FrameworkElement Watermark(string text, double size = 64) => new OutlinedText
    {
        Text = text, FontSize = size, Stroke = Hud.Solid("#334F6B8A"), StrokeThickness = 1.2, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -8, 0, 0),
    };
    /// <summary>区块小标题：暗红三角 + 中文 + 英文 + 右侧计数。</summary>
    public static FrameworkElement Section(string text, string english, FrameworkElement? right = null)
    {
        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Hud.Brush("Accent", "#A3283A"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        if (english.Length > 0) left.Children.Add(new TextBlock { Text = english, FontSize = 9, FontFamily = Theme.Font("LabelFont"), Foreground = Hud.Brush("SubtleText", "#5A646E"), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        dock.Children.Add(left);
        if (right != null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
        return dock;
    }
    public static TextBlock Count(string text) => new() { Text = text, FontSize = 10, FontFamily = Theme.Font("LabelFont"), FontWeight = FontWeights.SemiBold, Foreground = Hud.Brush("NormalAccent", "#3F5A78"), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>小号状态标签（边框 + 浅底）。</summary>
    public static Border Tag(string text, string tone = "mist")
    {
        var (fg, bg, line) = tone switch
        {
            "red" => ("#9C2536", "#F3E3E6", "#D9A9B1"),
            "brass" => ("#7F6223", "#F3ECDC", "#D3C196"),
            "teal" => ("#2F6664", "#E0ECEB", "#A9C9C7"),
            "grey" => ("#4B5560", "#EEF0F2", "#CBD1D7"),
            _ => ("#3F5A78", "#E3E9F0", "#B7C5D4"),
        };
        return new Border
        {
            Background = B(bg), BorderBrush = B(line), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = B(fg) },
        };
    }
    /// <summary>四角雾蓝括号（焦点 / 悬停）。</summary>
    public static CornerMarks Brackets(double length = 7) => new() { Stroke = MistBracket, StrokeThickness = 1.5, Length = length, IsHitTestVisible = false };
}
