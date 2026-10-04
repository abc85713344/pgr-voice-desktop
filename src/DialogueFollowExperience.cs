using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PgrVoice;

public partial class MainWindow
{
    DialogueFrameMonitor dialogueMonitor = new();
    readonly TextBlock dialogueStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    readonly CheckBox dialogueGuardBox = new() { Content = "检查对白后再跟随（试验）" };
    bool dialogueHeld, dialogueChecking, editingClickZone;
    readonly StackPanel dialogueRecovery = new();
    int dialogueGeneration;
    IntPtr monitoredGame;
    string? dialogueDeferredNode;
    RunMode? previousDialogueMode;
    string dialogueMessage = "只检查对白小区域，不启动 OCR。";

    void InitializeDialogueFollow()
    {
        keyFollowPanel.Children.Add(new TextBlock { Text = "按游戏的下一句键，配音跟一句", FontSize = 15, FontWeight = FontWeights.SemiBold });
        keyFollowPanel.Children.Add(new TextBlock { Text = "用于键盘或手柄。用鼠标左键推进，请选择旁边的“点按跟随”。", Margin = new Thickness(0, 5, 0, 8), TextWrapping = TextWrapping.Wrap });
        AddInputFollowStartPreview(keyFollowPanel);
        var start = new WrapPanel(); keyFollowPanel.Children.Add(start);
        AddButton(start, "对齐后开始按键跟随", StartKeyFollowing);
        AddButton(start, "去台词页对齐", () => { Expand(StoryTab); BrowseCurrent(); });
        AddButton(keyFollowPanel, "设置跟随按键", () => { Expand(SettingsTab); ExpandContainingSettings(InputSettingsContent); });
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        keyFollowPanel.Children.Add(new Expander { Header = "可选：检查对白画面", Content = panel });
        AddHeading(panel, "对白跟随检查");
        dialogueGuardBox.IsChecked = preferences.DialogueGuardEnabled;
        dialogueHeld = preferences.DialogueGuardEnabled;
        if (dialogueHeld) dialogueMessage = "请先核对当前台词，确认后开始跟随。";
        panel.Children.Add(dialogueGuardBox); panel.Children.Add(dialogueStatus);
        dialogueGuardBox.Checked += (_, _) => SetDialogueGuard(true);
        dialogueGuardBox.Unchecked += (_, _) => SetDialogueGuard(false);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        AddButton(buttons, "选取对白范围", async () => await SelectDialogueRegion());
        AddButton(buttons, "恢复默认范围", () => { preferences.DialogueRegion = new(); ResetDialogueObservation(); Save(); Tell("已恢复默认对白范围；请核对游戏与配音的当前句。"); });
        AddButton(buttons, "确认仍是当前句", ConfirmDialogueAnchor);
        AddButton(buttons, "定位游戏当前句", () => _ = Locate());
        panel.Children.Add(new TextBlock { Text = "不确定时暂停自动跟随。核对台词后，可确认当前句，或用“配音上一句 / 下一句”纠偏；F9 可识别一次画面。分支选完需要重新核对。", TextWrapping = TextWrapping.Wrap });
        dialogueRecovery.Children.Add(new TextBlock { Text = "自动跟随已停住，请核对上方当前台词。", TextWrapping = TextWrapping.Wrap });
        var recoveryButtons = new WrapPanel(); dialogueRecovery.Children.Add(recoveryButtons);
        AddButton(recoveryButtons, "游戏仍是当前句", ConfirmDialogueAnchor);
        AddButton(recoveryButtons, "定位当前画面", () => _ = Locate());
        ((StackPanel)PlaybackBar.Child).Children.Insert(0, dialogueRecovery);
        UpdateDialogueMonitor();
    }
    void StartKeyFollowing()
    {
        if (game == null || !Native.IsWindow(game.Handle)) { Tell("请先在设置中连接战双游戏窗口。"); return; }
        if (!ConfirmInputFollowStart()) return;
        preferences.MouseFollowEnabled = false;
        Collapse(); Save(); RefreshMouseFollow();
        Tell("按键跟随已开启，跟随键：" + KeyName("next") + "。请在游戏窗口内按下；分支需在两边选择相同路线。");
    }
    void AddInputFollowStartPreview(Panel panel)
    {
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 76, Margin = new Thickness(0, 3, 0, 9), Foreground = Theme.Brush("MutedText") };
        preview.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SelectedItem.Text") { Source = LinesList,
            StringFormat = "开始时播放台词页选中的这句：\n{0}", TargetNullValue = "请先到台词页选中游戏当前句。" });
        panel.Children.Add(preview);
    }
    bool ConfirmInputFollowStart()
    {
        if (engine == null || (LinesList.SelectedItem as LineRow)?.Node is not { Kind: "line" } selected)
        { Tell("请先在台词页选中与游戏一致的台词，再点击开始。"); return false; }
        if (engine.Mode == RunMode.Original) { Tell("请先结束游戏原声时段，再确认当前句。"); return false; }
        if (!engine.CanLocate(selected)) { GuideUnselectedRoute(selected); return false; }
        PrepareGamePlaybackAction();
        int before = playCalls;
        if (selected.Id != engine.CurrentId)
        {
            if (!engine.ConfirmGameLine(selected.Id)) { Tell(engine.NavigationError); return false; }
        }
        else if (!engine.ConfirmCurrentPosition()) { Tell(engine.NavigationError); return false; }
        singleResume = false; DialoguePositionConfirmed();
        if (playCalls == before) engine.Replay();
        Log.Write("follow-start", "节点=" + engine.CurrentId + "\t跟随键=" + KeyName("next"));
        return true;
    }
    void SetDialogueGuard(bool enabled)
    {
        preferences.DialogueGuardEnabled = enabled; dialogueGeneration++; dialogueChecking = false;
        dialogueHeld = enabled;
        dialogueMessage = enabled ? "请先核对当前台词，点击“确认仍是当前句”或定位后开始。" : "已关闭对白检查，按键直接跟随。";
        dialogueMonitor.Reset(); UpdateDialogueMonitor(); Save(); UpdateState(); Tell(dialogueMessage);
    }
    void ResetDialogueObservation()
    { dialogueGeneration++; dialogueChecking = false; dialogueMonitor.Reset(); UpdateDialogueMonitor(); }
    void ConfirmDialogueAnchor()
    {
        if (engine?.Current?.Kind != "line" || engine.Mode is not (RunMode.Following or RunMode.Paused or RunMode.Ready))
        { Tell("请先选择游戏当前显示的台词，分支待选时不能直接恢复。"); return; }
        if (!engine.ConfirmCurrentPosition()) { Tell(engine.NavigationError); return; }
        DialoguePositionConfirmed(); dialogueMessage = "当前位置已确认，收起面板后继续。";
        UpdateState(); Tell(dialogueMessage);
    }
    void DialoguePositionConfirmed()
    {
        if (engine?.Mode != RunMode.Following || engine.Current?.Kind != "line") return;
        ResetFollowInputSession(); ResetMouseFollowAnchor();
        dialogueHeld = false; dialogueMessage = "当前位置已确认。"; ResetDialogueObservation();
        if (dialogueDeferredNode == engine.CurrentId) { dialogueDeferredNode = null; engine.Replay(); }
    }
    void HoldDialogue(string reason)
    {
        if (!preferences.DialogueGuardEnabled) return;
        dialogueHeld = true; dialogueChecking = false; dialogueGeneration++;
        dialogueMessage = reason + "；请核对当前句，或按 " + KeyName("ocr") + " 定位。";
        dialogueMonitor.Configure(IntPtr.Zero, preferences.DialogueRegion);
        UpdateState(); Tell(dialogueMessage);
    }
    void DialogueEngineChanged()
    {
        if (followInputOwner != engine || followInputWindow != game?.Handle) ResetFollowInputSession();
        CancelPendingMouseFollow();
        bool interrupted = dialogueChecking;
        bool leftBranch = previousDialogueMode is RunMode.Choice or RunMode.Gap && engine?.Mode == RunMode.Following;
        previousDialogueMode = engine?.Mode; ResetDialogueObservation();
        if (interrupted && !applyingMemoryLine) HoldDialogue("检查期间播放状态或位置发生变化");
        if (leftBranch && !applyingMemoryLine) HoldDialogue("分支已选择，先核对路线第一句");
    }
    void UpdateDialogueMonitor()
    {
        if (dialogueStatus == null) return;
        var handle = game?.Handle ?? IntPtr.Zero;
        if (handle != monitoredGame)
        {
            monitoredGame = handle; dialogueGeneration++; dialogueChecking = false; dialogueMonitor.Reset();
            if (preferences.DialogueGuardEnabled) { dialogueHeld = true; dialogueMessage = "游戏窗口已变化，请重新核对当前句。"; }
        }
        bool active = ready && !closing && !ListeningActive && !automaticRunning && !TextFollowing && preferences.DialogueGuardEnabled && !dialogueHeld && !locating && !expanded && !KeyTesting &&
            recordingAction == null && !branchMenu.IsVisible && engine?.Mode is RunMode.Following or RunMode.Merge;
        dialogueMonitor.Configure(active ? game?.Handle ?? IntPtr.Zero : IntPtr.Zero, preferences.DialogueRegion);
        dialogueStatus.Text = dialogueMessage;
        dialogueRecovery.Visibility = !TextFollowing && Tabs.SelectedItem == StoryTab && preferences.DialogueGuardEnabled && dialogueHeld ? Visibility.Visible : Visibility.Collapsed;
    }
    async void RequestObservedNext(long timestamp, Task<bool>? inputConfirmation = null)
    {
        if (ListeningActive) return;
        if (engine == null || engine.Mode is not (RunMode.Following or RunMode.Merge)) return;
        if (!preferences.DialogueGuardEnabled)
        {
            if (inputConfirmation != null)
            {
                var source = engine; string? position = engine.CurrentId; int revision = dialogueGeneration;
                if (!await inputConfirmation || closing || engine != source || engine.CurrentId != position ||
                    revision != dialogueGeneration || !GameIsForeground() || locating || engine.Mode is not (RunMode.Following or RunMode.Merge)) return;
            }
            AdvanceObservedLine(); return;
        }
        if (dialogueHeld) { Tell(dialogueMessage); return; }
        if (dialogueChecking) { HoldDialogue("连续快按，无法确认推进了几句"); return; }
        var owner = engine; var node = engine.CurrentId; var handle = game?.Handle;
        int generation = ++dialogueGeneration; dialogueChecking = true;
        dialogueMessage = "正在核对对白变化…"; UpdateState(); UpdateDialogueMonitor();
        var observed = await dialogueMonitor.Observe(timestamp, inputConfirmation);
        if (inputConfirmation != null && !await inputConfirmation) return;
        if (closing || generation != dialogueGeneration || engine != owner || engine.CurrentId != node || game?.Handle != handle) return;
        dialogueChecking = false;
        if (!GameIsForeground() || engine.Mode is not (RunMode.Following or RunMode.Merge) || locating)
        { HoldDialogue("检查期间切换了窗口或播放状态"); return; }
        Log.Write("dialogue-check", observed.Verdict + " · " + observed.Reason);
        if (observed.Verdict == DialogueFrameVerdict.Advanced)
        {
            dialogueMessage = "观察到文字替换，已跟随一句。";
            AdvanceObservedLine();
        }
        else if (observed.Verdict == DialogueFrameVerdict.Typing)
        { dialogueMessage = "更像是补全当前句，配音位置保持不变。"; Tell(dialogueMessage); }
        else if (observed.Verdict == DialogueFrameVerdict.NoChange)
            HoldDialogue("观察期间没有确认换句，配音位置保持不变");
        else HoldDialogue(observed.Reason);
        UpdateState(); UpdateDialogueMonitor();
    }
    void AdvanceObservedLine()
    {
        if (engine == null) return;
        engine.Next();
        // merge 是虚拟汇合标记；检查开关不应改变一次真实换句所消耗的台词数。
        // 每一步仍由引擎检查路线边界；选择点、待续接和原声状态不跨越。
        var visited = new HashSet<string>();
        while (engine.Mode == RunMode.Merge && engine.Current is { Kind: "merge" } marker && visited.Add(marker.Id)) engine.Next();
        if (engine.Mode == RunMode.Merge)
        {
            PauseMouseFollow("汇合连接循环，需手动定位共同线");
        }
    }
    async Task SelectDialogueRegion()
    {
        if (game == null || !Native.IsWindow(game.Handle)) { Tell("请先绑定游戏窗口，再选取对白范围。"); return; }
        HoldDialogue("正在调整对白范围");
        string? file = null;
        try
        {
            var shot = await CaptureGameForOcr(game.Handle, System.Threading.CancellationToken.None); file = shot.Path;
            var dialog = new DialogueRegionWindow(this, file, preferences.DialogueRegion);
            if (dialog.ShowDialog() == true) { preferences.DialogueRegion = dialog.Region; ResetDialogueObservation(); Save(); Tell("对白范围已保存，请核对当前位置后继续。"); }
        }
        catch (Exception ex) { Tell("选取失败：" + ex.Message); }
        finally { if (file != null) try { File.Delete(file); } catch { } }
    }
    void PublishMenuCapture()
    {
        keyboard?.PublishMenuSnapshot(new MenuCaptureSnapshot(!KeyTesting && branchMenu.IsVisible && (branchMenu.IsNavigation || engine?.MenuWaiting == true),
            branchMenu.Epoch, game?.Handle ?? IntPtr.Zero, new WindowInteropHelper(this).Handle, branchMenu.Handle));
    }
}
