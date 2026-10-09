using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

// 浏览状态只属于此窗口；关闭、切类、滚轮都不能提交到播放器。
internal sealed class ChapterPickerWindow : Window
{
    readonly List<PackChoice> choices;
    readonly PackChoice? current;
    readonly GamepadUiNavigation navigation;
    internal readonly ListBox Categories = new(), Chapters = new();
    internal readonly TextBlock EmptyMessage = new() { Text = "暂无章节", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    internal readonly Button OpenButton = new() { Content = "打开章节", MinWidth = 110 };
    internal readonly Button CancelButton = new() { Content = "取消", MinWidth = 80, IsCancel = true };
    internal PackChoice? Result { get; private set; }

    internal ChapterPickerWindow(IEnumerable<PackChoice> choices, PackChoice? current, bool focusCategory)
    {
        this.choices = choices.OrderBy(p => ChapterCatalog.SortKey(p.PackId, p.Title, p.SortOrder), StringComparer.Ordinal).ToList();
        this.current = current;
        Title = "选择章节"; Width = 660; Height = 520; MinWidth = 470; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Bg"); Foreground = Theme.Brush("Fg"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "先选分类，再选章节", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var lists = new Grid(); lists.ColumnDefinitions.Add(new() { Width = new GridLength(150) }); lists.ColumnDefinitions.Add(new());
        Grid.SetRow(lists, 1); root.Children.Add(lists);
        Categories.Margin = new Thickness(0, 0, 12, 0);
        Chapters.DisplayMemberPath = nameof(PackChoice.Title);
        foreach (var list in new[] { Categories, Chapters })
        {
            ScrollViewer.SetCanContentScroll(list, false);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.BorderThickness = new Thickness(1); list.BorderBrush = Theme.Brush("Stroke");
            list.Padding = new Thickness(4);
        }
        lists.Children.Add(Categories); Grid.SetColumn(Chapters, 1); lists.Children.Add(Chapters);
        Grid.SetColumn(EmptyMessage, 1); lists.Children.Add(EmptyMessage);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        CancelButton.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(CancelButton); actions.Children.Add(OpenButton);
        footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Categories.ItemsSource = ChapterCatalog.Categories.Concat(this.choices.Select(p => p.Category)).Distinct().OrderBy(ChapterCatalog.CategoryOrder).ToArray();
        Categories.SelectionChanged += (_, _) => BrowseCategory();
        Chapters.SelectionChanged += (_, _) => OpenButton.IsEnabled = Chapters.SelectedItem is PackChoice;
        OpenButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => Cancel();
        Categories.SelectedItem = current?.Category;
        if (Categories.SelectedIndex < 0) Categories.SelectedIndex = 0;
        navigation = new GamepadUiNavigation(this);
        Closed += (_, _) => navigation.Dispose();
        Loaded += (_, _) => { if (focusCategory) Categories.Focus(); else Chapters.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            else if (e.Key == Key.Enter && Categories.IsKeyboardFocusWithin) { e.Handled = true; Chapters.Focus(); }
            else if (e.Key == Key.Enter && Chapters.IsKeyboardFocusWithin) { e.Handled = true; Confirm(); }
        };
    }

    void BrowseCategory()
    {
        var filtered = choices.Where(p => p.Category == Categories.SelectedItem as string).ToList();
        Chapters.ItemsSource = filtered;
        Chapters.SelectedItem = filtered.FirstOrDefault(p => p.File == current?.File);
        if (Chapters.SelectedIndex < 0 && filtered.Count > 0) Chapters.SelectedIndex = 0;
        EmptyMessage.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = Chapters.SelectedItem is PackChoice;
        Chapters.UpdateLayout();
        if (Chapters.SelectedItem != null) Chapters.ScrollIntoView(Chapters.SelectedItem);
    }
    internal void Confirm()
    {
        if (Chapters.SelectedItem is not PackChoice choice) return;
        Result = choice; DialogResult = true;
    }
    internal void Cancel() { Result = null; DialogResult = false; }
    internal void GamepadAction(string action)
    {
        if (action is "back" or "panel") { Cancel(); return; }
        if (action == "confirm")
        {
            if (Categories.IsKeyboardFocusWithin) Chapters.Focus();
            else if (Chapters.IsKeyboardFocusWithin) Confirm();
            else navigation.ActivateFocused();
        }
        else if (action is "up" or "down" or "left" or "right")
            navigation.Move(action switch { "up" => FocusNavigationDirection.Up, "down" => FocusNavigationDirection.Down, "left" => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right });
    }
}
