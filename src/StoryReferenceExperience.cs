using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        ShowExperienceDialog(new StoryReferenceWindow(BundledStoryReference.GetSummary(pack), detail,
            BundledStoryReference.GetSectionText(pack, node.SectionId)));
    }
}

internal sealed class StoryReferenceWindow : ExperienceDialog
{
    internal readonly TextBox Reader = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        FontSize = 14, Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 8)
    };
    internal readonly TextBox Search = new() { MinWidth = 150, Width = 210, Margin = new Thickness(0, 0, 8, 0) };
    internal readonly Button CurrentButton = new() { Content = "当前台词与路线", Padding = new Thickness(10, 6, 10, 6) };
    internal readonly Button SectionButton = new() { Content = "本小节全文", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock result = new() { Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap };

    internal StoryReferenceWindow(string summary, string current, string section) : base("内置剧情文本")
    {
        Width = 800; Height = 680;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        header.Children.Add(new TextBlock { Text = "游戏节点与稿内编号分别标明；查看资料不会播放或改变当前位置。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var tabs = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        tabs.Children.Add(CurrentButton); tabs.Children.Add(SectionButton); header.Children.Add(tabs);
        void Show(string text) { Reader.Text = string.IsNullOrWhiteSpace(text) ? "本小节暂无内置资料。" : text; Reader.Select(0, 0); Reader.ScrollToHome(); result.Text = ""; }
        CurrentButton.Click += (_, _) => Show(current);
        SectionButton.Click += (_, _) => Show(section);
        Grid.SetRow(Reader, 1); root.Children.Add(Reader);
        var footer = new StackPanel(); Grid.SetRow(footer, 2); root.Children.Add(footer);
        var searchRow = new WrapPanel(); footer.Children.Add(searchRow);
        searchRow.Children.Add(Search); Search.ToolTip = "在当前资料内查找文字";
        var find = new Button { Content = "查找下一处", Padding = new Thickness(10, 6, 10, 6) };
        find.Click += (_, _) => FindNext(); searchRow.Children.Add(find);
        var close = new Button { Content = "关闭", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close(); searchRow.Children.Add(close); footer.Children.Add(result);
        Search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; FindNext(); } };
        Content = root; Show(current);
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
