using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    sealed record BranchWaitFeedback(PlaybackEngine Owner, string NodeId, string SectionId, RunMode Mode,
        string? ReviewRoute, string Reason, bool PlaybackProblem = false, bool Paused = false);
    BranchWaitFeedback? branchWaitFeedback;
    readonly Button stripContinuationButton = new() { Content = "手动续接", Visibility = Visibility.Collapsed,
        Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(6, 5, 0, 0), FontSize = 11 };
    readonly TextBlock branchWaitNotice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11,
        Foreground = LineRow.BranchAccent, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    void InitializeDesktopBranchFeedback(Grid stripGrid)
    {
        // 状态可能包含完整缺音原因；正文和说明一起滚动，操作区保持可见。
        var body = stripGrid.Children.OfType<StackPanel>().FirstOrDefault(p => p.Children.Contains(StripSpeaker));
        if (body != null)
        {
            stripGrid.Children.Remove(body);
            stripGrid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        }
        // 快捷按钮被关闭时，等待状态仍保留一个直接入口。
        var expand = stripGrid.Children.OfType<Button>().FirstOrDefault();
        if (expand != null)
        {
            stripGrid.Children.Remove(expand);
            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(actions, 1); actions.Children.Add(expand); actions.Children.Add(stripContinuationButton);
            stripGrid.Children.Add(actions);
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(stripContinuationButton, "BranchFeedback-Continuation");
        stripContinuationButton.Click += (_, _) => OpenBranchFeedbackContinuation();
        if (CurrentCard.Child is StackPanel card) card.Children.Add(branchWaitNotice);
    }

    bool CurrentGameSingleLine => engine?.Current is { } node && engine.HistoryPosition >= 0 &&
        engine.HistoryPosition < engine.History.Count && engine.History[engine.HistoryPosition] is var visit &&
        visit.NodeId == node.Id && visit.SingleLine;

    string GameBranchLabel()
    {
        if (ListeningActive || previewingHistory || engine == null || engine.Mode == RunMode.Original ||
            engine.ReviewRoute != null || CurrentGameSingleLine || engine.Current is not { Kind: "line", Archived: false } node ||
            node.PathId.Length == 0 || !engine.Allowed(node)) return "";
        // 使用原菜单顺序，不按已过滤列表、最近选择或浏览行推测编号。
        var owners = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" &&
            n.SectionId == node.SectionId && n.Options.Any(o => o.PathId == node.PathId)).ToList();
        if (owners.Count != 1 || owners[0].MenuType != "exclusive" ||
            !engine.Choices.TryGetValue(owners[0].Id, out var selected) || selected != node.PathId) return "";
        var menu = owners[0];
        if (menu.Options.Count(o => o.PathId == node.PathId) != 1) return "";
        int number = menu.Options.FindIndex(o => o.PathId == node.PathId) + 1;
        string[] names = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        return "分支" + (number <= names.Length ? names[number - 1] : number.ToString());
    }

    string GameBranchLineText()
    {
        string text = engine?.Current?.Text ?? "请选择起始台词";
        string label = GameBranchLabel();
        return label.Length == 0 ? text : label + "：" + text;
    }

    bool BranchFeedbackContext => !ListeningActive && !previewingHistory && engine?.Current != null &&
        engine.Mode != RunMode.Original && !CurrentGameSingleLine &&
        (engine.MenuWaiting || inputBranchRecovery != null || unconfirmedStoryEnding ||
            branchWaitFeedback is { PlaybackProblem: true } audioWait && audioWait.Owner == engine && audioWait.NodeId == engine.CurrentId ||
            textArmed && (engine.Current.PathId.Length > 0 ||
                engine.Current.NextId is string next && engine.Pack.ById.TryGetValue(next, out var target) &&
                target.Kind is "choice" or "gap"));

    void ClearBranchWaitReason() => branchWaitFeedback = null;
    void ClearObservedBranchWaitReason()
    {
        // 停止监听后仍可显示读到的文字；新采样不能抹掉玩家主动暂停的说明。
        if (branchWaitFeedback?.Paused != true) ClearBranchWaitReason();
    }

    void CaptureBranchWaitReason(string reason, bool paused = false)
    {
        if (BranchFeedbackContext && reason.Length > 0)
            branchWaitFeedback = new(engine!, engine!.CurrentId!, engine.Current!.SectionId, engine.Mode, engine.ReviewRoute, reason, Paused: paused);
        RefreshCompactFollowControls();
    }

    void CaptureBranchPlaybackProblem(string reason)
    {
        if (GameBranchLabel().Length == 0) return;
        branchWaitFeedback = new(engine!, engine!.CurrentId!, engine.Current!.SectionId, engine.Mode, engine.ReviewRoute, reason, PlaybackProblem: true);
        RefreshCompactFollowControls();
    }

    string CurrentBranchWaitReason()
    {
        if (!BranchFeedbackContext) { ClearBranchWaitReason(); return ""; }
        if (branchWaitFeedback is { } feedback && (feedback.Owner != engine || feedback.NodeId != engine!.CurrentId ||
            feedback.SectionId != engine.Current?.SectionId || feedback.Mode != engine.Mode || feedback.ReviewRoute != engine.ReviewRoute)) ClearBranchWaitReason();
        if (branchWaitFeedback is { } current) return current.Reason;
        if (unconfirmedStoryEnding || engine!.Mode == RunMode.Gap)
            return "后续连接尚未确认。请按游戏当前画面手动选择续接句。";
        if (engine.ReviewRoute != null && engine.MenuWaiting && engine.Notice.Length > 0) return engine.Notice;
        if (textArmed && (inputBranchRecovery != null || engine.MenuWaiting))
        {
            if (textActiveConflict) return "两路对白内容不一致，暂不能确定当前句。";
            if (!textProbes.Any(p => p.Active)) return "尚未读到选后的完整对白；请先在游戏中选择。";
            return "正在核对游戏当前正文，确认后继续原跟随。";
        }
        if (engine.MenuWaiting) return "分支等待手动确认。请先在游戏中选择，再按当前台词续接。";
        return "";
    }

    void RefreshBranchFeedbackPresentation()
    {
        string reason = CurrentBranchWaitReason();
        branchWaitNotice.Text = reason;
        branchWaitNotice.Visibility = reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        stripContinuationButton.Visibility = reason.Length > 0 && !preferences.CompactControlsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (compactFollowActions.TryGetValue("continuation", out var action))
        {
            action.IsEnabled = reason.Length > 0;
            action.Visibility = reason.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        }
        if (!ListeningActive)
        {
            if (reason.Length > 0) StripState.Text = reason;
            CurrentText.Text = GameBranchLineText();
        }
        branchMenu.SetContinuationFeedback(engine, reason);
    }

    void OpenBranchFeedbackContinuation()
    {
        if (CurrentBranchWaitReason().Length == 0) return;
        // 只浏览时也撤销旧监听，避免列表刚打开就由异步正文提交路线。
        if (textArmed || inputBranchRecovery != null) PauseTextPlayback("已打开手动续接，文字监听暂停。确认当前台词后再播放。");
        ClearBranchWaitReason();
        OpenManualContinuation();
    }
}
