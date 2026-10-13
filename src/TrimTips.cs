using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PgrVoice;

// 被省略号截断的文字自动获得完整文本的悬停提示；未截断时不弹出，也不覆盖控件自己的提示。
static class TrimTips
{
    static readonly DependencyProperty AutoProperty = DependencyProperty.RegisterAttached("Auto", typeof(bool), typeof(TrimTips));
    static bool registered;

    public static void Register()
    {
        if (registered) return; registered = true;
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Attach((TextBlock)s)));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(Opening));
    }

    static void Attach(TextBlock text)
    {
        if (text.TextTrimming == TextTrimming.None || (bool)text.GetValue(AutoProperty)) return;
        if (text.ToolTip != null || HasOwnerTip(text)) return;
        text.SetValue(AutoProperty, true);
        text.ToolTip = text.Text;
    }

    static bool HasOwnerTip(DependencyObject node)
    {
        for (int i = 0; i < 8 && node != null; i++)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            if (node is Window) return false;
            if (node is FrameworkElement { ToolTip: not null }) return true;
        }
        return false;
    }

    static void Opening(object sender, ToolTipEventArgs e)
    {
        var text = (TextBlock)sender;
        if (!(bool)text.GetValue(AutoProperty)) return;
        if (!IsTrimmed(text)) { e.Handled = true; return; }
        text.ToolTip = new TextBlock { Text = text.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    }

    public static bool IsTrimmed(TextBlock text)
    {
        if (text.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0) return false;
        var format = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        double width = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        if (text.TextWrapping == TextWrapping.NoWrap) return format.WidthIncludingTrailingWhitespace > width + 0.5;
        format.MaxTextWidth = Math.Max(1, width);
        if (text.LineHeight > 0 && double.IsFinite(text.LineHeight)) format.LineHeight = text.LineHeight;
        return format.Height > text.ActualHeight - text.Padding.Top - text.Padding.Bottom + 0.5;
    }
}
