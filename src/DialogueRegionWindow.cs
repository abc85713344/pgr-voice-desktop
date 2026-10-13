using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace PgrVoice;

public sealed class DialogueRegionWindow : Window
{
    public DialogueRegion Region { get; private set; }
    public DialogueRegionWindow(Window owner, string imageFile, DialogueRegion current)
    {
        Owner = owner; Title = "选取对白正文"; Width = 960; Height = 660; MinWidth = 640; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Theme.ApplyDialog(this);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imageFile); bitmap.EndInit(); bitmap.Freeze();
        Region = current.IsValid ? current : new();
        // 09-B：截图内零装饰（只保留原色选框 + 框外压暗 42% 显示层）；PGR 风格只在外围：深色截图台斜纹、四角括号、刻度尺、说明条眉标。
        var layout = new DockPanel { Margin = new Thickness(14, 12, 14, 12) }; Content = layout;
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var kicker = new StackPanel { Orientation = Orientation.Horizontal };
        kicker.Children.Add(new Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Theme.Brush("Accent"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        kicker.Children.Add(new TextBlock { Text = "对话区域", FontWeight = FontWeights.SemiBold, FontSize = 12 });
        kicker.Children.Add(new TextBlock { Text = "DIALOGUE REGION", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Theme.Brush("SubtleText"), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(kicker);
        var instructions = new TextBlock { Text = "拖动框选对白正文，避开角色名、继续图标和悬浮窗。只适用于普通白字对白。", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 4, 0, 0) };
        head.Children.Add(instructions);
        DockPanel.SetDock(head, Dock.Top); layout.Children.Add(head);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var cancel = new Button { Content = "取消", Margin = new Thickness(5) }; cancel.Click += (_, _) => DialogResult = false;
        var accept = new Button { Content = "使用此范围", Margin = new Thickness(5), Style = Theme.Style("PrimaryButton") }; accept.Click += (_, _) => { if (Region.IsValid) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        // 右侧 240px 校准栏
        var values = new TextBlock[4];
        var valid = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };
        var reset = new Button { Content = "恢复默认范围", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var calibration = new StackPanel { Width = 240, Margin = new Thickness(12, 0, 0, 0) };
        calibration.Children.Add(Archive.Section("校准", "CALIBRATION"));
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new());
        string[] names = { "L 左", "T 上", "W 宽", "H 高" };
        for (int i = 0; i < 4; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            cell.Children.Add(new TextBlock { Text = names[i], FontSize = 11, Foreground = Theme.Brush("MutedText") });
            values[i] = new TextBlock { FontFamily = Theme.Font("NumberFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
            cell.Children.Add(values[i]);
            var frame = new Border { Background = Hud.Solid("#F4F6F8"), BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, i % 2 == 0 ? 6 : 0, 6), Child = cell };
            Grid.SetRow(frame, i / 2); Grid.SetColumn(frame, i % 2); grid.Children.Add(frame);
        }
        calibration.Children.Add(grid);
        string[] notes = { "只框对白正文", "避开角色名", "避开继续图标", "避开悬浮窗" };
        for (int i = 0; i < notes.Length; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = (i + 1).ToString("00"), FontFamily = Theme.Font("NumberFont"), FontSize = 12, Foreground = Theme.Brush("NormalAccent"), Width = 24 });
            row.Children.Add(new TextBlock { Text = notes[i], FontSize = 12.5 });
            calibration.Children.Add(row);
        }
        calibration.Children.Add(valid); calibration.Children.Add(reset);
        DockPanel.SetDock(calibration, Dock.Right); layout.Children.Add(calibration);
        // 下方「框内放大」条：实际读到的画面（只显示）。
        var zoomBrush = new ImageBrush(bitmap) { Stretch = Stretch.Uniform, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, AlignmentX = AlignmentX.Left };
        var zoom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var zoomLabel = new StackPanel { Width = 92, VerticalAlignment = VerticalAlignment.Center };
        zoomLabel.Children.Add(new TextBlock { Text = "框内放大", FontSize = 12, FontWeight = FontWeights.SemiBold });
        zoomLabel.Children.Add(new TextBlock { Text = "实际读到的画面", FontSize = 10.5, Foreground = Theme.Brush("MutedText") });
        DockPanel.SetDock(zoomLabel, Dock.Left); zoom.Children.Add(zoomLabel);
        var zoomView = new Border { Height = 74, Background = Hud.Solid("#14181D"), BorderBrush = Theme.Brush("Plate"), BorderThickness = new Thickness(1), Child = new Border { Background = zoomBrush, Margin = new Thickness(6) } };
        zoom.Children.Add(zoomView);
        DockPanel.SetDock(zoom, Dock.Bottom); layout.Children.Add(zoom);
        var canvas = new Canvas { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, ClipToBounds = true, Cursor = Cursors.Cross };
        canvas.Children.Add(new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight });
        var dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)), IsHitTestVisible = false };
        canvas.Children.Add(dim);
        var box = new Rectangle { Stroke = Theme.Brush("Accent"), StrokeThickness = Math.Max(2, bitmap.PixelWidth / 400.0), Fill = new SolidColorBrush(Color.FromArgb(40, 163, 40, 58)), IsHitTestVisible = false };
        // 选框外侧加暗红 L 形角标（按截图像素缩放，经 Viewbox 缩小后仍清晰可见）。
        double unit = Math.Max(2, bitmap.PixelWidth / 400.0);
        var reticle = new CornerMarks { StrokeThickness = unit * 1.6, Length = unit * 9 };
        canvas.Children.Add(box); canvas.Children.Add(reticle);
        // 刻度尺在截图外（同一 Viewbox 内，随截图等比缩放）。
        var topRuler = new RegionRuler { Horizontal = true, Height = unit * 12, Unit = unit };
        var leftRuler = new RegionRuler { Horizontal = false, Width = unit * 16, Unit = unit };
        var scaled = new Grid();
        scaled.RowDefinitions.Add(new() { Height = GridLength.Auto }); scaled.RowDefinitions.Add(new() { Height = GridLength.Auto });
        scaled.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); scaled.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(topRuler, 1); scaled.Children.Add(topRuler);
        Grid.SetRow(leftRuler, 1); scaled.Children.Add(leftRuler);
        Grid.SetRow(canvas, 1); Grid.SetColumn(canvas, 1); scaled.Children.Add(canvas);
        var stage = new Grid { Background = Archive.Stripes("#20262D", "#252C34", 7), ClipToBounds = true };
        stage.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = scaled, Margin = new Thickness(18) });
        var brackets = Archive.Brackets(12); brackets.Margin = new Thickness(6); stage.Children.Add(brackets);
        layout.Children.Add(new CutFrame { Cut = 12, Corners = CutCorners.TopRight, ClipContent = true, BorderBrush = Theme.Brush("Plate"), Child = stage });
        void Paint()
        {
            Canvas.SetLeft(box, Region.Left * canvas.Width); Canvas.SetTop(box, Region.Top * canvas.Height); box.Width = Region.Width * canvas.Width; box.Height = Region.Height * canvas.Height; accept.IsEnabled = Region.IsValid;
            Canvas.SetLeft(reticle, Region.Left * canvas.Width - unit * 3); Canvas.SetTop(reticle, Region.Top * canvas.Height - unit * 3);
            reticle.Width = Region.Width * canvas.Width + unit * 6; reticle.Height = Region.Height * canvas.Height + unit * 6;
            reticle.Visibility = Region.Width > 0 && Region.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
            var outside = new RectangleGeometry(new Rect(0, 0, canvas.Width, canvas.Height));
            var inside = new RectangleGeometry(new Rect(Region.Left * canvas.Width, Region.Top * canvas.Height, Region.Width * canvas.Width, Region.Height * canvas.Height));
            dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outside, inside);
            topRuler.Start = Region.Left; topRuler.End = Region.Left + Region.Width; topRuler.InvalidateVisual();
            leftRuler.Start = Region.Top; leftRuler.End = Region.Top + Region.Height; leftRuler.InvalidateVisual();
            values[0].Text = $"{Region.Left * 100:0.0}%"; values[1].Text = $"{Region.Top * 100:0.0}%"; values[2].Text = $"{Region.Width * 100:0.0}%"; values[3].Text = $"{Region.Height * 100:0.0}%";
            valid.Text = (Region.IsValid ? "范围有效" : "范围无效") + " · 最小宽 8%、高 2.5%";
            valid.Foreground = Region.IsValid ? Theme.Brush("TealText") : Theme.Brush("BranchAccent");
            zoomBrush.Viewbox = Region.Width > 0 && Region.Height > 0 ? new Rect(Region.Left, Region.Top, Region.Width, Region.Height) : new Rect(0, 0, 1, 1);
        }
        reset.Click += (_, _) => { Region = new DialogueRegion(); Paint(); };
        Point? start = null;
        Point Clamp(Point p) => new(Math.Clamp(p.X, 0, canvas.Width), Math.Clamp(p.Y, 0, canvas.Height));
        canvas.MouseLeftButtonDown += (_, e) => { start = Clamp(e.GetPosition(canvas)); canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseMove += (_, e) =>
        {
            if (start is not Point origin || e.LeftButton != MouseButtonState.Pressed) return;
            var end = Clamp(e.GetPosition(canvas));
            Region = new(Math.Min(origin.X, end.X) / canvas.Width, Math.Min(origin.Y, end.Y) / canvas.Height,
                Math.Abs(origin.X - end.X) / canvas.Width, Math.Abs(origin.Y - end.Y) / canvas.Height); Paint();
        };
        canvas.MouseLeftButtonUp += (_, _) => { start = null; canvas.ReleaseMouseCapture(); };
        Paint();
    }
}

/// <summary>09-B 截图外刻度尺：0–100%（每 10% 刻线、每 25% 标数），选框边界用绯红刻线标出。</summary>
internal sealed class RegionRuler : FrameworkElement
{
    public bool Horizontal { get; set; }
    public double Unit { get; set; } = 2;
    public double Start { get; set; }
    public double End { get; set; }
    public RegionRuler() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double length = Horizontal ? ActualWidth : ActualHeight, depth = Horizontal ? ActualHeight : ActualWidth;
        if (length <= 0) return;
        var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x8F, 0x9B, 0xA7)), Unit * .5);
        var mark = new Pen(Theme.Brush("Accent"), Unit * 1.2);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= 20; i++)
        {
            double at = length * i / 20, size = i % 5 == 0 ? depth * .55 : i % 2 == 0 ? depth * .35 : depth * .2;
            if (Horizontal) dc.DrawLine(tick, new Point(at, depth), new Point(at, depth - size));
            else dc.DrawLine(tick, new Point(depth, at), new Point(depth - size, at));
            if (i % 5 == 0)
            {
                var text = new FormattedText((i * 5).ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), Unit * 6, new SolidColorBrush(Color.FromRgb(0xAE, 0xB9, 0xC4)), dpi);
                if (Horizontal) dc.DrawText(text, new Point(Math.Clamp(at + Unit, 0, length - text.Width), 0));
                else dc.DrawText(text, new Point(0, Math.Clamp(at + Unit * .5, 0, length - text.Height)));
            }
        }
        foreach (double edge in new[] { Start, End })
        {
            double at = Math.Clamp(edge, 0, 1) * length;
            if (Horizontal) dc.DrawLine(mark, new Point(at, 0), new Point(at, depth)); else dc.DrawLine(mark, new Point(0, at), new Point(depth, at));
        }
    }
}
