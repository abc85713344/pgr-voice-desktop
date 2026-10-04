using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.Listening;

/// <summary>
/// 独立的听书导航。既不恢复也不改变 PlaybackEngine；不做 OCR，不控制游戏。
/// 全部分支是资料收听顺序，包含互斥/条件路线，不把它们拼成一次游戏经历。
/// </summary>
public sealed class ListeningSession
{
    readonly Pack pack;
    readonly Chapter chapter;
    readonly string fingerprint;
    Dictionary<string, string> choices = new(StringComparer.Ordinal);
    List<ListeningItem> items = new();
    Dictionary<string, int> menuPositions = new(StringComparer.Ordinal);
    int index;

    public ListeningSession(Pack pack, string chapterId, ListeningBranchPolicy policy = ListeningBranchPolicy.First)
    {
        this.pack = pack ?? throw new ArgumentNullException(nameof(pack));
        chapter = pack.Chapters.FirstOrDefault(c => c.Id == chapterId) ?? throw new ArgumentException("大章节不存在", nameof(chapterId));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Policy = policy;
        // 正文更改也使句内毫秒位置失效；音频文件/配音更新不改变位置与书签。
        fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "listening-plan:3:" + PlaybackEngine.NavigationFingerprint(pack) + JsonSerializer.Serialize(
                pack.Nodes.Where(n => chapter.Sections.Any(s => s.Id == n.SectionId))
                    .Select(n => new { n.Id, n.Text, n.Speaker }), Json.Options))));
        Rebuild();
    }

    public Pack Pack => pack;
    public Chapter Chapter => chapter;
    public string ChapterId => chapter.Id;
    public string ChapterTitle => chapter.Title;
    public ListeningBranchPolicy Policy { get; private set; }
    public ListeningItem? Current => index >= 0 && index < items.Count ? items[index] : null;
    public bool Completed => index >= items.Count;
    public IReadOnlyList<ListeningItem> Items => items;
    public int Position => index;
    public long ResumePositionMs { get; private set; }
    public bool HasPendingChoice => Current?.Kind == ListeningItemKind.Choice;

    void Rebuild()
    {
        var builder = new PlanBuilder(pack, chapter, Policy, choices);
        items = builder.Build(); menuPositions = builder.MenuPositions;
    }

    static bool TryMenuPosition(IReadOnlyList<ListeningItem> plan, IReadOnlyDictionary<string, int> positions,
        string? itemId, string sectionId, out int position)
    {
        position = -1;
        // 空菜单或被去重的菜单可能不产生任何内容，记录的插入位置随后会落到下一小节。
        // 这种情况不能解释成用户选择了跳节，也不能将无内容的锚点当成整章完结。
        if (itemId == null || !positions.TryGetValue(itemId, out int candidate)
            || candidate < 0 || candidate >= plan.Count || plan[candidate].SectionId != sectionId) return false;
        position = candidate;
        return true;
    }

    /// <summary>音频完成、显式跳句或 Notice 已展示后调用。Choice 必须 Choose/SkipChoice。</summary>
    public bool MoveNext()
    {
        if (Completed || HasPendingChoice) return false;
        index++;
        ResumePositionMs = 0;
        return Current != null;
    }

    public bool Previous()
    {
        for (int i = Math.Min(index - 1, items.Count - 1); i >= 0; i--)
            if (items[i].Kind == ListeningItemKind.Line)
            {
                index = i; ResumePositionMs = 0; return true;
            }
        return false;
    }

    public bool Choose(string optionId)
    {
        var current = Current;
        if (current?.Kind != ListeningItemKind.Choice || current.ChoiceKey == null || current.Options?.Any(o => o.Id == optionId) != true) return false;
        choices[current.ChoiceKey] = optionId;
        Rebuild();
        // 未决选择是本小节已展开部分的最后一项，替换后同一索引即为选中路线首项。
        index = Math.Min(index, items.Count);
        ResumePositionMs = 0;
        return true;
    }

    public bool SkipChoice()
    {
        if (Current?.Kind != ListeningItemKind.Choice || Current.ChoiceKey == null) return false;
        choices[Current.ChoiceKey] = PlanBuilder.Skip;
        Rebuild(); index = Math.Min(index, items.Count); ResumePositionMs = 0;
        return true;
    }

    public bool SeekNode(string nodeId)
    {
        int target = items.FindIndex(i => i.NodeId == nodeId && i.Kind == ListeningItemKind.Line);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; return true;
    }

    /// <summary>目录定位只接受当前计划中的台词或待选项；不展开或确认任何分支。</summary>
    public bool SeekItem(string itemId)
    {
        int target = items.FindIndex(i => i.Id == itemId && i.Kind is ListeningItemKind.Line or ListeningItemKind.Choice);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; return true;
    }

    public bool SeekSection(string sectionId)
    {
        int target = items.FindIndex(i => i.SectionId == sectionId);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; return true;
    }

    /// <summary>切换策略不发声；优先保留本句，旧策略没有对应句时回到该小节首项。</summary>
    public void SetPolicy(ListeningBranchPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy == Policy) return;
        var current = Current;
        Policy = policy; choices.Clear(); Rebuild(); index = 0; ResumePositionMs = 0;
        if (current != null)
        {
            if (current.Kind == ListeningItemKind.Choice && TryMenuPosition(items, menuPositions, current.Id, current.SectionId, out int menuPosition))
                index = menuPosition;
            else if (!SeekNode(current.NodeId)) SeekSection(current.SectionId);
        }
    }

    public ListeningSnapshot Capture(long positionMs = 0)
    {
        var current = Current;
        // 完结仍保留最后一句的可读位置，恢复则保持 Completed，不会自动重播。
        var description = current ?? items.LastOrDefault(i => i.Kind == ListeningItemKind.Line);
        return new()
        {
            PackId = pack.Id, ChapterId = chapter.Id, Fingerprint = fingerprint, Policy = Policy,
            ItemId = current?.Id, NodeId = description?.NodeId,
            SectionId = description?.SectionId ?? "", SectionTitle = description?.SectionTitle ?? "",
            LineNumber = description?.LineNumber ?? 0, Speaker = description?.Node?.Speaker ?? "",
            Text = description?.Node?.Text ?? description?.Notice ?? "", Completed = Completed,
            PositionMs = current?.Kind == ListeningItemKind.Line ? Math.Max(0, positionMs) : 0,
            Choices = new(choices, StringComparer.Ordinal), UpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    public bool TryRestore(ListeningSnapshot snapshot, out string reason)
    {
        reason = "";
        if (snapshot == null || snapshot.Version != 1 || snapshot.PackId != pack.Id || snapshot.ChapterId != chapter.Id || !Enum.IsDefined(snapshot.Policy) || snapshot.Choices == null)
        { reason = "这份听书位置不属于当前章节，或格式不受支持。"; return false; }
        // 更新仅改音频时精确恢复；正文/导航改变后只允许稳定节点定位，清除句内时间。
        var restoredChoices = snapshot.Choices.Where(p => p.Key != null && p.Value != null)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var builder = new PlanBuilder(pack, chapter, snapshot.Policy, restoredChoices);
        var restoredItems = builder.Build();
        bool same = snapshot.Fingerprint == fingerprint;
        int target = snapshot.Completed && same ? restoredItems.Count : restoredItems.FindIndex(i => i.Id == snapshot.ItemId);
        int menuPosition = -1;
        bool resumedMenu = target < 0 && TryMenuPosition(restoredItems, builder.MenuPositions, snapshot.ItemId, snapshot.SectionId, out menuPosition);
        if (resumedMenu) target = menuPosition;
        if (target < 0 && snapshot.NodeId != null)
        {
            string nodeId = snapshot.NodeId;
            var seen = new HashSet<string>();
            while (pack.Migrations.TryGetValue(nodeId, out var migrated) && seen.Add(nodeId)) nodeId = migrated;
            target = restoredItems.FindIndex(i => i.NodeId == nodeId && i.Kind == ListeningItemKind.Line);
        }
        bool resumeAtInteraction = false;
        if (target < 0 && !same)
        {
            // 旧听书计划曾跳过互动。更新后不要越过新增的待选点恢复后文，也不清空旧断点。
            target = restoredItems.FindIndex(i => i.SectionId == snapshot.SectionId && i.Kind == ListeningItemKind.Choice
                && i.Notice.StartsWith("等待互动：", StringComparison.Ordinal));
            resumeAtInteraction = target >= 0;
        }
        if (target < 0) { reason = "配音包内容已变化，原位置不在当前收听路线中，请重新选择小节。"; return false; }
        choices = restoredChoices; items = restoredItems; menuPositions = builder.MenuPositions; Policy = snapshot.Policy; index = target;
        ResumePositionMs = same && Current?.Kind == ListeningItemKind.Line ? Math.Clamp(snapshot.PositionMs, 0, 24 * 60 * 60 * 1000L) : 0;
        if (resumedMenu) reason = "已按收听策略接回原互动位置，点击播放继续。";
        else if (!same) reason = resumeAtInteraction ? "听书互动导航已更新，已停在本节互动选项，确认后继续。" : "章节内容或听书导航已更新，已按台词编号定位；本句从头播放。";
        return true;
    }

    sealed class PlanBuilder(Pack pack, Chapter chapter, ListeningBranchPolicy policy, Dictionary<string, string> selections)
    {
        internal const string Skip = "\u0000skip";
        readonly List<ListeningItem> result = new();
        readonly HashSet<string> emitted = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> menuVisits = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> heardOptions = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> lineNumbers = new(StringComparer.Ordinal);
        internal Dictionary<string, int> MenuPositions { get; } = new(StringComparer.Ordinal);
        bool waiting;
        int operations;
        const int MaxOperations = 200000;
        const int MaxDepth = 96;

        internal List<ListeningItem> Build()
        {
            foreach (var section in chapter.Sections)
            {
                // 手动选择只截断当前小节；后面小节仍列入目录，可由用户明确跳转。
                waiting = false;
                int number = 0;
                foreach (var line in pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section.Id)) lineNumbers[line.Id] = ++number;
                Walk(section.StartId, section, new(StringComparer.Ordinal), "", null, 0);
                if (policy == ListeningBranchPolicy.All && !waiting)
                {
                    var unconnected = pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section.Id && !emitted.Contains(n.Id)).ToList();
                    if (unconnected.Count > 0)
                    {
                        Notice(section, $"以下 {unconnected.Count} 句是配音包已收录、尚未接入导航的补充片段，按收录顺序播放；不代表已核实的连续游戏路线。");
                        foreach (var line in unconnected)
                        {
                            emitted.Add(line.Id);
                            result.Add(new("line:" + line.Id, ListeningItemKind.Line, line, section.Id, section.Title,
                                lineNumbers.GetValueOrDefault(line.Id), "小节补充片段（顺序未核实）", ""));
                        }
                    }
                }
            }
            return result;
        }

        bool Valid(string? id, Section section) => id != null && pack.ById.TryGetValue(id, out var n) && !n.Archived && n.SectionId == section.Id;

        void Notice(Section section, string message, string? nodeId = null)
        {
            string key = "notice:" + (nodeId ?? section.Id) + ":" + message;
            if (!emitted.Add(key)) return;
            result.Add(new(key, ListeningItemKind.Notice, nodeId != null ? pack.ById.GetValueOrDefault(nodeId) : null,
                section.Id, section.Title, 0, "", message));
        }

        string? Walk(string? id, Section section, HashSet<string> stops, string branch, HashSet<string>? scope, int depth, bool confirmEntry = false)
        {
            if (waiting) return null;
            if (depth > MaxDepth) { Notice(section, "分支嵌套过深，已跳过无法安全展开的部分。", id); return null; }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (id != null && !waiting)
            {
                if (++operations > MaxOperations) { Notice(section, "章节连接过于复杂，已停止展开此小节。", id); return null; }
                if (stops.Contains(id)) return id;
                if (!Valid(id, section)) return id;
                var node = pack.ById[id];
                if (scope != null && !scope.Contains(id)) return id;
                if (node.Kind != "choice" && !visited.Add(id))
                { Notice(section, "检测到重复连接，已跳过循环。", id); return null; }
                switch (node.Kind)
                {
                    case "line":
                        if (emitted.Add(node.Id)) result.Add(new("line:" + node.Id, ListeningItemKind.Line, node,
                            section.Id, section.Title, lineNumbers.GetValueOrDefault(node.Id), branch, ""));
                        id = node.NextId; break;
                    case "end": return null;
                    case "gap":
                    {
                        // 只接受包内明确登记的同小节续接菜单。不能把所有 gap.NextId 当成可靠出口。
                        var menus = node.ResumeMenuIds.Distinct(StringComparer.Ordinal)
                            .Where(m => Valid(m, section) && pack.ById[m].Kind == "choice").ToList();
                        if (menus.Count > 0)
                        {
                            string next = menus[0];
                            if (menus.Count > 1)
                            {
                                int visit = menuVisits.GetValueOrDefault("resume:" + node.Id);
                                menuVisits["resume:" + node.Id] = visit + 1;
                                string key = "resume:" + node.Id + ":" + visit;
                                MenuPositions["choice:" + key] = result.Count;
                                if (policy == ListeningBranchPolicy.Manual)
                                {
                                    if (!selections.TryGetValue(key, out var selected) || selected != Skip && !menus.Contains(selected))
                                    {
                                        var candidates = menus.Select(m => new ChoiceOption { Id = m, TargetId = m,
                                            Label = string.IsNullOrWhiteSpace(pack.ById[m].Text)
                                                ? string.Join(" / ", pack.ById[m].Options.Select(o => o.Label)) : pack.ById[m].Text }).ToList();
                                        result.Add(new("choice:" + key, ListeningItemKind.Choice, node, section.Id, section.Title,
                                            0, branch, "等待互动：请选择要进入的互动菜单，确认后显示该菜单的选项。", key, candidates));
                                        waiting = true; return null;
                                    }
                                    if (selected == Skip) return null;
                                    next = selected;
                                }
                                else if (policy == ListeningBranchPolicy.All)
                                {
                                    // 多个明确续接菜单也服从全听策略。共同出口最后处理，避免先播共同线再折返另一个菜单。
                                    var joins = menus.Select(m => FindJoin(pack.ById[m], pack.ById[m].Options.Where(o => Valid(o.TargetId, section)).ToList(), section, stops)).ToList();
                                    string? common = joins.All(j => j != null) && joins.Distinct().Count() == 1 ? joins[0] : null;
                                    var menuStops = new HashSet<string>(stops, StringComparer.Ordinal);
                                    if (common != null) menuStops.Add(common);
                                    string? returnedMenu = null;
                                    foreach (string menu in menus)
                                    {
                                        returnedMenu = Walk(menu, section, menuStops, branch, null, depth + 1, confirmEntry: true);
                                        if (waiting) return null;
                                    }
                                    return common != null ? Walk(common, section, stops, branch, null, depth + 1) : returnedMenu;
                                }
                            }
                            // 到已登记的菜单重新建立段落范围，仍保留父菜单/共同线的停止目标。
                            return Walk(next, section, stops, branch, null, depth + 1, confirmEntry: true);
                        }
                        Notice(section, "此处游戏出口尚未核实。听书仅采用菜单已登记的后续连接；没有连接的内容不自动续播。", node.Id);
                        return null;
                    }
                    case "return":
                    case "merge": id = node.NextId; break;
                    case "choice":
                    {
                        bool interaction = confirmEntry;
                        confirmEntry = false;
                        var allOptions = node.Options.Where(o => Valid(o.TargetId, section)).ToList();
                        if (!heardOptions.TryGetValue(node.Id, out var heard)) heardOptions[node.Id] = heard = new(StringComparer.Ordinal);
                        var options = allOptions.Where(o => !heard.Contains(o.Id)).ToList();
                        string? join = FindJoin(node, allOptions, section, stops);
                        if (options.Count == 0) { id = join; if (id == node.Id) id = null; break; }
                        var chosen = options;
                        int visit = menuVisits.GetValueOrDefault(node.Id);
                        menuVisits[node.Id] = visit + 1;
                        string key = node.Id + ":" + visit;
                        MenuPositions["choice:" + key] = result.Count;
                        if (policy == ListeningBranchPolicy.Manual)
                        {
                            if (!selections.TryGetValue(key, out var selection) || selection != Skip && options.All(o => o.Id != selection))
                            {
                                result.Add(new("choice:" + key, ListeningItemKind.Choice, node, section.Id, section.Title,
                                    0, branch, interaction
                                        ? "等待互动：请选择下方选项，确认后继续本节。"
                                        : "请选择要收听的路线", key, options));
                                waiting = true; return null;
                            }
                            if (selection == Skip) { foreach (var option in options) heard.Add(option.Id); id = join; break; }
                            chosen = options.Where(o => o.Id == selection).ToList();
                        }
                        else if (policy == ListeningBranchPolicy.First)
                        {
                            // 一处菜单最多默认选一次；返回菜单后不会重复第一条造成死循环。
                            if (visit > 0) { id = join; break; }
                            chosen = options.Take(1).ToList();
                        }
                        string? returned = null;
                        var continuations = new List<string>();
                        foreach (var option in chosen)
                        {
                            heard.Add(option.Id);
                            var branchStops = new HashSet<string>(stops, StringComparer.Ordinal) { node.Id };
                            if (join != null) branchStops.Add(join);
                            // 旧包可能把多个选项的正文按文件顺序直接串在一起；到另一个选项入口即结束本段。
                            foreach (var other in allOptions.Where(o => o.TargetId != option.TargetId)) branchStops.Add(other.TargetId);
                            HashSet<string>? branchScope = null;
                            if (pack.SchemaVersion >= 3 && option.SegmentIds.Count > 0)
                            {
                                branchScope = new(option.SegmentIds, StringComparer.Ordinal);
                                if (option.BoundaryId != null) branchScope.Add(option.BoundaryId);
                            }
                            bool verified = pack.SchemaVersion >= 3 ? option.BodyVerified : option.Verified;
                            if (!verified) Notice(section, "以下分支正文尚未完全核对；仅按包内现有连接收听。", option.TargetId);
                            returned = Walk(option.TargetId, section, branchStops,
                                string.IsNullOrEmpty(branch) ? option.Label : branch + " / " + option.Label, branchScope, depth + 1);
                            if (waiting) return null;
                            if (returned != null && returned != node.Id && !stops.Contains(returned) && Valid(returned, section)
                                && pack.ById[returned].PathId == node.PathId && allOptions.All(o => o.TargetId != returned)
                                && (pack.SchemaVersion < 3 ? option.Verified : option.ExitVerified))
                                continuations.Add(returned);
                        }
                        if (policy == ListeningBranchPolicy.Manual && returned == node.Id)
                        { id = node.Id; confirmEntry = interaction; break; }
                        if (returned != null && stops.Contains(returned)) return returned;
                        // 选项可以分别返回菜单或共同线。只采用确实走到的已核实出口，
                        // 不让旧 MergeId 把“返回上一层”错误解释成退出整个选择。
                        if (join == null && continuations.Count > 0)
                        {
                            var unique = continuations.Distinct(StringComparer.Ordinal).ToList();
                            foreach (var continuation in unique.SkipLast(1))
                            {
                                Walk(continuation, section, stops, branch, scope, depth + 1);
                                if (waiting) return null;
                            }
                            id = unique[^1];
                        }
                        else id = join;
                        break;
                    }
                    default: Notice(section, "遇到不支持的节点，已跳过。", node.Id); return null;
                }
            }
            return id;
        }

        string? FindJoin(Node menu, List<ChoiceOption> options, Section section, HashSet<string> stops)
        {
            bool Usable(string? id) => id != null && Valid(id, section) && id != menu.Id && !stops.Contains(id) && pack.ById[id].PathId == menu.PathId;
            if (options.Count == 0) return Usable(menu.NextId) ? menu.NextId : null;
            // v3 未核实出口绝不借旧 ReturnId/MergeId 猜回共同线。
            var exits = options.Select(o => pack.SchemaVersion >= 3 && !o.ExitVerified ? null : o.ReturnId ?? o.MergeId).ToList();
            if (exits.All(e => e != null) && exits.Distinct().Count() == 1 && Usable(exits[0])) return exits[0];
            if (Usable(menu.NextId) && options.All(o => o.TargetId != menu.NextId)) return menu.NextId;
            // 只在已确定的连接中寻找共同点，不让未知出口穿过范围。
            if (options.Count < 2 || pack.SchemaVersion >= 3 && options.Any(o => !o.ExitVerified)) return null;
            var maps = options.Select(o => Reachable(o.TargetId, section, menu.Id)).ToList();
            return maps[0].Keys.Where(id => Usable(id) && maps.All(m => m.ContainsKey(id)))
                .OrderBy(id => maps.Max(m => m[id])).ThenBy(id => maps.Sum(m => m[id])).FirstOrDefault();
        }

        Dictionary<string, int> Reachable(string start, Section section, string excluded)
        {
            var distances = new Dictionary<string, int>(StringComparer.Ordinal);
            var pending = new Queue<(string Id, int Distance)>(); pending.Enqueue((start, 0));
            while (pending.TryDequeue(out var item) && distances.Count <= pack.Nodes.Count)
            {
                if (item.Id == excluded || !Valid(item.Id, section) || !distances.TryAdd(item.Id, item.Distance)) continue;
                var node = pack.ById[item.Id];
                if (node.Kind is "gap" or "end") continue;
                if (node.NextId != null) pending.Enqueue((node.NextId, item.Distance + 1));
                if (node.Kind == "choice") foreach (var option in node.Options)
                    if (pack.SchemaVersion < 3 || option.ExitVerified) pending.Enqueue((option.TargetId, item.Distance + 1));
            }
            return distances;
        }
    }
}
