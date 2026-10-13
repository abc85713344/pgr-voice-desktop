using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 设计稿 12：听书「EXPLORE · 探索菜单逐项读」卡。
/// 只读显示：核心计划（core/Listening/ListeningSession）本就把探索菜单展开成「各回环选项按列出顺序读一次，再取出口」，
/// 并在选择点记下 AutoKind = 探索菜单自动读、AutoLabel = 「A → B → C → 出口」；每句的 BranchLabel 末段是所在选项。
/// 本卡据此算出已听 / 正在收听 / 未听，不改变收听计划，也不改 core。
/// </summary>
public partial class MainWindow
{
    readonly CutFrame listeningExploreCard = new() { Visibility = Visibility.Collapsed, Cut = 10, Corners = CutCorners.Diagonal, EdgeWidth = 3, Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 2, 0, 6) };
    /// <summary>「建议先找栗西」：栗西那一项已听完（✓）后隐藏。</summary>
    internal static bool ExploreHintStillUseful(string? hint, IEnumerable<ExploreRow> rows)
    {
        if (string.IsNullOrEmpty(hint) || !hint.StartsWith("建议先找", StringComparison.Ordinal)) return hint != null;
        string who = hint.Substring(4).Trim();
        return who.Length == 0 || !rows.Any(r => !r.Exit && r.State == ExploreState.Done && r.Label.StartsWith(who, StringComparison.Ordinal));
    }
    internal sealed record ExploreRow(string Label, int Lines, int Read, ExploreState State, bool Exit, string? NowLine);
    internal enum ExploreState { Done, Now, Todo }
    internal sealed record ExploreView(string Title, string? Hint, IReadOnlyList<ExploreRow> Rows, int Heard, int Total);

    /// <summary>当前句属于某个探索菜单的自动逐项读时，返回该菜单的逐项状态；否则 null。</summary>
    internal ExploreView? ListeningExploreState()
    {
        // 只在读台词时显示；停在选择点 / 停下卡时让位给选项卡（卡片区不被挤占）。
        var s = listeningSession; if (s?.Current is not { Kind: ListeningItemKind.Line } current) return null;
        int pos = s.Position; var items = s.Items;
        // 取包含当前句的最外层探索场景（设计稿 12 列的是营地人物，人物内的话题菜单也算在该人物名下）。
        foreach (var point in s.ChoicePoints.Where(p => p.Position <= pos && p.Item.Node is { Kind: "choice" })
            .OrderBy(p => p.Item.BranchLabel.Length == 0 ? 0 : p.Item.BranchLabel.Split(" / ").Length).ThenByDescending(p => p.Position))
        {
            var menu = point.Item.Node!; string branch = point.Item.BranchLabel, pre = branch.Length > 0 ? branch + " / " : "";
            var labels = menu.Options.Select(o => o.Label).ToList();
            // 同一探索场景：同小节、同上层路线、选项有重叠的各次菜单（如 29-1 营地每次回来是不同节点）。
            var related = s.ChoicePoints.Where(p => p.Item.Node is { Kind: "choice" } n && p.Item.SectionId == point.Item.SectionId && p.Item.BranchLabel == branch && n.Options.Any(o => labels.Contains(o.Label))).ToList();
            if (!related.Any(p => p.AutoKind == PlaybackLog.Types.Exploration)) continue;
            MenuShapeInfo? shape = SafeShape(menu); if (shape == null) continue;
            foreach (var p in related) foreach (var o in p.Item.Node!.Options) if (!labels.Contains(o.Label)) labels.Add(o.Label);
            int from = related.Min(p => p.Position);
            bool Of(ListeningItem it, string label) => it.BranchLabel == pre + label || it.BranchLabel.StartsWith(pre + label + " / ", StringComparison.Ordinal);
            var ranges = new Dictionary<string, (int Start, int End)>();
            foreach (var label in labels)
            {
                int start = -1, end = -1;
                for (int i = from; i < items.Count; i++)
                {
                    if (Of(items[i], label)) { if (start < 0) start = i; end = i; }
                    else if (start >= 0) break;
                }
                if (start >= 0) ranges[label] = (start, end);
            }
            if (!ranges.Values.Any(r => pos >= r.Start && pos <= r.End)) continue;
            string? nowLine = current.Kind == ListeningItemKind.Line && current.Node != null ? (string.IsNullOrEmpty(current.Node.Speaker) ? "" : current.Node.Speaker + "：") + current.Node.Text : null;
            var rows = new List<ExploreRow>();
            foreach (var label in labels)
            {
                bool exit = shape.Exits.Any(o => o.Label == label);
                if (!ranges.TryGetValue(label, out var r)) { rows.Add(new(label, 0, 0, ExploreState.Todo, exit, null)); continue; }
                int lines = Enumerable.Range(r.Start, r.End - r.Start + 1).Count(i => items[i].Kind == ListeningItemKind.Line);
                int read = Enumerable.Range(r.Start, Math.Max(0, Math.Min(pos, r.End + 1) - r.Start)).Count(i => items[i].Kind == ListeningItemKind.Line);
                var state = pos > r.End ? ExploreState.Done : pos >= r.Start ? ExploreState.Now : ExploreState.Todo;
                rows.Add(new(label, lines, read, state, exit, state == ExploreState.Now ? nowLine : null));
            }
            var loops = rows.Where(x => !x.Exit).ToList();
            if (loops.Count == 0) continue;
            string? hint = related.SelectMany(p => p.Item.Node!.Options).Select(o => o.ConditionNote).FirstOrDefault(n => n.StartsWith("建议先找", StringComparison.Ordinal));
            if (hint != null) hint = hint.Split('（', '(', '；', ';', '，', '。')[0];
            if (!ExploreHintStillUseful(hint, rows)) hint = null; // 复审小问题：建议的人物已听过后不再提示
            return new(BranchMenuWindow.ExploreMenuName(menu.Text, current.SectionTitle), hint, rows, loops.Count(x => x.State == ExploreState.Done), loops.Count);
        }
        return null;
    }

    int ListeningExploreRowCount;
    void RefreshListeningExplore()
    {
        var view = ListeningExploreState();
        ListeningExploreRowCount = view?.Rows.Count ?? 0;
        listeningExploreCard.Visibility = view == null ? Visibility.Collapsed : Visibility.Visible;
        if (view == null) { listeningExploreCard.Child = null; return; }
        listeningExploreCard.Background = Theme.Brush("RouteFill"); listeningExploreCard.BorderBrush = Theme.Brush("Brass"); listeningExploreCard.OutlineBrush = Theme.Brush("BranchStroke");
        listeningExploreCard.BracketBrush = Theme.Brush("Brass"); listeningExploreCard.BracketLength = 8;
        var body = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(Docked047(new TextBlock { Text = "已听 " + view.Heard + " / " + view.Total, FontFamily = Theme.Font("NumberFont"), FontSize = 11, Foreground = Theme.Brush("BrassText"), VerticalAlignment = VerticalAlignment.Center }, Dock.Right));
        head.Children.Add(new TextBlock { Text = "EXPLORE · 探索菜单逐项读", FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BrassText"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = view.Title, FontSize = 14.5, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 1) });
        body.Children.Add(new TextBlock { Text = "每位人物的对话读一次，读完打勾；离开选项最后读。", FontSize = 11.5, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap });
        if (view.Hint != null)
        {
            var hint = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
            hint.Children.Add(Docked047(new System.Windows.Shapes.Path { Data = Icons.Locate, Stroke = Theme.Brush("Brass"), StrokeThickness = 1.2, Width = 13, Height = 13, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
            var words = new TextBlock { FontSize = 11.5, Foreground = Theme.Brush("BrassText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            words.Inlines.Add(new System.Windows.Documents.Run(view.Hint) { FontWeight = FontWeights.Bold });
            words.Inlines.Add(new System.Windows.Documents.Run(" · 只是提示，其他人物不锁定") { Foreground = Hud.Solid("#6B5A36") });
            hint.Children.Add(words);
            hint.Margin = new Thickness(0);
            body.Children.Add(new Border { Background = Hud.Solid("#1AB08A3E"), BorderBrush = Theme.Brush("Brass"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 6, 0, 2), Child = hint });
        }
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var row in view.Rows.Where(r => !r.Exit)) list.Children.Add(ExploreListeningRow(row));
        body.Children.Add(list);
        if (view.Rows.Any(r => r.Exit))
        {
            var then = new DockPanel { Margin = new Thickness(2, 8, 2, 5) };
            then.Children.Add(Docked047(new TextBlock { Text = "THEN · 最后", FontFamily = Theme.Font("LabelFont"), FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Hud.Solid("#8A96A3"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
            then.Children.Add(new Rectangle047 { Height = 1, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(then);
            int rest = view.Rows.Count(r => !r.Exit && r.State != ExploreState.Done);
            foreach (var exit in view.Rows.Where(r => r.Exit))
                body.Children.Add(ExploreListeningExitRow(exit, rest, view.Rows.Where(r => !r.Exit && r.State != ExploreState.Done).Sum(r => r.Lines - r.Read)));
        }
        listeningExploreCard.Child = body;
        System.Windows.Automation.AutomationProperties.SetName(listeningExploreCard, "EXPLORE · 探索菜单逐项读");
    }
    /// <summary>右侧已听标（设计稿 heardMark）：✓ 已听 / ▶ 正在收听 / 未听。</summary>
    internal static FrameworkElement HeardMark(ExploreState state, string? now = null) => state switch
    {
        ExploreState.Done => new TextBlock { Text = "✓ 已听", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Hud.Solid("#2F7F7A"), VerticalAlignment = VerticalAlignment.Center },
        ExploreState.Now => new TextBlock { Text = now ?? "▶ 正在收听", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Hud.Solid("#B33144"), VerticalAlignment = VerticalAlignment.Center },
        _ => new TextBlock { Text = "未听", FontSize = 10.5, Foreground = Hud.Solid("#7A8591"), VerticalAlignment = VerticalAlignment.Center },
    };
    FrameworkElement ExploreListeningRow(ExploreRow row)
    {
        bool done = row.State == ExploreState.Done, now = row.State == ExploreState.Now;
        var dock = new DockPanel();
        dock.Children.Add(Docked047(HeardMark(row.State), Dock.Right));
        dock.Children.Add(Docked047(ExploreStatusIcon(row.State), Dock.Left));
        var words = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = row.Label, FontSize = 13, FontWeight = now ? FontWeights.Bold : done ? FontWeights.Normal : FontWeights.SemiBold, Foreground = done ? Theme.Brush("MutedText") : Theme.Brush("Fg"), TextTrimming = TextTrimming.CharacterEllipsis });
        string sub = now ? row.NowLine ?? "" : done ? (row.Lines > 0 ? row.Lines + " 句 · 已读完" : "已读完") : row.Lines > 0 ? row.Lines + " 句 · 按列出顺序" : "按列出顺序";
        words.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = now ? Theme.Brush("Fg") : Theme.Brush("SubtleText"), TextTrimming = TextTrimming.CharacterEllipsis });
        dock.Children.Add(words);
        return new CutFrame
        {
            Background = done ? Hud.Solid("#EEF3F2") : Brushes.White, BorderBrush = now ? Hud.Solid("#B33144") : Theme.Brush("Stroke"), OutlineBrush = now ? Hud.Solid("#B33144") : Theme.Brush("Stroke"),
            EdgeWidth = now ? 3 : 0, Cut = 7, Corners = CutCorners.BottomRight, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 0, 5), Child = dock,
        };
    }
    FrameworkElement ExploreListeningExitRow(ExploreRow exit, int rest, int restLines)
    {
        bool waiting = exit.State == ExploreState.Todo;
        var dock = new DockPanel();
        FrameworkElement right = waiting ? new TextBlock { Text = "最后读", FontSize = 10.5, Foreground = Hud.Solid("#7A8591"), VerticalAlignment = VerticalAlignment.Center } : HeardMark(exit.State);
        dock.Children.Add(Docked047(right, Dock.Right));
        dock.Children.Add(Docked047(new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,2.5 H2.5 V13.5 H6 M7,8 H14 M11,5 L14,8 L11,11"), Stroke = Hud.Solid("#7F6223"), StrokeThickness = 1.4, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
        dock.Children.Add(Docked047(new TextBlock { Text = "EXIT", FontFamily = Theme.Font("LabelFont"), FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Hud.Solid("#7F6223"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
        var words = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = exit.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string sub = waiting ? "其余 " + rest + " 段读完后自动选择" + (restLines > 0 ? " · " + restLines + " 句" : "") : exit.State == ExploreState.Now ? exit.NowLine ?? "" : (exit.Lines > 0 ? exit.Lines + " 句 · 已读完" : "已读完");
        words.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Theme.Brush("SubtleText"), TextTrimming = TextTrimming.CharacterEllipsis });
        dock.Children.Add(words);
        return new CutFrame
        {
            Background = Hud.Solid("#F3ECDC"), BorderBrush = Theme.Brush("Brass"), OutlineBrush = Hud.Solid("#D3C196"), EdgeWidth = 3, Cut = 7, Corners = CutCorners.BottomRight,
            Padding = new Thickness(9, 5, 9, 5), Opacity = waiting ? .55 : 1, Child = dock,
        };
    }
    static FrameworkElement ExploreStatusIcon(ExploreState state)
    {
        var grid = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        switch (state)
        {
            case ExploreState.Done:
                grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Hud.Solid("#2F7F7A") });
                grid.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,9.5 L8,12.5 L13.5,6.5"), Stroke = Brushes.White, StrokeThickness = 1.8 }); break;
            case ExploreState.Now:
                grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Hud.Solid("#B33144") });
                grid.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,5.5 L13,9 L7,12.5 Z"), Fill = Brushes.White }); break;
            default:
                grid.Children.Add(new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, Stroke = Hud.Solid("#AEB6BF"), StrokeThickness = 1.3, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45) }); break;
        }
        return grid;
    }
}

/// <summary>虚线分隔（THEN · 最后）。</summary>
public sealed class Rectangle047 : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(Hud.Solid("#AEB6BF"), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        dc.DrawLine(pen, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
    }
}
