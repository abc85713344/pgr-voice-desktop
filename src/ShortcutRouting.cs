using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    readonly Dictionary<Key, (uint MessageTime, long HandledAt)> locallyHandledKeys = new();
    void OnPreviewKey(object sender, KeyEventArgs e)
    {
        try { HandlePreviewKey(sender, e); }
        finally
        {
            if (e.Handled)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
                locallyHandledKeys[key] = (unchecked((uint)e.Timestamp), Stopwatch.GetTimestamp());
            }
        }
    }
    // WPF 和 Raw Input 保留同一条物理输入的原始消息时间。不能比较两次即时换算后的
    // Stopwatch 值：Windows 的 TickCount 精度会让它们产生毫秒级偏差。
    bool IsLocallyHandledShortcut(Key key, uint? messageTime) => messageTime.HasValue &&
        locallyHandledKeys.TryGetValue(key, out var handled) && messageTime.Value == handled.MessageTime &&
        Stopwatch.GetTimestamp() - handled.HandledAt < Stopwatch.Frequency * 60;

    static bool IsTextInputSource(object? source)
    {
        for (var element = source as DependencyObject; element != null;)
        {
            if (element is TextBoxBase or PasswordBox) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent;
        }
        return false;
    }
    void TogglePanelShortcut()
    {
        if (expanded) Collapse(!ListeningActive);
        else if (ListeningActive) Expand(listeningTab);
        else if (branchMenu.IsVisible) DismissSmallMenu();
        else OpenStory();
    }
    void PrepareGamePlaybackAction()
    {
        if (TextFollowing) PauseTextPlayback("已手动操作配音，文字跟随暂停；可在游戏配音页恢复。");
        if (automaticRunning || automaticPendingOwner != null || automaticPreparingOwner != null)
            StopAutomatic("已手动操作配音，自动播放关闭。");
        StopListeningForGame(); CancelOcr();
    }
    bool HandleListeningShortcut(string? action)
    {
        switch (action)
        {
            case "panel": TogglePanelShortcut(); return true;
            case "pause": if (listeningRunning) PauseListening(); else StartListening(); return true;
            case "replay":
                if (listeningSession != null) { PauseListening(); listeningOffset = 0; StartListening(); }
                return true;
            case "previous": MoveListening(() => listeningSession!.Previous()); return true;
            case "manualNext": MoveListening(() => listeningSession!.MoveNext()); return true;
            // 听书时保留游戏的原声、定位、目录和选路线状态。
            case "interactions": case "history": case "reselect": case "catalog": case "original": case "ocr": return true;
            default: return false;
        }
    }
    bool HandleGameShortcut(string? action, bool fromGame = false, long timestamp = 0)
    {
        switch (action)
        {
            case "panel": TogglePanelShortcut(); return true;
            case "catalog": FocusCatalog(); return true;
            case "reselect": Reselect(); return true;
            case "history": OpenHistory(); return true;
            case "interactions": OpenInteractions(); return true;
            case "original": Original(); return true;
            case "ocr": _ = Locate(); return true;
            case "next":
                if (fromGame && !expanded && !(branchMenu.IsVisible && branchMenu.IsNavigation))
                    RequestFollowNext(FollowInputSource.Keyboard, timestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : timestamp);
                return fromGame;
            case "manualNext":
                if (branchMenu.IsVisible && branchMenu.IsNavigation) return true;
                PrepareGamePlaybackAction(); engine?.Next(true); DialoguePositionConfirmed(); if(expanded) BrowseCurrent(); return true;
            case "previous":
                if (branchMenu.IsVisible && branchMenu.IsNavigation) return true;
                PrepareGamePlaybackAction(); engine?.Previous(); DialoguePositionConfirmed(); if(expanded) BrowseCurrent(); return true;
            case "replay":
                if (branchMenu.IsVisible && branchMenu.IsNavigation) return true;
                PrepareGamePlaybackAction(); engine?.Replay(); return true;
            case "pause":
                if (branchMenu.IsVisible && branchMenu.IsNavigation) return true;
                PrepareGamePlaybackAction(); engine?.TogglePause(); return true;
            default: return false;
        }
    }
}
