using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace PgrVoice;

/// <summary>
/// 0.4.7 悬浮快捷窗（设计稿 17–19）的视觉零件：16 格线性图标、键帽、红色「连接普通对白」主按钮、分段切换、次要按钮、暂停按钮、状态条。
/// 尺寸取自 x047q.css：主按钮高 46（状态卡内 44）、次要按钮 34、切换 34；颜色全部取 Tactical.xaml 令牌。
/// </summary>
static class QuickUi
{
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    public static readonly Dictionary<string, Geometry> Icons = new()
    {
        ["link"] = G("M2.4,8 A5.6,5.6 0 1 0 13.6,8 A5.6,5.6 0 1 0 2.4,8 M8,0.8 V3.8 M8,12.2 V15.2 M0.8,8 H3.8 M12.2,8 H15.2 M5.6,8.2 L7.3,9.8 L10.5,6.4"),
        ["explore"] = G("M8,1.2 L10,6 L14.8,8 L10,10 L8,14.8 L6,10 L1.2,8 L6,6 Z M6.8,8 A1.2,1.2 0 1 0 9.2,8 A1.2,1.2 0 1 0 6.8,8"),
        ["dialog"] = G("M1.6,2.4 H14.4 V10.6 H7.2 L4,13.6 V10.6 H1.6 Z M4.4,5.4 H11.6 M4.4,7.8 H9"),
        ["pause"] = G("M5.4,3 V13 M10.6,3 V13"),
        ["play"] = G("M4.6,2.6 L13,8 L4.6,13.4 Z"),
        ["replay"] = G("M3.2,8.6 A4.8,4.8 0 1 0 4.6,4.5 M4.6,1.6 V4.6 H1.6"),
        ["next"] = G("M3,3 L9.6,8 L3,13 M12.4,3 V13"),
        ["pin"] = G("M5.4,1.6 H10.6 L9.8,6 L12.2,8.4 H3.8 L6.2,6 Z M8,8.4 V14.4"),
        ["open"] = G("M2.4,2.4 H9.6 M2.4,2.4 V13.6 H13.6 V6.4 M9,7 L14,2 M10.4,2 H14 V5.6"),
        ["min"] = G("M3,8 H13"),
        ["chev"] = G("M5.6,3 L10.2,8 L5.6,13"),
        ["up"] = G("M3,10.4 L8,5.6 L13,10.4"),
        ["down"] = G("M3,5.6 L8,10.4 L13,5.6"),
        ["exit"] = G("M6,2.5 H2.5 V13.5 H6 M7,8 H14 M11,5 L14,8 L11,11"),
        ["fb"] = G("M1.6,2 H14.4 V10 H8 L4.6,13.4 V10 H1.6 Z"),
        ["stop"] = G("M3.4,3.4 H12.6 V12.6 H3.4 Z"),
        ["diag"] = G("M2,13.6 H14 M4,11 V7 M8,11 V3.4 M12,11 V5.6"),
        ["scene"] = G("M1.6,12.8 L5.6,6.8 L8.6,10.8 L10.6,8.2 L14.4,12.8 Z M9.8,4.4 A1.6,1.6 0 1 0 13,4.4 A1.6,1.6 0 1 0 9.8,4.4"),
        ["check"] = G("M2.6,8.4 L6,11.6 L13.4,4"),
        ["warn"] = G("M8,1.6 L14.6,13.6 H1.4 Z M8,6 V9.6 M8,11.4 V11.8"),
        ["grid"] = G("M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z"),
    };

    public static Brush Hex(string hex) => Hud.Solid(hex);
    public static Brush B(string key) => Theme.Brush(key);

    /// <summary>16 格线性图标，按 size 缩放（描边随之缩放，与设计稿 SVG viewBox 一致）。</summary>
    public static FrameworkElement Icon(string key, Brush stroke, double size = 16, double width = 1.4)
    {
        var path = new Path { Data = Icons[key], Stroke = stroke, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square, StrokeLineJoin = PenLineJoin.Miter, Width = 16, Height = 16, Stretch = Stretch.None, SnapsToDevicePixels = false };
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 16, Height = 16, Children = { path } }, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    /// <summary>键帽：细描边 + 窄体数字（.kbd）。</summary>
    public static Border Kbd(string key, Brush fg, double left = 6) => new()
    {
        BorderBrush = fg, BorderThickness = new Thickness(1), Height = 16, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(left, 0, 0, 0), Opacity = .85,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        Child = new TextBlock { Text = key, FontFamily = Theme.Font("NumberFont"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center },
    };

    static ControlTemplate? bare;
    /// <summary>只呈现内容的按钮模板：外观全部由内容里的 CutFrame 画出，悬停 / 按下由 <see cref="Hover"/> 改底色。</summary>
    public static ControlTemplate Bare => bare ??= (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><ContentPresenter RecognizesAccessKey='False'/></ControlTemplate>");

    public static Button Plate(FrameworkElement content, string tip, Action click, string automationName = "")
    {
        var button = new Button { Template = Bare, Content = content, Focusable = false, Cursor = Cursors.Hand, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName.Length > 0 ? automationName : tip);
        button.Click += (_, _) => click();
        return button;
    }
    /// <summary>悬停 / 按下：只换底色，不动布局。</summary>
    public static void Hover(Button button, CutFrame frame, Brush normal, Brush hot, Brush pressed)
    {
        void Paint() => frame.Background = !button.IsEnabled ? normal : button.IsPressed ? pressed : button.IsMouseOver ? hot : normal;
        frame.Background = normal;
        button.MouseEnter += (_, _) => Paint(); button.MouseLeave += (_, _) => Paint();
        button.PreviewMouseLeftButtonDown += (_, _) => button.Dispatcher.BeginInvoke(Paint);
        button.PreviewMouseLeftButtonUp += (_, _) => button.Dispatcher.BeginInvoke(Paint);
        button.IsEnabledChanged += (_, _) => { button.Opacity = button.IsEnabled ? 1 : .5; Paint(); };
    }

    static readonly Brush ConnectFill = Freeze(new LinearGradientBrush(Color.FromRgb(0xB3, 0x31, 0x44), Color.FromRgb(0x96, 0x22, 0x3A), 90));
    static readonly Brush ConnectHot = Freeze(new LinearGradientBrush(Color.FromRgb(0xC0, 0x3A, 0x4E), Color.FromRgb(0xA3, 0x28, 0x3A), 90));
    static readonly Brush ConnectDown = Freeze(new LinearGradientBrush(Color.FromRgb(0x96, 0x22, 0x3A), Color.FromRgb(0x82, 0x1C, 0x31), 90));
    static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    /// <summary>红色主按钮「连接普通对白」（.conn）：图标框、标题 + 小字说明、键帽；切角左下 / 右上，底部细高光线。</summary>
    public static Button Connect(string title, string sub, string key, double height, Action click, out TextBlock titleText, out TextBlock subText, out Border keycap)
    {
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double box = height >= 40 ? 26 : 22;
        var iconBox = new Border { Width = box, Height = box, BorderBrush = Hex("#BFFFFFFF"), BorderThickness = new Thickness(1.4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Child = Icon("link", Brushes.White, box - 10) };
        grid.Children.Add(iconBox);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        titleText = new TextBlock { Text = title, FontSize = height >= 40 ? 15 : 13.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        subText = new TextBlock { Text = sub, FontSize = 10.5, Foreground = Hex("#D9FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
        texts.Children.Add(titleText); texts.Children.Add(subText);
        keycap = Kbd(key, Brushes.White); Grid.SetColumn(keycap, 2); grid.Children.Add(keycap);
        keycap.Visibility = key.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var shine = new Rectangle { Height = 1, Fill = Hex("#59FFFFFF"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, height >= 40 ? 5 : 3), IsHitTestVisible = false }; Grid.SetColumnSpan(shine, 3); grid.Children.Add(shine);
        var frame = new CutFrame { Cut = 10, Corners = CutCorners.TopRight | CutCorners.BottomLeft, Padding = new Thickness(12, 0, 12, 0), Child = grid, SnapsToDevicePixels = true };
        var button = Plate(frame, "读取游戏当前对白并接着跟随（与主窗口「游戏配音 › 文字跟随」同一操作）", click, "连接普通对白");
        Hover(button, frame, ConnectFill, ConnectHot, ConnectDown);
        return button;
    }

    /// <summary>次要按钮（.sb）：白底细描边、右下 7px 切角；图标雾蓝。</summary>
    public static Button Secondary(string text, string icon, string key, Action click, string tip = "", string note = "", double height = 34)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Icon(icon, B("Slate"), 14)); row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = B("Fg"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (note.Length > 0) row.Children.Add(new TextBlock { Text = note, FontSize = 10, Foreground = B("SubtleText"), Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (key.Length > 0) row.Children.Add(Kbd(key, B("SubtleText"), 5));
        var frame = new CutFrame { BorderBrush = B("Stroke"), Cut = 7, Corners = CutCorners.BottomRight, Height = height, Child = row, Padding = new Thickness(6, 0, 6, 0) };
        var button = Plate(frame, tip.Length > 0 ? tip : text, click, text);
        Hover(button, frame, Brushes.White, B("Surface"), B("SlateFill"));
        return button;
    }

    /// <summary>暂停 / 继续（.pz）：雾蓝 1.4 描边 + 右下角折角；继续态为黄铜淡底黄铜字。</summary>
    public sealed class PauseButton
    {
        public readonly Button Button; readonly CutFrame frame; readonly TextBlock text; readonly ContentControl icon; readonly Border keycap; readonly Path tick;
        bool on;
        public PauseButton(string key, Action click, double height = 34)
        {
            text = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            icon = new ContentControl { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            keycap = Kbd(key, B("SubtleText"), 5);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon, text, keycap } };
            tick = new Path { Data = G("M0,6 H6 V0"), StrokeThickness = 1.4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 3), IsHitTestVisible = false };
            var grid = new Grid { Children = { row, tick } };
            frame = new CutFrame { StrokeThickness = 1.4, Cut = 7, Corners = CutCorners.BottomRight, Height = height, Child = grid, Padding = new Thickness(6, 0, 0, 0) };
            Button = Plate(frame, "暂停 / 继续配音跟随", click, "暂停");
            Button.MouseEnter += (_, _) => Paint(); Button.MouseLeave += (_, _) => Paint();
            Set(false, key);
        }
        public void Set(bool paused, string key)
        {
            on = paused;
            text.Text = paused ? "继续" : "暂停";
            ((TextBlock)keycap.Child).Text = key; keycap.Visibility = key.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(Button, text.Text);
            Paint();
        }
        void Paint()
        {
            var line = on ? B("Brass") : B("Slate");
            frame.BorderBrush = line; tick.Stroke = line;
            frame.Background = on ? (Button.IsMouseOver ? Hex("#EFE4CB") : B("BranchFill")) : (Button.IsMouseOver ? B("Surface") : Brushes.White);
            text.Foreground = on ? B("BrassText") : B("Plate");
            icon.Content = Icon(on ? "play" : "pause", line, 14, 1.6);
        }
    }

    /// <summary>分段切换（.seg）：对白跟随 / 探索面板；选中为雾蓝底，探索选中为黄铜底。</summary>
    public sealed class Segment
    {
        public readonly FrameworkElement View; readonly Button left, right; readonly CutFrame leftFrame, rightFrame; readonly TextBlock leftText, rightText; readonly ContentControl leftIcon, rightIcon;
        bool explore;
        public Segment(Action<bool> pick)
        {
            (left, leftFrame, leftText, leftIcon) = Half("对白跟随", () => pick(false));
            (right, rightFrame, rightText, rightIcon) = Half("探索面板", () => pick(true));
            var grid = new Grid { Height = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right);
            grid.Children.Add(new Rectangle { Width = 1, Fill = B("Stroke"), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Margin = new Thickness(0, 1, 0, 1) }.Also(r => Grid.SetColumn(r, 1)));
            View = new CutFrame { BorderBrush = B("StrokeStrong"), Background = Brushes.White, Cut = 7, Corners = CutCorners.BottomRight, Child = grid, ClipContent = true };
            Set(false);
        }
        static (Button, CutFrame, TextBlock, ContentControl) Half(string label, Action click)
        {
            var text = new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var icon = new ContentControl { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            var frame = new CutFrame { Cut = 0, Corners = CutCorners.None, Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon, text } } };
            return (Plate(frame, label, click, label), frame, text, icon);
        }
        public void Set(bool exploreOn)
        {
            explore = exploreOn;
            Paint(leftFrame, leftText, leftIcon, "dialog", !explore, B("Slate"));
            Paint(rightFrame, rightText, rightIcon, "explore", explore, B("Brass"));
        }
        static void Paint(CutFrame frame, TextBlock text, ContentControl icon, string key, bool on, Brush fill)
        {
            frame.Background = on ? fill : Brushes.Transparent;
            text.Foreground = on ? Brushes.White : B("SubtleText"); text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            icon.Content = Icon(key, on ? Brushes.White : B("SubtleText"), 14);
        }
    }

    /// <summary>状态条（.bn）：ok 青绿 / bad 红 / wait 黄铜；左 3px 色条 + 图标 + 粗标题 + 小字说明。</summary>
    public static FrameworkElement Banner(string tone, string icon, string eyebrow, string title, string detail)
    {
        (string bg, string edge, string fg) = tone switch { "ok" => ("#E3F0EC", "#2E7D6B", "#1F5C4E"), "bad" => ("#F6E4E6", "#A3283A", "#8C2232"), _ => ("#F3ECDC", "#B08A3E", "#6E5520") };
        var body = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        if (eyebrow.Length > 0) body.Children.Add(new TextBlock { Text = eyebrow, FontFamily = Theme.Font("LabelFont"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Hex(fg), Margin = new Thickness(0, 0, 0, 2) });
        body.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Hex(fg), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        if (detail.Length > 0) body.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = B("SubtleText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var row = new DockPanel();
        var mark = Icon(icon, Hex(edge), 16, tone == "ok" ? 1.8 : 1.4); mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0);
        DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark); row.Children.Add(body);
        return new CutFrame { Background = Hex(bg), BorderBrush = Hex(edge), OutlineBrush = Hex(bg), EdgeWidth = 3, Cut = 0, Corners = CutCorners.None, Padding = new Thickness(9, 8, 9, 8), Child = row };
    }

    /// <summary>小字链接（.lk）：雾蓝图标 + 文字 + 可选键帽。</summary>
    public static Button Link(string text, string icon, string key, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        row.Children.Add(Icon(icon, B("Slate"), 12));
        var label = new TextBlock { Text = text, FontSize = 11, Foreground = B("Slate"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        if (key.Length > 0) row.Children.Add(Kbd(key, B("Slate"), 5));
        var button = Plate(row, text, click, text); button.HorizontalAlignment = HorizontalAlignment.Left; button.MinHeight = 22;
        button.MouseEnter += (_, _) => label.TextDecorations = TextDecorations.Underline; button.MouseLeave += (_, _) => label.TextDecorations = null;
        return button;
    }

}
