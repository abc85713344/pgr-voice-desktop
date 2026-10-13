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
    internal readonly Button OpenButton = new() { Content = "打开章节", MinWidth = 110, Style = Theme.Style("PrimaryButton") };
    internal readonly Button CancelButton = new() { Content = "取消", MinWidth = 80, IsCancel = true };
    internal PackChoice? Result { get; private set; }
    /// <summary>游戏章节选择时由主窗口提供：章内原配句数、本章是否用原配、切换；听书选择不提供。</summary>
    internal Func<string, int>? NativeVoiceCount { get; set; }
    internal bool NativeVoiceToggles { get; set; }
    internal Func<string, bool>? NativeVoiceUsesNative { get; set; }
    internal Action<string, bool>? SetNativeVoiceUse { get; set; }

    /// <summary>章节封面（由主窗口按本机封面目录解析并异步解码）；为空时只显示占位底纹。</summary>
    internal Action<PackChoice, Action<ImageSource?>>? CoverLoader { get; set; }
    readonly Dictionary<string, ImageSource?> covers = new(StringComparer.OrdinalIgnoreCase);
    readonly TextBlock listHeading = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock listEnglish = new() { FontSize = 9, Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock listCount = Archive.Count("");
    readonly TextBlock tabCounter = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 10.5, Foreground = Hud.Solid("#9AA6B2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 14, 0) };
    readonly OutlinedText headerNumber = new() { FontSize = 76, Stroke = Hud.Solid("#2E4F6B8A"), StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, 6, 0) };
    // 预览区（01-B）：大封面卡 + 上次位置
    readonly Image previewCover = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock previewKicker = new() { FontSize = 11, Foreground = Hud.Solid("#E6EBF0") };
    readonly TextBlock previewTitle = new() { FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock previewEnglish = new() { FontSize = 10, FontFamily = Theme.Font("LabelFont"), Foreground = Hud.Solid("#C3CBD4") };
    readonly OutlinedText previewNumber = new() { FontSize = 72, Stroke = Hud.Solid("#B3FFFFFF"), StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, -6) };
    readonly Border previewState = new() { Background = Hud.Solid("#E6F3F5F7"), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 10, 0, 0) };
    readonly SpeakerPortrait resumePortrait = new() { Width = 38, Height = 38, Auto = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock resumeMeta = new() { FontSize = 11, Foreground = Theme.Brush("NormalAccent") };
    readonly TextBlock resumeLine = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 58, TextTrimming = TextTrimming.CharacterEllipsis };
    FrameworkElement preview = null!;
    ColumnDefinition previewColumn = null!;
    /// <summary>分类页签的横向滑动条（13 类在窄窗口放不下时可左右滚动）。</summary>
    internal HorizontalStrip CategoryStrip { get; }

    internal ChapterPickerWindow(IEnumerable<PackChoice> choices, PackChoice? current, bool focusCategory)
    {
        this.choices = choices.OrderBy(p => ChapterCatalog.SortKey(p.PackId, p.Title, p.SortOrder), StringComparer.Ordinal).ToList();
        this.current = current;
        Title = "选择章节"; Width = 780; Height = 560; MinWidth = 470; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.ApplyDialog(this);
        ShowInTaskbar = false;
        // 01-B：页眉 → 深色分类页签 → 章节列表 + 大封面预览 → 按键提示与按钮
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new Grid { Margin = new Thickness(18, 12, 18, 10) };
        head.Children.Add(headerNumber);
        head.Children.Add(Archive.Header("章节目录", "CHAPTER SELECT", "先选分类，再选章节", "Category  ›  Chapter"));
        root.Children.Add(head);
        var strip = new DockPanel { Background = Theme.Brush("PlateGradient"), LastChildFill = true };
        DockPanel.SetDock(tabCounter, Dock.Right); strip.Children.Add(tabCounter);
        Categories.Style = null; Categories.Background = Brushes.Transparent; Categories.BorderThickness = new Thickness(0); Categories.Padding = new Thickness(12, 0, 0, 0);
        Categories.AlternationCount = 100;
        Categories.ItemContainerStyle = Theme.Style("CategoryTab");
        var tabsPanel = new FrameworkElementFactory(typeof(StackPanel)); tabsPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        Categories.ItemsPanel = new ItemsPanelTemplate(tabsPanel);
        ScrollViewer.SetHorizontalScrollBarVisibility(Categories, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(Categories, ScrollBarVisibility.Disabled);
        ScrollViewer.SetPanningMode(Categories, PanningMode.None);
        Categories.ItemTemplate = CategoryTemplate();
        // 页签总宽超过窗口时：外层横向滑动条负责滚轮 / 触控板 / 触摸 / 两端箭头，列表自身不滚动。
        CategoryStrip = new HorizontalStrip(Categories, dark: true);
        strip.Children.Add(CategoryStrip);
        Grid.SetRow(strip, 1); root.Children.Add(strip);
        var body = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        body.ColumnDefinitions.Add(new()); previewColumn = new ColumnDefinition { Width = new GridLength(280) }; body.ColumnDefinitions.Add(previewColumn);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var listArea = new Grid(); listArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); listArea.RowDefinitions.Add(new());
        var listHead = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var listTitle = new StackPanel { Orientation = Orientation.Horizontal };
        listTitle.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Theme.Brush("Accent"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        listEnglish.FontFamily = Theme.Font("LabelFont"); listEnglish.Foreground = Theme.Brush("SubtleText");
        listTitle.Children.Add(listHeading); listTitle.Children.Add(listEnglish); listHead.Children.Add(listTitle);
        DockPanel.SetDock(listCount, Dock.Right); listHead.Children.Add(listCount);
        listArea.Children.Add(listHead);
        Chapters.Style = Theme.Style("ArchiveList");
        Chapters.ItemTemplate = ChapterTemplate();
        var paper = new ArchivePaper { Child = Chapters, ShowRail = false, EmblemSize = 0, TideHeight = 0, ShowOrbits = false, Veil = false };
        var listFrame = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = paper };
        var listHost = new Grid(); listHost.Children.Add(listFrame); listHost.Children.Add(EmptyMessage);
        Grid.SetRow(listHost, 1); listArea.Children.Add(listHost);
        body.Children.Add(listArea);
        preview = BuildPreview(); Grid.SetColumn(preview, 1); body.Children.Add(preview);
        foreach (var list in new[] { Categories, Chapters })
        {
            ScrollViewer.SetCanContentScroll(list, false);
            if (ReferenceEquals(list, Chapters)) { ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); }
        }
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        CancelButton.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(CancelButton); actions.Children.Add(OpenButton);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        footer.Children.Add(Archive.KeyHints(("↑↓", "章节"), ("←→", "分类"), ("ENTER", "打开"), ("ESC", "取消"), ("A", "确认"), ("B", "返回")));
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        // 矮窗口（< 430）收起页眉，把高度让给章节列表（标题栏已写明“选择章节”）。
        SizeChanged += (_, _) => head.Visibility = ActualHeight < 430 ? Visibility.Collapsed : Visibility.Visible;
        SizeChanged += (_, _) => { bool wide = ActualWidth >= 600; preview.Visibility = wide ? Visibility.Visible : Visibility.Collapsed; previewColumn.Width = wide ? new GridLength(Math.Min(300, ActualWidth * .38)) : new GridLength(0); };
        Categories.ItemsSource = ChapterCatalog.Categories.Concat(this.choices.Select(p => p.Category)).Distinct().OrderBy(ChapterCatalog.CategoryOrder).ToArray();
        Categories.SelectionChanged += (_, _) => BrowseCategory();
        Chapters.SelectionChanged += (_, _) => { OpenButton.IsEnabled = Chapters.SelectedItem is PackChoice; if (Chapters.SelectedItem is PackChoice selected) LoadCover(selected); ShowPreview(); };
        OpenButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => Cancel();
        Categories.SelectedItem = current?.Category;
        if (Categories.SelectedIndex < 0) Categories.SelectedIndex = 0;
        navigation = new GamepadUiNavigation(this);
        Closed += (_, _) => navigation.Dispose();
        Loaded += (_, _) =>
        {
            LoadCovers(); if (focusCategory) Categories.Focus(); else Chapters.Focus();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(RevealCategory));
        };
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
        string category = Categories.SelectedItem as string ?? "";
        int index = Categories.SelectedIndex;
        listHeading.Text = category; listEnglish.Text = Archive.CategoryEnglish(category);
        listCount.Text = $"{filtered.Count} CHAPTERS · {filtered.Count(p => p.Resume.Length > 0)} 有进度";
        tabCounter.Text = $"{index + 1:00} / {Categories.Items.Count:00}";
        headerNumber.Text = index >= 0 ? (index + 1).ToString("00") : "";
        if (Categories.SelectedItem != null) Categories.ScrollIntoView(Categories.SelectedItem);
        RevealCategory();
        LoadCovers(); ShowPreview();
    }
    /// <summary>当前分类页签始终完整可见（点选、键盘 / 手柄左右、打开窗口时）。</summary>
    internal void RevealCategory()
    {
        if (Categories.SelectedItem == null || !IsLoaded) return;
        Categories.UpdateLayout();
        CategoryStrip.Reveal(Categories.ItemContainerGenerator.ContainerFromItem(Categories.SelectedItem) as FrameworkElement);
    }
    static DataTemplate CategoryTemplate() => new() { VisualTree = new FrameworkElementFactory(typeof(CategoryTabView)) };
    static DataTemplate ChapterTemplate() => new() { VisualTree = new FrameworkElementFactory(typeof(ChapterRowView)) };
    internal int CategoryNumber(string category) => Array.IndexOf((string[])Categories.ItemsSource, category) + 1;
    internal int CountOf(string category) => choices.Count(p => p.Category == category);
    internal bool IsCurrent(PackChoice choice) => string.Equals(choice.File, current?.File, StringComparison.OrdinalIgnoreCase);
    internal event Action? CoversChanged;
    internal ImageSource? CoverOf(PackChoice choice) => covers.TryGetValue(choice.File, out var image) ? image : null;
    void LoadCovers()
    {
        if (CoverLoader == null || !IsLoaded) return;
        var wanted = (Chapters.ItemsSource as IEnumerable<PackChoice> ?? Enumerable.Empty<PackChoice>()).Take(40).ToList();
        if (Chapters.SelectedItem is PackChoice selected && !wanted.Contains(selected)) wanted.Add(selected);
        foreach (var choice in wanted) LoadCover(choice);
    }
    internal void LoadCover(PackChoice choice)
    {
        if (CoverLoader == null || !IsLoaded || covers.ContainsKey(choice.File)) return;
        covers[choice.File] = null;
        CoverLoader(choice, image => { covers[choice.File] = image; CoversChanged?.Invoke(); if (ReferenceEquals(Chapters.SelectedItem, choice)) ShowPreview(); });
    }
    FrameworkElement BuildPreview()
    {
        var stack = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        // 大封面卡：右上切角、底部暗角、大号描边编号、红色短线
        var art = new Grid { Height = 176, ClipToBounds = true, Background = Theme.Brush("Plate") };
        art.Children.Add(new Border { Background = Archive.Stripes("#2A3139", "#323A43", 8) });
        art.Children.Add(previewCover);
        art.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(20, 20, 26, 32), Color.FromArgb(230, 20, 26, 32), 90) });
        art.Children.Add(previewNumber);
        art.Children.Add(previewState);
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 60, 12) };
        var kicker = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        kicker.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L6,4 L0,8 Z"), Fill = Theme.Brush("Accent"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        kicker.Children.Add(previewKicker);
        caption.Children.Add(kicker); caption.Children.Add(previewTitle); caption.Children.Add(previewEnglish);
        art.Children.Add(caption);
        art.Children.Add(new Border { Height = 3, Width = 56, Background = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(new CutFrame { Cut = 14, Corners = CutCorners.TopRight, ClipContent = true, BorderBrush = Theme.Brush("Plate"), Child = art });
        // 上次位置卡（RESUME）
        var resume = new DockPanel { Margin = new Thickness(12, 8, 12, 10) };
        var resumeHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        resumeHead.Children.Add(new TextBlock { Text = "上次位置", FontWeight = FontWeights.SemiBold, FontSize = 12 });
        resumeHead.Children.Add(new TextBlock { Text = "RESUME", FontFamily = Theme.Font("LabelFont"), FontSize = 9, Foreground = Theme.Brush("SubtleText"), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(resumeHead, Dock.Top); resume.Children.Add(resumeHead);
        DockPanel.SetDock(resumePortrait, Dock.Left); resume.Children.Add(resumePortrait);
        var text = new StackPanel(); text.Children.Add(resumeMeta); text.Children.Add(resumeLine); resume.Children.Add(text);
        stack.Children.Add(new Border { Background = Hud.Solid("#E6FFFFFF"), BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(3, 1, 1, 1), Margin = new Thickness(0, 10, 0, 0), Child = resume }
            .Also(b => b.BorderBrush = Theme.Brush("Teal")));
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        hint.Children.Add(Archive.KeyHints(("ENTER", "或"), ("A", "打开此章节，从上次位置续听")));
        stack.Children.Add(hint);
        return stack;
    }
    void ShowPreview()
    {
        if (Chapters.SelectedItem is not PackChoice choice) { preview.Opacity = 0.35; previewTitle.Text = ""; previewKicker.Text = ""; previewEnglish.Text = ""; previewNumber.Text = ""; previewCover.Source = null; previewState.Visibility = Visibility.Collapsed; resumeMeta.Text = ""; resumeLine.Text = "暂无章节"; resumePortrait.Visibility = Visibility.Collapsed; return; }
        preview.Opacity = 1;
        var (number, name) = ChapterCover.Split(choice.Title);
        string display = ChapterCover.DisplayNumber(number);
        if (display.Length == 0) display = (Chapters.SelectedIndex + 1).ToString("00");
        previewKicker.Text = choice.Category + " · " + display;
        previewTitle.Text = name.Length > 0 ? name : choice.Title;
        previewEnglish.Text = Archive.CategoryEnglish(choice.Category) + " · " + display;
        previewNumber.Text = display;
        previewCover.Source = CoverOf(choice);
        bool resumable = choice.Resume.Length > 0;
        previewState.Child = new TextBlock { Text = IsCurrent(choice) ? "当前" : resumable ? "◆ 续听" : "未开始", FontSize = 11, Foreground = IsCurrent(choice) ? Theme.Brush("NormalAccent") : resumable ? Theme.Brush("TealText") : Theme.Brush("MutedText") };
        previewState.Visibility = Visibility.Visible;
        string section = "", speaker = "", line = choice.Resume;
        int colon = line.IndexOf('：');
        int split = colon > 0 ? line.LastIndexOf(" · ", colon, StringComparison.Ordinal) : line.LastIndexOf(" · ", StringComparison.Ordinal);
        if (split > 0) { section = line[..split]; line = line[(split + 3)..]; colon = line.IndexOf('：'); }
        if (colon > 0) { speaker = line[..colon]; line = line[(colon + 1)..]; }
        resumeMeta.Text = !resumable ? "" : section + (speaker.Length > 0 ? (section.Length > 0 ? " · " : "") + speaker : "");
        resumeLine.Text = resumable ? (speaker.Length > 0 ? "“" + line + "”" : line) : "尚未开始";
        resumePortrait.Speaker = speaker; resumePortrait.Visibility = speaker.Length > 0 ? Visibility.Visible : Visibility.Collapsed; resumePortrait.InvalidateVisual();
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

static class ElementExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

/// <summary>01-B 分类页签内容：编号 + 中文 / 压缩英文 + 角标章节数。</summary>
internal sealed class CategoryTabView : StackPanel
{
    readonly TextBlock number = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock name = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold };
    readonly TextBlock english = new() { FontFamily = Theme.Font("LabelFont"), FontSize = 8, Opacity = 0.75 };
    readonly TextBlock count = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 9, Opacity = 0.75, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    public CategoryTabView()
    {
        Orientation = Orientation.Horizontal; IsHitTestVisible = true;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(name); text.Children.Add(english);
        Children.Add(number); Children.Add(text); Children.Add(count);
        number.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1),
            Converter = new FuncConverter(v => v is true ? Theme.Brush("Accent") : Hud.Solid("#8FA6BF")),
        });
        DataContextChanged += (_, _) => Refresh(); Loaded += (_, _) => Refresh();
    }
    void Refresh()
    {
        string category = DataContext as string ?? "";
        var picker = Window.GetWindow(this) as ChapterPickerWindow;
        name.Text = category; english.Text = Archive.CategoryEnglish(category);
        int n = picker?.CategoryNumber(category) ?? 0; number.Text = n > 0 ? n.ToString("00") : "";
        int c = picker?.CountOf(category) ?? 0; count.Text = c.ToString();
    }
}

/// <summary>01-B 章节行：封面小缩略图（切角）+ 章名 / 摘要 + 状态芯片。序号格、色边与选中三角由 ArchiveRow 模板提供。</summary>
internal sealed class ChapterRowView : Grid
{
    readonly Image thumb = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock title = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock sub = new() { FontSize = 11, Foreground = Theme.Brush("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    readonly Border chip = new() { Padding = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly StackPanel native = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    ChapterPickerWindow? picker;
    public ChapterRowView()
    {
        ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var art = new Grid { Width = 62, Height = 34, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true, Background = Archive.Stripes("#DDE2E7", "#D2D8DE", 6) };
        art.Children.Add(thumb);
        art.Clip = Geometry.Parse("M0,0 L54,0 L62,8 L62,34 L0,34 Z");
        Children.Add(art);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(title); text.Children.Add(sub);
        SetColumn(text, 1); Children.Add(text);
        SetColumn(chip, 2); Children.Add(chip);
        SetColumn(native, 3); Children.Add(native);
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { picker = Window.GetWindow(this) as ChapterPickerWindow; if (picker != null) picker.CoversChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { if (picker != null) picker.CoversChanged -= Refresh; };
    }
    void Refresh()
    {
        if (DataContext is not PackChoice choice) return;
        picker ??= Window.GetWindow(this) as ChapterPickerWindow;
        picker?.LoadCover(choice);
        title.Text = choice.Title;
        sub.Text = choice.Resume.Length > 0 ? choice.Resume : Archive.CategoryEnglish(choice.Category);
        thumb.Source = picker?.CoverOf(choice);
        bool current = picker?.IsCurrent(choice) == true, resumable = choice.Resume.Length > 0;
        chip.Background = current ? Theme.Brush("SlateFill") : resumable ? Theme.Brush("TealFill") : Hud.Solid("#EEF0F2");
        chip.Child = new TextBlock { Text = current ? "当前" : resumable ? "◆ 续听" : "未开始", FontSize = 10.5, Foreground = current ? Theme.Brush("NormalAccent") : resumable ? Theme.Brush("TealText") : Theme.Brush("MutedText") };
        RefreshNativeVoice(choice);
    }
    // 原配：徽标“原配 N”；总开关打开时再给“用原配 / 用配音包”切换键（不抢焦点，不影响列表的键盘与手柄选择）。
    void RefreshNativeVoice(PackChoice choice)
    {
        native.Children.Clear();
        int count = picker?.NativeVoiceCount?.Invoke(choice.PackId) ?? 0;
        if (count == 0) return;
        native.Children.Add(new Border { Background = Theme.Brush("TealFill"), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = $"原配 {count}", FontSize = 10.5, Foreground = Theme.Brush("TealText") } });
        if (picker?.NativeVoiceToggles != true || picker.NativeVoiceUsesNative == null || picker.SetNativeVoiceUse == null) return;
        bool useNative = picker.NativeVoiceUsesNative(choice.PackId);
        var toggle = new Button { Content = useNative ? "用原配" : "用配音包", FontSize = 11, Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(6, 0, 0, 0), Focusable = false,
            Foreground = Theme.Brush(useNative ? "TealText" : "MutedText"), ToolTip = "本章游戏原配：点一下切换“用原配 / 用配音包”" };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "本章游戏原配：" + toggle.Content);
        toggle.Click += (_, e) => { e.Handled = true; picker.SetNativeVoiceUse(choice.PackId, !picker.NativeVoiceUsesNative(choice.PackId)); RefreshNativeVoice(choice); };
        native.Children.Add(toggle);
    }
}
