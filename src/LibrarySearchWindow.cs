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
    internal readonly Button OpenResult = new() { Content = "在台词页打开（不播放）", IsEnabled = false, Padding = new Thickness(12, 7, 12, 7), Style = Theme.Style("PrimaryButton") };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) };
    CancellationTokenSource? search;
    bool closed;
    internal LibrarySearchResult? Result { get; private set; }

    // 03-A（宽 ≥ 700）：左结果 + 右结构化预览；03-B（窄）：上结果 + 下固定 150 高深石板预览。
    readonly Grid body = new();
    readonly ArchivePaper resultsPaper = new() { ShowRail = false, EmblemSize = 0, TideHeight = 0, ShowOrbits = false, Veil = false };
    readonly ArchivePaper previewPaper = new() { ShowRail = false, EmblemSize = 200, EmblemRight = -40, EmblemBottom = -34, TideHeight = 46, WatermarkSize = 140, WatermarkRight = 18 };
    readonly Border previewHost = new() { BorderThickness = new Thickness(1) };
    readonly TextBlock bandKicker = new() { FontSize = 10.5, Foreground = Hud.Solid("#DCE3EA"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock bandTitle = new() { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly SpeakerPortrait plateAvatar = new() { Width = 40, Height = 40, Auto = true, ShowSerial = false, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock plateSpeaker = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    readonly TextBlock plateText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    readonly Grid table = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock compactLine = new() { Foreground = Hud.Solid("#F2F5F8"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock compactMeta = new() { Foreground = Hud.Solid("#AEB9C4"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock statusIcon = new() { Text = "ⓘ", Foreground = Hud.Solid("#E7C77A"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    FrameworkElement widePreview = null!, compactPreview = null!;

    internal LibrarySearchWindow(LibrarySearchIndex index, Func<IReadOnlyList<string>> sources) : base("搜索全部配音章节")
    {
        this.index = index; this.sources = sources; Width = 900; Height = 680; MinWidth = 560; MinHeight = 440;
        var root = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(new Border { Margin = new Thickness(18, 12, 18, 10), Child = Archive.Header("全库搜索", "LIBRARY SEARCH", "查找角色或台词", "SPEAKER / LINE QUERY") });
        var bar = new DockPanel { Margin = new Thickness(18, 0, 18, 8) }; Grid.SetRow(bar, 1); root.Children.Add(bar);
        var find = new Button { Content = "搜索", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        var refresh = new Button { Content = "重新读取配音库", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        DockPanel.SetDock(refresh, Dock.Right); DockPanel.SetDock(find, Dock.Right); bar.Children.Add(refresh); bar.Children.Add(find); bar.Children.Add(Query);
        status.Text = "只搜索当前配音库。选中结果可预览；打开后仍须确认台词才会播放。";
        status.Foreground = Hud.Solid("#E6EBF0"); status.FontSize = 12; status.Margin = new Thickness(0); status.VerticalAlignment = VerticalAlignment.Center;
        // 状态条：深石板斜切条（空闲 / 读取中 / 进度 / 完成 / 问题 ⓘ / 出错）
        var statusRow = new DockPanel();
        statusRow.Children.Add(new TextBlock { Text = "STATUS", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Hud.Solid("#9AA6B2"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(statusIcon, Dock.Right); statusRow.Children.Add(statusIcon); statusRow.Children.Add(status);
        var statusStrip = new CutFrame { Cut = 10, Corners = CutCorners.TopRight, Background = Theme.Brush("PlateGradient"), BorderBrush = Theme.Brush("Plate"), Margin = new Thickness(18, 0, 18, 10),
            Child = new Border { BorderBrush = Theme.Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 14, 6), Child = statusRow } };
        Grid.SetRow(statusStrip, 2); root.Children.Add(statusStrip);
        Results.Style = Theme.Style("ArchiveList"); Results.Background = System.Windows.Media.Brushes.Transparent;
        Results.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(LibraryResultRow)) };
        ScrollViewer.SetHorizontalScrollBarVisibility(Results, ScrollBarVisibility.Disabled);
        resultsPaper.Child = Results;
        var resultsFrame = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = resultsPaper };
        body.Margin = new Thickness(18, 0, 18, 0);
        body.Children.Add(resultsFrame);
        widePreview = BuildWidePreview(); compactPreview = BuildCompactPreview();
        var previewStack = new Grid(); previewStack.Children.Add(widePreview); previewStack.Children.Add(compactPreview);
        previewHost.Child = previewStack; body.Children.Add(previewHost);
        Grid.SetRow(body, 3); root.Children.Add(body);
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
        actions.Children.Add(cancel); actions.Children.Add(OpenResult); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        footer.Children.Add(Archive.KeyHints(("ENTER", "搜索 / 预览"), ("↑↓", "结果"), ("ESC", "关闭"), ("B", "返回")));
        Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
        // 原文预览仍保留在只读文本框里（可选中复制），由结构化预览承载显示。
        Preview.Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => ApplyWidth(ActualWidth >= 700);
        Loaded += (_, _) => ApplyWidth(ActualWidth >= 700);
        find.Click += async (_, _) => await RunSearch(); refresh.Click += async (_, _) => await RunSearch(true);
        Query.TextChanged += (_, _) => { search?.Cancel(); Results.ItemsSource = null; Preview.Clear(); OpenResult.IsEnabled = false; status.Text = "输入后点击搜索，或按回车。"; ShowPreview(); };
        Query.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunSearch(); } };
        Results.SelectionChanged += (_, _) => PreviewSelected();
        // 在结果列表按回车也只预览；明确点击打开按钮才能更改主窗口。
        Results.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; PreviewSelected(); Preview.Focus(); } };
        OpenResult.Click += (_, _) => { if (Results.SelectedItem is LibrarySearchResult selected) { Result = selected; DialogResult = true; } };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => { closed = true; search?.Cancel(); };
        Loaded += (_, _) => Query.Focus();
        ShowPreview();
    }
    void ApplyWidth(bool wide)
    {
        body.RowDefinitions.Clear(); body.ColumnDefinitions.Clear();
        if (wide)
        {
            body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Min(360, Math.Max(300, ActualWidth * .4))) });
            Grid.SetRow(previewHost, 0); Grid.SetColumn(previewHost, 1); previewHost.Margin = new Thickness(12, 0, 0, 0);
            previewHost.BorderBrush = Theme.Brush("Stroke"); previewHost.Background = null; previewHost.Height = double.NaN;
        }
        else
        {
            body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(previewHost, 1); Grid.SetColumn(previewHost, 0); previewHost.Margin = new Thickness(0, 10, 0, 0);
            previewHost.BorderBrush = Theme.Brush("Plate"); previewHost.Background = Theme.Brush("PlateGradient"); previewHost.Height = 150;
        }
        widePreview.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        compactPreview.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        LibraryResultRow.SetCompact(Results, !wide);
    }
    FrameworkElement BuildWidePreview()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        var art = new Grid { Height = 74, ClipToBounds = true, Background = Theme.Brush("Plate") };
        art.Children.Add(new Border { Background = Archive.Stripes("#2A3139", "#323A43", 8) });
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10) };
        caption.Children.Add(bandKicker); caption.Children.Add(bandTitle); art.Children.Add(caption);
        art.Children.Add(new Border { Height = 3, Width = 48, Background = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(new CutFrame { Cut = 10, Corners = CutCorners.TopRight, ClipContent = true, BorderBrush = Theme.Brush("Plate"), Child = art });
        var plate = new DockPanel();
        DockPanel.SetDock(plateAvatar, Dock.Left); plate.Children.Add(plateAvatar);
        var lines = new StackPanel(); lines.Children.Add(plateSpeaker); lines.Children.Add(plateText); plate.Children.Add(lines);
        stack.Children.Add(new Border { Background = Hud.Solid("#F0FFFFFF"), BorderBrush = Theme.Brush("Teal"), BorderThickness = new Thickness(3, 1, 1, 1), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 10, 0, 0), Child = plate });
        table.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); table.ColumnDefinitions.Add(new());
        stack.Children.Add(new Border { Background = Hud.Solid("#E6FFFFFF"), Padding = new Thickness(8, 2, 8, 6), Child = table, Margin = new Thickness(0, 8, 0, 0) });
        var safe = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        safe.Children.Add(Archive.Tag("只读", "teal"));
        safe.Children.Add(new TextBlock { Text = "选中不会更改路线或播放位置。", FontSize = 11.5, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(safe);
        stack.Children.Add(Preview);
        previewPaper.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        return previewPaper;
    }
    FrameworkElement BuildCompactPreview()
    {
        var stack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = "PREVIEW", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Hud.Solid("#9AA6B2") });
        head.Children.Add(new TextBlock { Text = "选中不会更改路线或播放位置", FontSize = 10.5, Foreground = Hud.Solid("#AEB9C4"), Margin = new Thickness(10, 0, 0, 0) });
        stack.Children.Add(head); stack.Children.Add(compactLine); stack.Children.Add(compactMeta);
        return stack;
    }
    void ShowPreview()
    {
        var row = Results.SelectedItem as LibrarySearchResult;
        table.Children.Clear(); table.RowDefinitions.Clear();
        if (row == null)
        {
            bandKicker.Text = "PREVIEW"; bandTitle.Text = "选中结果后在此预览";
            plateSpeaker.Text = ""; plateText.Text = "尚未选中结果。"; plateAvatar.Visibility = Visibility.Collapsed;
            compactLine.Text = "尚未选中结果。"; compactMeta.Text = ""; previewPaper.Watermark = "";
            return;
        }
        string speaker = row.Speaker.Length == 0 ? "旁白" : row.Speaker;
        bandKicker.Text = row.PackTitle; bandTitle.Text = row.ChapterTitle + " · " + row.SectionTitle;
        plateAvatar.Speaker = row.Speaker; plateAvatar.Visibility = row.Speaker.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        plateSpeaker.Text = speaker; plateText.Text = row.Text;
        int n = 0;
        foreach (var (key, value) in new[] { ("路线", row.Route), ("配音包", row.PackId), ("台词", row.NodeId), ("小节", row.SectionId),
            ("来源目录", System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(row.PackFile)) ?? "") })
        {
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var k = new TextBlock { Text = $"{++n:00}  {key}", FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 4, 12, 0) };
            var v = new TextBlock { Text = value, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(k, n - 1); Grid.SetRow(v, n - 1); Grid.SetColumn(v, 1); table.Children.Add(k); table.Children.Add(v);
        }
        compactLine.Text = speaker + "：" + row.Text; compactMeta.Text = row.ChapterTitle + " · " + row.SectionTitle + "  ·  " + row.Route;
        previewPaper.Watermark = (Results.SelectedIndex + 1).ToString("00");
    }

    internal async Task RunSearch(bool refresh = false)
    {
        search?.Cancel();
        using var request = new CancellationTokenSource(); search = request;
        Results.ItemsSource = null; Preview.Clear(); OpenResult.IsEnabled = false; statusIcon.Visibility = Visibility.Collapsed;
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
            statusIcon.ToolTip = status.ToolTip; statusIcon.Visibility = report.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ShowPreview();
    }
}

/// <summary>03 结果行：小头像 + 「说话人：台词」+ 章节 · 小节 · 路线；窄窗（Compact）时台词单行省略。</summary>
internal sealed class LibraryResultRow : DockPanel
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached("Compact", typeof(bool), typeof(LibraryResultRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as LibraryResultRow)?.Refresh()));
    public static void SetCompact(DependencyObject d, bool value) => d.SetValue(CompactProperty, value);
    public static bool GetCompact(DependencyObject d) => (bool)d.GetValue(CompactProperty);
    readonly SpeakerPortrait avatar = new() { Width = 28, Height = 28, Auto = true, ShowSerial = false, Margin = new Thickness(0, 2, 9, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock line = new() { FontSize = 13 };
    readonly TextBlock meta = new() { FontSize = 10.5, Foreground = Theme.Brush("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    public LibraryResultRow()
    {
        Margin = new Thickness(0, 2, 4, 2);
        SetDock(avatar, Dock.Left); Children.Add(avatar);
        var text = new StackPanel(); text.Children.Add(line); text.Children.Add(meta); Children.Add(text);
        DataContextChanged += (_, _) => Refresh();
    }
    void Refresh()
    {
        if (DataContext is not LibrarySearchResult row) return;
        bool compact = GetCompact(this);
        avatar.Speaker = row.Speaker; avatar.Visibility = row.Speaker.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        line.Text = (row.Speaker.Length == 0 ? "旁白" : row.Speaker) + "：" + row.Text;
        line.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap; line.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        meta.Text = row.PackTitle + " · " + row.SectionTitle + (compact ? "" : "  ·  " + row.Route);
        avatar.Width = avatar.Height = compact ? 22 : 28;
    }
}
