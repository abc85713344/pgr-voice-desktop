using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    ExperienceDialog? experienceDialog;
    string? audioSpeaker;
    Action<ExperienceDialog>? experienceDialogTest;

    LineFeedbackSnapshot? CaptureGameFeedback(bool history)
    {
        if (engine == null) return null;
        int index = engine.HistoryPosition;
        Node? node = engine.Current;
        if (history)
        {
            if (historyList.SelectedItem is not HistoryItem { Kind: "line" } selected
                || selected.Index < 0 || selected.Index >= engine.History.Count) return null;
            index = selected.Index;
            node = engine.Pack.ById.GetValueOrDefault(engine.History[index].NodeId);
        }
        if (node?.Kind != "line") return null;
        // 只取实际访问过且位于目标之前的记录；不从完整节点表拼出互斥分支。
        var known = engine.History.Take(Math.Max(0, index)).TakeLast(3)
            .Select(v => engine.Pack.ById.GetValueOrDefault(v.NodeId)).OfType<Node>().ToArray();
        return LineFeedback.Capture(engine.Pack, node, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知",
            "Windows", history ? "历史选中台词" : "游戏配音", known);
    }

    LineFeedbackSnapshot? CaptureListeningFeedback()
    {
        if (listeningSession?.Current?.Node is not Node { Kind: "line" } node) return null;
        // 听书计划可能含“全部听取”的互斥路线，只附已经实际播完的记录。
        return LineFeedback.Capture(listeningSession.Pack, node, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知",
            "Windows", "听书", listeningRecentNodes);
    }

    void FeedbackGameLine() => ShowLineFeedback(CaptureGameFeedback(false));
    void FeedbackHistoryLine() => ShowLineFeedback(CaptureGameFeedback(true));
    void FeedbackListeningLine() => ShowLineFeedback(CaptureListeningFeedback());

    void ShowLineFeedback(LineFeedbackSnapshot? snapshot)
    {
        if (snapshot == null) { Tell("当前没有可反馈的台词，请先选择一句台词。"); return; }
        if (experienceDialog != null) return;
        // 先冻结反馈，再暂停；迟到的自然完成和跟随回调不能在填写期间换句。
        // 关闭窗口后由用户主动恢复，角色音量窗口仍允许实时调节。
        if (listeningRunning) PauseListening();
        if (TextFollowing) PauseTextPlayback("正在填写台词反馈，游戏配音已暂停；关闭后可主动恢复。");
        CancelTextAutoAdvance("正在填写台词反馈，待执行的自动点击已取消。");
        if (automaticRunning || automaticPendingOwner != null || automaticPreparingOwner != null)
            StopAutomatic("正在填写台词反馈，自动播放已暂停；关闭后可主动恢复。", false);
        StopPreview(); CancelOcr(); engine?.PauseForBrowse(); StopAudio(); ResetFollowInputSession();
        string key = snapshot.PackId + "\n" + snapshot.NodeId + "\n" + snapshot.Mode;
        var window = new LineFeedbackWindow(snapshot);
        if (lineFeedbackDraft is { } draft && draft.Key == key) window.Restore(draft.Category, draft.Comment);
        ShowExperienceDialog(window);
        // 关掉但没复制或另存的反馈留作草稿；同一句再次打开时恢复，换一句不带过去。
        bool filled = window.Comment.Text.Trim().Length > 0 || window.Category.SelectedIndex > 0;
        if (!window.Exported && filled) lineFeedbackDraft = (key, window.Category.SelectedIndex, window.Comment.Text);
        else if (lineFeedbackDraft?.Key == key) lineFeedbackDraft = null;
    }
    (string Key, int Category, string Comment)? lineFeedbackDraft;

    bool? ShowExperienceDialog(ExperienceDialog dialog)
    {
        if (experienceDialog != null) return false;
        experienceDialog = dialog; dialog.Owner = this;
        try
        {
            ResetGamepadContext();
            dialog.Loaded += (_, _) => experienceDialogTest?.Invoke(dialog);
            return dialog.ShowDialog();
        }
        finally { experienceDialog = null; ResetGamepadContext(); }
    }

    void ApplySpeakerVolumes()
    {
        audio.Volume = playingNative ? 0 : SpeakerVolume.Apply((float)preferences.Volume / 100, preferences.SpeakerVolumes, audioSpeaker);
        SetListeningVolume((float)preferences.Volume / 100);
    }

    void OpenSpeakerVolumes()
    {
        var speakers = (engine?.Pack.Nodes ?? Enumerable.Empty<Node>())
            .Concat(listeningSession?.Pack.Nodes ?? Enumerable.Empty<Node>())
            .Where(n => n.Kind == "line").Select(n => SpeakerVolume.Key(n.Speaker))
            .Concat(preferences.SpeakerVolumes.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var dialog = new SpeakerVolumeWindow(speakers, preferences.SpeakerVolumes);
        if (ShowExperienceDialog(dialog) != true) return;
        preferences.SpeakerVolumes = dialog.Values;
        ApplySpeakerVolumes(); Save(); Tell("角色音量已保存，游戏配音与听书同时生效。");
    }
}

internal class ExperienceDialog : Window
{
    readonly GamepadUiNavigation navigation;
    protected ExperienceDialog(string title)
    {
        Title = title; Width = 720; Height = 640; MinWidth = 430; MinHeight = 380;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Theme.ApplyDialog(this);
        navigation = new GamepadUiNavigation(this);
        Closed += (_, _) => navigation.Dispose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };
    }
    internal void GamepadAction(string action)
    {
        if (action is "back" or "panel") { if (!navigation.BackFromControl()) Close(); }
        else if (action == "confirm") navigation.ActivateFocused();
        else if (action is "up" or "down" or "left" or "right")
            navigation.Move(action switch { "up" => FocusNavigationDirection.Up, "down" => FocusNavigationDirection.Down,
                "left" => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right });
    }
}

internal sealed class LineFeedbackWindow : ExperienceDialog
{
    readonly LineFeedbackSnapshot snapshot;
    internal readonly ListBox Category = new() { ItemsSource = LineFeedback.Categories, SelectedIndex = 0 };
    internal readonly TextBox Comment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 170,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "补充当时发生了什么（可留空）", MaxLength = 6000 };
    internal readonly TextBox Preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10, 14, 26), FontSize = 12.5 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    internal bool Exported { get; private set; }
    internal void Restore(int category, string comment)
    {
        Category.SelectedIndex = Math.Clamp(category, 0, LineFeedback.Categories.Count - 1);
        Comment.Text = comment; status.Text = "已恢复上次未完成的反馈。";
    }
    readonly TextBlock count = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 11, Foreground = Theme.Brush("MutedText"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };
    static string CategoryEnglish(string category) => category switch
    { "播错了" => "WRONG LINE", "没声音" => "NO AUDIO", "跟丢了" => "LOST TRACK", "读音问题" => "PRONUNCIATION", _ => "OTHER" };
    internal LineFeedbackWindow(LineFeedbackSnapshot snapshot) : base("反馈这一句")
    {
        this.snapshot = snapshot; Width = 900; Height = 660;
        // 05-B：左表单（名牌卡 + 竖向编号类型列表 + 较高说明框）/ 右全高导出档案纸面（F1b 170px）；窄窗（< 640）上下堆叠。
        var root = new Grid { Margin = new Thickness(18, 12, 18, 14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 10), Child = Archive.Header("反馈这一句", "LINE FEEDBACK", "台词已固定，配音已暂停", "SNAPSHOT  ›  EXPORT") });
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var form = new StackPanel { Margin = new Thickness(0, 0, 4, 0) }; scroll.Content = form;
        form.Children.Add(new TextBlock { Text = "台词已固定，配音已暂停。填写后预览、复制或保存，再自行发给开发者；关闭窗口后可主动恢复播放。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 0, 0, 10) });
        form.Children.Add(NamePlate());
        form.Children.Add(Archive.Section("问题类型", "CATEGORY"));
        Category.Style = Theme.Style("ArchiveList");
        Category.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(FeedbackCategoryRow)) };
        Category.BorderBrush = Theme.Brush("Stroke"); Category.BorderThickness = new Thickness(1);
        ScrollViewer.SetVerticalScrollBarVisibility(Category, ScrollBarVisibility.Disabled);
        form.Children.Add(Category);
        var commentHead = Archive.Section("补充说明（可选）", "NOTE"); commentHead.Margin = new Thickness(0, 12, 0, 0); form.Children.Add(commentHead);
        form.Children.Add(Comment); form.Children.Add(count);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; form.Children.Add(actions);
        void Button(string label, Action action, string kind = "")
        { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 6, 12, 6) }; if (kind == "go") button.Style = Theme.Style("PrimaryButton"); button.Click += (_, _) => action(); actions.Children.Add(button); }
        Button("复制反馈", () => { try { Clipboard.SetText(Preview.Text); Exported = true; status.Text = "已复制，可自行粘贴发送。"; } catch (Exception ex) { status.Text = "复制失败：" + ex.Message; } }, "go");
        Button("另存为文本…", SaveFeedback); Button("关闭", Close); form.Children.Add(status);
        // 右栏：全高导出纸面。装饰只在这里；正文底部留 26px 避开红潮带。
        var page = new DockPanel();
        var strip = Archive.Strip("EXPORT", "将导出的内容", "Plain text", snapshot.NodeId); DockPanel.SetDock(strip, Dock.Top); page.Children.Add(strip);
        page.Children.Add(Preview);
        var paper = new ArchivePaper { Child = page, DecorTop = 26, EmblemSize = 170, EmblemRight = -30, EmblemBottom = -28, TideHeight = 54, RailLabel = "FEEDBACK ARCHIVE",
            RailMarks = snapshot.NodeId.Length > 0 ? new[] { snapshot.NodeId } : null };
        var paperFrame = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = paper };
        body.Children.Add(scroll); body.Children.Add(paperFrame);
        void Layout()
        {
            bool wide = ActualWidth >= 640;
            body.ColumnDefinitions.Clear(); body.RowDefinitions.Clear();
            if (wide)
            {
                body.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Min(360, Math.Max(300, ActualWidth * .42))) }); body.ColumnDefinitions.Add(new());
                Grid.SetColumn(paperFrame, 1); Grid.SetRow(paperFrame, 0); paperFrame.Margin = new Thickness(14, 0, 0, 0); paperFrame.MinHeight = 0;
            }
            else
            {
                body.RowDefinitions.Add(new() { Height = new GridLength(1.1, GridUnitType.Star) }); body.RowDefinitions.Add(new());
                Grid.SetColumn(paperFrame, 0); Grid.SetRow(paperFrame, 1); paperFrame.Margin = new Thickness(0, 10, 0, 0); paperFrame.MinHeight = 140;
            }
        }
        SizeChanged += (_, _) => Layout(); Layout();
        Content = root;
        Category.SelectionChanged += (_, _) => RefreshPreview(); Comment.TextChanged += (_, _) => RefreshPreview();
        Loaded += (_, _) => { Category.Focus(); (Category.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Category.SelectedIndex)) as ListBoxItem)?.Focus(); }; RefreshPreview();
    }
    FrameworkElement NamePlate()
    {
        var plate = new DockPanel();
        var portrait = new SpeakerPortrait { Speaker = snapshot.Speaker, Auto = true, ShowSerial = false, Width = 44, Height = 44, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        if (snapshot.Speaker.Length == 0) portrait.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(portrait, Dock.Left); plate.Children.Add(portrait);
        var text = new StackPanel();
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(new TextBlock { Text = snapshot.Speaker.Length == 0 ? "旁白" : snapshot.Speaker, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        if (snapshot.Mode.Length > 0) { var tag = Archive.Tag(snapshot.Mode, "mist"); tag.Margin = new Thickness(8, 0, 0, 0); who.Children.Add(tag); }
        text.Children.Add(who);
        text.Children.Add(new TextBlock { Text = snapshot.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 56, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { snapshot.ChapterTitle, snapshot.SectionTitle, snapshot.NodeId.Length > 0 ? "#" + snapshot.NodeId : "" }.Where(x => x.Length > 0)),
            FontSize = 11, Foreground = Theme.Brush("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
        plate.Children.Add(text);
        return new Border { Background = Hud.Solid("#F4F6F8"), BorderBrush = Theme.Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 8, 10, 9), Margin = new Thickness(0, 0, 0, 12), Child = plate };
    }
    sealed class FeedbackCategoryRow : StackPanel
    {
        public FeedbackCategoryRow()
        {
            Orientation = Orientation.Horizontal; Margin = new Thickness(0, 2, 0, 2);
            var name = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var english = new TextBlock { FontFamily = Theme.Font("LabelFont"), FontSize = 9, Opacity = .8, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Children.Add(name); Children.Add(english);
            DataContextChanged += (_, _) => { string c = DataContext as string ?? ""; name.Text = c; english.Text = CategoryEnglish(c); };
        }
    }
    void RefreshPreview()
    {
        Preview.Text = LineFeedback.Format(snapshot, Category.SelectedItem as string ?? "其他", Comment.Text);
        count.Text = $"{Comment.Text.Length} / {Comment.MaxLength}";
    }
    void SaveFeedback()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存这一句的反馈", FileName = "战双配音反馈_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            Filter = "文本文件 (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, Preview.Text, new UTF8Encoding(false)); Exported = true; status.Text = "已保存反馈文本，未自动发送。"; }
        catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
    }
}

internal sealed class SpeakerVolumeWindow : ExperienceDialog
{
    internal readonly Dictionary<string, int> Values;
    readonly Dictionary<string, int> opening;
    readonly string[] speakers;
    internal readonly TextBox Search = new() { ToolTip = "搜索角色名字", Margin = new Thickness(0, 8, 0, 8) };
    internal readonly ListBox Speakers = new();
    internal readonly Slider Volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
    readonly TextBlock selected = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom },
        percentage = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 34, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, -6, 0, -4) };
    readonly TextBlock counts = new() { FontSize = 11.5, Foreground = Theme.Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center };
    readonly Border unsaved = Archive.Tag("已调整 · 未保存", "brass");
    internal event Action? ValuesChanged;
    bool refreshing;
    internal sealed record SpeakerChoice(string Key, string Label) { public override string ToString() => Label; }
    internal SpeakerVolumeWindow(IEnumerable<string> speakers, IReadOnlyDictionary<string, int> values) : base("角色音量")
    {
        Values = new(values, StringComparer.Ordinal); opening = new(values, StringComparer.Ordinal);
        this.speakers = speakers.Select(SpeakerVolume.Key).Distinct(StringComparer.Ordinal).OrderBy(SpeakerVolume.DisplayName, StringComparer.CurrentCulture).ToArray();
        Height = 680;
        // 06-A：单列；名单档案纸面（行半透明）+ 底部干净调节台（大号百分比 + 宽滑块，黄铜线 = 100%）。
        var root = new Grid { Margin = new Thickness(18, 12, 18, 14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); root.Children.Add(header);
        header.Children.Add(Archive.Header("角色音量", "SPEAKER VOLUME", "按角色分别调节", "SPEAKER  ›  VOLUME"));
        var countRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        countRow.Children.Add(counts); unsaved.Margin = new Thickness(10, 0, 0, 0); countRow.Children.Add(unsaved); header.Children.Add(countRow);
        header.Children.Add(new TextBlock { Text = "按当前配音包的角色名字分别调节。100% 保持原音量，0% 静音；最终响度还受总音量控制。此处不会更换录音音色。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Brush("MutedText") }); header.Children.Add(Search);
        Speakers.MinHeight = 70; Speakers.Style = Theme.Style("ArchiveList"); Speakers.Background = Brushes.Transparent;
        Speakers.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(SpeakerVolumeRow)) };
        ScrollViewer.SetHorizontalScrollBarVisibility(Speakers, ScrollBarVisibility.Disabled);
        var paper = new ArchivePaper { Child = Speakers, EmblemSize = 200, EmblemRight = -40, EmblemBottom = -36, TideHeight = 44, WatermarkSize = 150, WatermarkRight = 30, RailLabel = "VOICE ARCHIVE" };
        var list = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = paper };
        Grid.SetRow(list, 1); root.Children.Add(list);
        // 调节台：不透明、无装饰线穿过。
        var deck = new StackPanel();
        var title = new DockPanel { LastChildFill = false };
        title.Children.Add(selected);
        DockPanel.SetDock(percentage, Dock.Right); title.Children.Add(percentage);
        deck.Children.Add(title);
        var track = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        track.Children.Add(Volume);
        track.Children.Add(new Border { Width = 2, Background = Theme.Brush("Brass"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -4, 4, -4), IsHitTestVisible = false, ToolTip = "100% 原音量" });
        deck.Children.Add(track);
        var scale = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 2, 0, 0) };
        scale.Children.Add(new TextBlock { Text = "0% 静音", FontSize = 10.5, Foreground = Theme.Brush("MutedText") });
        var hundred = new TextBlock { Text = "100% 原音量", FontSize = 10.5, Foreground = Theme.Brush("MutedText") }; DockPanel.SetDock(hundred, Dock.Right); scale.Children.Add(hundred);
        deck.Children.Add(scale);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; deck.Children.Add(buttons);
        void Button(string label, Action action, bool primary = false)
        { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) }; if (primary) button.Style = Theme.Style("PrimaryButton"); button.Click += (_, _) => action(); buttons.Children.Add(button); }
        Button("本角色恢复 100%", () => { if (Speakers.SelectedItem != null) Volume.Value = 100; });
        Button("保存", () => DialogResult = true, true); Button("取消", Close);
        var footer = new Border { Background = Brushes.White, BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1, 0, 1, 1), Padding = new Thickness(14, 10, 14, 12), Child = deck };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Search.TextChanged += (_, _) => RefreshSpeakers(); Speakers.SelectionChanged += (_, _) => SelectSpeaker();
        Volume.ValueChanged += (_, _) =>
        {
            if (!refreshing && Speakers.SelectedItem is SpeakerChoice item)
            { SpeakerVolume.Set(Values, item.Key, (int)Math.Round(Volume.Value)); percentage.Text = $"{Volume.Value:0}%"; RefreshCounts(); ValuesChanged?.Invoke(); }
        };
        Content = root; Loaded += (_, _) => Search.Focus(); RefreshSpeakers(); RefreshCounts();
    }
    internal int PercentOf(string key) => SpeakerVolume.GetPercent(Values, key);
    void RefreshCounts()
    {
        int adjusted = speakers.Count(s => SpeakerVolume.GetPercent(Values, s) != 100);
        counts.Text = $"共 {speakers.Length} 个角色 / 已调整 {adjusted} 个";
        bool changed = speakers.Any(s => SpeakerVolume.GetPercent(Values, s) != SpeakerVolume.GetPercent(opening, s));
        unsaved.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }
    void RefreshSpeakers()
    {
        var previous = (Speakers.SelectedItem as SpeakerChoice)?.Key;
        var choices = speakers.Where(s => SpeakerVolume.DisplayName(s).Contains(Search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(s => new SpeakerChoice(s, SpeakerVolume.DisplayName(s))).ToArray();
        Speakers.ItemsSource = choices; Speakers.SelectedItem = choices.FirstOrDefault(s => s.Key == previous) ?? choices.FirstOrDefault(); SelectSpeaker();
    }
    void SelectSpeaker()
    {
        refreshing = true;
        try
        {
            var item = Speakers.SelectedItem as SpeakerChoice; Volume.IsEnabled = item != null;
            selected.Text = item == null ? "没有匹配角色；先打开配音章节。" : item.Label;
            Volume.Value = item == null ? 100 : SpeakerVolume.GetPercent(Values, item.Key);
            percentage.Text = item == null ? "" : $"{Volume.Value:0}%";
        }
        finally { refreshing = false; }
    }
}

/// <summary>06-A 名单行：小头像 + 名字 + 行内细音量条（黄铜端线 = 100%）+ 百分比 + 已调整 / 静音签。</summary>
internal sealed class SpeakerVolumeRow : DockPanel
{
    readonly SpeakerPortrait avatar = new() { Width = 24, Height = 24, Auto = true, ShowSerial = false, Margin = new Thickness(0, 0, 9, 0) };
    readonly TextBlock name = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock value = new() { FontFamily = Theme.Font("NumberFont"), FontSize = 13, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly Border fill = new() { HorizontalAlignment = HorizontalAlignment.Left, Height = 4 };
    readonly ContentControl tag = new() { Width = 54, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    SpeakerVolumeWindow? owner;
    public SpeakerVolumeRow()
    {
        LastChildFill = true; Margin = new Thickness(0, 1, 4, 1);
        SetDock(avatar, Dock.Left); Children.Add(avatar);
        SetDock(tag, Dock.Right); Children.Add(tag);
        SetDock(value, Dock.Right); Children.Add(value);
        var bar = new Grid { Width = 110, Height = 4, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Background = Hud.Solid("#DDE2E7") };
        bar.Children.Add(fill);
        bar.Children.Add(new Border { Width = 1.5, Height = 10, Margin = new Thickness(0, -3, 0, -3), HorizontalAlignment = HorizontalAlignment.Right, Background = Theme.Brush("Brass") });
        SetDock(bar, Dock.Right); Children.Add(bar);
        Children.Add(name);
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { owner = Window.GetWindow(this) as SpeakerVolumeWindow; if (owner != null) owner.ValuesChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { if (owner != null) owner.ValuesChanged -= Refresh; };
    }
    void Refresh()
    {
        if (DataContext is not SpeakerVolumeWindow.SpeakerChoice item) return;
        owner ??= Window.GetWindow(this) as SpeakerVolumeWindow;
        int percent = owner?.PercentOf(item.Key) ?? 100;
        avatar.Speaker = item.Key; name.Text = item.Label; value.Text = percent + "%";
        fill.Width = 110 * percent / 100.0;
        fill.Background = percent == 0 ? Theme.Brush("MutedText") : percent == 100 ? Theme.Brush("NormalAccent") : Theme.Brush("Teal");
        tag.Content = percent == 0 ? Archive.Tag("静音", "red") : percent != 100 ? Archive.Tag("已调整", "teal") : null;
    }
}
