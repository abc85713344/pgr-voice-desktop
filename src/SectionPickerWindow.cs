using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

internal sealed record SectionPickerChoice(Section Section, string Label);

// 只组织选择界面。每个选择仍返回原始 Section，不修改导航或拼接片段。
internal sealed class SectionPickerWindow : Window
{
    readonly Pack pack;
    readonly Section? current;
    readonly GamepadUiNavigation navigation;
    internal readonly ListBox Groups = new(), Segments = new();
    internal readonly Button OpenButton = new() { Content = "查看台词", MinWidth = 110 };
    internal readonly Button CancelButton = new() { Content = "取消", MinWidth = 80, IsCancel = true };
    internal Section? Result { get; private set; }

    internal SectionPickerWindow(Pack pack, IEnumerable<Section> sections, Section? current)
    {
        this.pack = pack; this.current = current;
        Title = "选择小节"; Width = 790; Height = 560; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Theme.Brush("Bg"); Foreground = Theme.Brush("Fg"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "先选小节，再按台词选择位置", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var lists = new Grid(); lists.ColumnDefinitions.Add(new() { Width = new GridLength(175) }); lists.ColumnDefinitions.Add(new());
        Grid.SetRow(lists, 1); root.Children.Add(lists);
        Groups.Margin = new Thickness(0, 0, 12, 0); Groups.DisplayMemberPath = nameof(SectionDisplayGroup.Title);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SectionPickerChoice.Label)));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(MarginProperty, new Thickness(5, 8, 5, 8));
        Segments.ItemTemplate = new DataTemplate { VisualTree = label };
        foreach (var list in new[] { Groups, Segments })
        {
            ScrollViewer.SetCanContentScroll(list, false); ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.BorderThickness = new Thickness(1); list.BorderBrush = Theme.Brush("Stroke"); list.Padding = new Thickness(4);
        }
        lists.Children.Add(Groups); Grid.SetColumn(Segments, 1); lists.Children.Add(Segments);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        CancelButton.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(CancelButton); footer.Children.Add(OpenButton);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        var groups = SectionDisplay.Groups(sections); Groups.ItemsSource = groups;
        Groups.SelectionChanged += (_, _) => Browse();
        Segments.SelectionChanged += (_, _) => OpenButton.IsEnabled = Segments.SelectedItem is SectionPickerChoice;
        OpenButton.Click += (_, _) => Confirm(); CancelButton.Click += (_, _) => Cancel();
        Groups.SelectedItem = groups.FirstOrDefault(g => g.Sections.Any(s => s.Id == current?.Id)) ?? groups.FirstOrDefault();
        navigation = new GamepadUiNavigation(this); Closed += (_, _) => navigation.Dispose();
        Loaded += (_, _) => Groups.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            else if (e.Key == Key.Enter && Groups.IsKeyboardFocusWithin) { e.Handled = true; Segments.Focus(); }
            else if (e.Key == Key.Enter && Segments.IsKeyboardFocusWithin) { e.Handled = true; Confirm(); }
        };
    }
    void Browse()
    {
        var group = Groups.SelectedItem as SectionDisplayGroup;
        var choices = group?.Sections.Select(s => new SectionPickerChoice(s, SectionDisplay.SegmentLabel(pack, group, s))).ToArray() ?? Array.Empty<SectionPickerChoice>();
        Segments.ItemsSource = choices;
        Segments.SelectedItem = choices.FirstOrDefault(c => c.Section.Id == current?.Id) ?? choices.FirstOrDefault();
        if (Segments.SelectedItem != null) Segments.ScrollIntoView(Segments.SelectedItem);
        OpenButton.IsEnabled = Segments.SelectedItem != null;
    }
    internal void Confirm() { if (Segments.SelectedItem is SectionPickerChoice choice) { Result = choice.Section; DialogResult = true; } }
    internal void Cancel() { Result = null; DialogResult = false; }
    internal void GamepadAction(string action)
    {
        if (action is "back" or "panel") { Cancel(); return; }
        if (action == "confirm") { if (Groups.IsKeyboardFocusWithin) Segments.Focus(); else if (Segments.IsKeyboardFocusWithin) Confirm(); else navigation.ActivateFocused(); }
        else if (action is "up" or "down" or "left" or "right") navigation.Move(action switch { "up" => FocusNavigationDirection.Up, "down" => FocusNavigationDirection.Down, "left" => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right });
    }
}
