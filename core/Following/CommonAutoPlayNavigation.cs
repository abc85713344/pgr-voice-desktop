namespace PgrVoice;

/// <summary>只读的游戏点击门控；Allowed 仅表示可以申请点击，不能代替屏幕、音频和会话校验。</summary>
public sealed record CommonAutoPlayDecision(bool CurrentIsCommon, bool Allowed, Node? Next, string Code, string Reason);

public sealed partial class PlaybackEngine
{
    /// <summary>
    /// 只有明确共同线之间的直接连接才允许点击。当前共同线的末句仍可播放，
    /// 但在下一步是选择、汇合、返回、未知段落或结束时，Allowed 为 false。
    /// 本方法不修改剧情、历史、条件或播放状态，应在与其他导航相同的串行队列上调用。
    /// </summary>
    public CommonAutoPlayDecision EvaluateCommonAutoPlayNext()
    {
        CommonAutoPlayDecision Deny(string code, string reason, bool common = false) => new(common, false, null, code, reason);
        if (Current == null) return Deny("no-current", "请先确认游戏当前台词。");
        if (Current.Kind != "line") return Deny(Current.Kind, BoundaryReason(Current.Kind));
        if (Mode != RunMode.Following) return Deny("not-following", "请先确认当前台词并恢复配音，再开启自动播放。");
        if (singleLine || ReviewRoute != null) return Deny("single-line", "单句核对期间不能自动播放，请先确认共同剧情位置。");
        if (Current.Archived || !Allowed(Current)) return Deny("current-unavailable", "当前位置尚未核实，请手动确认。");

        var graph = BuildCommonAutoPlayGraph();
        if (!graph.Common.Contains(Current.Id))
            return Deny(graph.Branch.Contains(Current.Id) || !string.IsNullOrEmpty(Current.PathId) ? "branch" : "unproven-common",
                graph.Branch.Contains(Current.Id) || !string.IsNullOrEmpty(Current.PathId)
                    ? "分支线内不自动播放。回到共同剧情后，请手动重新开启。"
                    : "无法确认这段属于共同剧情，已暂停自动播放。");
        if (HasCommonAutoPlayCycle(Current, graph.Common))
            return Deny("cycle", "剧情连接存在循环，已暂停自动播放，请手动定位。");
        if (Current.NextId == null) return Deny("end", "共同剧情已结束，自动播放已暂停。", true);
        if (!Pack.ById.TryGetValue(Current.NextId, out var next))
            return Deny("missing-next", "下一句不存在，已暂停自动播放。", true);
        if (next.Archived || next.SectionId != Current.SectionId)
            return Deny("invalid-next", "下一步跨小节或已经失效，请手动确认。", true);
        if (next.Kind != "line") return Deny(next.Kind, BoundaryReason(next.Kind), true);
        if (!graph.Common.Contains(next.Id) || !Allowed(next))
            return Deny(graph.Branch.Contains(next.Id) || !string.IsNullOrEmpty(next.PathId) ? "next-branch" : "next-unproven",
                "下一步进入分支或未核实段落，自动播放已暂停，请按游戏画面手动选择。", true);
        // 与旧 OCR 推进的直达预览再交叉核对，不能用点击门控扩大既有导航权限。
        if (GetAutomaticNextLine()?.Id != next.Id)
            return Deny("navigation-mismatch", "下一句与当前路线不一致，已暂停自动播放。", true);
        return new(true, true, next, "common-next", "共同剧情，可以等待本句配音结束后点击下一句。");
    }

    static string BoundaryReason(string kind) => kind switch
    {
        "choice" => "下一步是分支选择，自动播放已暂停。请手动选择，分支线内不会自动播放。",
        "merge" => "已到剧情汇合点，自动播放已暂停。请确认共同剧情后重新开启。",
        "return" => "已到路线返回点，自动播放已暂停，请手动确认共同剧情。",
        "gap" => "下一段尚未核实，自动播放已暂停，请手动定位。",
        "end" => "共同剧情已结束，自动播放已暂停。",
        _ => "当前位置不能自动播放，请手动确认。"
    };

    bool HasCommonAutoPlayCycle(Node start, HashSet<string> common)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var node = start;
        while (common.Contains(node.Id) && node.Kind == "line")
        {
            if (!seen.Add(node.Id)) return true;
            if (node.NextId == null || !Pack.ById.TryGetValue(node.NextId, out var next) || next.SectionId != start.SectionId) return false;
            node = next;
        }
        return false;
    }

    sealed record CommonAutoPlayGraph(HashSet<string> Common, HashSet<string> Branch);

    CommonAutoPlayGraph BuildCommonAutoPlayGraph()
    {
        var active = Pack.Nodes.Where(n => !n.Archived).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var menus = active.Values.Where(n => n.Kind == "choice").ToArray();
        var branch = new HashSet<string>(active.Values.Where(n => !string.IsNullOrEmpty(n.PathId)).Select(n => n.Id), StringComparer.Ordinal);
        // 旧版包的分支可能漏写 PathId。显式段落清单和从选项入口到出口的图范围都属于分支。
        foreach (var menu in menus)
        foreach (var option in menu.Options)
        {
            branch.UnionWith(option.SegmentIds);
            branch.UnionWith(option.LineIds);
            if (option.BoundaryId != null) branch.Add(option.BoundaryId);
            var stack = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            stack.Push(option.TargetId);
            while (stack.TryPop(out var id))
            {
                if (id != option.TargetId && (id == option.MergeId || id == option.ReturnId)) continue;
                if (!seen.Add(id) || !active.TryGetValue(id, out var node)) continue;
                branch.Add(id);
                if (node.SectionId != menu.SectionId) continue;
                if (node.NextId != null) stack.Push(node.NextId);
                foreach (var nested in node.Options) stack.Push(nested.TargetId);
            }
        }

        var common = new HashSet<string>(StringComparer.Ordinal);
        var seeds = Pack.Chapters.SelectMany(c => c.Sections).Select(s => (s.StartId, s.Id)).ToList();
        foreach (var menu in menus)
        {
            // 顶层所有选项必须明确汇合到同一个 merge；仅有空 PathId 或一个 ReturnId 不构成共同线证据。
            if (branch.Contains(menu.Id) || !string.IsNullOrEmpty(menu.PathId) || menu.Options.Count == 0) continue;
            string? mergeId = menu.Options[0].MergeId;
            if (mergeId == null || !active.TryGetValue(mergeId, out var merge) || merge.Kind != "merge" ||
                merge.SectionId != menu.SectionId || !string.IsNullOrEmpty(merge.PathId) || branch.Contains(merge.Id)) continue;
            if (menu.Options.Any(o => o.MergeId != mergeId || !HasVerifiedCommonExit(menu, o, merge, active))) continue;
            if (merge.NextId != null) seeds.Add((merge.NextId, menu.SectionId));
        }
        foreach (var (startId, sectionId) in seeds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? id = startId;
            while (id != null && seen.Add(id) && active.TryGetValue(id, out var node))
            {
                if (node.SectionId != sectionId || node.Kind != "line" || branch.Contains(id) || !string.IsNullOrEmpty(node.PathId) ||
                    node.Options.Count != 0 || node.SetFacts.Count != 0 || !string.IsNullOrEmpty(node.CompleteRoute)) break;
                common.Add(id);
                id = node.NextId;
            }
        }
        return new(common, branch);
    }

    bool HasVerifiedCommonExit(Node menu, ChoiceOption option, Node merge, Dictionary<string, Node> active)
    {
        if (Pack.SchemaVersion == 3)
        {
            if (!option.BodyVerified || !option.ExitVerified || option.BodyEvidence.Count == 0 || option.ExitEvidence.Count == 0 ||
                option.ReturnId != merge.Id || !option.SegmentIds.Contains(option.TargetId)) return false;
        }
        else if (!option.Verified || Pack.SchemaVersion == 2 && (option.Evidence.Count == 0 || option.ReturnId != merge.Id)) return false;
        if (option.Requires.Intersect(option.Excludes, StringComparer.Ordinal).Any()) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = option.TargetId;
        while (id != null && id != merge.Id)
        {
            if (!seen.Add(id) || !active.TryGetValue(id, out var node) || node.SectionId != menu.SectionId ||
                node.Kind is not ("line" or "return") || node.Options.Count > 0 ||
                !string.IsNullOrEmpty(node.PathId) && node.PathId != option.PathId ||
                Pack.SchemaVersion == 3 && !option.SegmentIds.Contains(id)) return false;
            id = node.NextId;
        }
        return id == merge.Id && seen.Count > 0;
    }
}
