using System;

namespace PgrVoice;

public partial class MainWindow
{
    // 真实跟随到选择点/待续接处（含紧邻前一句）时借用文字监听。
    // 不改用户的跟随方式、剧情图或自动点击设置。
    sealed record InputBranchRecovery(PlaybackEngine Owner, string Boundary, string Section,
        IntPtr Window, string PreviousLine, bool BeforeBranch);
    InputBranchRecovery? inputBranchRecovery;
    Func<bool>? branchRecoveryEnvironmentTest;

    void BeginInputBranchRecovery(string previousLine)
    {
        if (TextFollowing || closing || loadingPack || ListeningActive || automaticRunning || locating ||
            expanded || KeyTesting || engine?.Current == null || game == null ||
            SectionBox.SelectedItem is not Section section || section.Id != engine.Current.SectionId) return;
        bool beforeBranch = engine.Mode == RunMode.Following && engine.Current.Kind == "line" &&
            engine.Current.NextId is string next && engine.Pack.ById.TryGetValue(next, out var boundary) &&
            !boundary.Archived && boundary.SectionId == section.Id && boundary.Kind is "choice" or "gap";
        if (!engine.MenuWaiting && !beforeBranch) return;
        bool native = testUi ? branchRecoveryEnvironmentTest?.Invoke() == true :
            Native.IsGameWindow(game.Handle, preferences.GameExecutablePath) &&
            GameInstallation.IsNativeClientExecutable(Native.ExecutablePath(game.Handle));
        if (!native) return; // 非原生客户端沿用已有的手动续接入口。
        ClearBranchWaitReason();
        inputBranchRecovery = new(engine, engine.CurrentId!, section.Id, game.Handle, previousLine, beforeBranch);
        textOwner = engine; textSection = section.Id; textArmed = true; lastTextPlayed = "";
        textActiveConflict = false;
        CancelTextAutoAdvance(); HideBranchMenu(); ResetFollowInputSession();
        gameTextLive.Text = "等待选后第一句完整正文；选择前一句不会重播。";
        foreach (var probe in textProbes)
        {
            probe.Observed = probe.Pending = probe.ObservedSpeaker = probe.PendingSpeaker = "";
            probe.Active = false; probe.AutoConnect = true; probe.NextConnectAttempt = DateTime.MinValue;
            probe.Status.Text = "正在寻找支线后的对白…";
        }
        TextNotice("请先在游戏中选择；等待选后第一句完整正文，唯一匹配后先播这一句，再继续原来的跟随方式。同文或未显示完整时继续等待，可按 " + KeyName("ocr") + " 或手动确认。");
        Tell(gameTextNotice.Text); UpdateState();
    }

    bool InputBranchRecoveryCurrent()
    {
        if (inputBranchRecovery is not { } recovery) return true;
        return textArmed && engine == recovery.Owner && textOwner == recovery.Owner &&
            engine.CurrentId == recovery.Boundary && (engine.MenuWaiting || recovery.BeforeBranch && engine.Mode == RunMode.Following) &&
            game?.Handle == recovery.Window && textSection == recovery.Section &&
            SectionBox.SelectedItem is Section section && section.Id == recovery.Section &&
            !ListeningActive && !locating && !KeyTesting && !closing;
    }

    void CancelInputBranchRecovery(string reason)
    {
        if (inputBranchRecovery != null) PauseTextPlayback(reason);
    }

    void CompleteInputBranchRecovery()
    {
        if (inputBranchRecovery == null) return;
        // 先脱开临时探针的声音所有权；关闭扫描不会切断刚确认的新句。
        textSoundSource = null;
        string aligned = engine?.Current is { Kind: "line" } line ? line.Speaker + "：" + line.Text : "游戏当前句";
        PauseTextPlayback("已对齐：" + aligned + "；先播放本句，继续原来的跟随方式。", preserveBranchAudio: true);
        DialoguePositionConfirmed();
        // 松开选项键后才接收手柄推进；选择那次按压不能再消耗第一句。
        ResetGamepadContext();
        Tell(gameTextNotice.Text); UpdateState();
        BeginInputBranchRecovery(engine?.CurrentId ?? "");
    }
}
