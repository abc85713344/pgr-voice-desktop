namespace PgrVoice.Following;

/// <summary>
/// 0.4.7 游戏伴音（自动播放）里到达未归档的 end 节点（选项出口或小节正文末尾）时的等待票据。
/// 自动播放不关闭：提示使用 end 节点自己的文字，继续读取游戏画面；定位到下一句（含下一小节首句）后自动续播。听书模式不使用。
/// </summary>
public sealed record GameEndWait(string SourceId, string? EndId, string Hint, string SectionId, string? NextSectionId)
{
    /// <summary>本次等待会在哪些小节里识别下一句：终点所在小节与包内顺序的下一小节。</summary>
    public IReadOnlyList<string> SearchSections => NextSectionId == null ? new[] { SectionId } : new[] { SectionId, NextSectionId };
}

public static class GameEndWaitPolicy
{
    public const string FallbackHint = "请按游戏画面定位后续";
    public const string Eyebrow = "WAITING · 等待游戏画面定位";
    public const string Detail = "自动播放保持开启，正在读取游戏画面；定位到下一句后自动继续（含下一小节首句）。";

    /// <summary>当前句之后（或当前位置本身）的未归档 end 节点；越过选项出口的待续接 gap（例如 EX02 的 *_STOP → *_END）。</summary>
    public static Node? EndAhead(PlaybackEngine engine)
    {
        var node = engine.Current;
        for (int hop = 0; node != null && hop < 4; hop++)
        {
            if (node.Kind == "end") return node.Archived ? null : node;
            if (hop > 0 && node.Kind != "gap") return null;
            if (node.NextId == null || !engine.Pack.ById.TryGetValue(node.NextId, out var next)) return null;
            node = next;
        }
        return null;
    }

    public static string HintOf(Node? end) => string.IsNullOrWhiteSpace(end?.Text) ? FallbackHint : end!.Text.Trim();

    /// <summary>
    /// 自动续播被拒绝时判断是否属于“终点”：前方有未归档 end 节点，或导航给出 end（共同剧情末句没有下一句）。
    /// 允许续播的决定（含现稿续接）不进入等待。
    /// </summary>
    public static bool TryBegin(PlaybackEngine engine, CommonAutoPlayDecision decision, out GameEndWait? wait)
    {
        wait = null;
        if (decision.Allowed && decision.Next != null || engine.Current is not { } current) return false;
        var end = EndAhead(engine);
        if (end == null && decision.Code != "end") return false;
        string section = (end ?? current).SectionId;
        wait = new(current.Id, end?.Id, HintOf(end), section, NextSection(engine.Pack, section));
        return true;
    }

    public static string? NextSection(Pack pack, string sectionId)
    {
        var order = pack.Chapters.SelectMany(c => c.Sections).Select(s => s.Id).ToList();
        int index = order.IndexOf(sectionId);
        return index >= 0 && index + 1 < order.Count ? order[index + 1] : null;
    }

    /// <summary>识别到的一句能否结束等待：未归档台词、不是等待前的末句、位于搜索小节，并且导航器能把它确认为游戏当前句。</summary>
    public static bool CanResume(PlaybackEngine engine, GameEndWait wait, Node located, out PlaybackEngine? probe, out string reason)
    {
        probe = null; reason = "";
        if (located.Kind != "line" || located.Archived) { reason = "不是台词"; return false; }
        if (located.Id == wait.SourceId) { reason = "仍是终点前的末句"; return false; }
        if (!wait.SearchSections.Contains(located.SectionId)) { reason = "不在终点所在或下一小节"; return false; }
        var candidate = new PlaybackEngine(engine.Pack);
        if (!candidate.ImportNavigation(engine.ExportNavigation()) || !candidate.ConfirmGameLine(located.Id))
        { reason = string.IsNullOrWhiteSpace(candidate.NavigationError) ? "路线尚不能确认" : candidate.NavigationError; return false; }
        probe = candidate; return true;
    }

    /// <summary>下一小节的首句（越过开头的非台词节点）；等待时同文多句只在其中之一是它时才可采用。</summary>
    public static Node? ExpectedNext(Pack pack, GameEndWait wait)
    {
        if (wait.NextSectionId == null) return null;
        var section = pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == wait.NextSectionId);
        if (section == null || !pack.ById.TryGetValue(section.StartId, out var node)) return null;
        for (int i = 0; i < 50 && node.Kind != "line"; i++)
            if (node.NextId == null || !pack.ById.TryGetValue(node.NextId, out node!)) return null;
        return node.Kind == "line" && !node.Archived ? node : null;
    }

    /// <summary>从识别到的同文台词里选定位句：唯一即采用；多句同文时只采用下一小节首句，否则继续等待。</summary>
    public static Node? Pick(Pack pack, GameEndWait wait, IReadOnlyList<Node> exact)
    {
        var lines = exact.Where(n => n.Id != wait.SourceId).DistinctBy(n => n.Id).ToList();
        if (lines.Count == 1) return lines[0];
        return ExpectedNext(pack, wait) is { } expected && lines.Any(n => n.Id == expected.Id) ? expected : null;
    }

    public static string LogReason(GameEndWait wait) =>
        $"终点「{wait.Hint}」：自动播放保持开启，等待游戏画面定位下一句" + (wait.EndId == null ? "（共同剧情末句无终点节点）" : "");
}
