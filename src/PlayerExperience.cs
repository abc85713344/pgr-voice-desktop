using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    ProgressStore? progressStore;
    TabItem historyTab = null!;
    readonly ListBox historyList = new();
    readonly ComboBox historyFilter = new();
    readonly TextBlock historyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
    readonly TextBox bookmarkNote = new() { ToolTip = "书签备注（可选）", MinWidth = 110 };
    readonly ComboBox devicesBox = new();
    readonly TextBlock deviceStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock keyTestStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly CheckBox keyTestBox = new() { Content = "按键测试（测试时不推进剧情）" };
    readonly CheckBox clickZoneBox = new()
    {
        Content = "显示「下一句」点击热区（轻点松开后跟随）",
        Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "点击照常传给游戏。需要移动热区时，点击下方“调整热区位置”。"
    };
    readonly TextBlock diagnosticStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly ListBox diagnosticList = new() { MaxHeight = 170 };
    readonly Button checkPackButton = new() { Content = "检查当前章" };
    readonly CheckBox showAllSectionLines = new() { Content = "查看本节全部台词", ToolTip = "包含尚未接入路线的正文；仅浏览，不确认路线或播放。", Margin = new Thickness(8, 4, 0, 4) };
    CancellationTokenSource? diagnosticCancellation;
    PackDiagnosticReport? diagnosticReport;
    bool refreshingExperience, experienceReady, previewingHistory, applyReselectHighlight;
    DateTime lastReconnectCheck;
    long deviceSelectionRequest;
    string connectionNotice = "未绑定游戏";
    string? lastAudioProblem;
    bool KeyTesting => experienceReady && keyTestBox.IsChecked == true;
    sealed record HistoryItem(string Kind, int Index, long Sequence, string Label, NavigationBookmark? Bookmark = null)
    {
        public override string ToString() => Label;
    }

    void InitializeExperience()
    {
        progressStore = new ProgressStore(Path.Combine(Log.DataDir, "progress"));
        progressStore.Load();
        LibraryBox.GroupStyle.Add(ChapterGroupStyle());
        StoryChapterPickerHost.Children.Add(ChapterPickerControls(LibraryBox));
        StorySectionPickerHost.Children.Add(SectionPickerControl(SectionBox));
        NavigationToolbar.Children.Add(showAllSectionLines);
        showAllSectionLines.Checked += (_, _) => { FillLines(); if(engine?.Mode==RunMode.Gap)BrowseCurrent(); };
        showAllSectionLines.Unchecked += (_, _) => FillLines();
        InitializeManualContinuationView();
        if(progressStore.RecoveredFromBackup) Tell("部分章节进度已从有效备份恢复。");
        if(progressStore.LastError.Length>0)ShowSaveFailure(progressStore.LastError);
        LibraryBox.DropDownOpened += (_, _) =>
        {
            foreach (var item in LibraryBox.Items.OfType<PackChoice>())
                item.Resume = progressStore.GetSummary(item.PackId);
        };
        historyTab = new TabItem { Header = "历史" };
        var layout = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        historyFilter.ItemsSource = new[] { "台词履历", "最近选择", "我的书签" };
        historyFilter.SelectedIndex = 0;
        historyFilter.SelectionChanged += (_, _) => RefreshHistory();
        layout.Children.Add(historyFilter);
        historyList.Margin = new Thickness(0, 8, 0, 8);
        historyList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        historyList.ItemTemplate = WrappedTextTemplate();
        Grid.SetRow(historyList, 1); layout.Children.Add(historyList);
        var actions = new WrapPanel(); Grid.SetRow(actions, 2); layout.Children.Add(actions);
        AddButton(actions, "试听本句", PreviewHistory);
        AddButton(actions, "停止试听", StopPreview);
        AddButton(actions, "反馈选中句", FeedbackHistoryLine);
        AddButton(actions, "从这里继续", ContinueHistory);
        AddButton(actions, "删除书签", DeleteBookmark);
        Grid.SetRow(historyStatus, 3); layout.Children.Add(historyStatus);
        historyTab.Content = layout; Tabs.Items.Insert(1, historyTab);

        var preferencesPanel = AppearanceSettingsContent;
        AddHeading(preferencesPanel, "阅读与悬浮显示");
        var scale = new ComboBox { ItemsSource = new[] { "100%", "125%", "150%", "200%" }, SelectedIndex = Array.IndexOf(new[] { 1d, 1.25, 1.5, 2d }, preferences.ReadingScale) };
        if (scale.SelectedIndex < 0) scale.SelectedIndex = 0;
        scale.SelectionChanged += (_, _) => { preferences.ReadingScale = new[] { 1d, 1.25, 1.5, 2d }[scale.SelectedIndex]; ApplyReadingScale(); Save(); };
        preferencesPanel.Children.Add(scale);
        var compact = new ComboBox { ItemsSource = new[] { "收起为悬浮球", "收起为小台词条" }, SelectedIndex = preferences.CompactMode == "strip" ? 1 : 0, Margin = new Thickness(0, 6, 0, 0) };
        compact.SelectionChanged += (_, _) => { preferences.CompactMode = compact.SelectedIndex == 1 ? "strip" : "ball"; Save(); };
        preferencesPanel.Children.Add(compact);
        AddHeading(preferencesPanel, "声音输出");
        devicesBox.SelectionChanged += async (_, _) =>
        {
            if (refreshingExperience || devicesBox.SelectedItem is not AudioDeviceOption device) return;
            StopAutomatic("声音设备已切换，自动播放暂停。"); StopListeningForGame();
            StopPreview(); engine?.PauseForBrowse();
            long request = ++deviceSelectionRequest;
            preferences.OutputDeviceId = device.Id; Save(); deviceStatus.Text = "正在切换声音设备…";
            try
            {
                await Task.Run(async () =>
                {
                    await audioQueue.WaitAsync();
                    try { if (request == Interlocked.Read(ref deviceSelectionRequest) && !closing) audio.SelectDevice(device.Id); }
                    finally { audioQueue.Release(); }
                });
                if (!closing && request == deviceSelectionRequest) deviceStatus.Text = "输出：" + device.Name + "；主动重播后发声。";
            }
            catch (Exception ex) { if (!closing && request == deviceSelectionRequest) deviceStatus.Text = ex.Message; }
        };
        preferencesPanel.Children.Add(devicesBox); preferencesPanel.Children.Add(deviceStatus);
        AddButton(preferencesPanel, "刷新声音设备", RefreshAudioDevices);
        AddButton(preferencesPanel, "试听当前句", () => { if (engine?.Mode != RunMode.Original) { engine?.PauseForBrowse(); engine?.Replay(); } });
        AddButton(preferencesPanel, "角色音量…", OpenSpeakerVolumes);
        preferencesPanel.Children.Add(new TextBlock { Text = "按配音包中的角色名字分别调节，默认 100%，再乘以总音量。不会改变已有录音的音色。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText") });
        preferencesPanel.Children.Add(new TextBlock { Text = "角色固定声线：等待配音包，未开启", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText") });
        AddButton(NavigationToolbar, "反馈当前句", FeedbackGameLine);
        AddButton(NavigationToolbar, "搜索全部章节", OpenLibrarySearch);
        preferencesPanel = InputSettingsContent;
        AddHeading(preferencesPanel, "点击位置与按键测试");
        // 鼠标只观察；完整轻点通过后再跟随，始终让游戏自己处理点击。
        clickZoneBox.Checked += (_, _) => { if (!experienceReady) return; preferences.ClickZoneEnabled = true; Save(); UpdateClickZone(); Tell("已显示「下一句」热区；需要移动时点击“调整热区位置”。"); };
        clickZoneBox.Unchecked += (_, _) => { if (!experienceReady) return; preferences.ClickZoneEnabled = false; Save(); UpdateClickZone(); Tell("已隐藏「下一句」热区，键盘跟随不受影响。"); };
        clickZoneBox.IsChecked = preferences.ClickZoneEnabled;
        preferencesPanel.Children.Add(clickZoneBox);
        AddButton(preferencesPanel, "调整热区位置 / 完成", () => { editingClickZone = !editingClickZone; UpdateClickZone(); Tell(editingClickZone ? "现在可以拖动热区。完成后再次点击此按钮，或收起面板。" : "热区已恢复穿透。"); });
        keyTestBox.Checked += (_, _) => { StopPreview(); engine?.PauseForBrowse(); HideBranchMenu(); CancelOcr(); keyTestStatus.Text = "按下要检查的键；也可以切回游戏测试。不会改变配音位置。"; };
        keyTestBox.Unchecked += (_, _) => { keyTestStatus.Text = "测试结束；主动恢复跟随后继续。"; };
        preferencesPanel.Children.Add(keyTestBox); preferencesPanel.Children.Add(keyTestStatus);
        preferencesPanel = DiagnosticSettingsContent;
        AddHeading(preferencesPanel, "配音包与播放问题");
        var diagnosticButtons = new WrapPanel(); preferencesPanel.Children.Add(diagnosticButtons);
        checkPackButton.Click += async (_, _) => await CheckCurrentPack(); diagnosticButtons.Children.Add(checkPackButton);
        AddButton(diagnosticButtons, "取消检查", () => diagnosticCancellation?.Cancel());
        AddButton(diagnosticButtons, "导出诊断", ExportDiagnostics);
        AddButton(diagnosticButtons, "重试本句", () => { if (engine?.Mode != RunMode.Original) engine?.Replay(); });
        preferencesPanel.Children.Add(diagnosticStatus);
        diagnosticList.ItemTemplate = WrappedTextTemplate(); preferencesPanel.Children.Add(diagnosticList);
        var bookmarkPanel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddHeading(preferencesPanel, "书签");
        var bookmarkButton = new Button { Content = "记住当前位置", Margin = new Thickness(5, 0, 0, 0) };
        DockPanel.SetDock(bookmarkButton, Dock.Right); bookmarkPanel.Children.Add(bookmarkButton); bookmarkPanel.Children.Add(bookmarkNote);
        bookmarkButton.Click += (_, _) => SaveBookmark(); preferencesPanel.Children.Add(bookmarkPanel);
        experienceReady = true; ApplyReadingScale(); RefreshAudioDevices();
        try { audio.SelectDevice(preferences.OutputDeviceId); } catch (Exception ex) { deviceStatus.Text = ex.Message; }
        audio.PlaybackRequestFailed += (request, message) => Dispatcher.BeginInvoke(() =>
        {
            if (closing || request != Interlocked.Read(ref audioRequest) || audio.LastError!=message || audio.Playing) return;
            if (automaticRunning) StopAutomatic("声音播放中断，自动播放已暂停。");
            if (previewingHistory) historyStatus.Text = "试听失败：" + message;
            lastAudioProblem = message; previewingHistory = false; playingId = null;
            engine?.PauseForBrowse(); PaintPlaying(); Tell("声音播放中断：" + message); diagnosticStatus.Text = message;
        });
        audio.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if(!closing && previewingHistory && !audioStarting && !audio.Playing){previewingHistory=false;historyStatus.Text="试听结束，剧情位置未改变。";}
        });
        audio.DevicesChanged += () => Dispatcher.BeginInvoke(() => { if (!closing) RefreshAudioDevices(); });
    }

    static DataTemplate WrappedTextTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        return new DataTemplate { VisualTree = text };
    }
    static void AddHeading(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Foreground = Theme.Brush("NormalAccent"), Margin = new Thickness(0, 12, 0, 6), FontWeight = FontWeights.SemiBold });
    static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 3, 5, 3), Padding = new Thickness(8, 5, 8, 5) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    void ApplyReadingScale()
    {
        double scale = new[] { 1d, 1.25, 1.5, 2d }.Contains(preferences.ReadingScale) ? preferences.ReadingScale : 1;
        Resources["DialogueSize"] = 13 * scale; Resources["DialogueHeight"] = 21 * scale;
        Resources["CurrentDialogueSize"] = 15 * scale; Resources["CurrentDialogueHeight"] = 23 * scale;
        historyList.FontSize = 13 * scale; branchMenu.Options.FontSize = 14 * scale;
    }
    void ApplyCompactView()
    {
        bool strip = preferences.CompactMode == "strip";
        BallView.Visibility = strip ? Visibility.Collapsed : Visibility.Visible;
        StripView.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        Width = strip ? Math.Min(460, SystemParameters.WorkArea.Width - 20) : 64;
        Height = strip ? Math.Min(PreferredCompactHeight, SystemParameters.WorkArea.Height - 20) : 64;
        UpdateExperienceStatus();
    }
    void StripExpandClick(object sender, RoutedEventArgs e) => OpenStory();
    void StripDrag(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? target = e.OriginalSource as DependencyObject; target != null && target != StripView; target = VisualTreeHelper.GetParent(target))
            if (target is Button || target is System.Windows.Controls.Primitives.ScrollBar) return;
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); ClampPosition(); Save(); }
    }
    void UpdateExperienceStatus()
    {
        if (!experienceReady) return;
        UndoCorrectionButton.IsEnabled = engine?.CanUndoCorrection == true && engine.Mode != RunMode.Original;
        StripSpeaker.Text = engine?.Current?.Speaker ?? "剧情配音";
        StripText.Text = GameBranchLineText();
        StripState.Text = StateText.Text;
        if (engine?.Pack.IsDraft == true) StripState.Text = "抽检草稿 · 未逐句核验 · " + StripState.Text;
        StripView.BorderBrush = engine?.MenuWaiting == true ? LineRow.BranchAccent : Theme.Brush("NormalAccent");
        RefreshMouseFollow(); RefreshFollowInputStatus(); RefreshCompactFollowControls();
    }
    string FollowReason()
    {
        if (textArmed) return GameIsForeground() ? "文字跟随中" : "后台文字跟随中";
        if (engine?.Mode == RunMode.Original) return "游戏原声时段";
        if (engine?.MenuWaiting == true) return "分支或续接待选";
        if (engine?.Mode == RunMode.Paused) return "手动暂停";
        if (expanded) return "面板选句中";
        if (game == null || !Native.IsWindow(game.Handle)) return connectionNotice;
        if (Native.GetForegroundWindow() != game.Handle) return "游戏在后台";
        if (engine?.Mode == RunMode.Ready) return "等待确认播放";
        return "自动切换 · " + FollowInputLabel;
    }
    void ReconnectGameIfNeeded()
    {
        if (testUi || DateTime.UtcNow - lastReconnectCheck < TimeSpan.FromSeconds(2)) return;
        lastReconnectCheck = DateTime.UtcNow;
        if (game != null && Native.IsWindow(game.Handle) && (preferences.GameExecutablePath.Length == 0 || GameMatchesPreference(game))) return;
        game = null;
        if (preferences.GameProcess.Length == 0) { connectionNotice = "未绑定游戏"; return; }
        var candidates = Native.Windows().Where(GameMatchesPreference).ToList();
        if (candidates.Count == 1) { BindGame(candidates[0]); connectionNotice = "已重新连接游戏"; }
        else { connectionNotice = candidates.Count == 0 ? "游戏未打开" : "找到多个游戏窗口，请选择"; GameLabel.Text = connectionNotice; }
    }
    bool HandleTestKey(Key key)
    {
        if (!KeyTesting) return false;
        var action = preferences.Keys.FirstOrDefault(x => KeyBindingWindow.Matches(x.Value, key)).Key;
        keyTestStatus.Text = "收到：" + KeyBindingWindow.Display(key) + "\n动作：" + (action == null ? "未绑定" : actionLabels.GetValueOrDefault(action, action)) + "\n状态：" + FollowReason() + "\n测试中，没有执行剧情操作。";
        if (key == Key.Escape) keyTestBox.IsChecked = false;
        return true;
    }
    void FocusCatalog()
    {
        Expand(StoryTab); UpdateLayout();
        void FocusVisible(){if(chapterPickerButtons.TryGetValue(LibraryBox,out var buttons) && buttons.Chapter.IsVisible)Keyboard.Focus(buttons.Chapter);else if(ChapterBox.IsVisible)Keyboard.Focus(ChapterBox);else Keyboard.Focus(sectionPickerButtons[SectionBox]);}
        FocusVisible();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,FocusVisible);
    }
    void ReselectClick(object sender, RoutedEventArgs e) => Reselect();
    void HistoryClick(object sender, RoutedEventArgs e) => OpenHistory();
    void UndoCorrectionClick(object sender, RoutedEventArgs e)
    {
        StopPreview(); CancelOcr();
        if (engine?.UndoCorrection() == true) { BrowseCurrent(); Tell("已撤销纠偏；确认播放或恢复跟随后继续。"); }
        else Tell(engine?.NavigationError ?? "没有可撤销的纠偏。");
    }
    void BookmarkClick(object sender, RoutedEventArgs e) => SaveBookmark();
    void CurrentClick(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchChapterBox.IsChecked = false; BrowseCurrent(); }
    void SearchScopeChanged(object sender, RoutedEventArgs e) { if (ready) FillLines(); }
    void Reselect()
    {
        if (engine == null) return;
        StopListeningForGame();
        StopPreview(); CancelOcr();
        applyReselectHighlight=true;
        if (!engine.ReselectLastChoice()) Tell(engine.NavigationError.Length > 0 ? engine.NavigationError : "这里还没有可恢复的选择记录。");
    }
    void OpenHistory()
    {
        StopPreview(); engine?.PauseForBrowse(); CancelOcr(); Expand(historyTab); RefreshHistory(); historyList.Focus();
    }
    void RefreshHistory()
    {
        if (!experienceReady || engine == null) return;
        var previous = historyList.SelectedItem as HistoryItem;
        var entries = new List<HistoryItem>();
        if (historyFilter.SelectedIndex == 1)
            entries.AddRange(engine.RecentChoices.Select(c => new HistoryItem("choice", -1, c.Sequence, NodeLocation(c.MenuId) + "\n选择：" + c.Label)));
        else if (historyFilter.SelectedIndex == 2)
            entries.AddRange(progressStore!.Bookmarks(engine.Pack.Id).Select(b => new HistoryItem("bookmark", -1, 0, b.Label, b)));
        else
            for (int i = engine.HistoryPosition; i >= 0; i--)
            {
                var visit = engine.History[i];
                if (engine.Pack.ById.TryGetValue(visit.NodeId, out var node)) entries.Add(new("line", i, 0, NodeLocation(node.Id) + "\n" + node.Speaker + "：" + node.Text));
            }
        historyList.ItemsSource = entries;
        historyList.SelectedItem = entries.FirstOrDefault(x => x.Kind == previous?.Kind && x.Index == previous.Index && x.Sequence == previous.Sequence && x.Bookmark?.Id == previous.Bookmark?.Id) ?? entries.FirstOrDefault();
        historyStatus.Text = engine.Mode == RunMode.Original ? "游戏原声期间可查看记录；试听和路线恢复已暂停。" : "浏览不发声 · 回车试听台词 · “从这里继续”才改变位置";
    }
    string NodeLocation(string id)
    {
        if (engine == null || !engine.Pack.ById.TryGetValue(id, out var node)) return id;
        string section = engine.Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == node.SectionId)?.Title ?? "";
        var labels = new List<string>(); var seen = new HashSet<string>(); string path = node.PathId;
        while (path.Length > 0 && seen.Add(path))
        {
            var parent = engine.Pack.Nodes.FirstOrDefault(n => n.Options.Any(o => o.PathId == path));
            if (parent == null) break;
            labels.Add(parent.Options.First(o => o.PathId == path).Label); path = parent.PathId;
        }
        labels.Reverse(); return section + (labels.Count > 0 ? " → " + string.Join(" → ", labels) : "");
    }
    void StopPreview()
    {
        if (!previewingHistory) return;
        StopAudio(); previewingHistory = false; historyStatus.Text = "试听已停止，剧情位置未改变。";
    }
    void PreviewHistory()
    {
        if (engine == null || engine.Mode == RunMode.Original || historyList.SelectedItem is not HistoryItem { Kind: "line" } item) return;
        StopListeningForGame();
        engine.PauseForBrowse(); CancelOcr();
        var node = engine.Pack.ById.GetValueOrDefault(engine.History[item.Index].NodeId);
        if (node == null) return;
        string notice = engine.Pack.AudioNotice(node);
        if (notice.Length > 0) { historyStatus.Text = notice; return; }
        try { previewingHistory = true; if (!testUi) StartAudio(engine.Pack.ResolveAudio(node)!, node.Speaker); historyStatus.Text = "试听中 · " + node.Speaker + " · 剧情位置未改变"; }
        catch (Exception ex) { previewingHistory = false; historyStatus.Text = "试听失败：" + ex.Message; }
    }
    void ContinueHistory()
    {
        if (engine == null || engine.Mode == RunMode.Original || historyList.SelectedItem is not HistoryItem item) return;
        StopPreview(); CancelOcr();
        applyReselectHighlight=item.Kind=="choice";
        bool restored = item.Kind switch { "choice" => engine.ReselectChoice(item.Sequence), "bookmark" => engine.RestoreBookmark(item.Bookmark!), _ => engine.RestoreVisit(item.Index, false) };
        if (!restored) { historyStatus.Text = engine.NavigationError; return; }
        singleResume=engine.ReviewRoute!=null;
        if (!engine.MenuWaiting) { Expand(StoryTab); BrowseCurrent(); Tell("已定位；确认播放后继续。"); }
    }
    void SaveBookmark()
    {
        if (engine?.Current == null) { Tell("先选择剧情位置，再添加书签。"); return; }
        string label = bookmarkNote.Text.Trim();
        if (label.Length == 0) label = NodeLocation(engine.CurrentId!) + " · " + engine.Current.Text[..Math.Min(40, engine.Current.Text.Length)];
        try { progressStore!.SaveBookmark(engine.Pack.Id, engine.CreateBookmark(label)); Tell("已记住这里：" + label); RefreshHistory(); }
        catch (Exception ex) { ShowSaveFailure(ex.Message); }
    }
    void DeleteBookmark()
    {
        if (engine == null || historyList.SelectedItem is not HistoryItem { Bookmark: not null } item) return;
        try { progressStore!.RemoveBookmark(engine.Pack.Id, item.Bookmark.Id); RefreshHistory(); }
        catch (Exception ex) { ShowSaveFailure(ex.Message); }
    }
    void RefreshAudioDevices()
    {
        refreshingExperience = true;
        try
        {
            var devices = audio.GetDevices(); devicesBox.ItemsSource = devices;
            devicesBox.SelectedItem = devices.FirstOrDefault(d => d.Id == preferences.OutputDeviceId);
            deviceStatus.Text = devicesBox.SelectedItem == null ? "之前选择的声音设备未连接，请重新选择。" : "输出：" + devicesBox.SelectedItem;
        }
        catch (Exception ex) { deviceStatus.Text = ex.Message; }
        finally { refreshingExperience = false; }
    }
    async Task CheckCurrentPack()
    {
        if (engine == null || diagnosticCancellation != null) return;
        var owner = engine; var cancel = diagnosticCancellation = new();
        checkPackButton.IsEnabled = false; diagnosticStatus.Text = "正在检查当前章，随时可取消…";
        try
        {
            var report = await PlayerDiagnostics.ScanAsync(owner.Pack, cancel.Token, new Progress<DiagnosticProgress>(p => {if(owner==engine && ReferenceEquals(diagnosticCancellation,cancel) && !cancel.IsCancellationRequested)diagnosticStatus.Text = $"已检查 {p.CompletedLines} / {p.TotalLines} 句";}));
            if (owner != engine || cancel.IsCancellationRequested) return;
            diagnosticReport = report;
            diagnosticStatus.Text = $"检查完成：{report.TotalLines} 句，{report.ReadableLines} 句录音可读取，{report.Issues.Count} 项需查看。";
            diagnosticList.ItemsSource = report.Issues.Select(x => $"{x.CategoryLabel} · {x.Speaker}：{x.Text}\n{x.Detail}  [{x.NodeId}]").ToList();
        }
        catch (OperationCanceledException) { if(owner==engine)diagnosticStatus.Text = "检查已取消，配音包没有改动。"; }
        catch (Exception ex) { if(owner==engine)diagnosticStatus.Text = "检查失败：" + ex.Message; }
        finally { if(ReferenceEquals(diagnosticCancellation,cancel)){diagnosticCancellation = null; checkPackButton.IsEnabled = true;}cancel.Dispose(); }
    }
    void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "剧情配音诊断.txt", Filter = "文本报告|*.txt|JSON 报告|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var logs=File.Exists(Path.Combine(Log.DataDir,"player.log"))?File.ReadLines(Path.Combine(Log.DataDir,"player.log")).TakeLast(80).ToArray():Array.Empty<string>();
            var textConnections = textProbes.Select(p => new { title = p.Title, status = p.Status.Text, details = p.Diagnostics }).ToArray();
            var context=new {version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),chapter=engine?.Pack.Title,nodeId=engine?.CurrentId,state=FollowReason(),audioError=lastAudioProblem,device=audio.CurrentDeviceName,packCheck=diagnosticReport,textConnections,log=logs};
            if(Path.GetExtension(dialog.FileName).Equals(".json",StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(dialog.FileName,System.Text.Json.JsonSerializer.Serialize(context,Json.Options));
            else
            {
                var text=new StringBuilder($"剧情配音播放器诊断\n版本：{context.version}\n章节：{context.chapter}\n节点：{context.nodeId}\n状态：{context.state}\n声音输出：{context.device}\n播放问题：{context.audioError}\n");
                if(diagnosticReport!=null){text.AppendLine(diagnosticReport.Summary);foreach(var issue in diagnosticReport.Issues)text.AppendLine($"[{issue.CategoryLabel}] {issue.NodeId} {issue.Speaker}：{issue.Text}\n{issue.Audio} · {issue.Detail}");}
                text.AppendLine("\n最近一次文字连接：");
                foreach (var connection in textConnections) text.AppendLine($"{connection.title}：{connection.status}\n{connection.details}");
                text.AppendLine("\n最近运行记录：");foreach(var line in logs)text.AppendLine(line);
                File.WriteAllText(dialog.FileName,text.ToString());
            }
            diagnosticStatus.Text = "诊断已保存：" + dialog.FileName;
        }
        catch (Exception ex) { diagnosticStatus.Text = "导出失败：" + ex.Message; }
    }
    void ShowSaveFailure(string error)
    {
        SaveWarning.Text = "本次进度尚未保存：" + error;
        SaveWarning.Visibility = Visibility.Visible; Log.Write("save-error", error);
    }
    void GuideUnselectedRoute(Node node)
    {
        if(engine==null || engine.Mode==RunMode.Original)return;
        var owner=engine.Pack.Nodes.FirstOrDefault(n=>!n.Archived && n.Options.Any(o=>o.PathId==node.PathId));
        var seen=new HashSet<string>();
        while(owner!=null && !engine.Allowed(owner) && seen.Add(owner.Id))
            owner=engine.Pack.Nodes.FirstOrDefault(n=>!n.Archived && n.Options.Any(o=>o.PathId==owner.PathId));
        if(owner==null){Tell("这句所属路线尚未核实，请先定位游戏当前台词。");return;}
        StopPreview();CancelOcr();
        var previous=engine.RecentChoices.FirstOrDefault(c=>c.MenuId==owner.Id);
        if(previous!=null){applyReselectHighlight=true;engine.ReselectChoice(previous.Sequence);}
        else engine.Commit(owner.Id);
        Tell("已打开所属选择菜单，请按游戏当前画面确认路线。");
    }
    static string ReadPackIdentity(string path)
    {
        try
        {
            using var file=File.OpenRead(path);var bytes=new byte[16384];int count=file.Read(bytes);
            var reader=new System.Text.Json.Utf8JsonReader(bytes.AsSpan(0,count),false,default);
            while(reader.Read())if(reader.TokenType==System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth==1 && reader.ValueTextEquals("id")){return reader.Read()?reader.GetString()??"":"";}
        }
        catch(Exception ex){Log.Write("library",ex.Message);}
        return "";
    }
    PackChoice ReadPackChoice(string path)
    {
        string id=ReadPackIdentity(path),title=Path.GetFileName(Path.GetDirectoryName(path))??path,order=path;
        try
        {
            using var file=File.OpenRead(path);var bytes=new byte[16384];int count=file.Read(bytes);
            var reader=new System.Text.Json.Utf8JsonReader(bytes.AsSpan(0,count),false,default);
            while(reader.Read())
            {
                if(reader.TokenType!=System.Text.Json.JsonTokenType.PropertyName)continue;
                if(reader.CurrentDepth==1 && reader.ValueTextEquals("nodes"))break;
                if(reader.CurrentDepth==1 && reader.ValueTextEquals("title")){if(reader.Read())title=reader.GetString()??title;}
                else if(reader.CurrentDepth==2 && reader.ValueTextEquals("sortOrder")){if(reader.Read())order=reader.GetString()??order;}
            }
        }
        catch(Exception ex){Log.Write("library",ex.Message);}
        return new PackChoice(path,title){PackId=id,SortOrder=order,Resume=progressStore?.GetSummary(id)??""};
    }
    static string SearchNormalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))).ToUpperInvariant();
}
