using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    ChapterPickerWindow? chapterPicker;
    string chapterLibraryFolder = "";
    Action<ChapterPickerWindow>? chapterPickerTest;
    readonly Dictionary<ComboBox, (Button Category, Button Chapter)> chapterPickerButtons = new();

    Grid ChapterPickerControls(ComboBox model)
    {
        model.Visibility = Visibility.Collapsed;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
        var category = new Button { MinWidth = 100, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 6, 8, 6) };
        var categoryText = new TextBlock(); categoryText.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Category") { Source = model, StringFormat = "分类：{0}", TargetNullValue = "分类", FallbackValue = "分类" }); category.Content = categoryText;
        var chapter = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6) };
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        title.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.DisplayTitle") { Source = model, TargetNullValue = "选择章节", FallbackValue = "选择章节" }); chapter.Content = title;
        category.SetBinding(AutomationProperties.NameProperty, new Binding("Text") { Source = categoryText });
        chapter.SetBinding(AutomationProperties.NameProperty, new Binding("Text") { Source = title, StringFormat = "选择章节：{0}" });
        foreach (var button in new[] { category, chapter }) button.SetBinding(IsEnabledProperty, new Binding("HasItems") { Source = model });
        category.Click += (_, _) => OpenChapterPicker(model, true);
        chapter.Click += (_, _) => OpenChapterPicker(model, false);
        chapterPickerButtons[model] = (category, chapter);
        grid.Children.Add(category); Grid.SetColumn(chapter, 1); grid.Children.Add(chapter);
        return grid;
    }

    void OpenChapterPicker(ComboBox model, bool focusCategory)
    {
        if (chapterPicker != null) { chapterPicker.Activate(); return; }
        RefreshChapterLibrary();
        if (ReferenceEquals(model, listeningPacks)) RefreshListeningPacks();
        var choices = model.Items.OfType<PackChoice>().ToList();
        if (choices.Count == 0) return;
        var picker = new ChapterPickerWindow(choices, model.SelectedItem as PackChoice, focusCategory) { Owner = this };
        chapterPicker = picker;
        if (testUi && chapterPickerTest != null) picker.Loaded += (_, _) => chapterPickerTest(picker);
        try
        {
            if (picker.ShowDialog() == true && picker.Result is { } chosen && !ReferenceEquals(model.SelectedItem, chosen))
                model.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, chosen);
        }
        finally { chapterPicker = null; ResetGamepadContext(); }
    }

    void RefreshChapterLibrary()
    {
        if (chapterLibraryFolder.Length == 0) return;
        try
        {
            // 开窗时只重读目录；复用原条目身份，不触发选章事务或重新载入当前包。
            var before = LibraryBox.Items.OfType<PackChoice>().ToList();
            var previous = LibraryBox.SelectedItem as PackChoice;
            var existing = before.GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var refreshed = LibraryPaths.Packs(chapterLibraryFolder)
                .Select(file => existing.TryGetValue(file, out var choice) ? choice : ReadPackChoice(file)).ToList();
            // 当前包即使刚被移动，仍保留已载入的选择和播放位置，等用户明确切换。
            if (previous != null && !refreshed.Any(p => String.Equals(p.File, previous.File, StringComparison.OrdinalIgnoreCase)))
                refreshed.Add(previous);
            if (refreshed.Count == before.Count && before.All(refreshed.Contains)) return;
            bool wasSelecting = selectingLibrary;
            selectingLibrary = true;
            try
            {
                LibraryBox.ItemsSource = GroupedChapters(refreshed);
                LibraryBox.SelectedItem = previous;
            }
            finally { selectingLibrary = wasSelecting; }
        }
        catch (Exception ex)
        {
            Log.Write("library-refresh", ex.ToString());
            Tell("章节目录暂时无法刷新，继续显示原有章节。");
        }
    }

    static ListCollectionView GroupedChapters(IEnumerable<PackChoice> choices)
    {
        var ordered = choices.OrderBy(p => ChapterCatalog.SortKey(p.PackId, p.Title, p.SortOrder), System.StringComparer.Ordinal).ToList();
        var view = new ListCollectionView(ordered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackChoice.Category)));
        return view;
    }

    static GroupStyle ChapterGroupStyle()
    {
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetValue(TextBlock.ForegroundProperty, Theme.Brush("NormalAccent"));
        label.SetValue(TextBlock.MarginProperty, new Thickness(10, 12, 10, 5));
        return new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = label } };
    }
}
