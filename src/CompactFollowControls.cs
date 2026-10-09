using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    readonly UniformGrid compactFollowButtons = new() { Columns = 3, Rows = 2, Margin = new Thickness(0, 6, 0, 0) };
    readonly Dictionary<string, Button> compactFollowActions = new();
    readonly CheckBox compactControlsSetting = new() { Content = "小台词条显示快捷按钮", Margin = new Thickness(0, 5, 0, 4) };
    bool compactControlsReady, compactControlsRefreshing;

    double PreferredCompactHeight => preferences.CompactControlsEnabled ? 232 : 156;

    void InitializeCompactFollowControls()
    {
        if (compactControlsReady || StripView.Child is not Grid grid) return;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(compactFollowButtons, 1); Grid.SetColumnSpan(compactFollowButtons, 2);
        grid.Children.Add(compactFollowButtons);
        foreach (var entry in new[]
        {
            (Action: "previous", Label: "上一句", Help: "仅校正配音到上一句，不点击游戏"),
            (Action: "replay", Label: "重播", Help: "重播当前句"),
            (Action: "manualNext", Label: "下一句", Help: "仅校正配音到下一句，不点击游戏"),
            (Action: "pause", Label: "暂停跟随", Help: "暂停或恢复跟随；不会开启自动点击"),
            (Action: "ocr", Label: "定位", Help: "识别一次游戏画面，在候选页确认位置"),
            (Action: "continuation", Label: "手动续接", Help: "浏览本节全部台词；确认游戏当前句后才播放")
        })
        {
            string action = entry.Action;
            var button = new Button { Content = entry.Label, ToolTip = entry.Help, Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 2, 5, 2), MinHeight = 28, FontSize = 12 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "CompactFollow-" + action);
            button.Click += (_, _) => RunCompactFollowAction(action);
            compactFollowActions[action] = button; compactFollowButtons.Children.Add(button);
        }
        AddHeading(AppearanceSettingsContent, "小台词条快捷操作");
        AppearanceSettingsContent.Children.Add(compactControlsSetting);
        AppearanceSettingsContent.Children.Add(new TextBlock { Text = "收起为小台词条后，可用前后句纠偏、重播和暂停；这些按钮不会替你点击游戏。", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedText"), FontSize = 11 });
        compactControlsSetting.IsChecked = preferences.CompactControlsEnabled;
        compactControlsSetting.Checked += (_, _) => SetCompactControlsEnabled(true);
        compactControlsSetting.Unchecked += (_, _) => SetCompactControlsEnabled(false);
        StripState.TextWrapping = TextWrapping.Wrap;
        StripState.TextTrimming = TextTrimming.None;
        InitializeDesktopBranchFeedback(grid);
        compactControlsReady = true;
        RefreshCompactFollowControls();
    }

    void SetCompactControlsEnabled(bool enabled)
    {
        if (!compactControlsReady || compactControlsRefreshing) return;
        preferences.CompactControlsEnabled = enabled;
        Save(); RefreshCompactFollowControls();
        if (!expanded) { ApplyCompactView(); ClampPosition(); }
    }

    void RunCompactFollowAction(string action)
    {
        if (action == "continuation") { OpenBranchFeedbackContinuation(); return; }
        bool wasCompact = !expanded;
        IntPtr gameHandle = game?.Handle ?? IntPtr.Zero;
        bool automatic = automaticRunning || automaticPendingOwner != null || automaticPreparingOwner != null;
        if (automatic) StopAutomatic("已使用小台词条，自动播放停止；核对位置后再开启。");
        if (ListeningActive) HandleListeningShortcut(action);
        else if (!(action == "pause" && automatic)) HandleGameShortcut(action);
        // 暂停自动播放已经让引擎停住，不能再 TogglePause 把同一次“暂停”反转为恢复。
        RefreshCompactFollowControls();
        if (wasCompact && !expanded && !locating && !branchMenu.IsVisible && game?.Handle == gameHandle && Native.IsWindow(gameHandle))
            Native.SetForegroundWindow(gameHandle);
    }

    void RefreshCompactFollowControls()
    {
        if (!compactControlsReady || compactControlsRefreshing) return;
        compactControlsRefreshing = true;
        try
        {
            compactControlsSetting.IsChecked = preferences.CompactControlsEnabled;
            compactFollowButtons.Visibility = preferences.CompactControlsEnabled ? Visibility.Visible : Visibility.Collapsed;
            bool listening = ListeningActive;
            bool canPlay = listening ? listeningSession != null : engine != null && engine.Mode != RunMode.Original;
            foreach (var pair in compactFollowActions) pair.Value.IsEnabled = pair.Key == "ocr" ? !listening && engine != null : canPlay;
            if (listening)
            {
                var current = listeningSession?.Current;
                StripSpeaker.Text = "听书 · " + (current?.Node?.Speaker is { Length: > 0 } speaker ? speaker : listeningSession?.ChapterTitle ?? "未选择大章");
                StripText.Text = current?.Kind == ListeningItemKind.Line ? current.Node?.Text ?? "" : current?.Notice ?? "本章已听完";
                StripState.Text = (listeningRunning ? "听书播放中" : current?.Kind == ListeningItemKind.Choice ? "听书等待选择路线 · 展开后选择" : "听书已暂停")
                    + (current == null ? "" : " · " + current.PositionLabel);
                compactFollowActions["pause"].Content = listeningRunning ? "暂停听书" : "继续听书";
                compactFollowActions["ocr"].ToolTip = "听书不使用游戏定位；展开听书页可选择章节与台词";
                StripView.BorderBrush = current?.Kind == ListeningItemKind.Choice ? LineRow.BranchAccent : Theme.Brush("NormalAccent");
            }
            else
            {
                StripSpeaker.Text = engine?.Current?.Speaker ?? "剧情配音";
                StripText.Text = GameBranchLineText();
                StripState.Text = automaticRunning ? "共同线自动播放 · " + automaticStatus.Text
                    : automaticPreparingOwner != null ? "正在定位自动播放起点"
                    : automaticPendingOwner != null ? "等待确认自动播放起点"
                    : preferences.DialogueGuardEnabled && (dialogueHeld || dialogueChecking) ? dialogueMessage
                    : mouseFollowNeedsConfirmation ? mouseFollowNotice : StateText.Text + " · " + FollowReason();
                if (engine?.Pack.IsDraft == true) StripState.Text = "抽检草稿 · " + StripState.Text;
                compactFollowActions["pause"].Content = engine?.Mode == RunMode.Paused && !automaticRunning ? "恢复跟随" : "暂停跟随";
                compactFollowActions["ocr"].ToolTip = "识别一次游戏画面，在候选页确认位置";
                StripView.BorderBrush = engine?.MenuWaiting == true ? LineRow.BranchAccent : Theme.Brush("NormalAccent");
            }
            RefreshBranchFeedbackPresentation();
            StripState.ToolTip = StripState.Text;
        }
        finally { compactControlsRefreshing = false; }
    }
}
