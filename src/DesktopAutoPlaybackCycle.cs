using System;

namespace PgrVoice;

internal enum DesktopAutoPlaybackPhase { Off, StartingAudio, Playing, WaitingToClick, Clicking, WaitingForTransition, SilentPause }

// Pure policy only. All now values are monotonic milliseconds supplied by the
// owner. A completed click is an input acknowledgement, never proof of dialogue.
internal sealed class DesktopAutoPlaybackCycle
{
    public DesktopAutoPlaybackPhase Phase { get; private set; }
    public bool Running => Phase != DesktopAutoPlaybackPhase.Off;
    public long Epoch { get; private set; }
    public string NodeId { get; private set; } = "";
    public string? ExpectedNext { get; private set; }
    public long AudioTicket { get; private set; }
    public long ClickTicket { get; private set; }
    public long Deadline { get; private set; }
    public long SilentPauseUntil { get; private set; }
    public bool ClickAttempted { get; private set; }
    public bool ClickSubmitted { get; private set; }
    long nextClickTicket;

    public string PhaseText => Phase switch
    {
        DesktopAutoPlaybackPhase.StartingAudio => ClickSubmitted ? "已发送一次点击 · 等待下一句开播" : "正在等待当前句开播",
        DesktopAutoPlaybackPhase.Playing => "正在播音 · 读完后再点下一句",
        DesktopAutoPlaybackPhase.SilentPause => "标点停顿 · 稍后自动继续",
        DesktopAutoPlaybackPhase.WaitingToClick => "语音已结束 · 等待点击下一句",
        DesktopAutoPlaybackPhase.Clicking => "正在提交一次点击 · 不会重复补点",
        DesktopAutoPlaybackPhase.WaitingForTransition => "已发送一次点击 · 等待转场",
        _ => "自动播放未开启"
    };
    public string ClickOutcomeText => ClickSubmitted ? "已发送一次点击，请核对游戏位置；不会补点。" :
        ClickAttempted ? "已尝试发送一次点击，请核对游戏位置；不会补点。" : "本次尚未点击游戏下一句。";
    public string TimeoutReason => Phase switch
    {
        DesktopAutoPlaybackPhase.StartingAudio => "等待配音开播超过 10 秒，自动播放已暂停。",
        DesktopAutoPlaybackPhase.Playing => "配音超过预计时长仍未自然结束，自动播放已暂停。",
        DesktopAutoPlaybackPhase.SilentPause => "标点停顿处理超时，自动播放已暂停。",
        DesktopAutoPlaybackPhase.WaitingToClick => "等待下一次点击超时，自动播放已暂停。",
        DesktopAutoPlaybackPhase.Clicking => "等待点击完成超过 4 秒，自动播放已暂停。",
        DesktopAutoPlaybackPhase.WaitingForTransition => "点击后的等待已超时，自动播放已暂停。",
        _ => "自动播放已停止。"
    };

    public bool Start(string nodeId, long audioTicket, long now)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(nodeId) || audioTicket <= 0 || now < 0) return false;
        NodeId = nodeId; AudioTicket = audioTicket;
        Phase = DesktopAutoPlaybackPhase.StartingAudio; Deadline = Add(now, 10_000);
        return true;
    }
    public void Stop()
    {
        Epoch++; Phase = DesktopAutoPlaybackPhase.Off; NodeId = ""; ExpectedNext = null;
        AudioTicket = ClickTicket = Deadline = SilentPauseUntil = 0; ClickAttempted = ClickSubmitted = false;
    }
    public bool AudioStarted(long epoch, long ticket, long now, double durationSeconds)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.StartingAudio || ticket <= 0 || ticket != AudioTicket) return false;
        Phase = DesktopAutoPlaybackPhase.Playing;
        // Unknown/zero duration still has a bounded completion wait. Real long
        // recordings get their full duration plus thirty seconds, never 10 min.
        double durationMs = double.IsFinite(durationSeconds) && durationSeconds > 0 ? Math.Ceiling(durationSeconds * 1000) : 0;
        long duration = durationMs >= long.MaxValue - 30_000 ? long.MaxValue - 30_000 : (long)durationMs;
        Deadline = Add(now, duration + 30_000);
        ClickAttempted = ClickSubmitted = false;
        return true;
    }
    public bool CompleteAudio(long epoch, long ticket, long now, long delayMs)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.Playing || ticket <= 0 || ticket != AudioTicket || delayMs < 0 || delayMs > 60_000) return false;
        Phase = DesktopAutoPlaybackPhase.WaitingToClick; ExpectedNext = null;
        Deadline = Add(now, delayMs + 4_000); return true;
    }
    public bool BeginSilentPause(long epoch, long ticket, long now)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.StartingAudio || ticket <= 0 || ticket != AudioTicket) return false;
        SilentPauseUntil = Add(now, AutoPlaybackLinePolicy.SilentPauseMilliseconds);
        Deadline = Add(SilentPauseUntil, 10_000); Phase = DesktopAutoPlaybackPhase.SilentPause;
        ClickAttempted = ClickSubmitted = false;
        return true;
    }
    public bool CompleteSilentPause(long epoch, long ticket, long now)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.SilentPause || ticket != AudioTicket || now < SilentPauseUntil) return false;
        SilentPauseUntil = 0; ExpectedNext = null;
        Phase = DesktopAutoPlaybackPhase.WaitingToClick; Deadline = Add(now, 4_000);
        return true;
    }
    public bool BeginClick(long epoch, string nextNode, long now)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.WaitingToClick ||
            string.IsNullOrWhiteSpace(nextNode) || nextNode == NodeId) return false;
        ExpectedNext = nextNode; ClickTicket = ++nextClickTicket;
        ClickAttempted = true; ClickSubmitted = false;
        Phase = DesktopAutoPlaybackPhase.Clicking; Deadline = Add(now, 4_000); return true;
    }
    public bool ClickCompleted(long epoch, long clickTicket, string nextNode, long now)
    {
        if (!Running || epoch != Epoch || now < 0 || Phase != DesktopAutoPlaybackPhase.Clicking || clickTicket <= 0 || clickTicket != ClickTicket || nextNode != ExpectedNext) return false;
        // A late successful OS acknowledgement must not advance, but the pause
        // message must still disclose that one click was actually submitted.
        ClickSubmitted = true;
        if (TimedOut(now)) return false;
        Phase = DesktopAutoPlaybackPhase.WaitingForTransition;
        Deadline = Add(now, 4_000); return true;
    }
    public bool PrepareNextAudio(long epoch, long clickTicket, string nextNode, long ticket, long now)
    {
        if (!Accept(epoch, now) || Phase != DesktopAutoPlaybackPhase.WaitingForTransition || clickTicket <= 0 ||
            clickTicket != ClickTicket || nextNode != ExpectedNext || ticket <= AudioTicket) return false;
        NodeId = nextNode; ExpectedNext = null; AudioTicket = ticket; ClickTicket = 0;
        Phase = DesktopAutoPlaybackPhase.StartingAudio; Deadline = Add(now, 10_000); return true;
    }
    public bool TimedOut(long now) => Running && Deadline > 0 && now >= Deadline;
    bool Accept(long epoch, long now) => Running && epoch == Epoch && now >= 0 && !TimedOut(now);
    static long Add(long now, long duration) => now > long.MaxValue - duration ? long.MaxValue : now + duration;
}
