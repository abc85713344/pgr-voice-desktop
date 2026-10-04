using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PgrVoice;

public partial class MainWindow
{
    readonly Button automaticButton = new() { Content = "游戏自动播放（先定位）", Margin = new Thickness(0, 3, 5, 3) };
    readonly TextBlock automaticStatus = new() { Text = "只自动走共同线；遇到分支暂停。", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    readonly ComboBox automaticDelay = new() { ItemsSource = new[] { "0.6 秒", "1 秒（建议）", "1.5 秒", "2 秒" }, SelectedIndex = 1 };
    readonly DesktopAutoPlaybackCycle automaticCycle = new();
    readonly StackPanel automaticRecovery = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 5, 0, 4) };
    readonly TextBlock automaticRecoveryText = new() { TextWrapping = TextWrapping.Wrap };
    PlaybackEngine? automaticOwner, automaticPendingOwner, automaticPreparingOwner;
    string? automaticNode;
    long automaticTicket;
    int automaticEpoch;
    bool automaticRunning, automaticAdvancing, automaticWaiting;
    IntPtr automaticWindow;
    DesktopAdvanceInput.PixelRect automaticBounds, automaticZone;
    DateTime automaticPendingDeadline;
    long automaticLastStartedTicket, automaticLastCompletedTicket;
    double automaticLastStartedDuration;
    // 隔离自测仅替换环境读取与点击，不向用户窗口发送输入。
    Func<bool>? automaticEnvironmentTest;
    Func<bool>? automaticClickTest;
    Func<Task>? automaticLocateTest;
    bool automaticTestImmediate;
    bool automaticAudioStartedTest;
    Func<long>? automaticClockTest;

    long AutomaticNow() => testUi && automaticClockTest != null ? automaticClockTest() : (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    int AutomaticDelayMilliseconds => automaticTestImmediate ? 1 : (int)(new[] { .6, 1, 1.5, 2 }[Math.Clamp(automaticDelay.SelectedIndex, 0, 3)] * 1000);
    void ShowAutomaticPhase()
    { automaticStatus.Text = automaticCycle.PhaseText + " · 手动操作可停"; }

    void InitializeAutoPlayback()
    {
        var strip = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        strip.Children.Add(automaticButton); strip.Children.Add(automaticStatus);
        automaticRecovery.Children.Add(automaticRecoveryText);
        var recoveryButtons = new WrapPanel(); automaticRecovery.Children.Add(recoveryButtons);
        AddButton(recoveryButtons, "重新定位并开启", () => _ = BeginAutomatic());
        AddButton(recoveryButtons, "查看当前台词", OpenStory);
        strip.Children.Add(automaticRecovery);
        var legacyPanel = sequenceFollowPanel;
        legacyPanel.Children.Add(new TextBlock { Text = "按台本顺序，自动走共同剧情", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        legacyPanel.Children.Add(strip);
        Tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs)) return;
            CurrentCard.Visibility = Tabs.SelectedItem == StoryTab ? Visibility.Visible : Visibility.Collapsed;
            foreach (var gameControls in ((StackPanel)PlaybackBar.Child).Children.OfType<System.Windows.Controls.Primitives.UniformGrid>())
                gameControls.Visibility = Tabs.SelectedItem == StoryTab ? Visibility.Visible : Visibility.Collapsed;
        };
        automaticButton.Click += async (_, _) =>
        {
            if (automaticRunning || automaticPendingOwner != null || automaticPreparingOwner != null) StopAutomatic("自动播放已停止。重新开启会先定位当前句。");
            else await BeginAutomatic();
        };
        legacyPanel.Children.Add(new TextBlock { Text = "先截图定位并确认，再按台本顺序播放、点击游戏；遇到分支停下。需要截图定位组件。若能读取游戏文字，建议选择“文字跟随”中的自动下一句。", TextWrapping = TextWrapping.Wrap });
        legacyPanel.Children.Add(new TextBlock { Text = "语音结束后等待", Margin = new Thickness(0, 6, 0, 3) });
        legacyPanel.Children.Add(automaticDelay);
        int delayIndex = Array.IndexOf(new[] { .6, 1, 1.5, 2 }, preferences.AutomaticDelaySeconds);
        automaticDelay.SelectedIndex = delayIndex < 0 ? 1 : delayIndex;
        automaticDelay.SelectionChanged += (_, _) =>
        {
            preferences.AutomaticDelaySeconds = new[] { .6, 1, 1.5, 2 }[Math.Clamp(automaticDelay.SelectedIndex, 0, 3)];
            Save();
        };
        legacyPanel.Children.Add(new TextBlock { Text = "请关闭游戏自带自动播放。手动操作、展开面板或切出游戏会停止此方式；再次开启需要重新定位。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });
        audio.PlaybackCompleted += ticket => Dispatcher.BeginInvoke(() => OnAutomaticCompleted(ticket));
    }
    void SuspendGameForListening()
    {
        if (TextFollowing) PauseTextPlayback("已进入听书，文本配音暂停。");
        StopAutomatic("已进入听书，游戏自动播放已关闭。");
        StopPreview(); CancelOcr(); ocr.Stop(); HideBranchMenu(); engine?.PauseForBrowse(); StopAudio();
    }
    async Task BeginAutomatic()
    {
        if (TextFollowing) PauseTextPlayback("已切换到游戏自动播放，文本配音暂停。");
        if (automaticPreparingOwner != null) return;
        StopListeningForGame();
        if (engine == null) { Tell("请先选择配音章节和小节。"); return; }
        bool simulatedLocate = testUi && automaticLocateTest != null;
        if (!simulatedLocate && (!preferences.ClickZoneEnabled || !clickZone.IsVisible || editingClickZone))
        { Expand(SettingsTab); ExpandContainingSettings(InputSettingsContent); Tell("请先启用「下一句」点击热区，拖到游戏中可推进对白的位置，再点击完成。"); return; }
        if (!simulatedLocate && (game == null || !Native.IsGameWindow(game.Handle, preferences.GameExecutablePath))) { Expand(SettingsTab); Tell("请先选择当前战双客户端的目录并连接游戏窗口。"); return; }
        var owner = engine;
        // 本次明确请求 OCR；不需要循环识别，仍由用户确认识别出的候选。
        if (!simulatedLocate && !preferences.OcrEnabled) OcrEnabledBox.IsChecked = true;
        StopAutomatic("正在定位自动播放起点。", false);
        automaticRecovery.Visibility = Visibility.Collapsed;
        int epoch = automaticEpoch;
        automaticPreparingOwner = owner; automaticPendingDeadline = DateTime.UtcNow.AddMinutes(2);
        automaticButton.Content = "取消自动播放准备";
        try
        {
            if (simulatedLocate) await automaticLocateTest!(); else await Locate();
            if (epoch != automaticEpoch || automaticPreparingOwner != owner || engine != owner || closing || CandidatesList.Items.Count == 0) return;
            automaticPendingOwner = owner; automaticPendingDeadline = DateTime.UtcNow.AddMinutes(2);
            automaticStatus.Text = "核对定位页候选后点「确认定位并播放」，即可开始；支线不能开启。";
            Tell(automaticStatus.Text);
        }
        finally
        {
            if (epoch == automaticEpoch && automaticPreparingOwner == owner)
            {
                automaticPreparingOwner = null;
                if (automaticPendingOwner == null)
                {
                    automaticButton.Content = "游戏自动播放（先定位）";
                    automaticStatus.Text = "本次定位未获得可确认候选，自动播放未开启。";
                }
            }
        }
    }
    void StartAutomaticAtConfirmedLine()
    {
        automaticPendingOwner = null;
        var decision = engine?.EvaluateCommonAutoPlayNext();
        if (decision?.CurrentIsCommon != true)
        { StopAutomatic((decision?.Reason ?? "请先确认共同线当前句。") + "\n" + AutomaticBoundaryNextStep(decision?.Code), false, showRecovery: true); return; }
        if (engine!.Current == null || !AutoPlaybackLinePolicy.CanPlay(engine.Pack, engine.Current))
        { StopAutomatic("当前句缺少录音，自动播放未开启。\n请手动继续这句，或更新配音包；到有配音的共同剧情后重新定位。", showRecovery: true); return; }
        automaticOwner = engine; automaticNode = engine.CurrentId; automaticTicket = audioRequest;
        automaticWindow = game?.Handle ?? IntPtr.Zero;
        if (automaticEnvironmentTest == null && (!DesktopAdvanceInput.ClientBounds(automaticWindow, out automaticBounds) ||
            !DesktopAdvanceInput.GetWindowRect(new WindowInteropHelper(clickZone).Handle, out automaticZone)))
        { StopAutomatic("无法确定游戏与热区位置，请重新设置。"); return; }
        if (!automaticCycle.Start(automaticNode!, automaticTicket, AutomaticNow()))
        { StopAutomatic("没有可用的配音请求，请重新确认当前句。", showRecovery: true); return; }
        automaticEpoch++; automaticRunning = true; automaticWaiting = false;
        if (!AutomaticEnvironment()) { StopAutomatic("游戏未在前台，或下一句热区不在游戏内。请核对后重新开启。"); return; }
        automaticButton.Content = "停止游戏自动播放";
        automaticRecovery.Visibility = Visibility.Collapsed;
        ShowAutomaticPhase();
        BeginAutomaticCurrentLine();
        Tell(automaticStatus.Text);
    }
    void BeginAutomaticCurrentLine()
    {
        if (engine?.Current is { } node && AutoPlaybackLinePolicy.IsSilentPunctuation(node))
        {
            if (!automaticCycle.BeginSilentPause(automaticCycle.Epoch, automaticTicket, AutomaticNow()))
            { StopAutomatic("当前停顿状态已变化，请重新定位。"); return; }
            ShowAutomaticPhase();
        }
        else if (testUi && automaticAudioStartedTest) OnAutomaticAudioStarted(automaticTicket);
        else BindAutomaticAudioIfStarted();
    }
    // Called only after AudioService.Play returned successfully. A short clip's
    // success/completion callbacks can arrive before the user-confirmed start.
    void OnAutomaticAudioStarted(long request)
    {
        if (closing || request <= 0 || request != audioRequest || request < automaticLastStartedTicket) return;
        automaticLastStartedTicket = request;
        automaticLastStartedDuration = audio.DurationSeconds;
        BindAutomaticAudioIfStarted();
    }
    void BindAutomaticAudioIfStarted()
    {
        if (!automaticRunning || automaticTicket != automaticLastStartedTicket || automaticTicket != audioRequest ||
            automaticCycle.Phase != DesktopAutoPlaybackPhase.StartingAudio) return;
        if (!automaticCycle.AudioStarted(automaticCycle.Epoch, automaticTicket, AutomaticNow(), automaticLastStartedDuration))
        { if (automaticCycle.TimedOut(AutomaticNow())) StopAutomatic(automaticCycle.TimeoutReason); return; }
        ShowAutomaticPhase();
        if (automaticLastCompletedTicket == automaticTicket) OnAutomaticCompleted(automaticTicket);
    }
    bool AutomaticEnvironment()
    {
        if (automaticEnvironmentTest != null) return testUi && automaticEnvironmentTest();
        if (closing || ListeningActive || locating || expanded || KeyTesting || recordingAction != null || branchMenu.IsVisible || GamepadActiveInput ||
            game?.Handle != automaticWindow || !Native.IsGameWindow(automaticWindow, preferences.GameExecutablePath) || !GameIsForeground() ||
            !preferences.ClickZoneEnabled || !clickZone.IsVisible || !clickZone.IsClickThrough || editingClickZone) return false;
        if (!DesktopAdvanceInput.ClientBounds(automaticWindow, out var rect) || !rect.Equals(automaticBounds) ||
            !DesktopAdvanceInput.GetWindowRect(new WindowInteropHelper(clickZone).Handle, out var zone) || !zone.Equals(automaticZone)) return false;
        return rect.Contains((zone.Left + zone.Right)/2, (zone.Top + zone.Bottom)/2);
    }
    void TickAutomatic()
    {
        if (automaticPreparingOwner != null && (DateTime.UtcNow > automaticPendingDeadline || automaticPreparingOwner != engine))
            StopAutomatic("自动播放定位准备已取消，请重新定位。", false);
        if (automaticPendingOwner != null && (DateTime.UtcNow > automaticPendingDeadline || automaticPendingOwner != engine))
            StopAutomatic("自动播放准备已取消，请重新定位。", false);
        if (!automaticRunning || automaticAdvancing) return;
        if (automaticCycle.TimedOut(AutomaticNow())) { StopAutomatic(automaticCycle.TimeoutReason); return; }
        if (engine != automaticOwner || engine?.CurrentId != automaticNode || engine?.Mode != RunMode.Following || audioRequest != automaticTicket ||
            !AutomaticEnvironment())
        { StopAutomatic("自动播放已暂停：窗口、输入或播放状态发生变化。请核对当前句后重新开启。"); return; }
        if (automaticCycle.Phase == DesktopAutoPlaybackPhase.SilentPause && AutomaticNow() >= automaticCycle.SilentPauseUntil)
            OnAutomaticCompleted(automaticTicket, silentPause: true);
    }
    async void OnAutomaticCompleted(long ticket, bool silentPause = false)
    {
        if (!silentPause && ticket > 0 && ticket == audioRequest) automaticLastCompletedTicket = ticket;
        int epoch = automaticEpoch;
        try { await CompleteAutomaticLine(ticket, silentPause); }
        catch (Exception ex)
        {
            Log.Write("automatic-error", ex.ToString());
            if (epoch == automaticEpoch) StopAutomatic("自动播放遇到问题，已暂停：" + ex.Message);
        }
    }
    async Task CompleteAutomaticLine(long ticket, bool silentPause)
    {
        if (!automaticRunning || automaticWaiting || ticket != automaticTicket || ticket != audioRequest || engine != automaticOwner || engine?.CurrentId != automaticNode) return;
        if (!AutomaticEnvironment()) { StopAutomatic("游戏已离开前台，自动播放暂停。"); return; }
        if (automaticCycle.TimedOut(AutomaticNow())) { StopAutomatic(automaticCycle.TimeoutReason); return; }
        // If natural EOF won the dispatcher race, retain it until the matching
        // successful start notification arrives; it cannot authorize a click yet.
        if (automaticCycle.Phase == DesktopAutoPlaybackPhase.StartingAudio) return;
        int delay = silentPause ? 0 : AutomaticDelayMilliseconds;
        long cycleEpoch = automaticCycle.Epoch;
        if (silentPause ? !automaticCycle.CompleteSilentPause(cycleEpoch, ticket, AutomaticNow()) :
            !automaticCycle.CompleteAudio(cycleEpoch, ticket, AutomaticNow(), delay)) return;
        var decision = engine!.EvaluateCommonAutoPlayNext();
        if (!decision.Allowed) { StopAutomatic(decision.Reason + "\n" + AutomaticBoundaryNextStep(decision.Code)); ShowAutomaticBoundary(); return; }
        var next = decision.Next!;
        if (!AutoPlaybackLinePolicy.CanPlay(engine.Pack, next))
        { StopAutomatic("下一句缺少录音，自动播放已暂停。\n请手动继续这句，或更新配音包；到有配音的共同剧情后重新定位。" ); ShowAutomaticBoundary(); return; }
        int epoch = automaticEpoch; string current = automaticNode!;
        automaticWaiting = true; ShowAutomaticPhase();
        await Task.Delay(delay);
        if (!AutomaticContinuation(epoch, current, next.Id)) return;
        if (!automaticCycle.BeginClick(cycleEpoch, next.Id, AutomaticNow()))
        { StopAutomatic("点击请求已失效，请重新核对当前句。"); return; }
        long clickTicket = automaticCycle.ClickTicket;
        ShowAutomaticPhase();
        bool clicked;
        string error = "自动点击未完成，已暂停。";
        if (automaticClickTest != null) clicked = testUi && automaticClickTest();
        else
        {
            // 隐藏穿透标记后才检查真实命中窗口，防止点击遮挡物。
            clickZone.Hide();
            try { clicked = DesktopAdvanceInput.TryClick(automaticWindow, automaticBounds, (automaticZone.Left+automaticZone.Right)/2, (automaticZone.Top+automaticZone.Bottom)/2, preferences.GameExecutablePath, out error); }
            finally { if (!closing) UpdateClickZone(); }
        }
        if (!clicked) { StopAutomatic(error); return; }
        if (!automaticCycle.ClickCompleted(cycleEpoch, clickTicket, next.Id, AutomaticNow()))
        { if (automaticRunning && epoch == automaticEpoch) StopAutomatic("点击回执已过期，请重新核对游戏位置。"); return; }
        ShowAutomaticPhase();
        await Task.Delay(automaticTestImmediate ? 1 : 800);
        if (!AutomaticContinuation(epoch, current, next.Id)) return;
        automaticAdvancing = true;
        try
        {
            // 点击已提交；只允许图中刚刚检查过的直达下一句，不跨分支或汇合标记。
            if (!engine!.TryAdvanceAutomatically(next.Id)) { StopAutomatic("剧情位置发生变化，请手动重新定位。"); return; }
            if (!automaticRunning || epoch != automaticEpoch) return;
            automaticNode = next.Id; automaticTicket = audioRequest; automaticWaiting = false;
            if (!automaticCycle.PrepareNextAudio(cycleEpoch, clickTicket, next.Id, automaticTicket, AutomaticNow()))
            { StopAutomatic("下一句的播放请求已变化，请重新定位。"); return; }
            DialoguePositionConfirmed();
            ShowAutomaticPhase();
            BeginAutomaticCurrentLine();
        }
        finally { automaticAdvancing = false; }
    }
    bool AutomaticContinuation(int epoch, string node, string next)
    {
        if (!automaticRunning || epoch != automaticEpoch) return false;
        if (automaticCycle.TimedOut(AutomaticNow())) { StopAutomatic(automaticCycle.TimeoutReason); return false; }
        var decision = engine?.EvaluateCommonAutoPlayNext();
        if (engine != automaticOwner || engine?.CurrentId != node || audioRequest != automaticTicket || !AutomaticEnvironment() || decision?.Allowed != true || decision.Next?.Id != next)
        { StopAutomatic("自动播放等待期间状态改变，请重新定位当前句。"); return false; }
        return true;
    }
    static string AutomaticBoundaryNextStep(string? code) => code switch
    {
        "choice" or "branch" or "next-branch" => "请在游戏中手动继续，出现选项后，在游戏和配音中选择同一路线；支线使用手动跟随。",
        "end" => "本段已播完。请手动继续游戏，确认新的共同剧情起点后重新开启。",
        _ => "请手动继续游戏，通过台词列表或一次定位核对新的共同剧情起点。"
    };
    void ShowAutomaticBoundary()
    {
        // 不推进到选择节点；提示用户在游戏中手动进入选择并处理支线。
        routeToast.PlacementTarget = BallView; routeToast.Content = automaticStatus.Text; routeToast.IsOpen = true;
        var noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); routeToast.IsOpen = false; }; noticeTimer.Start();
    }
    void StopAutomatic(string reason, bool pauseSound = true, bool showRecovery = false)
    {
        bool wasRunning = automaticRunning;
        bool wasPreparing = automaticPreparingOwner != null;
        bool wasPending = automaticPendingOwner != null;
        if (wasRunning || wasPreparing || wasPending || showRecovery)
        {
            string detail = reason + "\n" + automaticCycle.ClickOutcomeText;
            if (!reason.Contains("请") && !reason.Contains("重新")) detail += "\n核对游戏当前句后，可重新定位并开启。";
            automaticRecoveryText.Text = detail;
            automaticRecovery.Visibility = Visibility.Visible;
            reason = detail;
        }
        automaticCycle.Stop();
        automaticRunning = false; automaticWaiting = false; automaticPendingOwner = null; automaticPreparingOwner = null; automaticOwner = null; automaticEpoch++;
        automaticButton.Content = "游戏自动播放（先定位）"; automaticStatus.Text = reason;
        if (wasPreparing) { CancelOcr(); ocr.Stop(); }
        if (wasRunning && pauseSound) engine?.PauseForBrowse();
        Tell(reason);
    }
}
