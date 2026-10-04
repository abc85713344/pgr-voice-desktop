using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PgrVoice;

public sealed class DialogueRegionWindow : Window
{
    public DialogueRegion Region { get; private set; }
    public DialogueRegionWindow(Window owner, string imageFile, DialogueRegion current)
    {
        Owner = owner; Title = "选取对白正文"; Width = 960; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Brush("BallNormal");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imageFile); bitmap.EndInit(); bitmap.Freeze();
        Region = current.IsValid ? current : new();
        var layout = new DockPanel { Margin = new Thickness(12) }; Content = layout;
        var instructions = new TextBlock { Text = "拖动框选对白正文，避开角色名、继续图标和悬浮窗。只适用于普通白字对白。", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(instructions, Dock.Top); layout.Children.Add(instructions);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var cancel = new Button { Content = "取消", Margin = new Thickness(5) }; cancel.Click += (_, _) => DialogResult = false;
        var accept = new Button { Content = "使用此范围", Margin = new Thickness(5) }; accept.Click += (_, _) => { if (Region.IsValid) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        var canvas = new Canvas { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, ClipToBounds = true, Cursor = Cursors.Cross };
        canvas.Children.Add(new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight });
        var box = new Rectangle { Stroke = Brushes.LimeGreen, StrokeThickness = Math.Max(2, bitmap.PixelWidth / 400.0), Fill = new SolidColorBrush(Color.FromArgb(35, 60, 255, 80)), IsHitTestVisible = false };
        canvas.Children.Add(box); layout.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = canvas });
        void Paint()
        { Canvas.SetLeft(box, Region.Left * canvas.Width); Canvas.SetTop(box, Region.Top * canvas.Height); box.Width = Region.Width * canvas.Width; box.Height = Region.Height * canvas.Height; accept.IsEnabled = Region.IsValid; }
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
