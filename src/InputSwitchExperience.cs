using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    readonly FollowInputGate followInputs = new();
    readonly TextBlock followInputStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 5) };
    PlaybackEngine? followInputOwner;
    IntPtr followInputWindow;
    bool gamepadFollowPress;
    bool inputSwitchReady;
    long followSessionStarted;

    string FollowInputLabel => followInputs.ActiveSource switch
    {
        FollowInputSource.Keyboard => "键盘",
        FollowInputSource.Mouse => "鼠标热区",
        FollowInputSource.Gamepad => "手柄",
        _ => "等待输入"
    };
    bool ActiveGamepadFollow => !ListeningActive && followInputOwner == engine &&
        followInputWindow == game?.Handle && followInputs.ActiveSource == FollowInputSource.Gamepad;

    void InitializeFollowInputSwitch()
    {
        AddHeading(keyFollowPanel, "输入设备");
        keyFollowPanel.Children.Add(followInputStatus);
        keyFollowPanel.Children.Add(new TextBlock { Text = "键盘与手柄可以直接换用，切换前松开上一设备。使用鼠标轻点时，请到“点按跟随”开启。", TextWrapping = TextWrapping.Wrap });
        inputSwitchReady = true; RefreshFollowInputStatus();
    }
    void RefreshFollowInputStatus()
    {
        if (!inputSwitchReady) return;
        followInputStatus.Text = "游戏内跟随键：" + KeyName("next") + "\n自动切换 · 当前：" + FollowInputLabel;
    }
    void ResetFollowInputSession()
    {
        CancelPendingMouseFollow();
        followInputs.Reset(); gamepadFollowPress = false;
        followSessionStarted = Stopwatch.GetTimestamp();
        followInputOwner = engine; followInputWindow = game?.Handle ?? IntPtr.Zero;
        RefreshFollowInputStatus();
    }
    bool RegisterFollowInput(FollowInputSource source, long timestamp)
    {
        if (closing || TextFollowing || ListeningActive || locating || expanded || KeyTesting || gamepadTest.IsChecked == true || recordingAction != null ||
            automaticRunning || branchMenu.IsVisible || !GameIsForeground() ||
            engine?.Mode is not (RunMode.Following or RunMode.Merge) ||
            (preferences.DialogueGuardEnabled && dialogueHeld))
        {
            if (!TextFollowing && !ListeningActive)
                Log.Write("follow-input-blocked", $"来源={source}\t模式={engine?.Mode}\t面板={expanded}\t前台游戏={GameIsForeground()}\t定位={locating}\t检查暂停={dialogueHeld}\t自动播放={automaticRunning}");
            return false;
        }
        if (followInputOwner != engine || followInputWindow != game?.Handle)
        {
            ResetFollowInputSession();
            PauseMouseFollow("跟随窗口或章节已经改变，需要重新核对当前句");
            return false;
        }
        if (timestamp <= 0) timestamp = Stopwatch.GetTimestamp();
        if (timestamp < followSessionStarted) return false;
        double age = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        if (age < 0 || age > 200)
        {
            PauseMouseFollow("推进输入到达过晚或时间异常，已停止跟随");
            return false;
        }
        // 按住手柄继续键期间可能有映射重复；必须先松开，才能接管键鼠推进。
        if (source != FollowInputSource.Gamepad && gamepadFollowPress) return false;
        var decision = followInputs.TryAccept(source, timestamp);
        if (source == FollowInputSource.Gamepad && decision != FollowInputDecision.Stale) gamepadFollowPress = true;
        RefreshFollowInputStatus();
        if (decision != FollowInputDecision.Accept) { RefreshCompactFollowControls(); return false; }
        if (pendingMouseFollow != null && source != FollowInputSource.Mouse)
        {
            PauseMouseFollow("鼠标尚未松开，又收到另一种设备推进；请先松开设备并核对当前句");
            return false;
        }
        Log.Write("follow-input", $"来源={source}\t节点={engine.CurrentId}");
        return true;
    }
    void RequestFollowNext(FollowInputSource source, long timestamp)
    {
        if (RegisterFollowInput(source, timestamp)) RequestObservedNext(timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp);
    }
    void ObserveFollowGamepadRelease(GamepadReading reading)
    {
        if (!gamepadFollowPress || !reading.Connected) return;
        var advance = GamepadLayout.Parse(preferences.GamepadAdvanceButton);
        if ((reading.Buttons & advance) != 0) return;
        // 仅真实已登记的按压结束时延长一次；空闲中性快照不能一直屏蔽键鼠。
        gamepadFollowPress = false;
        followInputs.GamepadReleased(reading.Timestamp);
    }
}
