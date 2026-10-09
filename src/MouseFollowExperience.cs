using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    readonly Button mouseFollowButton = new() { Content = "对齐后开始点按跟随" };
    readonly TextBlock mouseFollowStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 5) };
    PendingMouseFollow? pendingMouseFollow;
    IntPtr mouseAnchorWindow;
    DesktopAdvanceInput.PixelRect mouseAnchorBounds;
    string mouseFollowNotice = "先核对当前句；在热区轻点并松开后跟随。拖动、长按和连续快点会暂停。";
    bool mouseFollowReady;
    bool mouseFollowNeedsConfirmation;
    sealed record PendingMouseFollow(ObservedMouseInput Down, PlaybackEngine Owner, string? Node,
        DesktopAdvanceInput.PixelRect Bounds, TaskCompletionSource<bool> Confirmation);

    void InitializeMouseFollow()
    {
        var panel = mouseFollowPanel;
        panel.Children.Add(new TextBlock { Text = "鼠标左键轻点一次，配音跟一句", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        AddInputFollowStartPreview(panel);
        panel.Children.Add(mouseFollowButton); panel.Children.Add(mouseFollowStatus);
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(buttons);
        AddButton(buttons, "去台词页对齐", () => { Expand(StoryTab); BrowseCurrent(); });
        AddButton(buttons, "暂停点按跟随", () => { preferences.MouseFollowEnabled = false; PauseMouseFollow("点按跟随已暂停。"); Save(); });
        AddButton(buttons, "设置点按位置", () => { Expand(SettingsTab); ExpandContainingSettings(InputSettingsContent); });
        AddButton(buttons, "查看对白检查", () => followModeTabs.SelectedIndex = 1);
        panel.Children.Add(new TextBlock { Text = "先在台词页对齐当前句，再按游戏的下一句键或轻点热区。此方式按你的操作推进配音；错位后可用配音前后句纠正，或到辅助定位页识别画面。", TextWrapping = TextWrapping.Wrap });
        mouseFollowButton.Click += (_, _) => ToggleMouseFollow();
        mouseFollowReady = true; RefreshMouseFollow();
    }
    void ToggleMouseFollow()
    {
        if (!preferences.ClickZoneEnabled || !clickZone.IsVisible)
        { Tell("请先显示并设置「下一句」点击热区。"); return; }
        if (game == null || !DesktopAdvanceInput.ClientBounds(game.Handle, out _))
        { Tell("请先绑定可见的战双游戏窗口。"); return; }
        if (!ConfirmInputFollowStart()) return;
        preferences.MouseFollowEnabled = true; DialoguePositionConfirmed();
        mouseFollowNotice = "已确认起点。等字幕完整后轻点热区；原生电脑版选完分支后会按新对白自动接上。";
        Collapse(); Save(); RefreshMouseFollow(); Tell(mouseFollowNotice);
        BeginInputBranchRecovery(engine?.CurrentId ?? "");
    }
    void RefreshMouseFollow()
    {
        if (!mouseFollowReady) return;
        mouseFollowButton.Content = "对齐后开始点按跟随";
        mouseFollowStatus.Text = !preferences.MouseFollowEnabled ? "点按跟随已关闭，键盘操作保留。" : mouseFollowNotice;
    }
    void ResetMouseFollowAnchor()
    {
        CancelPendingMouseFollow();
        mouseAnchorWindow = IntPtr.Zero;
        if (game != null && DesktopAdvanceInput.ClientBounds(game.Handle, out mouseAnchorBounds)) mouseAnchorWindow = game.Handle;
        mouseFollowNotice = "当前位置已确认；等文字完整后轻点热区，不要连续快点。";
        mouseFollowNeedsConfirmation = false;
        RefreshMouseFollow();
    }
    void CancelPendingMouseFollow()
    {
        var pending = pendingMouseFollow; pendingMouseFollow = null;
        pending?.Confirmation.TrySetResult(false);
    }
    void PauseMouseFollow(string reason)
    {
        CancelPendingMouseFollow();
        mouseFollowNotice = reason + " 请核对游戏当前句，不会补点。";
        mouseFollowNeedsConfirmation = true;
        engine?.PauseForBrowse(); HoldDialogue(reason); RefreshMouseFollow(); Tell(mouseFollowNotice);
    }
    bool CanObserveMouseFollow(ObservedMouseInput input) => !ListeningActive && !TextFollowing && !automaticRunning && !closing &&
        preferences.MouseFollowEnabled && recordingAction == null && !locating && !branchMenu.IsVisible &&
        input.Timestamp > branchMenu.LastPointerInputTimestamp && preferences.ClickZoneEnabled && input.Button == "鼠标左键" &&
        game != null && input.Foreground == game.Handle && Native.GetForegroundWindow() == input.Foreground && !expanded &&
        clickZone.IsVisible && clickZone.IsClickThrough && clickZone.ContainsCursor(input.X, input.Y);

    void BeginMouseFollow(ObservedMouseInput input)
    {
        if (!CanObserveMouseFollow(input)) return;
        if (!testUi && rawKeyboard?.MouseGestureAvailable != true)
        { PauseMouseFollow(rawKeyboard?.MouseGestureError ?? "当前鼠标输入不能确认完整轻点"); return; }
        if (KeyTesting)
        { keyTestStatus.Text = "收到：点击热区\n等待松开确认轻点；测试中，没有执行剧情操作。"; return; }
        if (engine?.Mode is not (RunMode.Following or RunMode.Merge)) return;
        if (pendingMouseFollow != null) { PauseMouseFollow("一次轻点尚未结束，又收到新的按下"); return; }
        if (!DesktopAdvanceInput.ClientBounds(input.Foreground, out var bounds) || !bounds.Contains(input.X, input.Y))
        { PauseMouseFollow("点击热区不在游戏画面内"); return; }
        if (mouseAnchorWindow != IntPtr.Zero && (mouseAnchorWindow != input.Foreground || !mouseAnchorBounds.Equals(bounds)))
        { PauseMouseFollow("游戏窗口位置或尺寸变化，需要重新核对热区"); return; }
        if (Stopwatch.GetElapsedTime(input.Timestamp).TotalMilliseconds > 200)
        { PauseMouseFollow("点击消息到达过晚，无法确认当前位置"); return; }
        if (!RegisterFollowInput(FollowInputSource.Mouse, input.Timestamp)) return;
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingMouseFollow = new(input, engine, engine.CurrentId, bounds, confirmation);
        // 按下时立即冻结原有画面检查的前帧；最终推进仍等待轻点松开通过。
        RequestObservedNext(input.Timestamp, confirmation.Task);
    }
    void HandleMouseGesture(ObservedMouseGesture gesture)
    {
        var pending = pendingMouseFollow;
        if (pending == null || gesture.Down.Timestamp != pending.Down.Timestamp || gesture.Down.Device != pending.Down.Device) return;
        pendingMouseFollow = null;
        bool valid = gesture.Accepted && CanObserveMouseFollow(gesture.Up) && !KeyTesting && !automaticRunning &&
            engine == pending.Owner && engine.CurrentId == pending.Node &&
            DesktopAdvanceInput.ClientBounds(gesture.Up.Foreground, out var current) && current.Equals(pending.Bounds) &&
            Stopwatch.GetElapsedTime(gesture.Up.Timestamp).TotalMilliseconds <= 200;
        if (!valid)
        {
            pending.Confirmation.TrySetResult(false);
            PauseMouseFollow(gesture.Accepted ? "轻点期间窗口、热区或播放状态改变" : gesture.Reason); return;
        }
        pending.Confirmation.TrySetResult(true);
    }
    void TickMouseFollow()
    {
        var pending = pendingMouseFollow;
        if (pending == null) return;
        if (Stopwatch.GetElapsedTime(pending.Down.Timestamp).TotalMilliseconds > 900 || !CanObserveMouseFollow(pending.Down) ||
            engine != pending.Owner || engine?.CurrentId != pending.Node || engine?.Mode is not (RunMode.Following or RunMode.Merge) ||
            !DesktopAdvanceInput.ClientBounds(pending.Down.Foreground, out var bounds) || !bounds.Equals(pending.Bounds))
            PauseMouseFollow("轻点未正常完成或跟随环境改变");
    }
}
