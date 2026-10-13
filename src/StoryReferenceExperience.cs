using System;
using System.Linq;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PgrVoice.AndroidApp;

namespace PgrVoice;

public partial class MainWindow
{
    void StoryReferenceClick(object sender, RoutedEventArgs e)
    {
        var pack = engine?.Pack;
        var node = StoryTab.IsSelected && LinesList.SelectedItem is LineRow row ? row.Node : engine?.Current;
        if (pack == null || node == null)
        { Tell("请先打开章节，并在台词目录选中要查看的台词。"); return; }
        string detail = BundledStoryReference.TryGetNode(pack, node, out var reference) && reference != null
            ? reference.LocationText + "\n\n" + reference.DetailText
            : "这条台词暂未与内置资料准确对应。可查看本小节资料核对；资料中的待核连接不能作为自动续播依据。";
        if (!string.IsNullOrWhiteSpace(node.Text))
            detail = (string.IsNullOrWhiteSpace(node.Speaker) ? "" : node.Speaker + "：") + node.Text + "\n\n" + detail;
        // 只读快照。查看、查找和关闭都不提交定位、不请求播放、不保存新进度。
        string chapterTitle = PackChoice.ChapterTitle(pack.Title);
        var sections = pack.Chapters.SelectMany(c => c.Sections).ToList();
        int sectionIndex = sections.FindIndex(s => s.Id == node.SectionId);
        string chapterNumber = ChapterCover.DisplayNumber(ChapterCover.Split(chapterTitle).Number);
        string archiveNo = "PGR · " + (chapterNumber.Length > 0 ? chapterNumber : "--") + (sectionIndex >= 0 ? "-" + (sectionIndex + 1) : "") + " · " + node.Id;
        Action<Action<ImageSource?>>? cover = null;
        if (preferences.ShowChapterCovers)
            cover = apply =>
            {
                string? path = chapterCovers.Find(chapterTitle, pack.Id, SectionDisplayTitle(node.SectionId, pack), node.SectionId, PackFolderName(pack.Root), ChapterCatalog.Category(pack.Id, chapterTitle));
                if (path == null) apply(null); else LoadCharacterImage(path, CoverDecodeHeight, image => apply(image));
            };
        ShowExperienceDialog(new StoryReferenceWindow(BundledStoryReference.GetSummary(pack), detail,
            BundledStoryReference.GetSectionText(pack, node.SectionId), chapterTitle, chapterNumber, archiveNo, cover));
    }
}

internal sealed class StoryReferenceWindow : ExperienceDialog
{
    internal readonly TextBox Reader = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        FontSize = 14, Padding = new Thickness(16, 12, 16, 30), Margin = new Thickness(0),
        Background = Brushes.Transparent, BorderThickness = new Thickness(0)
    };
    internal readonly TextBox Search = new() { MinWidth = 150, Width = 210, Margin = new Thickness(0, 0, 8, 0) };
    internal readonly Button CurrentButton = new() { Content = "当前台词与路线" };
    internal readonly Button SectionButton = new() { Content = "本小节全文" };
    readonly TextBlock result = new() { Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap };

    internal StoryReferenceWindow(string summary, string current, string section, string chapterTitle = "", string chapterNumber = "", string archiveNo = "",
        Action<Action<ImageSource?>>? coverLoader = null) : base("内置剧情文本")
    {
        Width = 860; Height = 700; MinWidth = 600; MinHeight = 460;
        // 04-A6：头部（章节标题 + 统计 + 本章封面淡入 + 镂空章号）→ 深石板页签 → A3 档案纸面正文（F1b 220px）→ 页脚查找。
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new Grid { ClipToBounds = true, MinHeight = 104 };
        var cover = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right, Width = 360, Opacity = .55,
            OpacityMask = new LinearGradientBrush(Colors.Transparent, Colors.Black, 0) };
        head.Children.Add(cover);
        head.Children.Add(new OutlinedText { Text = chapterNumber, FontSize = 92, Stroke = Hud.Solid("#664F6B8A"), StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -14, 22, 0) });
        var headText = new StackPanel { Margin = new Thickness(18, 12, 160, 10) };
        headText.Children.Add(Archive.Header("剧情资料", "STORY REFERENCE", chapterTitle.Length > 0 ? chapterTitle : "内置剧情文本", "READ ONLY"));
        var summaryText = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 6, 0, 0) };
        var hintText = new TextBlock { Text = "游戏节点与稿内编号分别标明；查看资料不会播放或改变当前位置。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 2, 0, 0) };
        headText.Children.Add(summaryText); headText.Children.Add(hintText);
        // 矮窗口（< 600）收起两行说明，把高度让给正文；说明改为页眉悬停提示。
        SizeChanged += (_, _) =>
        {
            bool tight = ActualHeight < 600;
            summaryText.Visibility = hintText.Visibility = tight ? Visibility.Collapsed : Visibility.Visible;
            head.MinHeight = tight ? 0 : 104;
            head.ToolTip = tight ? summaryText.Text + "\n" + hintText.Text : null;
        };
        head.Children.Add(headText);
        root.Children.Add(head);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var tab in new[] { CurrentButton, SectionButton }) { tab.Template = TabTemplate(); tab.Cursor = Cursors.Hand; tabs.Children.Add(tab); }
        var strip = new DockPanel { Background = Theme.Brush("PlateGradient"), LastChildFill = false };
        strip.Children.Add(tabs);
        var ro = new TextBlock { Text = "READ ONLY", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Hud.Solid("#9AA6B2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(ro, Dock.Right); strip.Children.Add(ro);
        Grid.SetRow(strip, 1); root.Children.Add(strip);
        var dossier = Archive.Strip("ARCHIVE", "档案", "Read only", archiveNo);
        var page = new DockPanel(); DockPanel.SetDock(dossier, Dock.Top); page.Children.Add(dossier); page.Children.Add(Reader);
        var paper = new ArchivePaper { Child = page, DecorTop = 26, Watermark = chapterNumber, WatermarkSize = 220, WatermarkRight = 250, WatermarkFaint = true,
            EmblemSize = 220, EmblemTop = 34, EmblemRight = 6, RailLabel = "GRAY RAVEN ARCHIVE" };
        var paperFrame = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Margin = new Thickness(18, 12, 18, 0), Child = paper };
        Grid.SetRow(paperFrame, 2); root.Children.Add(paperFrame);
        void Show(string text, Button active)
        {
            Reader.Text = string.IsNullOrWhiteSpace(text) ? "本小节暂无内置资料。" : text; Reader.Select(0, 0); Reader.ScrollToHome(); result.Text = "";
            foreach (var tab in new[] { CurrentButton, SectionButton }) tab.Tag = ReferenceEquals(tab, active) ? "on" : null;
        }
        CurrentButton.Click += (_, _) => Show(current, CurrentButton);
        SectionButton.Click += (_, _) => Show(section, SectionButton);
        var footer = new StackPanel { Margin = new Thickness(18, 10, 18, 14) }; Grid.SetRow(footer, 3); root.Children.Add(footer);
        var searchRow = new DockPanel { LastChildFill = false }; footer.Children.Add(searchRow);
        var find = new Button { Content = "查找下一处", Padding = new Thickness(10, 6, 10, 6) };
        var close = new Button { Content = "关闭", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        searchRow.Children.Add(Search); Search.ToolTip = "在当前资料内查找文字";
        find.Click += (_, _) => FindNext(); searchRow.Children.Add(find);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); searchRow.Children.Add(close);
        var hints = Archive.KeyHints(("ENTER", "查找"), ("ESC", "关闭")); hints.Margin = new Thickness(14, 0, 0, 0); hints.VerticalAlignment = VerticalAlignment.Center;
        searchRow.Children.Add(hints);
        footer.Children.Add(result);
        Search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; FindNext(); } };
        Content = root; Show(current, CurrentButton);
        if (coverLoader != null) Loaded += (_, _) => coverLoader(image => cover.Source = image);
    }

    // 页签：深石板上的文字按钮，选中为白字 + 绯红底边；焦点用雾蓝底边。
    static ControlTemplate TabTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "b");
        border.SetValue(Border.PaddingProperty, new Thickness(14, 8, 14, 7));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 3));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(TextElement.ForegroundProperty, Hud.Solid("#C3CBD4"));
        text.SetValue(TextElement.FontSizeProperty, 12.5);
        border.AppendChild(text);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var on = new Trigger { Property = TagProperty, Value = "on" };
        on.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Accent"), "b"));
        on.Setters.Add(new Setter(Border.BackgroundProperty, Hud.Solid("#22FFFFFF"), "b"));
        on.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White));
        on.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BackgroundProperty, Hud.Solid("#336F8FB0"), "b"));
        template.Triggers.Add(focus);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Hud.Solid("#1AFFFFFF"), "b"));
        template.Triggers.Add(hover);
        template.Triggers.Add(on);
        return template;
    }

    internal bool FindNext()
    {
        string query = Search.Text;
        if (string.IsNullOrWhiteSpace(query)) { result.Text = "输入要查找的文字。"; return false; }
        int start = Math.Min(Reader.Text.Length, Reader.SelectionStart + Reader.SelectionLength);
        int index = Reader.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = Reader.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { result.Text = "当前资料中没有找到这段文字。"; return false; }
        Reader.Select(index, query.Length); Reader.ScrollToLine(Math.Max(0, Reader.GetLineIndexFromCharacterIndex(index)));
        result.Text = "已找到；再次查找可前往下一处。"; return true;
    }
}
