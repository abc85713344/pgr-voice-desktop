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
    readonly string legacyFingerprint;
    Dictionary<string, string> choices = new(StringComparer.Ordinal);
    List<ListeningItem> items = new();
    List<ListeningChoicePoint> choicePoints = new();
    Dictionary<string, int> menuPositions = new(StringComparer.Ordinal);
    int index;

    public ListeningSession(Pack pack, string chapterId, ListeningBranchPolicy policy = ListeningBranchPolicy.First)
    {
        this.pack = pack ?? throw new ArgumentNullException(nameof(pack));
        chapter = pack.Chapters.FirstOrDefault(c => c.Id == chapterId) ?? throw new ArgumentException("大章节不存在", nameof(chapterId));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Policy = policy;
        // 正文更改也使句内毫秒位置失效；音频文件/配音更新不改变位置与书签。
        string contentIdentity = PlaybackEngine.NavigationFingerprint(pack) + JsonSerializer.Serialize(
                pack.Nodes.Where(n => chapter.Sections.Any(s => s.Id == n.SectionId))
                    .Select(n => new { n.Id, n.Text, n.Speaker }), Json.Options);
        fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("listening-plan:4:" + contentIdentity)));
        legacyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("listening-plan:3:" + contentIdentity)));
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
    public IReadOnlyList<ListeningChoicePoint> ChoicePoints => choicePoints;
    public int Position => index;
    public long ResumePositionMs { get; private set; }
    public bool HasPendingChoice => Current?.Kind == ListeningItemKind.Choice;
    public bool HasBlockingNotice => Current?.IsBlocking == true;

    void Rebuild()
    {
        var builder = new PlanBuilder(pack, chapter, Policy, choices);
        items = builder.Build(); menuPositions = builder.MenuPositions; choicePoints = builder.ChoicePoints;
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

    /// <summary>音频完成、显式跳句或普通 Notice 已展示后调用。待选及未核续接均不能自动越过。</summary>
    public bool MoveNext()
    {
        if (Completed || HasPendingChoice || HasBlockingNotice) return false;
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

    /// <summary>
    /// 0.4.7 停下处的自动处理（G/I/L）：待选支线按默认序号选择；未核出口（UnverifiedExit / ScopeBoundary / UnverifiedBody）
    /// 在本节配音包顺序中最后一句已听台词之后还有未听台词时按顺序继续（I），之后再无未听台词则小节已读完、进入下一小节；
    /// 无效连接（下一处指向不存在的节点）、过深、尚不支持的连接为无文本死角（L）：跳到下一小节开头，最后一个小节则结束本章。
    /// </summary>
    public ListeningStop? ResolveStop(int defaultNumber)
    {
        var current = Current;
        if (current == null) return null;
        if (current.Kind == ListeningItemKind.Choice && current.Options is { Count: > 0 } options)
        {
            var option = options[defaultNumber == 2 && options.Count >= 2 ? 1 : 0];
            return new(ListeningStopAction.Choose, option.Id, option.Label, null, current.Notice);
        }
        if (!current.IsBlocking) return null;
        bool orderKind = current.WaitingKind is ListeningWaitingKind.UnverifiedExit or ListeningWaitingKind.ScopeBoundary or ListeningWaitingKind.UnverifiedBody;
        if (orderKind && NextLineInOrder() is { } line)
            return new(ListeningStopAction.OrderContinue, null, null, line, current.Notice);
        int next = NextSectionIndex();
        // 未核出口之后本节已无未听台词：小节已读完，按顺序进入下一小节（不是死角）；无效连接 / 过深 / 不支持的连接才是无文本死角。
        return new(next < items.Count ? ListeningStopAction.NextSection : ListeningStopAction.ChapterEnd, null, null, null, current.Notice, SectionComplete: orderKind);
    }

    /// <summary>本节台词按配音包列出顺序；从当前停下处之前最后一句已听台词之后，取第一句尚未列入本次计划的台词。</summary>
    public string? NextLineInOrder()
    {
        var current = Current;
        if (current == null) return null;
        var planned = items.Where(i => i.SectionId == current.SectionId && i.Kind == ListeningItemKind.Line).Select(i => i.NodeId).ToHashSet(StringComparer.Ordinal);
        var lines = pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == current.SectionId).ToList();
        string? last = items.Take(index).LastOrDefault(i => i.SectionId == current.SectionId && i.Kind == ListeningItemKind.Line)?.NodeId;
        int start = last == null ? 0 : lines.FindIndex(n => n.Id == last) + 1;
        return lines.Skip(Math.Max(0, start)).FirstOrDefault(n => !planned.Contains(n.Id))?.Id;
    }

    int NextSectionIndex()
    {
        var current = Current;
        if (current == null) return items.Count;
        int section = SectionRank(current.SectionId);
        for (int i = index + 1; i < items.Count; i++)
            if (items[i].SectionId != current.SectionId && SectionRank(items[i].SectionId) > section) return i;
        return items.Count;
    }

    // 与收听计划同一小节顺序比较先后；包内原顺序可能把「篇名-3」排在「篇名-1」前。
    int SectionRank(string sectionId)
    {
        var ordered = SectionDisplay.OrderTitledParts(chapter.Sections);
        for (int i = 0; i < ordered.Count; i++) if (ordered[i].Id == sectionId) return i;
        return -1;
    }

    public bool ApplyStop(ListeningStop stop)
    {
        var current = Current;
        if (current == null) return false;
        switch (stop.Action)
        {
            case ListeningStopAction.Choose: return stop.OptionId != null && Choose(stop.OptionId);
            case ListeningStopAction.OrderContinue:
                if (!current.IsBlocking || stop.LineId == null) return false;
                choices[PlanBuilder.OrderPrefix + current.Id] = stop.LineId;
                int keep = index; Rebuild(); index = Math.Min(keep, items.Count); ResumePositionMs = 0;
                return Current?.IsBlocking != true;
            case ListeningStopAction.NextSection:
            case ListeningStopAction.ChapterEnd:
                if (!current.IsBlocking) return false;
                index = NextSectionIndex(); ResumePositionMs = 0; return true;
        }
        return false;
    }

    public bool SkipChoice()
    {
        if (Current?.Kind != ListeningItemKind.Choice || Current.ChoiceKey == null) return false;
        choices[Current.ChoiceKey] = PlanBuilder.Skip;
        Rebuild(); index = Math.Min(index, items.Count); ResumePositionMs = 0;
        return true;
    }

    /// <summary>
    /// 显式重新打开当前手动计划中的已选菜单；不发声，也不借目录跳转隐式改选。
    /// 保留本节前序选择和其他小节，只清除此处与本节后续（含已经失效的嵌套选择）。
    /// </summary>
    public bool ReopenChoice(string choiceKey)
    {
        if (Policy != ListeningBranchPolicy.Manual || string.IsNullOrEmpty(choiceKey)) return false;
        var matches = choicePoints.Where(p => p.ChoiceKey == choiceKey).ToArray();
        if (matches.Length != 1 || !matches[0].IsResolved) return false;
        var point = matches[0];
        var earlier = choicePoints.Where(p => p.SectionId == point.SectionId && p.Order < point.Order)
            .Select(p => p.ChoiceKey).ToHashSet(StringComparer.Ordinal);
        var proposed = new Dictionary<string, string>(choices, StringComparer.Ordinal);
        foreach (string key in choices.Keys)
        {
            // 从现有节点生成合法前缀，不能按冒号拆分任意节点编号或把未知存档键当成路线。
            var owners = ChoiceKeySections(key).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length > 1 && owners.Contains(point.SectionId)) return false;
            if (owners.Length == 1 && owners[0] == point.SectionId && !earlier.Contains(key)) proposed.Remove(key);
        }
        if (proposed.ContainsKey(choiceKey)) return false;
        var builder = new PlanBuilder(pack, chapter, Policy, proposed);
        var proposedItems = builder.Build();
        var reopened = builder.ChoicePoints.Where(p => p.ChoiceKey == choiceKey).ToArray();
        if (reopened.Length != 1 || reopened[0].IsResolved || reopened[0].SectionId != point.SectionId
            || reopened[0].Item.NodeId != point.Item.NodeId) return false;
        int target = reopened[0].Position;
        if (target < 0 || target >= proposedItems.Count || proposedItems[target].Kind != ListeningItemKind.Choice
            || proposedItems[target].ChoiceKey != choiceKey) return false;
        choices = proposed; items = proposedItems; menuPositions = builder.MenuPositions;
        choicePoints = builder.ChoicePoints; index = target; ResumePositionMs = 0;
        return true;
    }

    IEnumerable<string> ChoiceKeySections(string key)
    {
        foreach (var node in pack.Nodes.Where(n => !n.Archived && chapter.Sections.Any(s => s.Id == n.SectionId)))
        {
            string? prefix = node.Kind == "choice" ? node.Id + ":"
                : node.Kind == "gap" && node.ResumeMenuIds.Count > 1 ? "resume:" + node.Id + ":" : null;
            if (prefix == null || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string suffix = key[prefix.Length..];
            if (int.TryParse(suffix, out int visit) && visit >= 0
                && suffix == visit.ToString(System.Globalization.CultureInfo.InvariantCulture)) yield return node.SectionId;
        }
    }

    public bool SeekNode(string nodeId)
    {
        int target = items.FindIndex(i => i.NodeId == nodeId && i.Kind == ListeningItemKind.Line);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; ReleaseEndingChoices(); return true;
    }

    /// <summary>
    /// 0.4.7 结局分歧：定位回到已选结局菜单之前（同一小节）时，放开该处及本节其后的选择，
    /// 继续收听会重新出示结局选项，可选另一个结局。默认分支不保存选择，按设定序号重新选择。
    /// </summary>
    void ReleaseEndingChoices()
    {
        var current = Current;
        if (current == null || choices.Count == 0) return;
        var released = choicePoints.Where(p => p.SectionId == current.SectionId && p.Position >= index && p.IsResolved &&
            choices.ContainsKey(p.ChoiceKey) && p.Item.Node is { Kind: "choice" } menu && MenuShape.Of(pack, menu).Ending).ToList();
        if (released.Count == 0) return;
        int order = released.Min(p => p.Order);
        foreach (var point in choicePoints.Where(p => p.SectionId == current.SectionId && p.Order >= order)) choices.Remove(point.ChoiceKey);
        int keep = index; Rebuild(); index = Math.Min(keep, items.Count);
    }

    /// <summary>目录定位只接受当前计划中的台词或待选项；不展开或确认任何分支。</summary>
    public bool SeekItem(string itemId)
    {
        // 0.4.7：未核出口 Notice 也可定位（听书「下一句」/ 测试 / 目录跳到停下处）。
        int target = items.FindIndex(i => i.Id == itemId && i.Kind is ListeningItemKind.Line or ListeningItemKind.Choice or ListeningItemKind.Notice);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; ReleaseEndingChoices(); return true;
    }

    public bool SeekSection(string sectionId)
    {
        int target = items.FindIndex(i => i.SectionId == sectionId);
        if (target < 0) return false;
        index = target; ResumePositionMs = 0; ReleaseEndingChoices(); return true;
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
        bool sameContentLegacy = snapshot.Fingerprint == legacyFingerprint;
        bool legacyCompleted = sameContentLegacy && snapshot.Completed && restoredItems.All(i => !i.IsBlocking);
        int target = snapshot.Completed && (same || legacyCompleted) ? restoredItems.Count : restoredItems.FindIndex(i => i.Id == snapshot.ItemId);
        int menuPosition = -1;
        bool resumedMenu = target < 0 && TryMenuPosition(restoredItems, builder.MenuPositions, snapshot.ItemId, snapshot.SectionId, out menuPosition);
        if (resumedMenu) target = menuPosition;
        if (target < 0 && snapshot.NodeId != null)
        {
            string nodeId = snapshot.NodeId;
            var seen = new HashSet<string>();
            while (pack.Migrations.TryGetValue(nodeId, out var migrated) && seen.Add(nodeId)) nodeId = migrated;
            target = restoredItems.FindIndex(i => i.NodeId == nodeId && i.Kind == ListeningItemKind.Line);
            // 旧提示文案可变，稳定边界节点仍保留为等待位置；不从提示跳到它后的正文。
            if (target < 0) target = restoredItems.FindIndex(i => i.NodeId == nodeId && i.IsBlocking);
            // 旧版停在“等待续接”处；新版沿包内登记的返回连接续播时，同一节点变为普通提示，从提示处接着听。
            if (target < 0) target = restoredItems.FindIndex(i => i.NodeId == nodeId && i.Kind == ListeningItemKind.Notice);
        }
        bool resumeAtBoundary = false;
        if (!same && snapshot.Completed)
        {
            // v3 会把未知出口当普通提示消费并越节。旧“已完结”不能越过新等待边界。
            int savedSection = SectionRank(snapshot.SectionId);
            int boundary = restoredItems.FindIndex(i => i.IsBlocking
                && (savedSection < 0 || SectionRank(i.SectionId) <= savedSection));
            if (boundary >= 0) { target = boundary; resumeAtBoundary = true; }
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
        choices = restoredChoices; items = restoredItems; menuPositions = builder.MenuPositions;
        choicePoints = builder.ChoicePoints; Policy = snapshot.Policy; index = target;
        ResumePositionMs = (same || sameContentLegacy) && !snapshot.Completed && Current?.Kind == ListeningItemKind.Line
            ? Math.Clamp(snapshot.PositionMs, 0, 24 * 60 * 60 * 1000L) : 0;
        if (resumeAtBoundary) reason = "原听书记录曾越过未核实的连接，现已停在等待续接处；可查看本节台词目录。";
        else if (resumedMenu) reason = "已按收听策略接回原互动位置，点击播放继续。";
        else if (sameContentLegacy && Current?.Kind == ListeningItemKind.Line) reason = "听书等待提示已更新，已保留原台词和句内位置。";
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
        internal List<ListeningChoicePoint> ChoicePoints { get; } = new();
        bool waiting;
        // 现稿续接段：从越过未核出口 / 未核正文的那一处起，到下一个选择点或小节结束。
        bool draftRun;
        // 0.4.7 结局分歧：本次计划已选入一个结局（选项含「开启结局」）。该小节读完即结束，不串接其他结局。
        bool endingTaken;
        int lastBlockIndex = -1;
        /// <summary>0.4.7（I）按顺序续接的选择键前缀：值为续接台词编号，随听书选择一起保存。</summary>
        internal const string OrderPrefix = "order:";
        internal string? EndingOptionId { get; private set; }
        string? deadEnd;
        int operations;
        const int MaxOperations = 200000;
        const int MaxDepth = 96;
        bool Defaulting => policy is ListeningBranchPolicy.First or ListeningBranchPolicy.Second;
        // 顺序收听（默认分支/手动选择）在已核正文结束后，沿包内为该选项登记的返回连接继续；全部听取保持原补充片段方式。
        bool FollowsDeclaredExits => Defaulting || policy == ListeningBranchPolicy.Manual;
        /// <summary>默认分支序号：设为第二个时只在确有第二项时生效，否则按第一个。</summary>
        int DefaultIndex(int count) => policy == ListeningBranchPolicy.Second && count >= 2 ? 1 : 0;

        internal List<ListeningItem> Build()
        {
            foreach (var section in SectionDisplay.OrderTitledParts(chapter.Sections))
            {
                // 手动选择只截断当前小节；后面小节仍列入目录，可由用户明确跳转。
                waiting = false; draftRun = false; lastBlockIndex = -1;
                int number = 0;
                foreach (var line in pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section.Id)) lineNumbers[line.Id] = ++number;
                Walk(section.StartId, section, new(StringComparer.Ordinal), "", null, 0);
                // （I）用户或倒计时选择了“按顺序继续”：等待提示改为普通提示，从本节下一句（配音包内顺序）接着规划。
                while (waiting && policy != ListeningBranchPolicy.All && lastBlockIndex >= 0 && lastBlockIndex == result.Count - 1 &&
                    selections.TryGetValue(OrderPrefix + result[lastBlockIndex].Id, out var orderLine) &&
                    Valid(orderLine, section) && pack.ById[orderLine].Kind == "line" && !emitted.Contains(orderLine))
                {
                    result[lastBlockIndex] = result[lastBlockIndex] with { WaitingKind = ListeningWaitingKind.None, Notice = ListeningText.OrderContinued };
                    waiting = false; lastBlockIndex = -1; draftRun = true;
                    Walk(orderLine, section, new(StringComparer.Ordinal), "", null, 0);
                }
                if (endingTaken && policy != ListeningBranchPolicy.All && !waiting)
                {
                    Notice(section, ListeningText.EndingFinished);
                    break;
                }
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

        void RecordChoice(ListeningItem item, string? selected, bool resolved, bool skipped = false, string? autoKind = null, string? autoLabel = null, string? emptyExit = null)
        {
            draftRun = false;
            ChoicePoints.Add(new(item, result.Count, selected, resolved, ChoicePoints.Count, skipped, autoKind, autoLabel, emptyExit));
        }

        /// <summary>现稿续接（店主决定的策略 B）：出口或正文未逐句核实时，按配音包写好的连接继续，只在真正没有下一句处停。</summary>
        void Draft(Section section, string message, string? nodeId)
        {
            Notice(section, ListeningText.DraftPrefix + message, nodeId);
            draftRun = true;
        }

        void Notice(Section section, string message, string? nodeId = null, ListeningWaitingKind waitingKind = ListeningWaitingKind.None)
        {
            string key = "notice:" + (nodeId ?? section.Id) + ":" + message;
            if (!emitted.Add(key)) return;
            result.Add(new(key, ListeningItemKind.Notice, nodeId != null ? pack.ById.GetValueOrDefault(nodeId) : null,
                section.Id, section.Title, 0, "", message, WaitingKind: waitingKind));
        }

        void Block(Section section, ListeningWaitingKind kind, string message, string? nodeId = null)
        {
            // 全听是资料浏览，继续保留补充片段；顺序收听必须等用户明确定位，不自动越节。
            if (policy == ListeningBranchPolicy.All)
            {
                string explanation = kind switch
                {
                    ListeningWaitingKind.ScopeBoundary => "本路线正文范围在此截断，后文尚未核实接入顺序。",
                    ListeningWaitingKind.InvalidConnection => "此处存在失效、跨小节或重复连接。",
                    ListeningWaitingKind.UnsupportedNode => "此处存在尚不支持的剧情连接。",
                    ListeningWaitingKind.TraversalLimit => "此处连接过于复杂，无法安全展开。",
                    ListeningWaitingKind.UnverifiedBody => "此分支正文入口及范围尚未核实。",
                    _ => "此处后续连接尚未核实。"
                };
                message = explanation + "全部听取会继续收录资料；不代表已核实的连续游戏路线。";
            }
            int before = result.Count;
            Notice(section, message, nodeId, policy == ListeningBranchPolicy.All ? ListeningWaitingKind.None : kind);
            if (policy != ListeningBranchPolicy.All) { waiting = true; lastBlockIndex = result.Count > before ? result.Count - 1 : -1; }
        }

        string? Walk(string? id, Section section, HashSet<string> stops, string branch, HashSet<string>? scope, int depth,
            bool confirmEntry = false, string? scopeReturnPath = null)
        {
            if (waiting) return null;
            if (depth > MaxDepth) { Block(section, ListeningWaitingKind.TraversalLimit, "分支嵌套过深，已暂停等待确认后续连接。", id); return null; }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? lastId = null;
            while (id != null && !waiting)
            {
                lastId = id;
                if (++operations > MaxOperations) { Block(section, ListeningWaitingKind.TraversalLimit, "章节连接过于复杂，已暂停等待确认后续连接。", id); return null; }
                if (stops.Contains(id)) return id;
                if (!Valid(id, section))
                { Block(section, ListeningWaitingKind.InvalidConnection, "后续连接不在本小节有效范围内，已暂停；请查看台词目录或明确选择小节。", id); return id; }
                var node = pack.ById[id];
                if (scope != null && !scope.Contains(id))
                {
                    // 已核子分支返回父路径由调用者处理；父路径自己的范围缺口不能冒作完成。
                    if (scopeReturnPath != null && node.PathId == scopeReturnPath) return id;
                    if (FollowsDeclaredExits)
                    {
                        // 已核段落之后包内仍写有下一句：按现稿连接继续，不再受已核范围限制（仍受停止点、小节与重复连接检查）。
                        Draft(section, ListeningText.DraftVerifiedPartEnds, id);
                        scope = null;
                    }
                    else
                    {
                        Block(section, ListeningWaitingKind.ScopeBoundary, "本路线已收录部分播放到这里，后续台词尚未核实接入顺序；已暂停，请查看本节台词目录。", id);
                        return id;
                    }
                }
                if (node.Kind != "choice" && !visited.Add(id))
                { Block(section, ListeningWaitingKind.InvalidConnection, "检测到重复连接，已暂停等待确认后续连接。", id); return null; }
                switch (node.Kind)
                {
                    case "line":
                        if (emitted.Add(node.Id)) result.Add(new("line:" + node.Id, ListeningItemKind.Line, node,
                            section.Id, section.Title, lineNumbers.GetValueOrDefault(node.Id), branch, "", DraftContinuation: draftRun));
                        id = node.NextId;
                        // 选项正文“并联”（各句直接接出口、后句无入边）：顺序收听按 lineIds 补读孤立正文。全部听取由补充片段收录，保持不变。
                        if (FollowsDeclaredExits && pack.SchemaVersion >= 3 && OptionBodyOrder.NextOrphan(pack, node, emitted.Contains) is { } orphan)
                        {
                            if (scope != null) scope.Add(orphan.Id);
                            id = orphan.Id;
                        }
                        break;
                    case "end": return null;
                    case "gap":
                    {
                        // 只接受包内明确登记的同小节续接菜单。不能把所有 gap.NextId 当成可靠出口。
                        var menus = node.ResumeMenuIds.Distinct(StringComparer.Ordinal)
                            .Where(m => Valid(m, section) && pack.ById[m].Kind == "choice").ToList();
                        if (menus.Count > 0)
                        {
                            string next = menus[Defaulting ? DefaultIndex(menus.Count) : 0];
                            if (menus.Count > 1)
                            {
                                int visit = menuVisits.GetValueOrDefault("resume:" + node.Id);
                                menuVisits["resume:" + node.Id] = visit + 1;
                                string key = "resume:" + node.Id + ":" + visit;
                                MenuPositions["choice:" + key] = result.Count;
                                var candidates = menus.Select(m => new ChoiceOption { Id = m, TargetId = m,
                                    Label = string.IsNullOrWhiteSpace(pack.ById[m].Text)
                                        ? string.Join(" / ", pack.ById[m].Options.Select(o => o.Label)) : pack.ById[m].Text }).ToList();
                                var choiceItem = new ListeningItem("choice:" + key, ListeningItemKind.Choice, node, section.Id, section.Title,
                                    0, branch, "等待互动：请选择要进入的互动菜单，确认后显示该菜单的选项。", key, candidates);
                                if (policy == ListeningBranchPolicy.Manual)
                                {
                                    if (!selections.TryGetValue(key, out var selected) || selected != Skip && !menus.Contains(selected))
                                    {
                                        RecordChoice(choiceItem, null, false);
                                        result.Add(choiceItem);
                                        waiting = true; return null;
                                    }
                                    RecordChoice(choiceItem, selected == Skip ? null : selected, true, selected == Skip);
                                    if (selected == Skip) return null;
                                    next = selected;
                                }
                                else
                                {
                                    RecordChoice(choiceItem, Defaulting ? next : null, true);
                                    if (policy == ListeningBranchPolicy.All)
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
                            }
                            // 到已登记的菜单重新建立段落范围，仍保留父菜单/共同线的停止目标。
                            return Walk(next, section, stops, branch, null, depth + 1, confirmEntry: true);
                        }
                        // 现稿续接：缺口标记之后包内仍写有同小节的下一处时照现稿继续；真正没有下一处才停。
                        if (FollowsDeclaredExits && node.NextId != null && Valid(node.NextId, section))
                        {
                            Draft(section, ListeningText.DraftExitUnverifiedHere, node.Id);
                            id = node.NextId; break;
                        }
                        Block(section, ListeningWaitingKind.UnverifiedExit, "此处游戏出口尚未核实。听书仅采用菜单已登记的后续连接；没有连接的内容不自动续播。", node.Id);
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
                        // 0.4.7 菜单形态：单选项自动继续（C）；探索菜单各回环选项读一次再走出口（D）。全部听取不变。
                        var shape = MenuShape.Of(pack, node);
                        // 同回应隐藏：回到菜单时，与已读选项回应相同的选项（如 19-14 两个错误答案）不再列出、不再读（全部听取不变）。
                        var options = allOptions.Where(o => !heard.Contains(o.Id) &&
                            (policy == ListeningBranchPolicy.All || !shape.ResponseHeard(o, heard))).ToList();
                        string? join = FindJoin(node, allOptions, section, stops);
                        List<ChoiceOption>? automatic = null;
                        bool explorationRun = false;
                        if (policy != ListeningBranchPolicy.All && options.Count > 0)
                        {
                            if (shape.Exploration)
                            {
                                // 回应已读过的回环不再读；同一轮里回应相同的回环只读第一个。
                                var loops = shape.Loops.Where(o => !shape.ResponseHeard(o, heard)).ToList();
                                loops = loops.Where((o, i) => !loops.Take(i).Any(p => shape.SameResponse(p, o))).ToList();
                                var exits = shape.Exits.Where(o => !heard.Contains(o.Id)).ToList();
                                bool quizFresh = shape.Quiz && !allOptions.Any(o => heard.Contains(o.Id) && !shape.EmptyLoops.Contains(o));
                                if (quizFresh)
                                {
                                    // 问答菜单：默认第一 / 第二在全部选项中选；答错只读一遍回应，回到菜单后同回应选项已隐藏，取出口（正确答案）。
                                    // 手动：照常停下由用户选择；答错回到菜单后只剩出口，自动取出口。
                                    if (Defaulting)
                                    {
                                        var pick = options[DefaultIndex(options.Count)];
                                        automatic = new() { pick };
                                        if (shape.Loops.Any(o => o.Id == pick.Id) && exits.Count > 0) automatic.Add(exits[DefaultIndex(exits.Count)]);
                                        explorationRun = true;
                                    }
                                }
                                else if (Defaulting || exits.Count <= 1)
                                {
                                    automatic = loops;
                                    if (exits.Count > 0) automatic.Add(exits[DefaultIndex(exits.Count)]);
                                    explorationRun = automatic.Count > 0;
                                    if (!explorationRun) automatic = null;
                                }
                                else if (loops.Count > 0) { automatic = loops; explorationRun = true; } // 手动：先读回环，回到菜单后在出口中选择
                            }
                            else if (shape.Single && options.Count == 1 && policy == ListeningBranchPolicy.Manual) automatic = options;
                        }
                        if (options.Count == 0) {
                            if (join == null || join == node.Id) Block(section, ListeningWaitingKind.UnverifiedExit, "本处已收录分支结束，后续连接尚未核实；已暂停，请查看本节台词目录。", node.Id);
                            id = join; if (id == node.Id) id = null; break;
                        }
                        var chosen = options;
                        int visit = menuVisits.GetValueOrDefault(node.Id);
                        menuVisits[node.Id] = visit + 1;
                        string key = node.Id + ":" + visit;
                        MenuPositions["choice:" + key] = result.Count;
                        var choiceItem = new ListeningItem("choice:" + key, ListeningItemKind.Choice, node, section.Id, section.Title,
                            0, branch, interaction ? "等待互动：请选择下方选项，确认后继续本节。" : "请选择要收听的路线", key, options);
                        if (automatic != null)
                        {
                            // 0.4.7 空出口：探索菜单最后取的出口没有台词（只经 return / merge 到汇合后共同线），记为已读、从汇合后第一句继续。
                            var emptyExit = explorationRun && shape.IsEmptyExit(automatic[^1]) ? automatic[^1].Label : null;
                            RecordChoice(choiceItem, automatic[^1].Id, true, autoKind: explorationRun ? PlaybackLog.Types.Exploration : PlaybackLog.Types.SingleOption,
                                autoLabel: string.Join(" → ", automatic.Select(o => o.Label)), emptyExit: emptyExit);
                            chosen = automatic;
                        }
                        else if (policy == ListeningBranchPolicy.Manual)
                        {
                            if (!selections.TryGetValue(key, out var selection) || selection != Skip && options.All(o => o.Id != selection))
                            {
                                RecordChoice(choiceItem, null, false);
                                result.Add(choiceItem);
                                waiting = true; return null;
                            }
                            RecordChoice(choiceItem, selection == Skip ? null : selection, true, selection == Skip);
                            if (selection == Skip) {
                                foreach (var option in options) heard.Add(option.Id);
                                if (join == null) Block(section, ListeningWaitingKind.UnverifiedExit, "本处没有已核实的共同后文，已暂停等待续接。", node.Id);
                                id = join; break;
                            }
                            chosen = options.Where(o => o.Id == selection).ToList();
                        }
                        else if (Defaulting)
                        {
                            var preferred = options[DefaultIndex(options.Count)];
                            RecordChoice(choiceItem, visit == 0 ? preferred.Id : null, true,
                                autoKind: visit == 0 ? (options.Count == 1 ? PlaybackLog.Types.SingleOption : PlaybackLog.Types.AutoSelect) : null, autoLabel: visit == 0 ? preferred.Label : null);
                            // 一处菜单最多默认选一次；返回菜单后不会重复第一条造成死循环。
                            if (visit > 0) {
                                if (join == null) Block(section, ListeningWaitingKind.UnverifiedExit, "本处默认分支已播放，后续连接尚未核实；已暂停等待续接。", node.Id);
                                id = join; break;
                            }
                            chosen = new() { preferred };
                        }
                        else RecordChoice(choiceItem, null, true);
                        string? returned = null;
                        var continuations = new List<string>();
                        foreach (var option in chosen)
                        {
                            heard.Add(option.Id);
                            if (MenuShape.IsEndingOption(option) && policy != ListeningBranchPolicy.All) { endingTaken = true; EndingOptionId = option.Id; }
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
                            var tail = DeclaredTail(node, option, section);
                            // 包内只有互动标题、没有任何台词的选项：无可播放内容，按登记的返回连接继续，不播放任何未核正文。
                            bool emptyInteraction = pack.SchemaVersion >= 3 && !verified && tail != null && option.LineIds.Count == 0 &&
                                option.SegmentIds.Count == 0 && tail.Contains(option.TargetId);
                            if (emptyInteraction) branchScope = new(tail!, StringComparer.Ordinal);
                            else if (tail != null && branchScope != null) branchScope.UnionWith(tail);
                            if (pack.SchemaVersion >= 3 && !verified && !emptyInteraction && FollowsDeclaredExits)
                            {
                                // 未核正文：按包内现有连接收听（无已核范围），全程标为现稿续接。
                                Draft(section, ListeningText.DraftOptionBodyUnverified(option.Label), option.TargetId);
                                branchScope = null;
                            }
                            else if (pack.SchemaVersion >= 3 && !verified && !emptyInteraction)
                            {
                                Block(section, ListeningWaitingKind.UnverifiedBody, "所选分支的正文入口及范围尚未核实，已暂停；可在本节台词目录查看或单句试听，不自动接入此路线。", option.TargetId);
                                if (waiting) return null;
                            }
                            else if (!verified && !emptyInteraction) Notice(section, "以下分支正文尚未完全核对；仅按包内现有连接收听。", option.TargetId);
                            bool exitVerified = pack.SchemaVersion >= 3 ? option.ExitVerified : option.Verified;
                            deadEnd = null;
                            returned = Walk(option.TargetId, section, branchStops,
                                string.IsNullOrEmpty(branch) ? option.Label : branch + " / " + option.Label, branchScope, depth + 1,
                                scopeReturnPath: exitVerified || tail != null || FollowsDeclaredExits ? node.PathId : null);
                            if (waiting) return null;
                            // 只认可经由本选项登记的 return/merge 回到父路线的那一处出口；其他跳出仍按未核实出口暂停。
                            bool declaredExit = !exitVerified && tail != null && returned != null && DeclaredExits(node, option, tail, section).Contains(returned);
                            // 现稿续接：未核出口经包内写好的连接回到本菜单所在路线（含未登记为 return/merge 的直连）。
                            bool draftExit = !declaredExit && !exitVerified && FollowsDeclaredExits && pack.SchemaVersion >= 3 && returned != null
                                && returned != node.Id && Valid(returned, section) && pack.ById[returned].PathId == node.PathId;
                            if (declaredExit)
                                Draft(section, emptyInteraction
                                    ? "互动“" + option.Label + "”未收录台词，按配音包登记的返回连接继续收听。"
                                    : "此段出口尚未逐句核实，按配音包登记的返回连接继续收听。", ExitNode(option, tail!));
                            else if (draftExit)
                                Draft(section, ListeningText.DraftExitUnverified, returned);
                            else if (FollowsDeclaredExits && pack.SchemaVersion >= 3 && !exitVerified && returned == null && deadEnd != null)
                            {
                                Block(section, ListeningWaitingKind.UnverifiedExit, ListeningText.DeadEnd, deadEnd);
                                if (waiting) return null;
                            }
                            else if (emptyInteraction && !FollowsDeclaredExits)
                            {
                                Block(section, ListeningWaitingKind.UnverifiedBody, "所选分支的正文入口及范围尚未核实，已暂停；可在本节台词目录查看或单句试听，不自动接入此路线。", option.TargetId);
                                if (waiting) return null;
                            }
                            else if (pack.SchemaVersion >= 3 && (option.SegmentIds.Count > 0 || option.BoundaryId != null) && !exitVerified && !FollowsDeclaredExits)
                            {
                                Block(section, ListeningWaitingKind.UnverifiedExit, "所选分支的出口尚未核实，已暂停等待续接；可查看本节台词目录。", option.BoundaryId ?? returned ?? option.TargetId);
                                if (waiting) return null;
                            }
                            if (returned != null && returned != node.Id && !stops.Contains(returned) && Valid(returned, section)
                                && pack.ById[returned].PathId == node.PathId && allOptions.All(o => o.TargetId != returned)
                                && (pack.SchemaVersion < 3 ? option.Verified : option.ExitVerified || declaredExit || draftExit))
                                continuations.Add(returned);
                        }
                        if (explorationRun && returned == node.Id)
                        {
                            // 回环选项都已读：手动在剩余出口中选择；全部是回环的菜单按普通缺口暂停。
                            if (policy == ListeningBranchPolicy.Manual && shape.Exits.Any(o => !heard.Contains(o.Id))) { id = node.Id; confirmEntry = interaction; break; }
                            if (join == null) Block(section, ListeningWaitingKind.UnverifiedExit, "本处已收录分支结束，后续连接尚未核实；已暂停，请查看本节台词目录。", node.Id);
                            id = join; break;
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
                                Walk(continuation, section, stops, branch, scope, depth + 1, scopeReturnPath: scopeReturnPath);
                                if (waiting) return null;
                            }
                            id = unique[^1];
                        }
                        else
                        {
                            // 不同选项可在共同汇合点之前各有一段父路线正文。
                            // 沿实际走到的已核出口补完这段，仍受父范围与未知边界限制。
                            if (join != null)
                            {
                                var continuationStops = new HashSet<string>(stops, StringComparer.Ordinal) { node.Id, join };
                                foreach (var other in allOptions) continuationStops.Add(other.TargetId);
                                foreach (var continuation in continuations.Distinct(StringComparer.Ordinal).Where(c => c != join))
                                {
                                    var reached = Walk(continuation, section, continuationStops, branch, scope, depth + 1, scopeReturnPath: scopeReturnPath);
                                    if (waiting) return null;
                                    if (reached != join) return reached;
                                }
                            }
                            id = join;
                        }
                        break;
                    }
                    default: Block(section, ListeningWaitingKind.UnsupportedNode, "遇到尚不支持的剧情连接，已暂停；请查看本节台词目录。", node.Id); return null;
                }
            }
            // 台词 / 返回 / 汇合之后包内没有写下一处：真实缺口（区别于 end 节点的正常结束）。
            if (!waiting && id == null && lastId != null && pack.ById.TryGetValue(lastId, out var dead) && dead.Kind is "line" or "return" or "merge")
                deadEnd = lastId;
            return id;
        }

        /// <summary>
        /// v3 出口未核实、但包内为该选项登记了返回连接（同路线的 return/merge 节点，其 NextId 即选项 ReturnId/MergeId 且属于父路线）。
        /// 返回这些结构节点；它们不含台词，经过它们不会播放任何未核实正文。全部听取不使用。
        /// </summary>
        HashSet<string>? DeclaredTail(Node menu, ChoiceOption option, Section section)
        {
            if (!FollowsDeclaredExits || pack.SchemaVersion < 3 || option.ExitVerified || string.IsNullOrEmpty(option.PathId) || option.PathId == menu.PathId) return null;
            var tail = pack.Nodes.Where(n => !n.Archived && n.SectionId == section.Id && n.PathId == option.PathId && n.Kind is "return" or "merge")
                .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            return tail.Count > 0 && DeclaredExits(menu, option, tail, section).Count > 0 ? tail : null;
        }

        HashSet<string> DeclaredExits(Node menu, ChoiceOption option, HashSet<string> tail, Section section) =>
            tail.Select(id => pack.ById[id].NextId)
                .Where(next => next != null && Valid(next, section) && pack.ById[next].PathId == menu.PathId && (next == option.ReturnId || next == option.MergeId))
                .Select(next => next!).ToHashSet(StringComparer.Ordinal);

        // 与旧版暂停处使用同一节点，旧存档可从这条提示接着听。
        string ExitNode(ChoiceOption option, HashSet<string> tail) =>
            option.SegmentIds.Select(id => pack.ById.GetValueOrDefault(id)?.NextId).FirstOrDefault(next => next != null && tail.Contains(next))
            ?? (tail.Contains(option.TargetId) ? option.TargetId : tail.First());

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
