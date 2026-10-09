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
        ShowExperienceDialog(new LineFeedbackWindow(snapshot));
    }

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
        audio.Volume = SpeakerVolume.Apply((float)preferences.Volume / 100, preferences.SpeakerVolumes, audioSpeaker);
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
        Background = Theme.Brush("Bg"); Foreground = Theme.Brush("Fg");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
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
    internal readonly ComboBox Category = new() { ItemsSource = LineFeedback.Categories, SelectedIndex = 0 };
    internal readonly TextBox Comment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 58, MaxHeight = 100,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "补充当时发生了什么（可留空）", MaxLength = 6000 };
    internal readonly TextBox Preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    internal LineFeedbackWindow(LineFeedbackSnapshot snapshot) : base("反馈这一句")
    {
        this.snapshot = snapshot;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var form = new StackPanel(); root.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "台词已固定，配音已暂停。填写后预览、复制或保存，再自行发给开发者；关闭窗口后可主动恢复播放。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        form.Children.Add(new TextBlock { Text = "问题类型", Margin = new Thickness(0, 0, 0, 4) }); form.Children.Add(Category);
        form.Children.Add(new TextBlock { Text = "补充说明（可选）", Margin = new Thickness(0, 8, 0, 4) }); form.Children.Add(Comment);
        form.Children.Add(new TextBlock { Text = "将导出的内容", Margin = new Thickness(0, 10, 0, 5) });
        Grid.SetRow(Preview, 1); root.Children.Add(Preview);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        void Button(string label, Action action)
        { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        Button("复制反馈", () => { try { Clipboard.SetText(Preview.Text); status.Text = "已复制，可自行粘贴发送。"; } catch (Exception ex) { status.Text = "复制失败：" + ex.Message; } });
        Button("另存为文本…", SaveFeedback); Button("关闭", Close); footer.Children.Add(status); Content = root;
        Category.SelectionChanged += (_, _) => RefreshPreview(); Comment.TextChanged += (_, _) => RefreshPreview();
        Loaded += (_, _) => Category.Focus(); RefreshPreview();
    }
    void RefreshPreview() => Preview.Text = LineFeedback.Format(snapshot, Category.SelectedItem as string ?? "其他", Comment.Text);
    void SaveFeedback()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存这一句的反馈", FileName = "战双配音反馈_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            Filter = "文本文件 (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, Preview.Text, new UTF8Encoding(false)); status.Text = "已保存反馈文本，未自动发送。"; }
        catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
    }
}

internal sealed class SpeakerVolumeWindow : ExperienceDialog
{
    internal readonly Dictionary<string, int> Values;
    readonly string[] speakers;
    internal readonly TextBox Search = new() { ToolTip = "搜索角色名字", Margin = new Thickness(0, 8, 0, 8) };
    internal readonly ListBox Speakers = new();
    internal readonly Slider Volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
    readonly TextBlock selected = new(), percentage = new();
    bool refreshing;
    sealed record SpeakerChoice(string Key, string Label) { public override string ToString() => Label; }
    internal SpeakerVolumeWindow(IEnumerable<string> speakers, IReadOnlyDictionary<string, int> values) : base("角色音量")
    {
        Values = new(values, StringComparer.Ordinal);
        this.speakers = speakers.Select(SpeakerVolume.Key).Distinct(StringComparer.Ordinal).OrderBy(SpeakerVolume.DisplayName, StringComparer.CurrentCulture).ToArray();
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "按当前配音包的角色名字分别调节。100% 保持原音量，0% 静音；最终响度还受总音量控制。此处不会更换录音音色。", TextWrapping = TextWrapping.Wrap }); header.Children.Add(Search);
        Speakers.MinHeight = 70; Grid.SetRow(Speakers, 1); root.Children.Add(Speakers);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var title = new DockPanel(); DockPanel.SetDock(percentage, Dock.Right); title.Children.Add(percentage); title.Children.Add(selected); footer.Children.Add(title); footer.Children.Add(Volume);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(buttons);
        void Button(string label, Action action)
        { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) }; button.Click += (_, _) => action(); buttons.Children.Add(button); }
        Button("本角色恢复 100%", () => { if (Speakers.SelectedItem != null) Volume.Value = 100; });
        Button("保存", () => DialogResult = true); Button("取消", Close);
        Search.TextChanged += (_, _) => RefreshSpeakers(); Speakers.SelectionChanged += (_, _) => SelectSpeaker();
        Volume.ValueChanged += (_, _) =>
        { if (!refreshing && Speakers.SelectedItem is SpeakerChoice item) { SpeakerVolume.Set(Values, item.Key, (int)Math.Round(Volume.Value)); percentage.Text = $"{Volume.Value:0}%"; } };
        Content = root; Loaded += (_, _) => Search.Focus(); RefreshSpeakers();
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
