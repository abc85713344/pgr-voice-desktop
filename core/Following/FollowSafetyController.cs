namespace PgrVoice.Following;

public enum FollowMode { Manual, Automatic }
public enum FollowDecisionKind { Ignored, Waiting, Candidates, Advance }

/// <param name="FrameKey">稳定的字幕像素摘要；不要用动画背景或每帧时间戳代替。</param>
/// <param name="Sequence">当前会话内单调增加的截图编号，同一次 OCR 结果不能被重复计数。</param>
public sealed record FollowObservation(long Epoch, long Sequence, string FrameKey, bool IsStable,
    IReadOnlyList<OcrBlock> Blocks, string SectionId);

public sealed record FollowDecision(FollowDecisionKind Kind, string? NodeId, string Reason,
    IReadOnlyList<MatchCandidate> Candidates, long Epoch, long Sequence);

/// <summary>
/// 不直接播放或推进。协调器在同一串行队列上 Evaluate 后调用 TryApply；
/// 手动导航、原声、暂停、切章、重新授权和恢复进度必须先 Invalidate。
/// </summary>
public sealed class FollowSafetyController
{
    public FollowMode Mode { get; private set; }
    public long Epoch { get; private set; }
    public bool IsArmed { get; private set; }
    public bool HasPendingConfirmation => confirmations > 0;
    string? packId, position, baselineFrame, lastPlayedNode, pendingNode, pendingText;
    int confirmations;
    long lastSequence = -1, pendingSequence = -1;

    public void SetMode(FollowMode mode) { Mode = mode; Invalidate(); }
    public void Invalidate()
    {
        Epoch++;
        IsArmed = false; packId = position = baselineFrame = lastPlayedNode = null;
        lastSequence = -1; ResetConsensus();
    }

    /// <summary>由用户明确确认当前位置后调用；本操作不播放，模式切换不能自行调用它。</summary>
    public bool ConfirmPosition(PlaybackEngine engine, string? frameKey = null)
    {
        Invalidate();
        if (Mode != FollowMode.Automatic || engine.Current?.Kind != "line" ||
            engine.Mode != RunMode.Following || !engine.Allowed(engine.Current)) return false;
        IsArmed = true; packId = engine.Pack.Id; position = lastPlayedNode = engine.CurrentId;
        baselineFrame = string.IsNullOrWhiteSpace(frameKey) ? null : frameKey;
        return true;
    }

    public FollowDecision Evaluate(PlaybackEngine engine, FollowObservation observation)
    {
        FollowDecision Result(FollowDecisionKind kind, string reason, IReadOnlyList<MatchCandidate>? candidates = null, string? id = null) =>
            new(kind, id, reason, candidates ?? Array.Empty<MatchCandidate>(), observation.Epoch, observation.Sequence);
        if (observation.Epoch != Epoch || observation.Sequence <= lastSequence)
            return Result(FollowDecisionKind.Ignored, "识别结果已过期。");
        lastSequence = observation.Sequence;
        if (Mode != FollowMode.Automatic || !IsArmed)
            return Result(FollowDecisionKind.Ignored, "请先确认当前位置，再开始自动跟随。");
        if (engine.Pack.Id != packId || engine.CurrentId != position || engine.Mode != RunMode.Following)
        {
            Invalidate();
            return Result(FollowDecisionKind.Ignored, "剧情状态已改变，请重新确认位置。");
        }
        if (!observation.IsStable || string.IsNullOrWhiteSpace(observation.FrameKey))
        { ResetConsensus(); return Result(FollowDecisionKind.Waiting, "等待字幕显示完整。"); }
        if (baselineFrame == null)
        { baselineFrame = observation.FrameKey; ResetConsensus(); return Result(FollowDecisionKind.Waiting, "已记录当前字幕，等待画面变化。"); }
        if (baselineFrame == observation.FrameKey)
        { ResetConsensus(); return Result(FollowDecisionKind.Waiting, "字幕画面未变化；重复台词可使用下一句。"); }
        if (observation.SectionId != engine.Current!.SectionId)
        { ResetConsensus(); return Result(FollowDecisionKind.Ignored, "识别结果来自其他小节。"); }

        var blocks = observation.Blocks.Where(b => double.IsFinite(b.Score) && b.Score >= .35).ToList();
        var candidates = Matcher.Find(engine, observation.SectionId, blocks);
        var next = engine.GetAutomaticNextLine();
        // 字幕区还可能含有白色背景动画；仅凭掩码变化无法证明同文的下一句已经出现。
        // 标点差异也被规范化忽略，因此相邻同规范文本一律由玩家手动确认。
        string currentText = Matcher.Normalize(engine.Current.Text);
        if (next != null && Matcher.Normalize(next.Text) == currentText)
        { ResetConsensus(); return Result(FollowDecisionKind.Candidates, "下一句与当前台词文字相同，请使用手动下一句确认。", candidates); }

        var reliable = Matcher.FindAll(engine, observation.SectionId, blocks.Where(b => b.Score >= .80).ToList());
        // 箭头或小动画变化后再次读到已确认的当前句，说明位置仍一致；不应提示定位失败。
        // 只更新画面基准并清除旧共识，绝不借此推进、重播或选择另一个同文节点。
        if (currentText.Length >= 2 && reliable.Any(c => c.Node.Id == engine.CurrentId && c.Node.Kind == "line" &&
            Matcher.Normalize(c.Evidence) == currentText))
        {
            baselineFrame = observation.FrameKey;
            ResetConsensus();
            return Result(FollowDecisionKind.Waiting, "当前句已到位，等待下一句字幕。", candidates);
        }
        if (next == null)
        { ResetConsensus(); return Result(FollowDecisionKind.Candidates, "分支、段落边界或当前位置需要确认。", candidates); }

        // 排名分数包含距离加分，不把它当作 OCR 准确率；自动推进必须完整文字精确一致。
        // 下一句仍使用原来的前三候选范围；当前句核对不放宽自动推进条件。
        var exact = reliable.Take(3).FirstOrDefault(c => c.Node.Id == next.Id && c.Node.Kind == "line" &&
            Matcher.Normalize(c.Evidence) == Matcher.Normalize(next.Text));
        string target = Matcher.Normalize(next.Text);
        bool speakerKnown = blocks.Any(b => b.Score >= .80 && !string.IsNullOrWhiteSpace(next.Speaker) &&
            Matcher.Normalize(b.Text) == Matcher.Normalize(next.Speaker));
        if (exact == null || target.Length < 2 || target.Length <= 3 && !speakerKnown || next.Id == lastPlayedNode)
        { ResetConsensus(); return Result(FollowDecisionKind.Candidates, "识别不够明确或不是下一句，请确认候选。", candidates); }
        if (pendingNode == next.Id && pendingText == target) confirmations++;
        else { pendingNode = next.Id; pendingText = target; confirmations = 1; }
        pendingSequence = observation.Sequence;
        if (confirmations < 2) return Result(FollowDecisionKind.Waiting, "等待连续两次识别一致。", candidates);
        return Result(FollowDecisionKind.Advance, "当前路线的下一句已连续确认。", candidates, next.Id);
    }

    /// <summary>再次检查会话、位置与路线；决策只可应用一次。</summary>
    public bool TryApply(PlaybackEngine engine, FollowObservation observation, FollowDecision decision)
    {
        if (decision.Kind != FollowDecisionKind.Advance || decision.Epoch != Epoch || observation.Epoch != Epoch ||
            decision.Sequence != observation.Sequence || decision.Sequence != pendingSequence || lastSequence != pendingSequence ||
            !IsArmed || Mode != FollowMode.Automatic || engine.Pack.Id != packId || engine.CurrentId != position ||
            decision.NodeId != pendingNode || confirmations < 2 || decision.NodeId == null) return false;
        if (!engine.TryAdvanceAutomatically(decision.NodeId)) { Invalidate(); return false; }
        position = lastPlayedNode = decision.NodeId; baselineFrame = observation.FrameKey;
        ResetConsensus();
        return true;
    }

    void ResetConsensus() { pendingNode = pendingText = null; pendingSequence = -1; confirmations = 0; }
}
