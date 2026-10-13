using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>10-A3 指令按钮的附加属性：英文副标、线性图标、样式种类（"" 普通 / "go" 主按钮 / "ghost" 待机），Compact 隐藏副标（半宽按钮）。</summary>
public static class ActionButton
{
    static FrameworkPropertyMetadata Meta(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty SublabelProperty = DependencyProperty.RegisterAttached("Sublabel", typeof(string), typeof(ActionButton), Meta(""));
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(ActionButton), Meta(null));
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(ActionButton), Meta(""));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached("Compact", typeof(bool), typeof(ActionButton), Meta(false));
    public static string GetSublabel(DependencyObject d) => (string)d.GetValue(SublabelProperty);
    public static void SetSublabel(DependencyObject d, string v) => d.SetValue(SublabelProperty, v);
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? v) => d.SetValue(IconProperty, v);
    public static string GetKind(DependencyObject d) => (string)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string v) => d.SetValue(KindProperty, v);
    public static bool GetCompact(DependencyObject d) => (bool)d.GetValue(CompactProperty);
    public static void SetCompact(DependencyObject d, bool v) => d.SetValue(CompactProperty, v);

    /// <summary>代码侧一次设好：样式 + 副标 + 图标（+ 种类）。不改 Content、Click 或可用状态。</summary>
    internal static T Apply<T>(T button, string sublabel, Geometry? icon, string kind = "") where T : System.Windows.Controls.Button
    {
        button.Style = Theme.Style("TacticalActionButton");
        SetSublabel(button, sublabel); SetIcon(button, icon); SetKind(button, kind);
        return button;
    }
}

/// <summary>10-A3 图标（14×14 网格，1.25 描边，方头）。</summary>
public static class ActionIcons
{
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    public static readonly Geometry Confirm = G("M3,3.2 L7,7 L3,10.8 M7.4,3.2 L11.4,7 L7.4,10.8");
    public static readonly Geometry Line = G("M2,3.5 H12 M2,7 H12 M2,10.5 H7.5 M9.5,9 L11.5,10.5 L9.5,12");
    public static readonly Geometry Fork = G("M7,12.5 V7.4 L3.2,3.6 M7,7.4 L10.8,3.6 M2.4,5.6 V2.8 H5.2 M11.6,5.6 V2.8 H8.8");
    public static readonly Geometry Return = G("M5,2.8 L2.4,5.4 L5,8 M2.6,5.4 H8.8 A3,3 0 0 1 8.8,11.4 H5.2");
    public static readonly Geometry Locate = G("M3.4,7 A3.6,3.6 0 1 0 10.6,7 A3.6,3.6 0 1 0 3.4,7 M7,1 V3.4 M7,10.6 V13 M1,7 H3.4 M10.6,7 H13");
    public static readonly Geometry Link = G("M6,8.2 L8.2,6 M5.2,5.8 L3.6,7.4 A2,2 0 0 0 6.5,10.3 L8.1,8.7 M8.8,8.2 L10.4,6.6 A2,2 0 0 0 7.5,3.7 L5.9,5.3");
    public static readonly Geometry Collapse = G("M3.2,7.8 L7,4.4 L10.8,7.8 M2,11 H12");
    public static readonly Geometry Scope = G("M2,3 H12 M2,7 H12 M2,11 H12 M4,1.6 V4.4 M9,5.6 V8.4 M6,9.6 V12.4");
    public static readonly Geometry Expand = G("M2,3.5 H12 M2,7 H12 M2,10.5 H12 M10,1.8 L12,3.5 L10,5.2");
    public static readonly Geometry Menu = G("M2,2.5 H12 V11.5 H2 Z M4.5,5.5 H9.5 M4.5,8.5 H8");
    public static readonly Geometry Reopen = G("M10.6,4.6 A4.2,4.2 0 1 0 11.2,8.4 M11,1.8 V4.8 H8");
    /// <summary>F1b 派生的羽翼小标（22×10）。</summary>
    public static readonly Geometry Wing = G("M11,8.6 L1.2,2.4 M11,8.6 L3,5.2 M11,8.6 L5,7.4 M11,8.6 L20.8,2.4 M11,8.6 L19,5.2 M11,8.6 L17,7.4");
    public static readonly Geometry WingDiamond = G("M11,1.4 L12.2,2.8 L11,4.2 L9.8,2.8 Z");
}

/// <summary>按钮外形：右上 / 左下 7px 切角 + 1px 描边；按状态画角标、雾蓝括号、主按钮扫描线与光泽、禁用斜纹。</summary>
public sealed class ActionPlate : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object? v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(ActionPlate), Render(""));
    public static readonly DependencyProperty HotProperty = DependencyProperty.Register(nameof(Hot), typeof(bool), typeof(ActionPlate), Render(false));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed), typeof(bool), typeof(ActionPlate), Render(false));
    public static readonly DependencyProperty OffProperty = DependencyProperty.Register(nameof(Off), typeof(bool), typeof(ActionPlate), Render(false));
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(nameof(Cut), typeof(double), typeof(ActionPlate), Render(7.0));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool Hot { get => (bool)GetValue(HotProperty); set => SetValue(HotProperty, value); }
    public bool Pressed { get => (bool)GetValue(PressedProperty); set => SetValue(PressedProperty, value); }
    public bool Off { get => (bool)GetValue(OffProperty); set => SetValue(OffProperty, value); }
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public ActionPlate() { IsHitTestVisible = false; SnapsToDevicePixels = false; }

    static readonly Brush GoFill = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromRgb(0xB8, 0x34, 0x45), 0), new GradientStop(Color.FromRgb(0xA3, 0x28, 0x3A), .55), new GradientStop(Color.FromRgb(0x8F, 0x22, 0x32), 1),
    }, 90));
    static readonly Brush Gloss = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(0, 255, 255, 255), .30), new GradientStop(Color.FromArgb(33, 255, 255, 255), .38), new GradientStop(Color.FromArgb(10, 255, 255, 255), .46),
        new GradientStop(Color.FromArgb(0, 255, 255, 255), .47), new GradientStop(Color.FromArgb(0, 255, 255, 255), .58), new GradientStop(Color.FromArgb(18, 255, 255, 255), .61), new GradientStop(Color.FromArgb(0, 255, 255, 255), .64),
    }, 22));
    static readonly Brush Scan = Freeze(new DrawingBrush(new GeometryDrawing(Hud.Solid("#0DFFFFFF"), null, new RectangleGeometry(new Rect(0, 0, 4, 1))))
    { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 3), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 4, 3), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None });
    static readonly Brush Disabled = Archive.Stripes("#EEF0F2", "#E7EAED", 5);
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (w < 12 || h < 12) return;
        double c = Math.Min(Cut, h / 3);
        bool go = Kind == "go", ghost = Kind == "ghost", hot = Hot && !Off;
        var outer = Hud.Chamfer(new Rect(.5, .5, w - 1, h - 1), 0, c, 0, c);
        Brush fill = Off ? Disabled : go ? GoFill : ghost ? Hud.Solid("#B3F4F6F8") : hot ? Hud.Solid("#F1F5F9") : Hud.Solid("#F9FAFB");
        string line = Off ? "#C9CFD5" : go ? (hot ? "#CFE0EE" : "#7E1E2C") : hot ? "#6F8FB0" : ghost ? "#B9C2CB" : "#AAB4BE";
        dc.DrawGeometry(fill, null, outer);
        if (go && !Off)
        {
            dc.PushClip(outer);
            dc.DrawRectangle(Scan, null, new Rect(0, 0, w, h)); dc.DrawRectangle(Gloss, null, new Rect(0, 0, w, h));
            if (hot) dc.DrawRectangle(Hud.Solid("#12FFFFFF"), null, new Rect(0, 0, w, h));
            dc.Pop();
            dc.DrawGeometry(null, Hud.Pen(Hud.Solid("#9ED6B268"), 1), Hud.Chamfer(new Rect(2.5, 2.5, w - 5, h - 5), 0, c - 1.6, 0, c - 1.6));
        }
        if (Pressed && !Off) dc.DrawGeometry(Hud.Solid(go ? "#1A000000" : "#0D1D232A"), null, outer);
        dc.DrawGeometry(null, Hud.Pen(Hud.Solid(line), 1), outer);
        if (hot)
        {
            var mist = Hud.Solid(go ? "#DBE8F3" : "#6F8FB0"); var pen = Hud.Pen(mist, 1.5);
            double i = 3.75, l = 8;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(i, i + l), false, false); ctx.LineTo(new Point(i, i), true, false); ctx.LineTo(new Point(i + l, i), true, false);
                ctx.BeginFigure(new Point(w - i, h - i - l), false, false); ctx.LineTo(new Point(w - i, h - i), true, false); ctx.LineTo(new Point(w - i - l, h - i), true, false);
                // 切角处的 45° 短线
                double d = l / Math.Sqrt(2) / 2, cx = w - c / 2 - 2.4, cy = c / 2 + 2.4;
                ctx.BeginFigure(new Point(cx - d, cy - d), false, false); ctx.LineTo(new Point(cx + d, cy + d), true, false);
                cx = c / 2 + 2.4; cy = h - c / 2 - 2.4;
                ctx.BeginFigure(new Point(cx - d, cy - d), false, false); ctx.LineTo(new Point(cx + d, cy + d), true, false);
            }
            g.Freeze(); dc.DrawGeometry(null, pen, g);
            return;
        }
        // 角标：左上绯红 5px L，右下黄铜 5px L
        var red = Off ? Hud.Solid("#B9C0C7") : go ? Hud.Solid("#B3FFFFFF") : Hud.Brush("Accent", "#A3283A");
        var brass = Off ? Hud.Solid("#B9C0C7") : go ? Hud.Solid("#D6B268") : Hud.Brush("Brass", "#B08A3E");
        var tl = new StreamGeometry(); using (var ctx = tl.Open()) { ctx.BeginFigure(new Point(3.5, 8), false, false); ctx.LineTo(new Point(3.5, 3.5), true, false); ctx.LineTo(new Point(8, 3.5), true, false); }
        var br = new StreamGeometry(); using (var ctx = br.Open()) { ctx.BeginFigure(new Point(w - 3.5, h - 8), false, false); ctx.LineTo(new Point(w - 3.5, h - 3.5), true, false); ctx.LineTo(new Point(w - 8, h - 3.5), true, false); }
        tl.Freeze(); br.Freeze();
        dc.DrawGeometry(null, Hud.Pen(red, 1), tl); dc.DrawGeometry(null, Hud.Pen(brass, 1), br);
    }
}

public sealed class UpperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value?.ToString() ?? "").ToUpperInvariant();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
/// <summary>压缩英文的字距（WPF 无 letter-spacing）：字符间插入细空格。</summary>
public sealed class TrackingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string s = value?.ToString() ?? ""; return s.Length < 2 ? s : string.Join("\u200A", s.ToCharArray());
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
