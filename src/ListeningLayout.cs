using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PgrVoice;

public partial class MainWindow
{
    readonly Expander listeningOptions = new() { Header = "收听选项与书签", Margin = new Thickness(0, 4, 0, 4) };
    readonly TextBlock listeningLineCount = new() { FontSize = 11, Foreground = Theme.Brush("MutedText") };
    readonly TextBlock listeningBrowseHint = new() { FontSize = 11, Foreground = Theme.Brush("BranchAccent"),
        TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Button listeningPendingChoice = new() { Content = "打开本节待选互动", Visibility = Visibility.Collapsed,
        BorderBrush = Theme.Brush("BranchAccent"), Foreground = Theme.Brush("BranchAccent"), Margin = new Thickness(0, 3, 5, 3), Padding = new Thickness(9, 6, 9, 6) };
    readonly Button listeningLocate = new() { Content = "定位到选中台词", Margin = new Thickness(0, 3, 5, 3), Padding = new Thickness(9, 6, 9, 6) };
    readonly ScrollViewer listeningCurrentView = new() { MaxHeight = 105,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    void BuildListeningLayout()
    {
        listeningTab = new() { Header = "听书" };
        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto,
            new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new() { Height = height });
        void Row(UIElement element, int row) { Grid.SetRow(element, row); layout.Children.Add(element); }

        var chapter = new StackPanel(); Row(chapter, 0);
        chapter.Children.Add(new TextBlock { Text = "选章即显示台词 · 点击播放开始收听", FontSize = 11,
            Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 0, 0, 5) });
        listeningPacks.DisplayMemberPath = nameof(PackChoice.Title);
        chapter.Children.Add(listeningPacks); chapter.Children.Add(listeningChapters);
        listeningChapters.Margin = new Thickness(0, 4, 0, 0);

        var options = new StackPanel { Margin = new Thickness(0, 5, 6, 5) };
        var open = new WrapPanel(); options.Children.Add(open);
        AddButton(open, "打开所选大章", OpenSelectedListeningChapter);
        AddButton(open, "恢复上回章节", OpenLastListeningChapter);
        AddButton(open, "从当前游戏章开始听", () => { if (engine != null) OpenListeningPack(preferences.PackFile,
            (engine.Pack.Chapters.FirstOrDefault(c => c.Sections.Any(s => s.Id == engine.Current?.SectionId)) ?? engine.Pack.Chapters.FirstOrDefault())?.Id); });
        options.Children.Add(listeningResume);
        AddHeading(options, "遇到支线");
        listeningPolicy.ItemsSource = new[] { "手动选择：遇到支线暂停", "默认第一项：按首个可用选项继续", "全部听取：依次听互斥及条件路线" };
        listeningPolicy.SelectedIndex = 0; options.Children.Add(listeningPolicy);
        options.Children.Add(new TextBlock { Text = "手动选择会在支线和互动处暂停；默认第一项自动继续；全部听取依次播放所有选项（含互动及互斥路线）。未接入导航的补充片段另行标注。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 4) });
        AddHeading(options, "听书书签"); options.Children.Add(listeningBookmarkName);
        AddButton(options, "记下本句", AddListeningBookmark); options.Children.Add(listeningBookmarks);
        var bookmarks = new WrapPanel(); options.Children.Add(bookmarks);
        AddButton(bookmarks, "恢复选中书签", RestoreListeningBookmark); AddButton(bookmarks, "删除选中书签", DeleteListeningBookmark);
        listeningOptions.Content = new ScrollViewer { Content = options, MaxHeight = 155,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Row(listeningOptions, 1);

        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 5) }; Row(section, 2);
        var sectionRow = new DockPanel(); section.Children.Add(sectionRow);
        var sectionActions = new StackPanel(); DockPanel.SetDock(sectionActions, Dock.Right);
        AddButton(sectionActions, "定位到小节开头", () => { if (listeningSections.SelectedItem is ListeningEntry x) MoveListening(() => listeningSession!.SeekSection(x.Id)); });
        sectionRow.Children.Add(sectionActions); sectionRow.Children.Add(listeningSections);
        section.Children.Add(listeningLineCount);
        section.Children.Add(listeningBrowseHint);

        listeningLines.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        listeningLines.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        listeningLines.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        listeningLines.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ListeningEntry.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 6, 8, 6));
        text.Name = "EntryText";
        var template = new DataTemplate { VisualTree = text };
        var choiceStyle = new DataTrigger { Binding = new Binding(nameof(ListeningEntry.IsChoice)), Value = true };
        choiceStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Brush("BranchAccent"), "EntryText"));
        choiceStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold, "EntryText"));
        template.Triggers.Add(choiceStyle);
        listeningLines.ItemTemplate = template;
        Row(listeningLines, 3);
        var locate = new WrapPanel(); Row(locate, 4);
        locate.Children.Add(listeningLocate); listeningLocate.Click += (_, _) => LocateListeningEntry();
        AddButton(locate, "回到正在听的句子", () => { listeningBrowsingLocation = false; RefreshListening(); });
        locate.Children.Add(listeningPendingChoice); listeningPendingChoice.Click += (_, _) => OpenListeningPendingChoice();

        var current = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        listeningPosition.FontSize = 11; listeningText.FontSize = 14; listeningText.Margin = new Thickness(0, 4, 0, 3);
        current.Children.Add(listeningPosition); current.Children.Add(listeningText); current.Children.Add(listeningChoices);
        listeningCurrentView.Content = current; Row(listeningCurrentView, 5);
        var footer = new StackPanel(); Row(footer, 6);
        var playback = new WrapPanel(); footer.Children.Add(playback);
        listeningPlay.Margin = new Thickness(0, 3, 5, 3); listeningPlay.Padding = new Thickness(9, 6, 9, 6);
        playback.Children.Add(listeningPlay);
        listeningPlay.Click += (_, _) => { if (listeningRunning) PauseListening(); else StartListening(); };
        AddButton(playback, "上一句", () => MoveListening(() => listeningSession!.Previous()));
        AddButton(playback, "下一句", () => MoveListening(() => listeningSession!.MoveNext()));
        AddButton(playback, "从本句开头", () => { PauseListening(); listeningOffset = 0; SaveListeningProgress(); RefreshListening(); });
        AddButton(playback, "退出听书", () => { StopListeningForGame(); Tabs.SelectedItem = StoryTab; });
        listeningStatus.FontSize = 11;
        footer.Children.Add(new ScrollViewer { Content = listeningStatus, MaxHeight = 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        listeningTab.Content = layout;
    }
}
