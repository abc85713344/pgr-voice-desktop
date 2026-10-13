using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PgrVoice;

// 11-B「本节全部选择点」节点航线图：每行 = 航线节点列（36px）+ 两行摘要；选中行展开选项标签、前句 / 首句与状态说明。
public sealed partial class BranchMenuWindow
{
    static readonly IValueConverter SelectedVisible = new FuncConverter(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    static readonly IValueConverter SelectedCollapsed = new FuncConverter(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    static Binding RowSelected(IValueConverter converter) => new(nameof(ListBoxItem.IsSelected)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1), Converter = converter };

    static (string Tone, RouteNodeKind Kind) StatusStyle(StoryMenuTarget t) => t.Status switch
    {
        "返回已访问选择" => ("brass", RouteNodeKind.Visited),
        "已核实互动" => ("teal", RouteNodeKind.Locatable),
        "仅定位选择点" => ("mist", RouteNodeKind.Locatable),
        "先到所属选择点" => ("red", RouteNodeKind.Blocked),
        _ => ("grey", RouteNodeKind.Unverified),
    };

    FrameworkElement StoryMenuRow(StoryMenuTarget target, bool first, bool last)
    {
        var (tone, kind) = StatusStyle(target);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new RouteNode { Kind = target.IsCurrent ? RouteNodeKind.Current : kind, First = first, Last = last });
        var body = new StackPanel { Margin = new Thickness(2, 7, 8, 7) }; Grid.SetColumn(body, 1); grid.Children.Add(body);
        // 第 1 行：描边序号 + 选择点文字（+ 当前）+ 状态标签
        string text = target.Label; int dot = text.IndexOf(" · ", StringComparison.Ordinal);
        if (target.Index > 0 && dot > 0 && dot <= 4) text = text[(dot + 3)..];
        var line1 = new DockPanel { LastChildFill = true };
        var tag = Archive.Tag(target.Status, tone); tag.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(tag, Dock.Right); line1.Children.Add(tag);
        if (target.Index > 0)
        {
            var index = new OutlinedText { Text = target.Index.ToString("00"), FontSize = 15, StrokeThickness = 1, Stroke = target.IsCurrent ? Theme.Brush("Accent") : Hud.Solid("#8FA6BF"), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(index, Dock.Left); line1.Children.Add(index);
        }
        line1.Children.Add(new TextBlock
        {
            Text = text + (target.IsCurrent ? " · 当前" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = target.IsCurrent ? Theme.Brush("AccentText") : Theme.Brush("Fg"),
        });
        body.Children.Add(line1);
        var lines = target.Preview.Split('\n');
        string options = lines.Length > 0 ? lines[0] : "";
        // 第 2 行（未选中）：选项以「／」连接，超出省略
        var collapsed = new TextBlock { Text = options, FontSize = 11.5, Foreground = Theme.Brush("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
        collapsed.SetBinding(VisibilityProperty, RowSelected(SelectedCollapsed));
        body.Children.Add(collapsed);
        // 选中展开：选项标签（曾选择的一项黄铜底 + 菱形）、前句 / 首句、状态说明、原因
        var expanded = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        expanded.SetBinding(VisibilityProperty, RowSelected(SelectedVisible));
        var chips = new WrapPanel();
        foreach (var option in options.Split(" ／ ", StringSplitOptions.RemoveEmptyEntries))
        {
            bool taken = target.TakenOptionLabel.Length > 0 && option == target.TakenOptionLabel;
            var chip = Archive.Tag(taken ? "◆ " + option : option, taken ? "brass" : "grey"); chip.Margin = new Thickness(0, 0, 5, 4);
            chips.Children.Add(chip);
        }
        expanded.Children.Add(chips);
        foreach (var extra in lines.Skip(1))
        {
            int colon = extra.IndexOf('：');
            if (colon <= 0) { expanded.Children.Add(new TextBlock { Text = extra, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }); continue; }
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var label = Archive.Tag(extra[..colon], "mist"); label.Margin = new Thickness(0, 0, 6, 0); label.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(new TextBlock { Text = extra[(colon + 1)..], FontSize = 11.5, Foreground = Theme.Brush("Fg"), TextWrapping = TextWrapping.Wrap });
            expanded.Children.Add(row);
        }
        expanded.Children.Add(new TextBlock { Text = target.IsVisited ? "曾选择 · 可重新定位" : "按游戏当前画面确认", FontSize = 11, Foreground = LineRow.BranchAccent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (target.Reason.Length > 0 && target.Status is "先到所属选择点" or "待核对")
            expanded.Children.Add(new Border
            {
                BorderBrush = Theme.Brush("Accent"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Hud.Solid("#80F3E3E6"), Padding = new Thickness(7, 3, 6, 3), Margin = new Thickness(0, 5, 0, 0),
                Child = new TextBlock { Text = target.Reason, FontSize = 11, Foreground = Theme.Brush("AccentText"), TextWrapping = TextWrapping.Wrap },
            });
        body.Children.Add(expanded);
        return grid;
    }
}

public enum RouteNodeKind { Visited, Current, Locatable, Blocked, Unverified }

/// <summary>航线节点：黄铜虚线贯穿整行（首行从节点开始、末行到节点结束），节点图标按状态绘制。</summary>
public sealed class RouteNode : FrameworkElement
{
    public RouteNodeKind Kind { get; set; }
    public bool First { get; set; }
    public bool Last { get; set; }
    public RouteNode() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height; if (h < 10) return;
        double cx = w / 2, cy = 17;
        var brass = Theme.Brush("Brass"); var line = new Pen(brass, 1) { DashStyle = new DashStyle(new[] { 3.0, 2.5 }, 0) }; line.Freeze();
        dc.DrawLine(line, new Point(cx, First ? cy : 0), new Point(cx, Last ? cy : h));
        var mist = Hud.Solid("#6F8FB0"); var bg = Hud.Solid("#F6F7F9");
        switch (Kind)
        {
            case RouteNodeKind.Visited:
                var d = new StreamGeometry(); using (var c = d.Open()) { c.BeginFigure(new Point(cx, cy - 5.5), true, true); c.LineTo(new Point(cx + 5.5, cy), true, false); c.LineTo(new Point(cx, cy + 5.5), true, false); c.LineTo(new Point(cx - 5.5, cy), true, false); }
                d.Freeze(); dc.DrawGeometry(brass, null, d); break;
            case RouteNodeKind.Current:
                dc.DrawEllipse(bg, new Pen(Theme.Brush("Accent"), 2), new Point(cx, cy), 5.5, 5.5);
                dc.DrawEllipse(Theme.Brush("Accent"), null, new Point(cx, cy), 2, 2); break;
            case RouteNodeKind.Locatable:
                dc.DrawEllipse(bg, new Pen(mist, 1.4), new Point(cx, cy), 5, 5); break;
            case RouteNodeKind.Blocked:
                dc.DrawEllipse(Theme.Brush("AccentFill"), new Pen(Theme.Brush("Accent"), 1.2), new Point(cx, cy), 6, 6);
                var bang = Hud.Text(this, "!", "NumberFont", 10, Theme.Brush("Accent"), FontWeights.Bold);
                dc.DrawText(bang, new Point(cx - bang.Width / 2, cy - bang.Height / 2)); break;
            default:
                dc.DrawEllipse(bg, new Pen(Hud.Solid("#8D97A2"), 1.2) { DashStyle = new DashStyle(new[] { 1.5, 1.5 }, 0) }, new Point(cx, cy), 5, 5); break;
        }
    }
}

/// <summary>委托转换器（代码侧绑定用）。</summary>
public sealed class FuncConverter(Func<object?, object?> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => convert(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}
