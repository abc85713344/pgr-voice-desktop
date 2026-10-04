using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    readonly AudioService listeningAudio = new();
    readonly SemaphoreSlim listeningAudioQueue = new(1, 1);
    readonly ComboBox listeningPacks = new(), listeningChapters = new(), listeningSections = new();
    readonly ListBox listeningLines = new();
    readonly ComboBox listeningPolicy = new();
    readonly TextBlock listeningPosition = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    readonly TextBlock listeningText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 17, Margin = new Thickness(0, 10, 0, 10) };
    readonly TextBlock listeningStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock listeningResume = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    readonly TextBox listeningBookmarkName = new() { MinWidth = 150, ToolTip = "书签名称（可选）" };
    readonly ComboBox listeningBookmarks = new();
    readonly StackPanel listeningChoices = new();
    readonly Button listeningPlay = new() { Content = "播放 / 续听" };
    TabItem listeningTab = null!;
    ListeningSession? listeningSession;
    ListeningProgressStore? listeningStore;
    ListeningProgressDocument? listeningDocument;
    DispatcherTimer? listeningSaveTimer;
    long listeningRequest, listeningTicket, listeningOffset;
    bool listeningReady, listeningRefreshing, listeningRunning, listeningActive, listeningStarting, listeningDisposed, listeningPreserveResume;
    bool listeningTestAudio, listeningBrowsingLocation;
    int listeningSkipped, listeningNotices;
    string listeningFile = "", listeningMessage = "选择大章，离线连续收听。本章小节会依次播放。", listeningProgressWarning = "", listeningSelectionWarning = "", listeningRouteNotice = "";
    string listeningSaveWarning => string.Join("\n", new[] { listeningProgressWarning, listeningSelectionWarning }.Where(x => x.Length > 0));
    ListeningLastSelection listeningLast = new();
    bool ListeningActive => listeningActive;
    sealed class ListeningLastSelection { public ListeningLastSelection() { } public string File { get; set; } = ""; public string ChapterId { get; set; } = ""; }
    sealed record ListeningEntry(string Id, string Label, bool IsChoice = false, string? ItemId = null, bool IsPreview = false)
    { public override string ToString() => Label; }
    string ListeningSettingsFile => Path.Combine(Log.DataDir, "listening", "last-selection.json");
    ListeningChapterProgress? ListeningProgress => listeningSession == null ? null : listeningDocument?.ForChapter(listeningSession.ChapterId);
    long ListeningPositionMs => listeningTicket != 0 && !listeningStarting && !listeningTestAudio
        ? (long)Math.Max(0, listeningAudio.PositionSeconds * 1000) : listeningOffset;

    void InitializeListeningExperience()
    {
        listeningStore = new(Path.Combine(Log.DataDir, "listening"));
        try { if (File.Exists(ListeningSettingsFile) || File.Exists(ListeningSettingsFile + ".bak")) listeningLast = Json.ReadWithBackup<ListeningLastSelection>(ListeningSettingsFile, out _); }
        catch (Exception ex) { listeningMessage = "上回章节暂不能恢复，请重新选择。"; Log.Write("listening", ex.Message); }
        listeningLast.File ??= ""; listeningLast.ChapterId ??= "";
        BuildListeningLayout();
        Tabs.Items.Insert(Math.Min(2, Tabs.Items.Count), listeningTab);
        listeningPacks.DropDownOpened += (_, _) => RefreshListeningPacks();
        listeningPacks.SelectionChanged += (_, _) =>
        {
            if (!listeningReady || listeningRefreshing) return;
            if (RefreshListeningChapterChoices()) OpenSelectedListeningChapter();
            else RestoreListeningSelectors();
        };
        listeningChapters.SelectionChanged += (_, _) =>
        { if (listeningReady && !listeningRefreshing && listeningChapters.SelectedItem != null) OpenSelectedListeningChapter(); };
        listeningSections.SelectionChanged += (_, _) => { if (!listeningRefreshing) { listeningBrowsingLocation = true; RefreshListeningLineChoices(); } };
        listeningLines.SelectionChanged += (_, _) =>
        { if (!listeningRefreshing) { listeningBrowsingLocation = true; RefreshListeningLocateButton(); } };
        listeningPolicy.SelectionChanged += (_, _) =>
        {
            if (!listeningReady || listeningRefreshing || listeningSession == null) return;
            var selectedPolicy = ListeningSelectedPolicy;
            if (!PauseListening()) return;
            listeningSession.SetPolicy(selectedPolicy); listeningOffset = 0;
            listeningPreserveResume = false; SaveListeningProgress(); RefreshListening();
        };
        Tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs) || !listeningReady) return;
            if (Tabs.SelectedItem != listeningTab)
            { HeaderChapter.Text = SectionBox.SelectedItem?.ToString() ?? engine?.Pack.Title ?? "选择章节，继续你的故事"; return; }
            ActivateListening(); RefreshListeningPacks();
            if (listeningSession == null)
            {
                if (listeningLast.File.Length > 0) OpenLastListeningChapter();
                else OpenSelectedListeningChapter();
            }
            RefreshListening();
        };
        listeningAudio.PlaybackCompleted += request => Dispatcher.BeginInvoke(() => OnListeningCompleted(request));
        listeningAudio.PlaybackRequestFailed += (request, error) => Dispatcher.BeginInvoke(() =>
        {
            if (!listeningRunning || listeningDisposed || request != listeningRequest || request != listeningTicket) return;
            PauseListening(); listeningMessage = "听书播放中断：" + error; RefreshListening();
        });
        listeningSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        listeningSaveTimer.Tick += (_, _) => { if (listeningRunning) { SaveListeningProgress(); RefreshListeningStatus(); } };
        listeningSaveTimer.Start(); listeningReady = true; RefreshListening();
    }
    ListeningBranchPolicy ListeningSelectedPolicy => listeningPolicy.SelectedIndex switch { 1 => ListeningBranchPolicy.First, 2 => ListeningBranchPolicy.All, _ => ListeningBranchPolicy.Manual };
    void ActivateListening()
    {
        if (!listeningActive) { SuspendGameForListening(); listeningActive = true; }
    }
    void RefreshListeningPacks(string? preferredFile = null)
    {
        string selected = preferredFile ?? (listeningPacks.SelectedItem as PackChoice)?.File ?? listeningFile;
        listeningRefreshing = true;
        try
        {
            var packs = LibraryBox.Items.OfType<PackChoice>().ToList();
            if (listeningSession != null && listeningFile.Length > 0 && !packs.Any(p => String.Equals(p.File, listeningFile, StringComparison.OrdinalIgnoreCase)))
                packs.Add(new PackChoice(listeningFile, listeningSession.Pack.Title) { PackId = listeningSession.Pack.Id });
            listeningPacks.ItemsSource = packs;
            listeningPacks.SelectedItem = packs.FirstOrDefault(x => String.Equals(x.File, selected, StringComparison.OrdinalIgnoreCase)) ?? packs.FirstOrDefault();
        }
        finally { listeningRefreshing = false; }
        RefreshListeningChapterChoices();
    }
    bool RefreshListeningChapterChoices()
    {
        if (listeningPacks.SelectedItem is not PackChoice choice) return false;
        bool wasRefreshing = listeningRefreshing; listeningRefreshing = true;
        try
        {
            var pack = Pack.Load(choice.File);
            var chapters = pack.Chapters.Select(c => new ListeningEntry(c.Id, c.Title)).ToList();
            listeningChapters.ItemsSource = chapters;
            bool samePack = String.Equals(choice.File, listeningFile, StringComparison.OrdinalIgnoreCase);
            listeningChapters.SelectedItem = chapters.FirstOrDefault(c => samePack && c.Id == listeningSession?.ChapterId) ?? chapters.FirstOrDefault();
            listeningChapters.Visibility = chapters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            return chapters.Count > 0;
        }
        catch (Exception ex) { listeningMessage = "章节暂时无法读取：" + ex.Message; RefreshListeningStatus(); return false; }
        finally { listeningRefreshing = wasRefreshing; }
    }
    void RestoreListeningSelectors()
    {
        if (listeningSession != null) RefreshListeningPacks(listeningFile);
    }
    void OpenSelectedListeningChapter()
    {
        if (listeningPacks.SelectedItem is PackChoice choice && listeningChapters.SelectedItem is ListeningEntry chapter) OpenListeningPack(choice.File, chapter.Id);
        else { listeningMessage = "先在剧情页导入配音包，再选择要听的大章。"; RefreshListeningStatus(); }
    }
    void OpenLastListeningChapter()
    {
        if (listeningLast.File.Length == 0) { listeningMessage = "还没有上回听书记录。"; RefreshListeningStatus(); return; }
        OpenListeningPack(listeningLast.File, listeningLast.ChapterId);
    }
    void OpenListeningPack(string file, string? chapterId)
    {
        try
        {
            var pack = Pack.Load(file); chapterId ??= pack.Chapters.FirstOrDefault()?.Id;
            if (chapterId == null) throw new InvalidDataException("配音包没有可听的大章。");
            var next = new ListeningSession(pack, chapterId, ListeningBranchPolicy.Manual);
            if (!PauseListening())
            {
                listeningMessage = "当前听书位置尚未保存，已保留当前大章；请处理保存问题后再切换。";
                RestoreListeningSelectors(); RefreshListeningStatus(); return;
            }
            var document = listeningStore!.Load(pack.Id, out string notice);
            var resume = document.ForChapter(chapterId).Resume;
            string restoredNotice = "";
            bool restored = resume != null && next.TryRestore(resume, out restoredNotice);
            if (resume != null && !restored) notice += "上回位置与当前配音包不匹配，原记录已保留；请重新定位。";
            ActivateListening();
            listeningSession = next; listeningDocument = document; listeningFile = file;
            listeningBrowsingLocation = false;
            listeningPreserveResume = resume != null && !restored; listeningOffset = restored ? next.ResumePositionMs : 0;
            listeningSkipped = listeningNotices = 0; listeningRouteNotice = "";
            listeningMessage = notice.Length > 0 ? notice : restoredNotice.Length > 0 ? restoredNotice : restored ? "已恢复上回位置，点击播放继续。" : "已打开大章，点击播放开始。";
            listeningLast = new() { File = file, ChapterId = chapterId }; SaveListeningSelection(); RefreshListeningPacks(file); RefreshListening();
        }
        catch (Exception ex) { listeningMessage = "无法打开听书章节：" + ex.Message; RestoreListeningSelectors(); RefreshListeningStatus(); }
    }
    void SaveListeningSelection()
    {
        try { Json.Save(ListeningSettingsFile, listeningLast); listeningSelectionWarning = ""; }
        catch (Exception ex) { listeningSelectionWarning = "上回章节保存失败：" + ex.Message; }
    }
    void StartListening()
    {
        if (listeningSession == null) { OpenSelectedListeningChapter(); if (listeningSession == null) return; }
        if (listeningSession.Completed) { listeningMessage = "本章已听完，可从目录选择小节重听。"; RefreshListening(); return; }
        ActivateListening(); listeningPreserveResume = false; listeningRunning = true;
        long request = ++listeningRequest; PlayListeningCurrent(request);
    }
    void PlayListeningCurrent(long request)
    {
        if (!listeningRunning || listeningSession == null || request != listeningRequest || listeningDisposed) return;
        for (int i = 0; i < 32; i++)
        {
            var current = listeningSession.Current;
            if (current == null)
            { listeningRunning = false; listeningTicket = 0; listeningOffset = 0; listeningMessage = "本章已听完。"; SaveListeningProgress(); RefreshListening(); return; }
            if (current.Kind == ListeningItemKind.Choice)
            {
                listeningRunning = false; listeningTicket = 0; listeningOffset = 0;
                listeningMessage = current.Notice.StartsWith("等待互动", StringComparison.Ordinal)
                    ? "等待互动：请选择下面的互动选项，确认后继续本节。"
                    : "遇到支线，请选择要听的路线。";
                SaveListeningProgress(); RefreshListening(); return;
            }
            if (current.Kind == ListeningItemKind.Notice)
            { listeningNotices++; listeningRouteNotice = current.Notice; Log.Write("listening-route", current.Notice); listeningSession.MoveNext(); listeningOffset = 0; continue; }
            // 战斗开始、互动目标等动作提示本来就不朗读，不把它们误报为缺配音。
            if (current.Node?.AudioStatus == "not-spoken")
            { listeningSession.MoveNext(); listeningOffset = 0; continue; }
            string? file = current.Node == null ? null : listeningSession.Pack.ResolveAudio(current.Node);
            if (file == null || !File.Exists(file))
            { listeningSkipped++; listeningSession.MoveNext(); listeningOffset = 0; continue; }
            listeningTicket = request; listeningStarting = true;
            listeningMessage = "正在收听" + (current.BranchLabel.Length > 0 ? " · " + current.BranchLabel : "");
            SaveListeningProgress(); RefreshListening();
            if (listeningTestAudio) { listeningStarting = false; return; }
            long offset = listeningOffset; string device = preferences.OutputDeviceId;
            listeningAudio.Volume = (float)preferences.Volume / 100;
            _ = Task.Run(async () =>
            {
                await listeningAudioQueue.WaitAsync();
                string? error = null;
                try
                {
                    if (request != Volatile.Read(ref listeningRequest) || listeningDisposed) return;
                    listeningAudio.SelectDevice(device);
                    listeningAudio.Play(file, request, offset / 1000d);
                    if (request != Volatile.Read(ref listeningRequest)) listeningAudio.Stop();
                }
                catch (Exception ex) { error = ex.Message; }
                finally { listeningAudioQueue.Release(); }
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (request != listeningRequest || listeningDisposed) return;
                    listeningStarting = false;
                    if (error != null) { PauseListening(); listeningMessage = "本句播放失败，已暂停：" + error; RefreshListening(); }
                });
            });
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PlayListeningCurrent(request)));
    }
    void OnListeningCompleted(long request)
    {
        if (!listeningRunning || listeningSession == null || listeningTicket != request || request != listeningRequest || listeningDisposed) return;
        listeningTicket = 0; listeningOffset = 0; listeningStarting = false;
        listeningSession.MoveNext(); SaveListeningProgress();
        long next = ++listeningRequest;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PlayListeningCurrent(next)));
    }
    bool PauseListening()
    {
        if (listeningTicket != 0) listeningOffset = ListeningPositionMs;
        ++listeningRequest; listeningRunning = false; listeningTicket = 0; listeningStarting = false;
        QueueListeningStop(listeningRequest); bool saved = SaveListeningProgress();
        listeningMessage = saved ? "听书已暂停，位置已保存。" : "听书已暂停，当前位置暂未保存。";
        RefreshListening(); return saved;
    }
    void QueueListeningStop(long request)
    {
        _ = Task.Run(async () =>
        {
            await listeningAudioQueue.WaitAsync();
            try { if (!listeningDisposed && request == Volatile.Read(ref listeningRequest)) listeningAudio.Stop(); }
            finally { listeningAudioQueue.Release(); }
        });
    }
    void StopListeningForGame()
    {
        if (!listeningReady || !listeningActive) return;
        PauseListening(); listeningActive = false;
    }
    void SetListeningVolume(float volume)
    {
        if (!listeningDisposed) listeningAudio.Volume = volume;
    }
    void ShutdownListening()
    {
        if (listeningDisposed) return;
        StopListeningForGame(); SaveListeningProgress(); listeningSaveTimer?.Stop(); listeningDisposed = true;
        _ = Task.Run(async () => { await listeningAudioQueue.WaitAsync(); try { listeningAudio.Dispose(); } finally { listeningAudioQueue.Release(); } });
    }
    void MoveListening(Func<bool> move)
    {
        if (listeningSession == null) return;
        if (!PauseListening()) { listeningMessage = "当前位置暂未保存，未移动听书位置。"; RefreshListeningStatus(); return; }
        ActivateListening();
        if (move() || listeningSession.Completed) { listeningOffset = 0; listeningPreserveResume = false; listeningBrowsingLocation = false; SaveListeningProgress(); listeningMessage = listeningSession.Completed ? "本章已听完。" : listeningSession.HasPendingChoice ? "已打开本节选项，确认后继续收听。" : "已定位，点击播放继续。"; }
        else listeningMessage = listeningSession.HasPendingChoice ? "请选择分支，或明确跳过本处选择。" : "已经到达可用台词边界。";
        RefreshListening();
    }
    bool SaveListeningProgress()
    {
        if (listeningSession != null && listeningDocument != null && listeningStore != null && (!listeningPreserveResume || listeningProgressWarning.Length > 0))
        {
            try
            {
                // 无法恢复的旧断点保持原样；书签保存失败仍允许重试，不能永远卡住退出。
                if (!listeningPreserveResume) ListeningProgress!.Resume = listeningSession.Capture(ListeningPositionMs);
                listeningStore.Save(listeningDocument); listeningProgressWarning = "";
            }
            catch (Exception ex) { listeningProgressWarning = "听书位置保存失败：" + ex.Message; Log.Write("listening-save", ex.Message); }
        }
        // 断点与“上回章节”是两个文件：写好前者不能掩盖后者的失败。
        if (listeningSelectionWarning.Length > 0) SaveListeningSelection();
        return listeningSaveWarning.Length == 0;
    }
    void AddListeningBookmark()
    {
        if (listeningSession?.Current?.Kind != ListeningItemKind.Line || ListeningProgress == null) return;
        PauseListening(); string label = listeningBookmarkName.Text.Trim();
        if (label.Length == 0) label = listeningSession.Current.PositionLabel;
        ListeningProgress.Bookmarks.Add(new() { Label = label, Snapshot = listeningSession.Capture(listeningOffset) });
        listeningPreserveResume = false; SaveListeningProgress(); listeningMessage = "已记下书签：" + label; RefreshListening();
    }
    void RestoreListeningBookmark()
    {
        if (listeningBookmarks.SelectedItem is not ListeningEntry selected || ListeningProgress == null || listeningSession == null) return;
        var bookmark = ListeningProgress.Bookmarks.FirstOrDefault(x => x.Id == selected.Id); if (bookmark == null) return;
        PauseListening(); ActivateListening();
        if (listeningSession.TryRestore(bookmark.Snapshot, out var reason))
        { listeningOffset = listeningSession.ResumePositionMs; listeningPreserveResume = false; listeningBrowsingLocation = false; SaveListeningProgress(); listeningMessage = "已恢复书签，点击播放继续。" + reason; }
        else listeningMessage = reason;
        RefreshListening();
    }
    void DeleteListeningBookmark()
    {
        if (listeningBookmarks.SelectedItem is not ListeningEntry selected || ListeningProgress == null || listeningDocument == null) return;
        ListeningProgress.Bookmarks.RemoveAll(x => x.Id == selected.Id);
        try { listeningStore!.Save(listeningDocument); listeningProgressWarning = ""; listeningMessage = "已删除书签。"; }
        catch (Exception ex) { listeningProgressWarning = "书签保存失败：" + ex.Message; }
        RefreshListening();
    }
    void RefreshListening()
    {
        if (!listeningReady) return;
        listeningRefreshing = true;
        try
        {
            var current = listeningSession?.Current;
            listeningPolicy.SelectedIndex = listeningSession?.Policy switch { ListeningBranchPolicy.First => 1, ListeningBranchPolicy.All => 2, _ => 0 };
            listeningPosition.Text = listeningSession == null ? "尚未选择大章" : "收听位置：" + listeningSession.ChapterTitle + " · " + (current?.PositionLabel ?? "已听完");
            if (Tabs.SelectedItem == listeningTab) HeaderChapter.Text = "听书 · " + (listeningSession?.ChapterTitle ?? "选择章节");
            listeningText.Text = current?.Kind == ListeningItemKind.Line ? (String.IsNullOrEmpty(current.Node?.Speaker) ? "" : current.Node.Speaker + "\n") + current.Node?.Text : current?.Notice ?? "";
            listeningPlay.Content = listeningRunning ? "暂停" : "播放 / 续听";
            listeningChoices.Children.Clear();
            listeningCurrentView.MaxHeight = current?.Kind == ListeningItemKind.Choice ? 175 : 105;
            if (current?.Kind == ListeningItemKind.Choice)
            {
                var choiceSession = listeningSession;
                var choiceKey = current.ChoiceKey;
                bool IsCurrentChoice() => ReferenceEquals(listeningSession, choiceSession)
                    && listeningSession?.Current?.Kind == ListeningItemKind.Choice && listeningSession.Current.ChoiceKey == choiceKey;
                foreach (var option in current.Options ?? Array.Empty<ChoiceOption>())
                {
                    string id = option.Id;
                    AddButton(listeningChoices, option.Label, () =>
                    {
                        if (!IsCurrentChoice()) return;
                        if (!PauseListening()) return;
                        if (listeningSession!.Choose(id)) { listeningOffset = 0; listeningBrowsingLocation = false; StartListening(); }
                    });
                }
                AddButton(listeningChoices, "跳过本处选择", () =>
                {
                    if (!IsCurrentChoice()) return;
                    if (!PauseListening()) return;
                    if (listeningSession!.SkipChoice()) { listeningOffset = 0; listeningBrowsingLocation = false; StartListening(); }
                });
                // 上一个长句可能滚到了底部；进入菜单后让提示与首项立即可见。
                listeningCurrentView.ScrollToTop();
            }
            var sections = listeningSession?.Chapter.Sections.Select(s => new ListeningEntry(s.Id, s.Title)).ToList() ?? new();
            string? selectedSection = listeningBrowsingLocation ? (listeningSections.SelectedItem as ListeningEntry)?.Id : current?.SectionId;
            listeningSections.ItemsSource = sections; listeningSections.SelectedItem = sections.FirstOrDefault(s => s.Id == selectedSection) ?? sections.FirstOrDefault();
            RefreshListeningLineChoices();
            var bookmarks = ListeningProgress?.Bookmarks.Select(b => new ListeningEntry(b.Id, b.Label + " · " + b.Snapshot.SectionTitle + " · 第 " + b.Snapshot.LineNumber + " 句")).ToList() ?? new();
            string? selectedBookmark = (listeningBookmarks.SelectedItem as ListeningEntry)?.Id;
            listeningBookmarks.ItemsSource = bookmarks; listeningBookmarks.SelectedItem = bookmarks.FirstOrDefault(b => b.Id == selectedBookmark) ?? bookmarks.LastOrDefault();
            var resume = ListeningProgress?.Resume;
            listeningResume.Text = resume == null ? "还没有听书记录。" : resume.Completed ? "上回已听完本章。" : $"上回：{listeningSession?.ChapterTitle} · {resume.SectionTitle} · 第 {resume.LineNumber} 句 · {resume.PositionMs / 1000} 秒";
        }
        finally { listeningRefreshing = false; }
        RefreshListeningStatus();
    }
    void RefreshListeningLineChoices()
    {
        string? section = (listeningSections.SelectedItem as ListeningEntry)?.Id;
        var current = listeningSession?.Current;
        string? selectedLine = listeningBrowsingLocation ? (listeningLines.SelectedItem as ListeningEntry)?.Id
            : current?.Kind == ListeningItemKind.Choice ? current.Id : current?.NodeId;
        var pending = ListeningPendingForSection(section);
        var available = listeningSession?.Items.Where(i => i.Kind == ListeningItemKind.Line && i.SectionId == section)
            .ToDictionary(i => i.NodeId, StringComparer.Ordinal) ?? new();
        // 阅读目录完整展示源正文；播放资格仍由独立计划控制，不能靠看到了正文就越过选择。
        var source = listeningSession?.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section).ToList() ?? new();
        var lines = source.Select((n, index) =>
        {
            bool preview = !available.ContainsKey(n.Id);
            string label = $"{index + 1}. {n.Speaker}：{n.Text}";
            if (preview) label += pending != null ? "\n〔待选择后收听〕" : "\n〔未接入当前收听路线〕";
            return new ListeningEntry(n.Id, label, ItemId: available.GetValueOrDefault(n.Id)?.Id, IsPreview: preview);
        }).ToList();
        if (pending != null)
        {
            string label = "◆ 待选互动 / 分支：" + string.Join(" / ", (pending.Options ?? Array.Empty<ChoiceOption>()).Select(o => o.Label));
            int before = listeningSession!.Pack.Nodes.TakeWhile(n => n.Id != pending.NodeId)
                .Count(n => !n.Archived && n.Kind == "line" && n.SectionId == section);
            lines.Insert(Math.Min(before, lines.Count), new(pending.Id, label, IsChoice: true, ItemId: pending.Id));
        }
        bool wasRefreshing = listeningRefreshing; listeningRefreshing = true;
        try
        {
            listeningLines.ItemsSource = lines;
            listeningLines.SelectedItem = lines.FirstOrDefault(i => i.Id == selectedLine) ?? lines.FirstOrDefault();
            if (!listeningBrowsingLocation && listeningLines.SelectedItem != null) listeningLines.ScrollIntoView(listeningLines.SelectedItem);
            listeningLineCount.Text = source.Count > 0 ? $"本小节完整正文 {source.Count} 条（含动作提示） · 浏览不播放"
                + (pending != null ? "\n后文已显示；待选内容需确认选项后收听。" : "") :
                listeningSession == null ? "选择章节后显示台词。" : pending != null ? "本节从选择开始，请打开选项。" : "本节没有收录正文。";
            listeningPendingChoice.Visibility = pending != null ? Visibility.Visible : Visibility.Collapsed;
            bool different = current != null && section != null && section != current.SectionId;
            listeningBrowseHint.Visibility = different ? Visibility.Visible : Visibility.Collapsed;
            listeningBrowseHint.Text = different ? $"正在浏览：{(listeningSections.SelectedItem as ListeningEntry)?.Label}；收听仍在：{current!.SectionTitle}。" : "";
            RefreshListeningLocateButton();
        }
        finally { listeningRefreshing = wasRefreshing; }
    }
    ListeningItem? ListeningPendingForSection(string? section) => listeningSession?.Items.FirstOrDefault(i =>
        i.SectionId == section && i.Kind == ListeningItemKind.Choice);
    void RefreshListeningLocateButton()
    {
        var selected = listeningLines.SelectedItem as ListeningEntry;
        listeningLocate.Content = selected?.IsChoice == true ? "打开选中互动 / 分支"
            : selected?.IsPreview == true ? "打开后续剧情的选项" : "定位到选中台词";
        listeningLocate.IsEnabled = selected != null;
    }
    void OpenListeningPendingChoice()
    {
        var pending = ListeningPendingForSection((listeningSections.SelectedItem as ListeningEntry)?.Id);
        if (pending != null) MoveListening(() => listeningSession!.SeekItem(pending.Id));
    }
    void LocateListeningEntry()
    {
        if (listeningLines.SelectedItem is not ListeningEntry selected || listeningSession == null) return;
        if (selected.IsPreview)
        {
            var pending = ListeningPendingForSection((listeningSections.SelectedItem as ListeningEntry)?.Id);
            if (pending != null) MoveListening(() => listeningSession.SeekItem(pending.Id));
            else { listeningMessage = "这句可在目录中阅读，但尚未接入当前收听路线。可在收听选项中选择全部听取。"; RefreshListeningStatus(); }
        }
        else if (selected.ItemId != null) MoveListening(() => listeningSession.SeekItem(selected.ItemId));
    }
    void RefreshListeningStatus() => listeningStatus.Text = listeningMessage
        + (listeningRunning ? $" · 本句 {ListeningPositionMs / 1000} 秒" : "")
        + (listeningSkipped > 0 ? $"；已跳过 {listeningSkipped} 句未收录配音" : "")
        + (listeningNotices > 0 ? $"\n路线提示：{listeningRouteNotice}" : "")
        + (listeningSaveWarning.Length > 0 ? "\n" + listeningSaveWarning : "");
}
