using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;

namespace PgrVoice;

internal sealed class LibrarySearchWindow : ExperienceDialog
{
    readonly LibrarySearchIndex index;
    readonly Func<IReadOnlyList<string>> sources;
    internal readonly TextBox Query = new() { MaxLength = 200, ToolTip = "搜索全部已安装章节的角色或台词；多个关键词用空格分开" };
    internal readonly ListBox Results = new();
    internal readonly TextBox Preview = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal readonly Button OpenResult = new() { Content = "在台词页打开（不播放）", IsEnabled = false, Padding = new Thickness(12, 7, 12, 7) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) };
    CancellationTokenSource? search;
    bool closed;
    internal LibrarySearchResult? Result { get; private set; }

    internal LibrarySearchWindow(LibrarySearchIndex index, Func<IReadOnlyList<string>> sources) : base("搜索全部配音章节")
    {
        this.index = index; this.sources = sources; Width = 860; Height = 700;
        var root = new Grid { Margin = new Thickness(18) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(150), GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(new TextBlock { Text = "查找角色或台词", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var bar = new DockPanel(); Grid.SetRow(bar, 1); root.Children.Add(bar);
        var find = new Button { Content = "搜索", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        var refresh = new Button { Content = "重新读取配音库", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        DockPanel.SetDock(refresh, Dock.Right); DockPanel.SetDock(find, Dock.Right); bar.Children.Add(refresh); bar.Children.Add(find); bar.Children.Add(Query);
        status.Text = "只搜索当前配音库。选中结果可预览；打开后仍须确认台词才会播放。";
        Grid.SetRow(status, 2); root.Children.Add(status);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(MarginProperty, new Thickness(6));
        Results.ItemTemplate = new DataTemplate { VisualTree = text };
        ScrollViewer.SetHorizontalScrollBarVisibility(Results, ScrollBarVisibility.Disabled);
        Grid.SetRow(Results, 3); root.Children.Add(Results);
        Preview.Margin = new Thickness(0, 10, 0, 10); Grid.SetRow(Preview, 4); root.Children.Add(Preview);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
        footer.Children.Add(cancel); footer.Children.Add(OpenResult); Grid.SetRow(footer, 5); root.Children.Add(footer); Content = root;
        find.Click += async (_, _) => await RunSearch(); refresh.Click += async (_, _) => await RunSearch(true);
        Query.TextChanged += (_, _) => { search?.Cancel(); Results.ItemsSource = null; Preview.Clear(); OpenResult.IsEnabled = false; status.Text = "输入后点击搜索，或按回车。"; };
        Query.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunSearch(); } };
        Results.SelectionChanged += (_, _) => PreviewSelected();
        // 在结果列表按回车也只预览；明确点击打开按钮才能更改主窗口。
        Results.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; PreviewSelected(); Preview.Focus(); } };
        OpenResult.Click += (_, _) => { if (Results.SelectedItem is LibrarySearchResult selected) { Result = selected; DialogResult = true; } };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => { closed = true; search?.Cancel(); };
        Loaded += (_, _) => Query.Focus();
    }

    internal async Task RunSearch(bool refresh = false)
    {
        search?.Cancel();
        using var request = new CancellationTokenSource(); search = request;
        Results.ItemsSource = null; Preview.Clear(); OpenResult.IsEnabled = false;
        if (string.IsNullOrWhiteSpace(Query.Text)) { status.Text = "先输入要查找的角色或台词。"; search = null; return; }
        try
        {
            string query = Query.Text;
            var progress = new Progress<LibrarySearchProgress>(p =>
            { if (!closed && ReferenceEquals(search, request) && !request.IsCancellationRequested) status.Text = $"正在搜索配音文本：{p.Completed} / {p.Total} 章…"; });
            status.Text = "正在读取配音文本…";
            var report = await index.SearchAsync(sources(), query, request.Token, progress, refresh);
            if (closed || request.IsCancellationRequested || !ReferenceEquals(search, request)) return;
            Results.ItemsSource = report.Results;
            status.Text = $"已搜索 {report.SearchedPacks} 个配音包，找到 {report.TotalMatches} 句。" +
                (report.TotalMatches > report.Results.Count ? $"仅显示前 {report.Results.Count} 句，请增加关键词缩小范围。" : "") +
                (report.Problems.Count > 0 ? $"有 {report.Problems.Count} 个包暂不能读取。" : "");
            status.ToolTip = report.Problems.Count > 0 ? string.Join("\n", report.Problems) : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && ReferenceEquals(search, request)) status.Text = "搜索未完成：" + ex.Message; }
        finally { if (ReferenceEquals(search, request)) search = null; }
    }
    void PreviewSelected()
    {
        OpenResult.IsEnabled = Results.SelectedItem is LibrarySearchResult;
        Preview.Text = Results.SelectedItem is LibrarySearchResult row ?
            $"{row.PackTitle}\n{row.ChapterTitle} · {row.SectionTitle}\n{row.Route}\n{row.Speaker}：{row.Text}\n\n配音包编号：{row.PackId}；台词编号：{row.NodeId}；小节编号：{row.SectionId}\n来源目录：{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(row.PackFile))}\n选中不会更改路线或播放位置。" : "";
    }
}
