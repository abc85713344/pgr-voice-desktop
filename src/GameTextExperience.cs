using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    sealed class TextProbe(GameTextKind kind, string title)
    {
        public readonly GameTextKind Kind = kind;
        public readonly string Title = title;
        public readonly TextBox Anchor = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 42, MaxHeight = 90, AcceptsReturn = true };
        public readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), Text = "尚未连接" };
        public readonly Button Connect = new() { Content = "连接这路文本" };
        public readonly Button ExportConnection = new() { Content = "导出连接诊断", Visibility = Visibility.Collapsed };
        public string Diagnostics = "";
        public GameTextReader? Reader;
        public CancellationTokenSource? Binding;
        public string Observed = "", Pending = "";
        public string ObservedSpeaker = "", PendingSpeaker = "";
        public long PendingSince;
        public bool Active;
        public int Revision;
        public bool AutoConnect;
        public DateTime NextConnectAttempt;
        public readonly Expander ManualPanel = new() { Header = "手动连接（备用）", IsExpanded = false };
    }
    readonly TextProbe[] textProbes = { new(GameTextKind.Ordinary, "普通剧情 · 立绘 / 全屏对白"), new(GameTextKind.Scene3D, "3D 场景 · 战斗 / 营地对白") };
    readonly TextBlock gameTextLive = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 8, 0, 8), Text = "等待连接游戏对白" };
    readonly TextBlock gameTextNotice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    readonly Button gameTextResume = new() { Content = "开始 / 恢复配音" };
    readonly TabItem gameTextTab = new() { Header = "游戏配音" };
    readonly TabControl followModeTabs = new() { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly StackPanel memoryFollowPanel = new(), keyFollowPanel = new(), mouseFollowPanel = new(), sequenceFollowPanel = new();
    readonly ComboBox gameTextLibrary = new(), gameTextSection = new(), gameTextChapter = new();
    readonly TextBlock gameTextMode = new() { FontSize = 11, Foreground = Theme.Brush("PlayingAccent"), Margin = new Thickness(0, 0, 0, 5) };
    readonly DispatcherTimer gameTextTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    PlaybackEngine? textOwner;
    string textSection = "", lastTextPlayed = "";
    bool textArmed, applyingMemoryLine, textActiveConflict;
    TextProbe? textSoundSource;
    bool TextFollowing => textArmed || textProbes.Any(p => p.Binding != null);
    Func<TextProbe, GameTextSample>? gameTextSampleTest;

    void InitializeGameText()
    {
        gameTextTimer.Tick += (_, _) => TickGameText();
        gameTextTimer.Start();
        var panel = new StackPanel { Margin = new Thickness(0, 8, 5, 8) };
        gameTextTab.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Tabs.Items.Insert(0, gameTextTab);
        panel.Children.Add(new TextBlock { Text = "选择你的跟随方式", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "先选章节，再选下方方式。切换选项只查看设置，点击开始才启用。", FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 4, 0, 10) });
        var activeMode = new TextBlock { FontSize = 11, Foreground = Theme.Brush("PlayingAccent"), Margin = new Thickness(0, 0, 0, 8) };
        activeMode.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = StateText, StringFormat = "运行状态：{0}" }); panel.Children.Add(activeMode);
        void Mirror(ComboBox target, ComboBox source)
        {
            target.DisplayMemberPath = source.DisplayMemberPath;
            target.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("ItemsSource") { Source = source });
            target.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding("SelectedItem") { Source = source, Mode = BindingMode.TwoWay });
        }
        Mirror(gameTextLibrary, LibraryBox); Mirror(gameTextSection, SectionBox); Mirror(gameTextChapter, ChapterBox);
        var selectors = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        selectors.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); selectors.ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) });
        var chapter = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; chapter.Children.Add(new TextBlock { Text = "章节", FontSize = 11, Foreground = Theme.Brush("MutedText") }); chapter.Children.Add(gameTextLibrary); chapter.Children.Add(ChapterPickerControls(gameTextLibrary));
        var section = new StackPanel(); section.Children.Add(new TextBlock { Text = "小节", FontSize = 11, Foreground = Theme.Brush("MutedText") }); section.Children.Add(gameTextSection); section.Children.Add(SectionPickerControl(gameTextSection));
        Grid.SetColumn(section, 1); selectors.Children.Add(chapter); selectors.Children.Add(section); panel.Children.Add(selectors);
        gameTextChapter.SetBinding(VisibilityProperty, new Binding("Visibility") { Source = ChapterSelector }); panel.Children.Add(gameTextChapter);
        panel.Children.Add(followModeTabs);
        foreach (var (title, content) in new[] { ("文字跟随", memoryFollowPanel), ("键盘 / 手柄", keyFollowPanel), ("点按跟随", mouseFollowPanel), ("顺序播放", sequenceFollowPanel) })
        {
            content.Margin = new Thickness(0, 10, 0, 0);
            followModeTabs.Items.Add(new TabItem { Header = title, Content = content });
        }
        followModeTabs.SelectedIndex = 0;
        panel = memoryFollowPanel;
        panel.Children.Add(gameTextMode);
        panel.Children.Add(new TextBlock { Text = "点击开始即可同时监听普通与 3D 对白，无需填字。对白出现后自动跟随，切场景后自动重新寻找。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 4, 0, 6) });
        foreach (var probe in textProbes)
        {
            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            if (probe.Kind == GameTextKind.Scene3D)
            {
                panel.Children.Add(new Expander { Header = "3D 营地 / 战斗对白", Content = body, Margin = new Thickness(0, 2, 0, 8) });
                body.Children.Add(new TextBlock { Text = "可提前开启监听；没有对白时等待，无需抄写台词和标点。", FontSize = 11, Foreground = Theme.Brush("MutedText") });
            }
            else panel.Children.Add(body);
            probe.Anchor.ToolTip = "填写游戏当前显示的完整正文（含标点），或从剧情页选中后使用下方按钮。";
            var buttons = new WrapPanel(); body.Children.Add(buttons);
            probe.Connect.Content = probe.Kind == GameTextKind.Ordinary ? "连接普通对白" : "连接 3D 对白";
            probe.Connect.Margin = new Thickness(0, 0, 6, 3); buttons.Children.Add(probe.Connect);
            if (probe.Kind == GameTextKind.Scene3D)
            {
                var manual = new StackPanel(); probe.ManualPanel.Content = manual;
                manual.Children.Add(new TextBlock { Text = "自动寻找仍无法识别时，填写当前完整正文（含标点，至少 6 个字）。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText") });
                manual.Children.Add(probe.Anchor);
                var manualButtons = new WrapPanel(); manual.Children.Add(manualButtons);
                AddButton(manualButtons, "填入选中台词", () => probe.Anchor.Text = (LinesList.SelectedItem as LineRow)?.Node.Text ?? engine?.Current?.Text ?? "");
                var manualConnect = new Button { Content = "连接填写的这句" };
                manualConnect.Click += async (_, _) => await ConnectTextProbe(probe);
                manualButtons.Children.Add(manualConnect); body.Children.Add(probe.ManualPanel);
            }
            else buttons.Children.Add(new TextBlock { Text = "立绘 / 全屏剧情 · 无需填写台词", FontSize = 11, Foreground = Theme.Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center });
            probe.Connect.Click += (_, _) => BeginTextMonitoring(probe);
            probe.Status.FontSize = 11; probe.Status.Foreground = Theme.Brush("MutedText"); probe.Status.Margin = new Thickness(0, 2, 0, 0); body.Children.Add(probe.Status);
            probe.ExportConnection.HorizontalAlignment = HorizontalAlignment.Left;
            probe.ExportConnection.Margin = new Thickness(0, 4, 0, 0);
            probe.ExportConnection.Click += (_, _) => ExportDiagnostics(); body.Children.Add(probe.ExportConnection);
        }
        gameTextLive.FontSize = 14; gameTextLive.Margin = new Thickness(0);
        panel.Children.Add(new Border { Background = Theme.Brush("Surface"), Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 3, 0, 8),
            Child = new ScrollViewer { Content = gameTextLive, MaxHeight = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var automatic = new StackPanel { Margin = new Thickness(0, 0, 0, 5) }; InitializeTextAutoAdvance(automatic);
        var automaticCard = new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 6), CornerRadius = new CornerRadius(4), Child = automatic, Margin = new Thickness(0, 4, 0, 0) };
        var playback = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(playback);
        gameTextResume.Style = (Style)FindResource("PrimaryButton"); gameTextResume.Margin = new Thickness(0, 3, 6, 3); playback.Children.Add(gameTextResume);
        gameTextResume.Click += (_, _) => BeginTextMonitoring();
        AddButton(playback, "暂停配音", () => PauseTextPlayback("游戏配音已暂停，仍显示读取到的文字。"));
        AddButton(playback, "断开连接", () => StopGameText("已断开游戏文字连接。"));
        AddButton(playback, "反馈当前句", FeedbackGameLine);
        gameTextNotice.FontSize = 11; gameTextNotice.Foreground = Theme.Brush("MutedText"); panel.Children.Add(gameTextNotice);
        panel.Children.Add(automaticCard);
        var help = new TextBlock { Text = "无需 OCR。只读监听游戏已有的对白框；首次寻找可能需要数秒。缺音频、对白未出现或当前句不明确时等待。切场景后后台重新寻找；暂停或断开后不会自行恢复播放。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 5, 0, 5) };
        panel.Children.Add(new Expander { Header = "连接说明", Content = help, Margin = new Thickness(0, 5, 0, 0) });
        UpdateGameTextModeLabel();
    }
    void UpdateGameTextModeLabel() => gameTextMode.Text = inputBranchRecovery != null ? "正在接续游戏支线 · 接上后保持原跟随方式" : textArmed ? (textAutoBox.IsChecked == true ? "当前方式：文字跟随 + 播完自动下一句" : "当前方式：文字跟随 · 你手动换句") : "游戏文字跟随 · 推荐";
    void TextNotice(string message) { gameTextNotice.Text = message; UpdateGameTextModeLabel(); Log.Write("game-text", message); }
    void PauseTextPlayback(string reason, bool preserveBranchAudio = false)
    {
        bool showBranchPause = BranchFeedbackContext;
        ClearBranchWaitReason();
        bool branchRecovery = inputBranchRecovery != null;
        inputBranchRecovery = null;
        CancelTextAutoAdvance();
        textArmed = false;
        foreach (var probe in textProbes)
        {
            probe.AutoConnect = false;
            if (branchRecovery) { probe.Reader?.Dispose(); probe.Reader = null; }
            if (probe.Binding is not { } binding) continue;
            probe.Revision++; probe.Binding = null; binding.Cancel();
            probe.Connect.Content = probe.Kind == GameTextKind.Ordinary ? "连接普通对白" : "连接 3D 对白";
            probe.Status.Text = "连接已取消";
        }
        if (textSoundSource != null || branchRecovery && !preserveBranchAudio) StopAudio();
        textSoundSource = null;
        TextNotice(reason); ResetFollowInputSession();
        if (showBranchPause && !preserveBranchAudio) CaptureBranchWaitReason(reason, paused: true);
    }
    void StopGameText(string reason)
    {
        PauseTextPlayback(reason);
        if (closing) gameTextTimer.Stop();
        foreach (var probe in textProbes)
        {
            probe.Revision++; probe.Binding?.Cancel(); probe.Reader?.Dispose(); probe.Reader = null;
            probe.Status.Text = "尚未连接"; probe.Observed = probe.Pending = probe.ObservedSpeaker = probe.PendingSpeaker = ""; probe.Active = false;
        }
    }
    async Task ConnectTextProbe(TextProbe probe)
    {
        if (engine == null || SectionBox.SelectedItem is not Section section) { TextNotice("请先在上方选择配音章节和小节。"); return; }
        if (game == null || !Native.IsWindow(game.Handle) || (preferences.GameExecutablePath.Length > 0 && !GameMatchesPreference(game)))
        {
            var games = FindTextGameWindows();
            if (games.Length != 1) { TextNotice("请先打开游戏，再到设置中选择游戏位置并连接战双窗口。"); return; }
            BindGame(games[0]);
        }
        if (!Native.IsGameWindow(game!.Handle, preferences.GameExecutablePath) ||
            !GameInstallation.IsNativeClientExecutable(Native.ExecutablePath(game.Handle)))
        { TextNotice("请在设置中选择当前战双客户端的游戏目录，再连接文字；这项追踪仅支持原生电脑版。"); return; }
        // 普通正文组件已能自动识别，重连时不能被输入框残留的旧句限制。
        string anchor = probe.Kind == GameTextKind.Ordinary ? "" : probe.Anchor.Text.Trim();
        if ((anchor.Length > 0 && anchor.Length < 6) || (anchor.Length == 0 && probe.Kind == GameTextKind.Scene3D))
        { TextNotice("请填入游戏当前至少 6 个字的完整正文；普通剧情可以留空自动寻找。"); return; }
        PauseTextPlayback("正在连接 " + probe.Title + "，请保持游戏当前台词。");
        StopAutomatic("已切换到文本追踪。"); StopListeningForGame(); CancelOcr(); HideBranchMenu();
        probe.Reader?.Dispose(); probe.Reader = null;
        var owner = engine; var window = game.Handle;
        int gameId = GameTextReader.WindowProcessId(window);
        int revision = ++probe.Revision;
        using var cancel = new CancellationTokenSource(); probe.Binding = cancel;
        probe.Connect.Content = "取消连接"; probe.Status.Text = "正在寻找对白文字组件…";
        probe.ExportConnection.Visibility = Visibility.Collapsed; probe.Diagnostics = "";
        string connectionDiagnostics = "";
        GameTextReader? reader = null;
        try
        {
            reader = await Task.Run(() =>
            {
                var candidate = new GameTextReader(gameId);
                try { candidate.Bind(anchor, probe.Kind, cancel.Token); return candidate; }
                catch { candidate.Dispose(); throw; }
                finally { connectionDiagnostics = candidate.ConnectionDiagnostics; }
            }, cancel.Token);
            if (closing || cancel.IsCancellationRequested || revision != probe.Revision || owner != engine || game?.Handle != window) return;
            probe.Reader = reader; reader = null; probe.Observed = ""; probe.Pending = ""; probe.Active = false;
            textOwner = owner; textSection = section.Id;
            probe.Status.Text = "正文与启用状态已连接，全程只读。";
            ArmTextPlayback();
        }
        catch (OperationCanceledException) { if (revision == probe.Revision) probe.Status.Text = "连接已取消"; }
        catch (Exception ex)
        {
            if (!closing && !cancel.IsCancellationRequested && revision == probe.Revision && owner == engine &&
                game?.Handle == window && GameTextReader.WindowProcessId(window) == gameId &&
                SectionBox.SelectedItem is Section currentSection && currentSection.Id == section.Id)
            {
                probe.Status.Text = ex.Message; TextNotice("未连接：" + ex.Message);
                probe.Diagnostics = connectionDiagnostics.Length > 0 ? connectionDiagnostics : ex.ToString();
                probe.ExportConnection.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            reader?.Dispose();
            if (revision == probe.Revision && connectionDiagnostics.Length > 0) probe.Diagnostics = connectionDiagnostics;
            if (ReferenceEquals(probe.Binding, cancel)) { probe.Binding = null; probe.Connect.Content = probe.Kind == GameTextKind.Ordinary ? "连接普通对白" : "连接 3D 对白"; }
        }
    }
    void ArmTextPlayback()
    {
        if (engine == null || SectionBox.SelectedItem is not Section section)
        { TextNotice("请先选好配音章节和小节。"); return; }
        if (engine.Mode == RunMode.Original) { TextNotice("请先结束游戏原声时段，再开始文本配音。"); return; }
        StopAutomatic("已切换到文本追踪。"); StopListeningForGame(); StopPreview(); CancelOcr(); HideBranchMenu();
        CancelTextAutoAdvance();
        ClearBranchWaitReason();
        textOwner = engine; textSection = section.Id; textArmed = true; lastTextPlayed = "";
        foreach (var probe in textProbes)
        {
            probe.Observed = probe.Pending = probe.ObservedSpeaker = probe.PendingSpeaker = ""; probe.Active = false;
            probe.AutoConnect = true; probe.NextConnectAttempt = DateTime.MinValue;
        }
        ResetFollowInputSession(); TextNotice("已开启监听，等待游戏对白；普通和 3D 台词出现后自动跟随。"); Collapse();
    }
    void ObserveGameText(TextProbe probe, GameTextSample sample, long now)
    {
        if (!sample.Valid || !sample.Active || sample.Text.Length == 0)
        {
            if (probe.Active) ClearObservedBranchWaitReason();
            // 分支/场景切换可能重建对白框。首次消失后短暂等待再扫描，
            // 不让原有15秒冷却迫使用户重连；连续空帧不重复续期或启动扫描。
            if (probe.Active && textArmed && probe.AutoConnect)
                probe.NextConnectAttempt = DateTime.UtcNow.AddMilliseconds(500);
            probe.Active = false; probe.Pending = probe.Observed = probe.PendingSpeaker = probe.ObservedSpeaker = "";
            probe.Status.Text = sample.Reason.Length > 0 ? sample.Reason : "正文已清空，等待下一句。";
            if (textSoundSource == probe) { StopAudio(); textSoundSource = null; }
            return;
        }
        probe.Active = true;
        if (sample.Text == probe.Observed && sample.Speaker == probe.ObservedSpeaker) return;
        ClearObservedBranchWaitReason();
        probe.Observed = probe.Pending = sample.Text; probe.PendingSince = now;
        probe.ObservedSpeaker = probe.PendingSpeaker = sample.Speaker;
        gameTextLive.Text = probe.Title + "\n" + (sample.Speaker.Length > 0 ? sample.Speaker + "：" : "") + sample.Text;
        probe.Status.Text = "已读到启用的正文 · " + DateTime.Now.ToString("HH:mm:ss");
        if (textSoundSource == probe) { StopAudio(); textSoundSource = null; }
    }
    bool TextReady(TextProbe probe, long now) => probe.Active && probe.Pending.Length > 0 &&
        Stopwatch.GetElapsedTime(probe.PendingSince, now) >= TimeSpan.FromMilliseconds(75);
    void TickGameText()
    {
        bool simulated = testUi && gameTextSampleTest != null;
        if (closing || (testUi && !simulated) || (!simulated && !textProbes.Any(p => p.Reader != null || p.AutoConnect))) return;
        if (!InputBranchRecoveryCurrent()) PauseTextPlayback("续接位置或状态已变化，分支监听已取消。");
        if (textOwner != engine) { PauseTextPlayback("配音章节已变化，请选好小节后恢复文本配音。"); textOwner = engine; }
        if (textArmed && SectionBox.SelectedItem is Section selectedSection && selectedSection.Id != textSection)
            PauseTextPlayback("所选小节已变化，请核对后恢复文本配音。");
        try
        {
            MaintainTextConnections();
            foreach (var probe in textProbes)
            {
                if (simulated) { ObserveGameText(probe, gameTextSampleTest!(probe), Stopwatch.GetTimestamp()); continue; }
                if (probe.Reader is not { } reader) continue;
                if (game == null || GameTextReader.WindowProcessId(game.Handle) != reader.ProcessId)
                {
                    ObserveGameText(probe, new(false, "", false, "游戏进程已变化，需要重新连接这一路。"), Stopwatch.GetTimestamp());
                    probe.Reader.Dispose(); probe.Reader = null; probe.NextConnectAttempt = DateTime.MinValue; continue;
                }
                var sample = reader.Sample();
                ObserveGameText(probe, sample, Stopwatch.GetTimestamp());
                if (sample.Active) probe.NextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
                if (!sample.Valid)
                {
                    probe.Reader.Dispose(); probe.Reader = null; probe.NextConnectAttempt = DateTime.MinValue;
                }
            }
            // 先采样两路，再决定唯一当前句；不以遍历次序让另一路旧正文抢播。
            bool conflict = textProbes.Where(p => p.Active).Select(p => (p.Observed, p.ObservedSpeaker)).Distinct().Count() > 1;
            if (conflict)
            {
                CancelTextAutoAdvance("两路正文存在冲突，自动点击已取消。");
                if (!textActiveConflict) TextNotice("两路同时启用了不同正文，等待明确当前句。");
                textActiveConflict = true;
                if (textArmed) CaptureBranchWaitReason("两路同时启用了不同正文，等待明确当前句。");
                if (textSoundSource != null) { StopAudio(); textSoundSource = null; }
                return;
            }
            if (textActiveConflict) ClearObservedBranchWaitReason();
            textActiveConflict = false;
            TickTextAutoAdvance();
            // 读取和播放不依赖焦点；只有向游戏发送鼠标点击才需要前台。
            if (!textArmed || ListeningActive || locating || KeyTesting || recordingAction != null || engine?.Mode == RunMode.Original) return;
            foreach (var probe in textProbes)
            {
                if (!TextReady(probe, Stopwatch.GetTimestamp()) || (!simulated && probe.Reader == null)) continue;
                string text = probe.Pending;
                string speaker = probe.PendingSpeaker;
                if (!simulated && !probe.Reader!.Accept(text, speaker)) continue;
                probe.Pending = "";
                CommitMemoryGameText(probe, engine!, text, speaker);
                break;
            }
        }
        catch (Exception ex) { if (!closing) PauseTextPlayback("文本读取已暂停：" + ex.Message); }
        finally
        {
            if (!simulated && textArmed && !textProbes.Any(p => p.Reader != null || p.AutoConnect)) PauseTextPlayback("两路文本均未连接，请重新连接当前场景。");
            RefreshCompactFollowControls();
        }
    }
    void WaitForNextMemoryLine(TextProbe probe, string reason)
    {
        if (textSoundSource != null) { StopAudio(); textSoundSource = null; }
        probe.Status.Text = reason; TextNotice(reason);
        CaptureBranchWaitReason(reason);
    }
    void CommitMemoryGameText(TextProbe probe, PlaybackEngine owner, string text, string speaker = "")
    {
        if (!textArmed || owner != engine || owner != textOwner || owner.Mode == RunMode.Original) return;
        var node = GameTextPolicy.Match(owner.Pack, textSection, text, out string reason, speaker);
        if (node == null) { Log.Write("game-text-wait", $"角色={speaker}\t正文={text}"); WaitForNextMemoryLine(probe, reason); return; }
        if (inputBranchRecovery is { } recovery &&
            (!InputBranchRecoveryCurrent() || node.Id == recovery.PreviousLine || node.Id == recovery.Boundary)) return;
        if (!AutoPlaybackLinePolicy.CanPlay(owner.Pack, node))
        { WaitForNextMemoryLine(probe, "已定位：" + node.Speaker + " · 这句暂无可用配音，继续等待下一句。"); return; }
        string key = owner.Pack.Id + "/" + node.Id + "/" + text;
        if (key == lastTextPlayed) return;
        applyingMemoryLine = true;
        try
        {
            if (!owner.ConfirmGameLine(node.Id)) { WaitForNextMemoryLine(probe, owner.NavigationError + "；继续等待下一句。"); return; }
            lastTextPlayed = key; textSoundSource = probe; singleResume = false; DialoguePositionConfirmed();
            if (inputBranchRecovery == null) CaptureTextAutoLine(probe, owner, text, speaker);
            Log.Write("game-text-line", $"节点={node.Id}\t角色={node.Speaker}\t读取角色={speaker}\t正文={text}");
            TextNotice(probe.Title + " · 已匹配 " + node.Speaker +
                (AutoPlaybackLinePolicy.IsSilentPunctuation(node) ? " · 标点停顿" : " 并播放"));
            CompleteInputBranchRecovery();
        }
        finally { applyingMemoryLine = false; }
    }
}
