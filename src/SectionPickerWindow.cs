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
    internal readonly Button OpenButton = new() { Content = "查看台词", MinWidth = 110, Style = Theme.Style("PrimaryButton") };
    internal readonly Button CancelButton = new() { Content = "取消", MinWidth = 80, IsCancel = true };
    internal Section? Result { get; private set; }

    /// <summary>小节封面（本机封面目录，主窗口异步解码）；为空时封面带只显示深石板底纹。</summary>
    internal Action<Section, Action<ImageSource?>>? CoverLoader { get; set; }
    readonly Image bandCover = new() { Stretch = Stretch.UniformToFill, Opacity = 0.9 };
    readonly TextBlock bandKicker = new() { FontSize = 10.5, Foreground = Hud.Solid("#DCE3EA") };
    readonly TextBlock bandTitle = new() { FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly OutlinedText bandNumber = new() { FontSize = 64, Stroke = Hud.Solid("#B3FFFFFF"), StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, -8) };
    readonly StackPanel cast = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock segmentCount = Archive.Count("");
    readonly ArchivePaper segmentPaper = new() { ShowRail = false, EmblemSize = 220, EmblemRight = -46, EmblemBottom = -40, TideHeight = 48, WatermarkSize = 150, WatermarkRight = 24 };
    FrameworkElement band = null!, castRow = null!;
    internal HorizontalStrip CastStrip { get; private set; } = null!;
    ColumnDefinition groupColumn = null!;
    string wantedCover = "";

    internal SectionPickerWindow(Pack pack, IEnumerable<Section> sections, Section? current)
    {
        this.pack = pack; this.current = current;
        Title = "选择小节"; Width = 860; Height = 580; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Theme.ApplyDialog(this);
        // 02-A2（宽）：左小节列表 + 右预览（封面带 → 出场角色细条 → 片段列表）；02-B（窄 < 700）：紧凑双栏，无预览。
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        string chapterTitle = PackChoice.ChapterTitle(pack.Title);
        var head = new Grid { Margin = new Thickness(18, 12, 18, 10) };
        head.Children.Add(Archive.Header("小节目录", "SECTION INDEX", "先选小节，再按台词选择位置", "SECTION  ›  LINE"));
        root.Children.Add(head);
        var context = new DockPanel { Background = Theme.Brush("PlateGradient"), LastChildFill = false };
        context.Children.Add(new TextBlock { Text = chapterTitle, Foreground = Hud.Solid("#E6EBF0"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 6, 8, 7), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        context.Children.Add(new TextBlock { Text = Archive.CategoryEnglish(ChapterCatalog.Category(pack.Id, chapterTitle)), FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Hud.Solid("#9AA6B2"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(context, 1); root.Children.Add(context);
        var body = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        groupColumn = new ColumnDefinition { Width = new GridLength(250) }; body.ColumnDefinitions.Add(groupColumn); body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 2); root.Children.Add(body);
        var groupArea = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var groupHead = Archive.Section("小节", "SECTIONS"); DockPanel.SetDock(groupHead, Dock.Top); groupArea.Children.Add(groupHead);
        Groups.Style = Theme.Style("ArchiveList"); Groups.DisplayMemberPath = nameof(SectionDisplayGroup.Title);
        groupArea.Children.Add(new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = Groups });
        body.Children.Add(groupArea);
        var right = new DockPanel(); Grid.SetColumn(right, 1); body.Children.Add(right);
        band = BuildBand(); DockPanel.SetDock(band, Dock.Top); right.Children.Add(band);
        castRow = BuildCast(); DockPanel.SetDock(castRow, Dock.Top); right.Children.Add(castRow);
        var segmentHead = Archive.Section("片段", "LINES", segmentCount); DockPanel.SetDock(segmentHead, Dock.Top); right.Children.Add(segmentHead);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SectionPickerChoice.Label)));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(MarginProperty, new Thickness(0, 4, 4, 4));
        label.SetValue(TextBlock.FontSizeProperty, 13.0);
        Segments.ItemTemplate = new DataTemplate { VisualTree = label };
        Segments.Style = Theme.Style("ArchiveList"); Segments.Background = Brushes.Transparent;
        segmentPaper.Child = Segments;
        right.Children.Add(new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = segmentPaper });
        foreach (var list in new[] { Groups, Segments })
        {
            ScrollViewer.SetCanContentScroll(list, false); ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        }
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        CancelButton.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(CancelButton); actions.Children.Add(OpenButton);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        footer.Children.Add(Archive.KeyHints(("↑↓", "选择"), ("ENTER", "下一栏 / 查看"), ("ESC", "取消"), ("A", "确认"), ("B", "返回")));
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        SizeChanged += (_, _) => ApplyWidth(ActualWidth >= 700);
        // 矮窗口（< 430）收起页眉，把高度让给小节与片段列表（标题栏已写明“选择小节”）。
        SizeChanged += (_, _) => head.Visibility = ActualHeight < 430 ? Visibility.Collapsed : Visibility.Visible;
        var groups = SectionDisplay.Groups(sections); Groups.ItemsSource = groups;
        Groups.SelectionChanged += (_, _) => Browse();
        Segments.SelectionChanged += (_, _) => OpenButton.IsEnabled = Segments.SelectedItem is SectionPickerChoice;
        OpenButton.Click += (_, _) => Confirm(); CancelButton.Click += (_, _) => Cancel();
        Groups.SelectedItem = groups.FirstOrDefault(g => g.Sections.Any(s => s.Id == current?.Id)) ?? groups.FirstOrDefault();
        navigation = new GamepadUiNavigation(this); Closed += (_, _) => navigation.Dispose();
        Loaded += (_, _) => { ApplyWidth(ActualWidth >= 700); LoadCover(); Groups.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            else if (e.Key == Key.Enter && Groups.IsKeyboardFocusWithin) { e.Handled = true; Segments.Focus(); }
            else if (e.Key == Key.Enter && Segments.IsKeyboardFocusWithin) { e.Handled = true; Confirm(); }
        };
    }
    void ApplyWidth(bool wide)
    {
        // 窄窗：收起封面带与出场角色，片段纸面只保留底纹（无大徽记），小节栏变窄。
        band.Visibility = castRow.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        groupColumn.Width = new GridLength(wide ? 250 : 175);
        segmentPaper.EmblemSize = wide ? 220 : 0; segmentPaper.TideHeight = wide ? 48 : 0; segmentPaper.Watermark = wide ? bandNumber.Text : "";
    }
    FrameworkElement BuildBand()
    {
        var art = new Grid { Height = 96, ClipToBounds = true, Background = Theme.Brush("Plate") };
        art.Children.Add(new Border { Background = Archive.Stripes("#2A3139", "#323A43", 8) });
        art.Children.Add(bandCover);
        art.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(235, 20, 26, 32), Color.FromArgb(60, 20, 26, 32), 0) });
        art.Children.Add(bandNumber);
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 90, 12) };
        caption.Children.Add(bandKicker); caption.Children.Add(bandTitle); art.Children.Add(caption);
        art.Children.Add(new Border { Height = 3, Width = 56, Background = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        return new CutFrame { Cut = 12, Corners = CutCorners.TopRight, ClipContent = true, BorderBrush = Theme.Brush("Plate"), Child = art, Margin = new Thickness(0, 0, 0, 8) };
    }
    FrameworkElement BuildCast()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        label.Children.Add(new TextBlock { Text = "出场角色", FontSize = 11.5, FontWeight = FontWeights.SemiBold });
        label.Children.Add(new TextBlock { Text = "CAST", FontFamily = Theme.Font("LabelFont"), FontSize = 8.5, Foreground = Theme.Brush("SubtleText") });
        DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
        // 出场角色最多 10 人，窄窗口放不下时可横向滚动（滚轮 / 触控板 / 触摸 / 两端箭头）。
        CastStrip = new HorizontalStrip(cast, dark: false);
        row.Children.Add(CastStrip);
        return new Border { Background = Hud.Solid("#F2F4F6"), BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = row };
    }
    void ShowGroup(SectionDisplayGroup? group, int count)
    {
        int index = Groups.SelectedIndex;
        bandKicker.Text = PackChoice.ChapterTitle(pack.Title) + (index >= 0 ? $"  ·  SECTION {index + 1:00}" : "");
        bandTitle.Text = group?.Title ?? "";
        bandNumber.Text = index >= 0 ? (index + 1).ToString("00") : "";
        segmentPaper.Watermark = band.Visibility == Visibility.Visible ? bandNumber.Text : "";
        segmentCount.Text = $"本小节共 {count} 段";
        cast.Children.Clear(); CastStrip.Viewer.ScrollToHorizontalOffset(0);
        if (group != null)
        {
            var ids = new HashSet<string>(group.Sections.Select(s => s.Id));
            foreach (string speaker in pack.Nodes.Where(n => n.Kind == "line" && ids.Contains(n.SectionId) && n.Speaker.Trim().Length > 0)
                .Select(n => n.Speaker.Trim()).Distinct().Take(10))
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), ToolTip = speaker };
                item.Children.Add(new SpeakerPortrait { Speaker = speaker, Auto = true, ShowSerial = false, Width = 26, Height = 26, Margin = new Thickness(0, 0, 5, 0) });
                item.Children.Add(new TextBlock { Text = speaker, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                cast.Children.Add(item);
            }
            if (cast.Children.Count == 0) cast.Children.Add(new TextBlock { Text = "本小节没有标注说话人", FontSize = 11.5, Foreground = Theme.Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center });
        }
        LoadCover();
    }
    void LoadCover()
    {
        var section = (Groups.SelectedItem as SectionDisplayGroup)?.Sections.FirstOrDefault();
        string key = section?.Id ?? "";
        if (CoverLoader == null || !IsLoaded || key == wantedCover) return;
        wantedCover = key;
        if (section == null) { bandCover.Source = null; return; }
        CoverLoader(section, image => { if (wantedCover == key) bandCover.Source = image; });
    }
    void Browse()
    {
        var group = Groups.SelectedItem as SectionDisplayGroup;
        var choices = group?.Sections.Select(s => new SectionPickerChoice(s, SectionDisplay.SegmentLabel(pack, group, s))).ToArray() ?? Array.Empty<SectionPickerChoice>();
        Segments.ItemsSource = choices;
        Segments.SelectedItem = choices.FirstOrDefault(c => c.Section.Id == current?.Id) ?? choices.FirstOrDefault();
        if (Segments.SelectedItem != null) Segments.ScrollIntoView(Segments.SelectedItem);
        OpenButton.IsEnabled = Segments.SelectedItem != null;
        ShowGroup(group, choices.Length);
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
