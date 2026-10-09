using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice;

/// <summary>玩家按当前游戏正文指认的有限开头候选；Position 从1开始，不能据此自动跳句。</summary>
public sealed record BranchAnchorCard(string Id, string MenuId, string OptionId, string OptionLabel,
    string NodeId, string Speaker, string Text, int Position, bool IsAmbiguous);

/// <summary>一次只读候选快照。调用方仍负责窗口、会话及前台代次；本对象不授予播放权限。</summary>
public sealed class BranchAnchorOffer
{
    public string MenuId { get; }
    public string SectionId { get; }
    public IReadOnlyList<BranchAnchorCard> Cards { get; }
    public string Reason { get; }
    internal PlaybackEngine Engine { get; }
    internal string Navigation { get; }
    internal string Graph { get; }
    internal int MaximumLinesPerOption { get; }

    internal BranchAnchorOffer(PlaybackEngine engine, string menuId, string sectionId, List<BranchAnchorCard> cards,
        string reason, string navigation, string graph, int maximumLinesPerOption)
    {
        Engine = engine; MenuId = menuId; SectionId = sectionId; Cards = cards.AsReadOnly(); Reason = reason;
        Navigation = navigation; Graph = graph; MaximumLinesPerOption = maximumLinesPerOption;
    }
}

/// <summary>
/// 两端共用的“先游戏选择，再点相同正文”候选规则。只读，不发声、不选项、不推算任务状态。
/// 明确的后继正文由玩家展开后指认；不会因为与选项同文而省略第一句。
/// </summary>
public static class BranchAnchorPolicy
{
    public static BranchAnchorOffer Create(PlaybackEngine engine, int maximumLinesPerOption = 3)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (maximumLinesPerOption is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maximumLinesPerOption));
        string navigation = JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
        string graph = PlaybackEngine.NavigationFingerprint(engine.Pack);
        string section = engine.Current?.SectionId ?? "";
        BranchAnchorOffer Empty(string reason, string menuId = "") => new(engine, menuId, section, new(), reason, navigation, graph, maximumLinesPerOption);
        if (engine.Pack.SchemaVersion != 3 || engine.Current == null || engine.ReviewRoute != null
            || engine.Mode is not (RunMode.Choice or RunMode.Gap))
            return Empty("请先到当前分支或明确的待续接菜单，再对照游戏下一句。");
        Node? menu = null;
        if (engine.Current.Kind == "choice" && engine.Mode == RunMode.Choice && engine.Allowed(engine.Current)) menu = engine.Current;
        else if (engine.Current.Kind == "gap" && engine.Mode == RunMode.Gap)
        {
            // 只接受登记的唯一菜单；有多个时应先由玩家明确打开哪个菜单，不能替他选第一项。
            var menus = engine.Current.ResumeMenuIds.Distinct(StringComparer.Ordinal).ToArray();
            if (menus.Length == 1 && engine.Pack.ById.TryGetValue(menus[0], out var target)
                && !target.Archived && target.Kind == "choice" && target.SectionId == section && engine.Allowed(target)) menu = target;
        }
        if (menu == null) return Empty("此处没有唯一已登记的续接菜单，请先按游戏画面选择菜单。");
        if (menu.Archived || menu.MenuType != "exclusive" || menu.Options.Count == 0
            || menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute))
            return Empty("此处是人物互动、条件或复杂菜单，请使用原菜单入口核对。", menu.Id);
        if (menu.Options.Any(o => string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.PathId))
            || menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count
            || menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count)
            return Empty("选项所属路线不能唯一确认，请使用原菜单入口核对。", menu.Id);

        var cards = new List<BranchAnchorCard>(); int unavailable = 0;
        foreach (var option in menu.Options)
        {
            if (!option.BodyVerified || option.BodyEvidence.Count == 0 || option.BodyEvidence.Any(string.IsNullOrWhiteSpace)
                || option.Requires.Count > 0 || option.Excludes.Count > 0 || !string.IsNullOrWhiteSpace(option.ConditionNote))
            { unavailable++; continue; }
            // 路线身份必须唯一属于当前菜单，而不只是碰巧与某条台词PathId相同。
            var owners = engine.Pack.Nodes.Where(n => !n.Archived && n.SectionId == section && n.Kind == "choice")
                .SelectMany(n => n.Options.Select(o => (Menu: n, Option: o))).Where(x => x.Option.PathId == option.PathId).ToArray();
            if (owners.Length != 1 || owners[0].Menu.Id != menu.Id || owners[0].Option.Id != option.Id)
            { unavailable++; continue; }
            int before = cards.Count; string? id = option.TargetId; var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int position = 1; position <= maximumLinesPerOption && id != null; position++)
            {
                if (!seen.Add(id) || !engine.Pack.ById.TryGetValue(id, out var node) || node.Archived
                    || node.Kind != "line" || node.SectionId != section || node.PathId != option.PathId
                    || !option.SegmentIds.Contains(node.Id) || node.Options.Count > 0 || node.SetFacts.Count > 0
                    || !string.IsNullOrEmpty(node.CompleteRoute) || string.IsNullOrWhiteSpace(node.Text)) break;
                // 使用真实确认语义探测，确保不会变成未核单句或依赖推测的父路线。
                var probe = new PlaybackEngine(engine.Pack);
                if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.OpenGameMenu(menu.Id, section)
                    || !probe.ConfirmGameLine(node.Id) || probe.CurrentId != node.Id || probe.Mode != RunMode.Following
                    || probe.ReviewRoute != null || probe.ExportNavigation().Current.SingleLine || !probe.Allowed(node)) break;
                string text = Matcher.Normalize(node.Text), speaker = Matcher.Normalize(node.Speaker);
                bool ambiguous = engine.Pack.Nodes.Any(other => !other.Archived && other.Kind == "line" && other.SectionId == section
                    && other.Id != node.Id && other.PathId != node.PathId && Matcher.Normalize(other.Text) == text
                    && Matcher.Normalize(other.Speaker) == speaker);
                cards.Add(new(CardId(menu.Id, option.Id, node.Id), menu.Id, option.Id, option.Label, node.Id,
                    node.Speaker, node.Text, position, ambiguous));
                // 不穿过菜单、gap、return/merge/end，也不从容器顺序取后文。
                id = node.NextId;
            }
            if (cards.Count == before) unavailable++;
        }
        string reason = cards.Count == 0 ? "没有可可靠指认的已核开头正文，请使用原菜单或台词目录核对。"
            : unavailable > 0 ? $"另有 {unavailable} 个选项未能提供已核正文候选；可使用原菜单或台词目录核对。" : "";
        if (cards.Any(c => c.IsAmbiguous)) reason += (reason.Length > 0 ? " " : "") + "同角色同文出现在其他路线，请展开并核对下一句或使用原选项。";
        return new(engine, menu.Id, section, cards, reason, navigation, graph, maximumLinesPerOption);
    }

    /// <summary>只在原快照仍有效且卡片唯一时解析。成功后由用户操作入口调用既有ConfirmGameLine一次。</summary>
    public static bool TryResolve(PlaybackEngine engine, BranchAnchorOffer offer, string cardId, out Node? node, out string reason)
    {
        node = null; reason = "候选已经变化，请重新对照游戏下一句。";
        if (engine == null || offer == null || !ReferenceEquals(engine, offer.Engine) || string.IsNullOrEmpty(cardId)
            || JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) != offer.Navigation
            || PlaybackEngine.NavigationFingerprint(engine.Pack) != offer.Graph) return false;
        var old = offer.Cards.Where(c => c.Id == cardId).ToArray();
        if (old.Length != 1) return false;
        if (old[0].IsAmbiguous) { reason = "这句在其他路线中也出现，请展开核对不同的下一句或使用原选项。"; return false; }
        var current = Create(engine, offer.MaximumLinesPerOption);
        if (current.MenuId != offer.MenuId || current.SectionId != offer.SectionId
            || !current.Cards.Any(c => c == old[0] && !c.IsAmbiguous)) return false;
        if (!engine.Pack.ById.TryGetValue(old[0].NodeId, out node)) return false;
        reason = ""; return true;
    }

    static string CardId(string menu, string option, string node) => "anchor:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(menu + "\0" + option + "\0" + node)));
}
