using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PgrVoice;

public partial class MainWindow
{
    readonly Expander listeningOptions = new() { Header = "收听设置与书签", Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock listeningLineCount = new() { FontSize = 11, Foreground = Theme.Brush("MutedText") };
    readonly TextBlock listeningBrowseHint = new() { FontSize = 11, Foreground = Theme.Brush("BranchAccent"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Button listeningPendingChoice = new() { Content = "打开本节待选互动", Visibility = Visibility.Collapsed, BorderBrush = Theme.Brush("BranchAccent"), Foreground = Theme.Brush("BranchAccent"), Margin = new Thickness(5, 0, 0, 0) };
    readonly Button listeningRechoose = new() { Content = "重选分支", IsEnabled = false, ToolTip = "查看本小节已选过的分支，重新打开后再选择路线。", Margin = new Thickness(5, 0, 0, 0) };
    readonly Button listeningLocate = new() { Content = "定位到选中台词", Margin = new Thickness(0, 0, 5, 0) };
    readonly ScrollViewer listeningCurrentView = new() { MaxHeight = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Border listeningPlayerCard = new() { Background = Theme.Brush("Inset"), BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 10) };

    void BuildListeningLayout()
    {
        SizeChanged += (_, _) => UpdateListeningViewport();
        listeningTab = new() { Header = "听书" };
        var layout = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) layout.RowDefinitions.Add(new() { Height = height });
        void Row(UIElement element, int row) { Grid.SetRow(element, row); layout.Children.Add(element); }

        // 当前收听和控制固定在上方，浏览目录不会把播放位置挤到页面底端。
        var player = new StackPanel(); listeningPlayerCard.Child = player; Row(listeningPlayerCard, 0);
        listeningPosition.FontSize = 11; listeningPosition.Foreground = Theme.Brush("MutedText");
        listeningPosition.TextWrapping = TextWrapping.NoWrap; listeningPosition.TextTrimming = TextTrimming.CharacterEllipsis;
        listeningPosition.SetBinding(ToolTipProperty, new Binding("Text") { Source = listeningPosition });
        player.Children.Add(listeningPosition);
        var current = new StackPanel();
        listeningText.FontSize = 16; listeningText.Margin = new Thickness(0, 7, 0, 7);
        current.Children.Add(listeningText); current.Children.Add(listeningChoices);
        listeningCurrentView.Content = current; player.Children.Add(listeningCurrentView);
        var playback = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        playback.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        playback.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        playback.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var mainControls = new StackPanel { Orientation = Orientation.Horizontal };
        AddButton(mainControls, "上一句", () => MoveListening(() => listeningSession!.Previous()));
        listeningPlay.Style = (Style)FindResource("PrimaryButton"); listeningPlay.MinWidth = 116;
        listeningPlay.Margin = new Thickness(5, 0, 5, 0); listeningPlay.Padding = new Thickness(12, 6, 12, 6);
        mainControls.Children.Add(listeningPlay);
        listeningPlay.Click += (_, _) => { if (listeningRunning || listeningPreviewTicket != 0) PauseListening(); else StartListening(); };
        AddButton(mainControls, "下一句", () => MoveListening(() => listeningSession!.MoveNext()));
        playback.Children.Add(mainControls);
        Grid.SetColumn(listeningRechoose, 2); playback.Children.Add(listeningRechoose);
        listeningRechoose.Click += (_, _) => ShowListeningRechoices(); player.Children.Add(playback);
        var status = new Grid();
        status.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        listeningRemainingTime.Margin = new Thickness(0, 0, 12, 0); listeningRemainingTime.FontSize = 10;
        listeningStatus.FontSize = 10; Grid.SetColumn(listeningStatus, 1);
        status.Children.Add(listeningRemainingTime); status.Children.Add(listeningStatus);
        player.Children.Add(new ScrollViewer { Content = status, MaxHeight = 28, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // 章、小节、台词属于同一浏览区，只有明确定位才移动收听位置。
        var chapter = new StackPanel(); Row(chapter, 1);
        listeningPacks.DisplayMemberPath = nameof(PackChoice.Title); listeningPacks.GroupStyle.Add(ChapterGroupStyle());
        chapter.Children.Add(listeningPacks); chapter.Children.Add(ChapterPickerControls(listeningPacks)); chapter.Children.Add(listeningChapters);
        listeningChapters.Margin = new Thickness(0, 4, 0, 0);
        var section = new StackPanel { Margin = new Thickness(0, 5, 0, 5) }; Row(section, 2);
        var sectionRow = new DockPanel(); section.Children.Add(sectionRow);
        var sectionActions = new StackPanel(); DockPanel.SetDock(sectionActions, Dock.Right);
        AddButton(sectionActions, "定位到小节开头", () => { if (listeningSections.SelectedItem is ListeningEntry x) MoveListening(() => listeningSession!.SeekSection(x.Id)); });
        sectionRow.Children.Add(sectionActions); sectionRow.Children.Add(listeningSections); sectionRow.Children.Add(SectionPickerControl(listeningSections));
        listeningLineCount.Margin = new Thickness(0, 4, 0, 0);
        section.Children.Add(listeningLineCount); section.Children.Add(listeningBrowseHint);
        listeningLines.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        listeningLines.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        listeningLines.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        listeningLines.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ListeningEntry.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 8, 9, 8)); text.Name = "EntryText";
        var template = new DataTemplate { VisualTree = text };
        var choiceStyle = new DataTrigger { Binding = new Binding(nameof(ListeningEntry.IsChoice)), Value = true };
        choiceStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Brush("BranchAccent"), "EntryText"));
        choiceStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold, "EntryText"));
        template.Triggers.Add(choiceStyle); listeningLines.ItemTemplate = template; Row(listeningLines, 3);
        var locate = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; Row(locate, 4);
        locate.Children.Add(listeningLocate); listeningLocate.Click += (_, _) => LocateListeningEntry();
        AddButton(locate, "回到正在听的句子", () => { listeningBrowsingLocation = false; RefreshListening(); });
        locate.Children.Add(listeningPendingChoice); listeningPendingChoice.Click += (_, _) => OpenListeningPendingChoice();

        // 次要操作收进一个可滚动区域，避免与主播放按钮混排。
        var options = new StackPanel { Margin = new Thickness(0, 5, 6, 5) };
        var actions = new WrapPanel(); options.Children.Add(actions);
        AddButton(actions, "从本句开头", () => { PauseListening(); listeningOffset = 0; MarkExplicitListeningPosition(); SaveListeningProgress(); RefreshListening(); });
        AddButton(actions, "反馈当前句", FeedbackListeningLine);
        AddButton(actions, "退出听书", () => { StopListeningForGame(); Tabs.SelectedItem = StoryTab; });
        AddHeading(options, "遇到支线");
        listeningPolicy.ItemsSource = new[] { "手动选择：遇到支线暂停", "默认第一项：按首个可用选项继续", "全部听取：依次听互斥及条件路线" };
        listeningPolicy.SelectedIndex = 0; options.Children.Add(listeningPolicy);
        options.Children.Add(new TextBlock { Text = "选过的路线可通过上方“重选分支”更改。打开选项先暂停，确认后再播放。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 5, 0, 4) });
        AddHeading(options, "听书书签"); options.Children.Add(listeningBookmarkName);
        AddButton(options, "记下本句", AddListeningBookmark); options.Children.Add(listeningBookmarks);
        var bookmarks = new WrapPanel(); options.Children.Add(bookmarks);
        AddButton(bookmarks, "恢复选中书签", RestoreListeningBookmark); AddButton(bookmarks, "删除选中书签", DeleteListeningBookmark);
        AddHeading(options, "恢复收听");
        var open = new WrapPanel(); options.Children.Add(open);
        AddButton(open, "打开所选大章", OpenSelectedListeningChapter);
        AddButton(open, "恢复上回章节", OpenLastListeningChapter);
        AddButton(open, "从当前游戏章开始听", () => { if (engine != null) OpenListeningPack(preferences.PackFile, (engine.Pack.Chapters.FirstOrDefault(c => c.Sections.Any(s => s.Id == engine.Current?.SectionId)) ?? engine.Pack.Chapters.FirstOrDefault())?.Id); });
        options.Children.Add(listeningResume);
        smartListeningResume.IsChecked = preferences.SmartListeningResume;
        smartListeningResume.Checked += (_, _) => { preferences.SmartListeningResume = true; Save(); };
        smartListeningResume.Unchecked += (_, _) => { preferences.SmartListeningResume = false; Save(); };
        options.Children.Add(smartListeningResume);
        listeningOptions.Content = new ScrollViewer { Content = options, MaxHeight = 155, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Row(listeningOptions, 5); listeningTab.Content = layout;
    }

    void UpdateListeningViewport()
    {
        bool compact = Height < 680;
        listeningCurrentView.MaxHeight = listeningSession?.Current?.Kind == PgrVoice.Listening.ListeningItemKind.Choice
            ? (compact ? 105 : 175) : (compact ? 56 : 84);
        listeningPlayerCard.Padding = new Thickness(12, compact ? 6 : 9, 12, compact ? 6 : 9);
        listeningPlayerCard.Margin = new Thickness(0, 0, 0, compact ? 6 : 10);
        listeningText.Margin = new Thickness(0, compact ? 4 : 7, 0, compact ? 4 : 7);
        listeningOptions.Margin = new Thickness(0, compact ? 2 : 6, 0, 0);
    }
}
