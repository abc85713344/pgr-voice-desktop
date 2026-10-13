using System.Runtime.CompilerServices;

namespace PgrVoice.Listening;

/// <summary>
/// 0.4.7 菜单形态（只读结构分析，听书与游戏伴音共用）：
/// · 单选项菜单：只有一个有效选项（相当于“继续”），自动进入，不停下等待选择；
/// · 探索菜单：部分选项读完后经 return / merge / 台词回到同一菜单（loop-back），其余为出口；
///   每个回环选项按列出顺序各读一次（本次收听记住已读），再按默认第一/第二在出口中选择；
/// · 同回应隐藏（问答菜单，如 19-14）：回到菜单时，与刚选项回应相同的选项被隐藏（游戏按跳转目标比较）；同一段回应只读一次。
///   只对 Movie 选择菜单生效（选项记有原表 Action，或在 19-14 白名单里）；关卡行为树按钮菜单（如 29-1 menu-020）不隐藏。
/// · 结局分歧菜单：选项文字含「开启结局」。选择方式与普通分支相同；选入结局后读到该结局结束，不再串接其他结局。
/// 有效选项与听书规划一致：目标存在、未归档、属于同一小节。
/// </summary>
public sealed record MenuShapeInfo(IReadOnlyList<ChoiceOption> Options, IReadOnlyList<ChoiceOption> Loops, IReadOnlyList<ChoiceOption> Exits, bool Ending)
{
    public bool Single => Options.Count == 1;
    public bool Exploration => Options.Count >= 2 && Loops.Count > 0;
    public bool AllLoop => Exploration && Exits.Count == 0;
    /// <summary>空回环选项：选项链不经任何台词或嵌套菜单、直接（经 return / merge）回到本菜单，如 29-1 menu-026「再等等……」。
    /// 没有可读内容，读一遍即视为已读；游戏伴音的探索顺序直接跳过它，不停下、不循环。</summary>
    public IReadOnlyList<ChoiceOption> EmptyLoops { get; init; } = Array.Empty<ChoiceOption>();
    /// <summary>空出口选项：探索菜单的出口本身不含台词（只经 return / merge 到汇合后的共同线），如 29-1 menu-026「离开营地」。
    /// 选入即视为已读，听书与游戏伴音都不交给手动，直接从汇合后第一句继续（见 <see cref="MenuShape.EmptyExitLanding"/>）。</summary>
    public IReadOnlyList<ChoiceOption> EmptyExits { get; init; } = Array.Empty<ChoiceOption>();
    public bool IsEmptyExit(ChoiceOption option) => EmptyExits.Any(o => o.Id == option.Id);

    /// <summary>同回应分组（选项编号 → 组号）：回应相同的选项同组。见 <see cref="MenuShape.SameResponse"/>。</summary>
    public IReadOnlyDictionary<string, int> ResponseGroups { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    /// <summary>每组的判定方式：「原表 Action」「白名单（原表 Action）」或「整段回应台词相同」（仅 Movie 选择菜单内）；单独一组的选项不列出。非 Movie 选择菜单为空。</summary>
    public IReadOnlyDictionary<int, string> ResponseMatch { get; init; } = new Dictionary<int, string>();
    /// <summary>
    /// 问答菜单（如 19-14 何为希望）：所有有台词的回环选项回应相同（≥2 个），另有出口。
    /// 游戏里答错后回到菜单时，与刚选项回应相同的选项被隐藏，只剩正确答案；因此听书 / 游戏伴音按默认序号在全部选项中选一次，
    /// 答错则只读一遍回应，回到菜单后取正确答案；默认就是正确答案则直接通过。
    /// </summary>
    public bool Quiz { get; init; }
    public bool SameResponse(ChoiceOption a, ChoiceOption b) => a.Id == b.Id ||
        ResponseGroups.TryGetValue(a.Id, out int ga) && ResponseGroups.TryGetValue(b.Id, out int gb) && ga == gb;
    /// <summary>与给定选项回应相同的其他选项（不含自身）。</summary>
    public IReadOnlyList<ChoiceOption> SameResponseOthers(ChoiceOption chosen) => Options.Where(o => o.Id != chosen.Id && SameResponse(o, chosen)).ToList();
    /// <summary>
    /// 0.4.7 同回应隐藏：选过 lastChosenId 后回到本菜单时游戏显示的选项。与刚选项回应相同的选项（含刚选项本身）被隐藏；
    /// 刚选项没有同回应的其他选项时不隐藏任何项（普通探索菜单行为不变，已读记录另由探索记忆负责）。
    /// </summary>
    public IReadOnlyList<ChoiceOption> VisibleAfter(string? lastChosenId)
    {
        var chosen = lastChosenId == null ? null : Options.FirstOrDefault(o => o.Id == lastChosenId);
        if (chosen == null || SameResponseOthers(chosen).Count == 0) return Options;
        return Options.Where(o => !SameResponse(o, chosen)).ToList();
    }
    /// <summary>某选项的回应是否已读过（自身或同回应的其他选项已读）。</summary>
    public bool ResponseHeard(ChoiceOption option, IReadOnlySet<string> heard) => heard.Contains(option.Id) || Options.Any(o => heard.Contains(o.Id) && SameResponse(o, option));
}

public static class MenuShape
{
    public const string EndingMarker = "开启结局";
    const int MaxNestedDepth = 4;
    static readonly ConditionalWeakTable<Pack, Dictionary<string, MenuShapeInfo>> cache = new();

    public static bool IsEndingOption(ChoiceOption option) => option.Label?.Contains(EndingMarker, StringComparison.Ordinal) == true;

    public static MenuShapeInfo Of(Pack pack, Node menu)
    {
        var map = cache.GetValue(pack, _ => new(StringComparer.Ordinal));
        lock (map)
        {
            if (map.TryGetValue(menu.Id, out var known)) return known;
            var options = ValidOptions(pack, menu);
            var loops = new List<ChoiceOption>(); var exits = new List<ChoiceOption>();
            foreach (var option in options)
                (ReturnsTo(pack, option.TargetId, new HashSet<string>(StringComparer.Ordinal) { menu.Id }, menu.SectionId, 0, new(StringComparer.Ordinal)) != null ? loops : exits).Add(option);
            var emptyLoops = loops.Where(o => IsEmptyLoop(pack, menu, o)).ToList();
            var (groups, match) = ResponseGrouping(pack, menu, options);
            var spoken = loops.Where(o => !emptyLoops.Contains(o)).ToList();
            bool quiz = options.Count >= 2 && spoken.Count >= 2 && spoken.Count == loops.Count && exits.Count > 0 &&
                spoken.All(o => groups.TryGetValue(o.Id, out int g) && g == groups[spoken[0].Id]) && match.ContainsKey(groups[spoken[0].Id]);
            var info = new MenuShapeInfo(options, loops, exits, options.Any(IsEndingOption))
            {
                EmptyLoops = emptyLoops,
                EmptyExits = loops.Count > 0 ? exits.Where(o => EmptyExitLanding(pack, menu, o) != null).ToList() : Array.Empty<ChoiceOption>(),
                ResponseGroups = groups,
                ResponseMatch = match,
                Quiz = quiz
            };
            map[menu.Id] = info;
            return info;
        }
    }

    public const string MatchAction = "原表 Action";
    public const string MatchText = "整段回应台词相同";
    public const string MatchAllowlist = "白名单（原表 Action）";
    const string DirectTargetMarker = "直接目标：";
    const int MinResponseLetters = 4;

    /// <summary>
    /// 没有记录原表 Action 的 Movie 选择菜单白名单：（配音包编号, 小节编号, 菜单编号）→ 错误答案回应在原表里的 Action。
    /// 19-14 何为希望（moviezx02814ba.csv）：Q1 A175 两个错误答案都跳 A176；Q2 A194 两个错误答案都跳 A195。
    /// 按编号匹配、按原表 Action 分组，不看台词文字：19-14 错字修订（只改两个错误选项节点的文字 / 音频）不影响。
    /// 白名单菜单里回到本菜单且有台词的选项（错误答案）取这里的 Action；出口（正确答案）不取。
    /// </summary>
    static readonly Dictionary<(string Pack, string Section, string Menu), string> MovieAllowlist = new()
    {
        [("pgr-ch19", "ch19-185fecd83dca9abdd3cc", "ch19-185fecd83dca9abdd3cc-menu-000")] = "moviezx02814ba-A176",
        [("pgr-ch19", "ch19-185fecd83dca9abdd3cc", "ch19-185fecd83dca9abdd3cc-menu-001")] = "moviezx02814ba-A195",
    };

    /// <summary>
    /// 同回应隐藏只对 Movie 选择菜单生效（游戏 xmovieactionselection OnEnter 只管 Movie 选择）。
    /// Movie 选择 = 至少一个选项记有原表 Action（exitEvidence「冻结来源的此选项直接目标：MOVIE-action-N」），或在白名单里。
    /// 关卡行为树按钮菜单（如 29-1 menu-020，各按钮有自己的已选标记）没有 Action 记录，不在白名单 → 不分组、不隐藏。
    /// 返回判定依据（<see cref="MatchAction"/> / <see cref="MatchAllowlist"/>）；不是 Movie 选择返回 null。
    /// </summary>
    public static string? MovieSource(Pack pack, Node menu) =>
        menu.Options.Any(o => ResponseAction(o) != null) ? MatchAction :
        MovieAllowlist.ContainsKey((pack.Id, menu.SectionId, menu.Id)) ? MatchAllowlist : null;

    /// <summary>
    /// 选项回应的原表 Action：exitEvidence 中「冻结来源的此选项直接目标：MOVIE-action-N」（游戏按跳转目标比较）。
    /// 只有一个不同的目标时采用；没有记录（如 19-14）返回 null。
    /// </summary>
    public static string? ResponseAction(ChoiceOption option)
    {
        string? found = null;
        foreach (var e in option.ExitEvidence)
        {
            int at = e?.IndexOf(DirectTargetMarker, StringComparison.Ordinal) ?? -1;
            if (at < 0) continue;
            var token = e![(at + DirectTargetMarker.Length)..].Trim().Split(' ', '\t', '；', ';', '，', ',')[0];
            if (token.Length == 0) continue;
            if (found != null && found != token) return null;
            found = token;
        }
        return found;
    }

    /// <summary>白名单菜单里错误答案（回到本菜单、有台词）的原表 Action；其他情况 null。</summary>
    static string? AllowlistAction(Pack pack, Node menu, ChoiceOption option) =>
        MovieAllowlist.TryGetValue((pack.Id, menu.SectionId, menu.Id), out var action) && ResponseChain(pack, menu, option) is { Count: > 0 } ? action : null;

    /// <summary>
    /// 回应台词链：从选项入口起沿 nextId 收集台词（说话人 + 文字，去空白），越过 return / merge，直到回到本菜单为止。
    /// 不回到本菜单（出口、嵌套菜单、缺口、断链）返回 null。
    /// </summary>
    public static IReadOnlyList<(string Speaker, string Text)>? ResponseChain(Pack pack, Node menu, ChoiceOption option)
    {
        var lines = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (string? id = option.TargetId; id != null && seen.Add(id) && seen.Count <= 64;)
        {
            if (id == menu.Id) return lines;
            if (!pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != menu.SectionId) return null;
            if (node.Kind == "line") lines.Add((Squash(node.Speaker), Squash(node.Text)));
            else if (node.Kind is not ("return" or "merge")) return null;
            id = node.NextId;
        }
        return null;
    }
    static string Squash(string? s) => string.Concat((s ?? "").Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// 两个选项的回应是否相同（游戏 xmovieactionselection OnEnter：跳转目标与上次所选相同的选项隐藏）。只用于 Movie 选择菜单（<see cref="MovieSource"/>）：
    /// 不是 Movie 选择一律不同。两者都有原表 Action（记录的或白名单的）时按 Action 比较；
    /// 否则在 Movie 选择菜单内兜底比较整段回应台词链（逐句说话人 + 文字全等，都回到本菜单，且至少一句有 4 个以上文字 / 数字）。
    /// </summary>
    public static (bool Same, string? How) SameResponse(Pack pack, Node menu, ChoiceOption a, ChoiceOption b)
    {
        if (a.Id == b.Id) return (true, null);
        if (MovieSource(pack, menu) == null) return (false, null);
        string? ra = ResponseAction(a), rb = ResponseAction(b);
        string? aa = ra ?? AllowlistAction(pack, menu, a), ab = rb ?? AllowlistAction(pack, menu, b);
        if (aa != null && ab != null)
            return aa == ab ? (true, ra != null && rb != null ? MatchAction : MatchAllowlist) : (false, null);
        var ca = ResponseChain(pack, menu, a); var cb = ResponseChain(pack, menu, b);
        if (ca == null || cb == null || ca.Count == 0 || ca.Count != cb.Count) return (false, null);
        for (int i = 0; i < ca.Count; i++) if (ca[i] != cb[i]) return (false, null);
        return ca.Any(l => l.Text.Count(char.IsLetterOrDigit) >= MinResponseLetters) ? (true, MatchText) : (false, null);
    }

    static (Dictionary<string, int>, Dictionary<int, string>) ResponseGrouping(Pack pack, Node menu, List<ChoiceOption> options)
    {
        var groups = new Dictionary<string, int>(StringComparer.Ordinal); var match = new Dictionary<int, string>();
        bool movie = MovieSource(pack, menu) != null;
        for (int i = 0; i < options.Count; i++)
        {
            if (groups.ContainsKey(options[i].Id)) continue;
            groups[options[i].Id] = i;
            if (!movie) continue;
            for (int j = i + 1; j < options.Count; j++)
            {
                if (groups.ContainsKey(options[j].Id)) continue;
                var (same, how) = SameResponse(pack, menu, options[i], options[j]);
                if (!same) continue;
                groups[options[j].Id] = i;
                match[i] = match.TryGetValue(i, out var known) && known != how ? known + "+" + how : how!;
            }
        }
        return (groups, match);
    }

    public static List<ChoiceOption> ValidOptions(Pack pack, Node menu) => menu.Options.Where(o =>
        o.TargetId != null && pack.ById.TryGetValue(o.TargetId, out var t) && !t.Archived && t.SectionId == menu.SectionId).ToList();

    /// <summary>选项入口到回到本菜单之间只有 return / merge 节点（没有台词、缺口或嵌套菜单）。</summary>
    static bool IsEmptyLoop(Pack pack, Node menu, ChoiceOption option)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (string? id = option.TargetId; id != null && seen.Add(id);)
        {
            if (id == menu.Id) return true;
            if (!pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != menu.SectionId || node.Kind is not ("return" or "merge")) return false;
            id = node.NextId;
        }
        return false;
    }

    /// <summary>
    /// 空出口的落点：选项入口起只经过 return / merge 节点（至少一个，同小节、未归档），第一次遇到的其他节点若是台词即为落点（汇合后第一句）；
    /// 回到本菜单（空回环）、遇到嵌套菜单 / 缺口 / 结束或断链都返回 null（不是空出口）。
    /// </summary>
    public static Node? EmptyExitLanding(Pack pack, Node menu, ChoiceOption option)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int structural = 0;
        for (string? id = option.TargetId; id != null && seen.Add(id);)
        {
            if (id == menu.Id || !pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != menu.SectionId) return null;
            if (node.Kind is "return" or "merge") { structural++; id = node.NextId; continue; }
            return structural > 0 && node.Kind == "line" ? node : null;
        }
        return null;
    }

    /// <summary>
    /// 选项链（台词 / return / merge / 有下一处的缺口）回到的菜单：返回当前菜单栈（本菜单 + 链上进入的嵌套菜单）中被回到的那个，没有回来则为 null（出口）。
    /// 嵌套菜单（如角色子菜单）：话题选项回到子菜单本身也算回环，不当作出口；只有当子菜单的每个选项都回到栈内、且至少一个回到子菜单之上的菜单时，
    /// 整条链才算回到上层（全部只在子菜单内打转则视为不返回）。任一子选项链永不回到栈内 → 出口。
    /// </summary>
    static string? ReturnsTo(Pack pack, string? id, HashSet<string> stack, string sectionId, int depth, HashSet<string> seen)
    {
        while (id != null)
        {
            if (stack.Contains(id)) return id;
            if (!seen.Add(id)) return null;
            if (!pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != sectionId) return null;
            switch (node.Kind)
            {
                case "end": return null;
                case "choice":
                {
                    if (depth >= MaxNestedDepth) return null;
                    var nested = ValidOptions(pack, node);
                    if (nested.Count == 0) return null;
                    var inner = new HashSet<string>(stack, StringComparer.Ordinal) { node.Id };
                    string? above = null;
                    foreach (var o in nested)
                    {
                        var hit = ReturnsTo(pack, o.TargetId, inner, sectionId, depth + 1, new(seen, StringComparer.Ordinal));
                        if (hit == null) return null;
                        if (hit != node.Id) above ??= hit;
                    }
                    return above;
                }
                default: id = node.NextId; break;
            }
        }
        return null;
    }

    /// <summary>
    /// 探索菜单的下一项：先按列出顺序取未读的回环选项（空回环视为已读）；回环读完后在出口中按默认序号（1/2，出口不足两个时取第一个）选择。
    /// 全部为回环且都已读：返回 null（调用方按普通缺口处理）。
    /// </summary>
    public static ChoiceOption? NextExploration(MenuShapeInfo shape, IReadOnlySet<string> heard, int number)
    {
        // 问答菜单：首次按默认序号在全部选项中选（答错 = 回环，答对 = 出口）；答错读过一次后同回应选项都已隐藏，下面取出口。
        if (shape.Quiz && !shape.Options.Any(o => heard.Contains(o.Id) && !shape.EmptyLoops.Contains(o)))
            return shape.Options[number == 2 && shape.Options.Count >= 2 ? 1 : 0];
        // 空回环（无台词直接回到本菜单）读一遍即已读：不作为下一项，避免停在无法确认首句的选项上或反复回到它。
        // 同回应：回应已读过（同组其他选项已读）的回环视为已读，同一段回应不读第二遍。
        var loop = shape.Loops.FirstOrDefault(o => !shape.ResponseHeard(o, heard) && !shape.EmptyLoops.Contains(o));
        if (loop != null) return loop;
        var exits = shape.Exits.Where(o => !heard.Contains(o.Id)).ToList();
        if (exits.Count == 0) exits = shape.Exits.ToList();
        if (exits.Count == 0) return null;
        return exits[number == 2 && exits.Count >= 2 ? 1 : 0];
    }
}
