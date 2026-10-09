using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice;

// 独立于配音包版本；这里只保存播放器经历，不声称恢复了游戏内部状态。
public sealed class NavigationPoint
{
    public string? NodeId { get; set; }
    public RunMode Mode { get; set; } = RunMode.Ready;
    public Dictionary<string, string> Choices { get; set; } = new();
    public HashSet<string> Facts { get; set; } = new();
    public HashSet<string> Heard { get; set; } = new();
    public HashSet<string> ObservedMenus { get; set; } = new();
    public string? PendingMenu { get; set; }
    public bool SingleLine { get; set; }
    public string? ReviewRoute { get; set; }
    public int HistoryPosition { get; set; } = -1;
    public int ChoiceCursor { get; set; }
    public string? ReselectOptionId { get; set; }
}
public sealed class ChoiceVisit
{
    public long Sequence { get; set; }
    public string MenuId { get; set; } = "";
    public string OptionId { get; set; } = "";
    public string Label { get; set; } = "";
    public NavigationPoint Before { get; set; } = new();
}
public sealed class NavigationSnapshot
{
    public int Version { get; set; } = 2;
    public string PackId { get; set; } = "";
    public string PackFingerprint { get; set; } = "";
    public NavigationPoint Current { get; set; } = new();
    public List<Visit> Visits { get; set; } = new();
    public List<ChoiceVisit> Choices { get; set; } = new();
    public long NextSequence { get; set; } = 1;
    // 至多一层；撤销点自身不再携带撤销点，避免存档递归增长。
    public NavigationSnapshot? Undo { get; set; }
}
public sealed class NavigationBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset CreatedAt => CreatedUtc;
    public string SectionTitle { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public NavigationSnapshot Snapshot { get; set; } = new();
}
public sealed record InteractionMenuTarget(string MenuId, string Label, string Kind, bool IsCurrent, bool IsReturn)
{
    public override string ToString() => Label;
}
public sealed record StoryMenuTarget(string MenuId, string Label, string Kind, string Preview, string Status,
    bool CanOpen, string Reason, string? FallbackMenuId, bool IsCurrent, bool IsVisited)
{
    public override string ToString() => Label;
}
public sealed partial class PlaybackEngine
{
    readonly List<ChoiceVisit> choiceVisits = new();
    int choiceCursor;
    long nextChoiceSequence = 1;
    NavigationSnapshot? undoCorrection;
    string? graphFingerprint;
    public string? CurrentReselectOptionId { get; private set; }
    public string NavigationError { get; private set; } = "";
    public bool CanUndoCorrection => undoCorrection != null;
    public List<ChoiceVisit> RecentChoices => choiceVisits.Take(choiceCursor).Reverse().Select(CloneChoice).ToList();
    sealed record InteractionStep(Node Menu, ChoiceOption Option);

    static bool IsInteractionMenu(Node node) => !node.Archived && node.Kind == "choice" && node.MenuType is "interaction" or "topics";
    bool IsTrustedInteractionRoot(Node node) => IsInteractionMenu(node) && node.MenuType == "interaction" && node.PathId.Length == 0 && node.MenuNavigationEvidence.Count > 0;
    List<Node> InteractionAncestors()
    {
        var result = new List<Node>();
        if (Current == null) return result;
        string path = Current.PathId; var seen = new HashSet<string>();
        while (path.Length > 0 && seen.Add(path))
        {
            var owners = Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == Current.SectionId && n.Options.Any(o => o.PathId == path)).ToList();
            if (owners.Count != 1) break;
            var owner = owners[0];
            if (IsInteractionMenu(owner) && Allowed(owner)) result.Add(owner);
            path = owner.PathId;
        }
        return result;
    }
    bool ContinuousSegmentTo(ChoiceOption option, string menuId)
    {
        if (!option.BodyVerified || !option.SegmentIds.Contains(menuId)) return false;
        var seen = new HashSet<string>(); string? id = option.TargetId;
        while (id != null && seen.Add(id))
        {
            if (!option.SegmentIds.Contains(id) || !Pack.ById.TryGetValue(id, out var node) || node.Archived || node.PathId != option.PathId) return false;
            if (id == menuId) return node.Kind == "choice";
            // 直达只跳过经过核对的开场句，不代选中间菜单，不跨越未知出口。
            if (node.Kind != "line") return false;
            id = node.NextId;
        }
        return false;
    }
    bool TryInteractionPlan(Node target, out List<InteractionStep> steps)
    {
        steps = new();
        if (Current == null || target.SectionId != Current.SectionId || !IsInteractionMenu(target) || target.MenuNavigationEvidence.Count == 0) return false;
        if (IsTrustedInteractionRoot(target)) return true;
        if (Pack.SchemaVersion != 3 || target.MenuType != "topics") return false;
        var seen = new HashSet<string>(); var child = target;
        while (child.PathId.Length > 0 && seen.Add(child.Id))
        {
            var owners = Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == target.SectionId && n.Options.Any(o => o.PathId == child.PathId)).ToList();
            if (owners.Count != 1 || !IsInteractionMenu(owners[0])) return false;
            var owner = owners[0]; var option = owner.Options.First(o => o.PathId == child.PathId);
            if (!option.Requires.All(Facts.Contains) || option.Excludes.Any(Facts.Contains) || !ContinuousSegmentTo(option, child.Id)) return false;
            steps.Add(new(owner, option)); child = owner;
        }
        if (!IsTrustedInteractionRoot(child)) return false;
        steps.Reverse();
        var probe = new PlaybackEngine(Pack) { Choices = new(Choices), Facts = new(Facts), Heard = new(Heard) };
        foreach (var step in steps)
        {
            if (!probe.Allowed(step.Menu)) return false;
            probe.Choices[step.Menu.Id] = step.Option.PathId;
        }
        return probe.Allowed(target);
    }
    public List<InteractionMenuTarget> InteractionMenus
    {
        get
        {
            if (Current == null) return new();
            var ancestors = InteractionAncestors();
            var back = ancestors.FirstOrDefault(n => n.MenuType == "topics") ?? ancestors.FirstOrDefault();
            var eligible = ancestors.Concat(Pack.Nodes.Where(n => n.SectionId == Current.SectionId && TryInteractionPlan(n, out _))).DistinctBy(n => n.Id);
            return eligible.Select(n => new InteractionMenuTarget(n.Id, n.Text, n.MenuType, n.Id == CurrentId, n.Id == back?.Id)).ToList();
        }
    }
    public InteractionMenuTarget? ReturnInteractionTarget
    {
        get
        {
            var ancestors = InteractionAncestors();
            var menu = ancestors.FirstOrDefault(n => n.MenuType == "topics") ?? ancestors.FirstOrDefault();
            return menu == null ? null : new InteractionMenuTarget(menu.Id, menu.Text, menu.MenuType, menu.Id == CurrentId, true);
        }
    }
    public bool ReturnToInteractionMenu()
    {
        var target = ReturnInteractionTarget;
        return target == null ? FailNavigation("当前位置没有已核实的人物或话题返回菜单。") : OpenInteractionMenu(target.MenuId);
    }
    public bool OpenInteractionMenu(string menuId)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能切换人物或话题。");
        if (!Pack.ById.TryGetValue(menuId, out var target) || Current == null || target.SectionId != Current.SectionId)
            return FailNavigation("请选择当前小节的人物或话题菜单。");
        bool returning = InteractionAncestors().Any(n => n.Id == menuId);
        List<InteractionStep> steps = new();
        if (!returning && !TryInteractionPlan(target, out steps)) return FailNavigation("这处菜单的入口或开场连接尚未核实，请手动定位当前台词。");
        BeginCorrection(); StopRequested?.Invoke(); DiscardFuture();
        if (!returning)
        {
            foreach (var step in steps)
            {
                if (Choices.TryGetValue(step.Menu.Id, out var selected) && selected == step.Option.PathId) continue;
                CurrentId = step.Menu.Id; Mode = RunMode.Choice; PendingMenu = CurrentId; singleLine = false; ReviewRoute = null;
                RecordChoice(step.Option); Choices[step.Menu.Id] = step.Option.PathId;
            }
        }
        CurrentId = target.Id; Mode = RunMode.Choice; PendingMenu = target.Id; singleLine = false; ReviewRoute = null; CurrentReselectOptionId = null;
        Notice = "已打开：" + target.Text + "。请按游戏当前画面选择；尚未播放台词。";
        Changed?.Invoke(); return true;
    }

    string? StorySection(string? sectionId = null) => sectionId ?? Current?.SectionId ?? Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault()?.Id;
    bool HasMenuSource(Node menu) => !string.IsNullOrWhiteSpace(menu.Source) || !string.IsNullOrWhiteSpace(menu.RenderSource) || menu.MenuNavigationEvidence.Count > 0;
    static int SourceMenuOrder(Node menu)
    {
        string source = menu.Source ?? menu.RenderSource ?? "";
        int marker = source.LastIndexOf("#menu-", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return int.MaxValue;
        string digits = new(source[(marker + 6)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int number) ? number : int.MaxValue;
    }
    List<Node> SectionMenuNodes(string sectionId) => Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == sectionId).OrderBy(SourceMenuOrder).ToList();
    static string MenuExcerpt(string? text, int limit = 100)
    {
        string value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > limit ? value[..limit] + "…" : value;
    }
    ChoiceVisit? LatestStoryVisit(string menuId) => choiceVisits.Take(choiceCursor).LastOrDefault(c => c.MenuId == menuId && ValidatePoint(c.Before, History.Count, c.Before.ChoiceCursor));
    bool TryStoryPlan(Node target, string sectionId, out List<InteractionStep> steps, out string? fallbackId, out string reason)
    {
        steps = new(); fallbackId = null; reason = "";
        if (target.Kind != "choice" || target.Archived || target.SectionId != sectionId) { reason = "这不是当前小节的有效选择点。"; return false; }
        if (!HasMenuSource(target) && target.Id != CurrentId && LatestStoryVisit(target.Id) == null) { reason = "此选择点尚无可核对来源。"; return false; }
        var chain = new List<(Node Owner, ChoiceOption Option, Node Child)>();
        var seen = new HashSet<string>(); var child = target;
        while (child.PathId.Length > 0)
        {
            if (!seen.Add(child.Id)) { reason = "上级路线存在循环，需手动定位。"; return false; }
            var owners = Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == sectionId && n.Options.Any(o => o.PathId == child.PathId)).ToList();
            if (owners.Count != 1) { reason = "无法唯一确定所属上级选择点。"; return false; }
            var owner = owners[0]; chain.Add((owner, owner.Options.First(o => o.PathId == child.PathId), child)); child = owner;
        }
        if (!HasMenuSource(child) && child.Id != CurrentId && LatestStoryVisit(child.Id) == null) { reason = "最外层选择点尚无可核对来源。"; return false; }
        chain.Reverse(); fallbackId = child.Id;
        var probe = new PlaybackEngine(Pack) { Choices = new(Choices), Facts = new(Facts), Heard = new(Heard) };
        foreach (var link in chain)
        {
            if (!probe.Allowed(link.Owner)) { reason = "所属路线尚未确认。"; return false; }
            if (!link.Option.Requires.All(Facts.Contains) || link.Option.Excludes.Any(Facts.Contains)) { reason = "这处选择点的前置条件尚未确认，先打开所属菜单。"; return false; }
            if (Pack.SchemaVersion != 3 || !ContinuousSegmentTo(link.Option, link.Child.Id))
            {
                reason = "通向此处的连续正文尚未核实，或中途还有其他选择。先打开最近可达的所属菜单。"; return false;
            }
            steps.Add(new(link.Owner, link.Option)); probe.Choices[link.Owner.Id] = link.Option.PathId; fallbackId = link.Child.Id;
        }
        if (!probe.Allowed(target)) { reason = "这处选择点不在已核实的正文范围内。"; return false; }
        fallbackId = null; return true;
    }
    StoryMenuTarget DescribeStoryMenu(Node node, int index, string sectionId, HashSet<string>? interactionIds = null)
    {
        bool visited = LatestStoryVisit(node.Id) != null;
        bool interaction = interactionIds?.Contains(node.Id) ?? (Current?.SectionId == sectionId && (InteractionAncestors().Any(m => m.Id == node.Id) || TryInteractionPlan(node, out _)));
        bool open = TryStoryPlan(node, sectionId, out _, out var fallback, out var reason);
        if (visited || interaction) { open = true; fallback = null; reason = ""; }
        string status = interaction ? "已核实互动" : visited ? "返回已访问选择" : open ? "仅定位选择点" : fallback != null ? "先到所属选择点" : "待核对";
        string preview = string.Join(" ／ ", node.Options.Take(4).Select(o => o.Label));
        if (node.Options.Count > 4) preview += " …";
        var before = Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == node.SectionId && n.PathId == node.PathId && n.NextId == node.Id).ToList();
        if (before.Count == 1) preview += "\n前句：" + MenuExcerpt(before[0].Speaker + "：" + before[0].Text);
        var firstLines = node.Options.Select(o => !string.IsNullOrWhiteSpace(o.Preview) ? o.Preview : Pack.ById.TryGetValue(o.TargetId, out var line) && line.Kind == "line" ? line.Speaker + "：" + line.Text : "")
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(2).Select(s => MenuExcerpt(s, 90)).ToList();
        if (firstLines.Count > 0) preview += "\n首句：" + string.Join(" ／ ", firstLines);
        string label = $"{index + 1:00} · " + (string.IsNullOrWhiteSpace(node.Text) ? "选择点" : node.Text);
        return new(node.Id, label, node.MenuType, preview, status, open, reason, fallback, node.Id == CurrentId, visited);
    }
    public List<StoryMenuTarget> StoryMenus => GetStoryMenus();
    public List<StoryMenuTarget> GetStoryMenus(string? sectionId = null)
    {
        var section = StorySection(sectionId);
        if (section == null || !Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == section)) return new();
        var interactionIds = Current?.SectionId == section ? InteractionMenus.Select(m => m.MenuId).ToHashSet() : new HashSet<string>();
        return SectionMenuNodes(section).Select((n, i) => DescribeStoryMenu(n, i, section, interactionIds)).ToList();
    }
    public StoryMenuTarget? ParentStoryMenu
    {
        get
        {
            if (Current == null || Current.PathId.Length == 0) return null;
            var owners = Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == Current.SectionId && n.Options.Any(o => o.PathId == Current.PathId)).ToList();
            if (owners.Count != 1) return null;
            var menus = SectionMenuNodes(Current.SectionId);
            var target = DescribeStoryMenu(owners[0], menus.FindIndex(n => n.Id == owners[0].Id), Current.SectionId);
            return target;
        }
    }
    public bool ReturnToStoryMenu()
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能返回选择点。");
        var target = ParentStoryMenu;
        return target == null ? FailNavigation("当前位置没有可确认的上级选择点。") : OpenStoryMenu(target.MenuId);
    }
    public bool OpenStoryMenu(string menuId) => OpenStoryMenu(menuId, StorySection());
    public bool OpenStoryMenu(string menuId, string? sectionId)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能定位选择点。");
        string? section = StorySection(sectionId);
        if (section == null || !Pack.ById.TryGetValue(menuId, out var target) || target.Archived || target.Kind != "choice" || target.SectionId != section)
            return FailNavigation("请选择当前小节的有效选择点。");
        if (Current?.SectionId == section && InteractionMenus.Any(m => m.MenuId == menuId)) return OpenInteractionMenu(menuId);
        var visited = LatestStoryVisit(menuId);
        if (visited != null) return ReselectChoice(visited.Sequence);
        bool reachable = TryStoryPlan(target, section, out var steps, out var fallback, out var reason);
        string prefix = "";
        if (!reachable)
        {
            if (fallback == null || fallback == menuId || !Pack.ById.TryGetValue(fallback, out var safe)) return FailNavigation(reason);
            target = safe;
            var prior = LatestStoryVisit(safe.Id);
            if (prior != null)
            {
                bool restored = ReselectChoice(prior.Sequence);
                if (restored) { Notice = reason + " 已回到：" + safe.Text + "。请与游戏选择相同选项。"; Changed?.Invoke(); }
                return restored;
            }
            if (!TryStoryPlan(safe, section, out steps, out _, out _)) return FailNavigation(reason);
            prefix = reason + " ";
        }
        BeginCorrection(); StopRequested?.Invoke(); DiscardFuture();
        foreach (var step in steps)
        {
            if (Choices.TryGetValue(step.Menu.Id, out var selected) && selected == step.Option.PathId) continue;
            CurrentId = step.Menu.Id; Mode = RunMode.Choice; PendingMenu = CurrentId; singleLine = false; ReviewRoute = null;
            RecordChoice(step.Option); Choices[step.Menu.Id] = step.Option.PathId;
        }
        CurrentId = target.Id; Mode = RunMode.Choice; PendingMenu = target.Id; singleLine = false; ReviewRoute = null; CurrentReselectOptionId = null;
        Notice = prefix + "已定位选择点：" + target.Text + "。请按游戏画面选择，确认选项后播放。";
        Changed?.Invoke(); return true;
    }

    // 音频、正文纠错不改变路线存档资格；连接、核对边界和条件变化必须重新确认。
    public static string NavigationFingerprint(Pack pack)
    {
        var graph = new
        {
            pack.Id, pack.SchemaVersion,
            Chapters = pack.Chapters.Select(c => new { c.Id, Sections = c.Sections.Select(s => new { s.Id, s.StartId }) }),
            Nodes = pack.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => new
            {
                n.Id, n.Kind, n.Archived, n.SectionId, n.PathId, n.NextId, n.MenuType,
                n.CompleteRoute, n.SetFacts, n.ResumeMenuIds,
                Options = n.Options.Select(o => new { o.Id, o.PathId, o.TargetId, o.ReturnId, o.MergeId,
                    o.Verified, o.BodyVerified, o.ExitVerified, o.SegmentIds, o.BoundaryId,
                    o.Requires, o.Excludes, o.LineIds })
            })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(graph, Json.Options))));
    }
    void ResetNavigation()
    {
        choiceVisits.Clear(); choiceCursor = 0; nextChoiceSequence = 1; undoCorrection = null;
        CurrentReselectOptionId = null; NavigationError = "";
        ObservedMenus.Clear();
    }
    void BeginCorrection()
    {
        undoCorrection = CurrentId == null ? null : ExportCore();
        CurrentReselectOptionId = null;
    }
    void ClearCorrection() { undoCorrection = null; NavigationError = ""; }
    void DiscardFuture()
    {
        if (historyPosition < History.Count - 1) History.RemoveRange(historyPosition + 1, History.Count - historyPosition - 1);
        if (choiceCursor < choiceVisits.Count) choiceVisits.RemoveRange(choiceCursor, choiceVisits.Count - choiceCursor);
    }
    void RecordChoice(ChoiceOption option)
    {
        var before = CapturePoint();
        DiscardFuture();
        choiceVisits.Add(new ChoiceVisit { Sequence = nextChoiceSequence++, MenuId = CurrentId!, OptionId = option.Id, Label = option.Label, Before = before });
        choiceCursor = choiceVisits.Count;
        CurrentReselectOptionId = null;
    }
    NavigationPoint CapturePoint() => new()
    {
        NodeId = CurrentId, Mode = Mode, Choices = new(Choices), Facts = new(Facts), Heard = new(Heard), ObservedMenus = new(ObservedMenus),
        PendingMenu = PendingMenu, SingleLine = singleLine, ReviewRoute = ReviewRoute,
        HistoryPosition = historyPosition, ChoiceCursor = choiceCursor, ReselectOptionId = CurrentReselectOptionId
    };
    internal static Visit CloneVisit(Visit v) => new(v.NodeId, new(v.Choices), new(v.Facts ?? new()), new(v.Heard ?? new()), v.PendingMenu, v.SingleLine, v.ReviewRoute, v.ChoiceCursor, new(v.ObservedMenus ?? new()));
    static NavigationPoint ClonePoint(NavigationPoint p) => new()
    {
        NodeId = p.NodeId, Mode = p.Mode, Choices = new(p.Choices), Facts = new(p.Facts), Heard = new(p.Heard), ObservedMenus = new(p.ObservedMenus),
        PendingMenu = p.PendingMenu, SingleLine = p.SingleLine, ReviewRoute = p.ReviewRoute,
        HistoryPosition = p.HistoryPosition, ChoiceCursor = p.ChoiceCursor, ReselectOptionId = p.ReselectOptionId
    };
    static ChoiceVisit CloneChoice(ChoiceVisit c) => new() { Sequence = c.Sequence, MenuId = c.MenuId, OptionId = c.OptionId, Label = c.Label, Before = ClonePoint(c.Before) };
    NavigationSnapshot ExportCore() => new()
    {
        PackId = Pack.Id, PackFingerprint = graphFingerprint ??= NavigationFingerprint(Pack), Current = CapturePoint(),
        Visits = History.Select(CloneVisit).ToList(), Choices = choiceVisits.Select(CloneChoice).ToList(), NextSequence = nextChoiceSequence
    };
    static NavigationSnapshot CloneCore(NavigationSnapshot snapshot) => new()
    {
        Version = snapshot.Version, PackId = snapshot.PackId, PackFingerprint = snapshot.PackFingerprint,
        Current = ClonePoint(snapshot.Current), Visits = snapshot.Visits.Select(CloneVisit).ToList(),
        Choices = snapshot.Choices.Select(CloneChoice).ToList(), NextSequence = snapshot.NextSequence
    };
    public NavigationSnapshot ExportNavigation()
    {
        var snapshot = ExportCore();
        if (undoCorrection != null) snapshot.Undo = CloneCore(undoCorrection);
        return snapshot;
    }
    void ApplyPoint(NavigationPoint point)
    {
        CurrentId = point.NodeId; Mode = point.Mode; Choices = new(point.Choices); Facts = new(point.Facts); Heard = new(point.Heard);
        ObservedMenus = new(point.ObservedMenus);
        PendingMenu = point.PendingMenu; singleLine = point.SingleLine; ReviewRoute = point.ReviewRoute;
        historyPosition = point.HistoryPosition; choiceCursor = point.ChoiceCursor; CurrentReselectOptionId = point.ReselectOptionId;
    }
    void ApplySnapshot(NavigationSnapshot snapshot, bool cold)
    {
        History.Clear(); History.AddRange(snapshot.Visits.Select(CloneVisit));
        choiceVisits.Clear(); choiceVisits.AddRange(snapshot.Choices.Select(CloneChoice));
        nextChoiceSequence = snapshot.NextSequence;
        ApplyPoint(snapshot.Current);
        if (cold && Mode != RunMode.Original) Mode = RunMode.Ready;
        else if (Mode == RunMode.Following) Mode = RunMode.Paused;
        Notice = "已恢复配音位置，等待确认；游戏画面不会随之回退。";
    }
    bool FailNavigation(string message) { NavigationError = message; return false; }
    bool ValidatePoint(NavigationPoint point, int visitCount, int choiceCount)
    {
        if (point.Choices == null || point.Facts == null || point.Heard == null || point.ObservedMenus == null || !Enum.IsDefined(point.Mode)) return false;
        if(point.ObservedMenus.Any(id=>!Pack.ById.TryGetValue(id,out var menu) || menu.Archived || menu.Kind!="choice"))return false;
        if (point.HistoryPosition < -1 || point.HistoryPosition >= visitCount || point.ChoiceCursor < 0 || point.ChoiceCursor > choiceCount) return false;
        if (point.PendingMenu != null && (!Pack.ById.TryGetValue(point.PendingMenu, out var pending) || pending.Archived || pending.Kind != "choice")) return false;
        foreach (var entry in point.Choices)
        {
            if (!Pack.ById.TryGetValue(entry.Key, out var menu) || menu.Archived || menu.Kind != "choice" ||
                !menu.Options.Any(o => o.PathId == entry.Value && (Pack.SchemaVersion == 3 ? o.BodyVerified : o.Verified))) return false;
        }
        var review = point.ReviewRoute == null ? null : Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).FirstOrDefault(o => o.Id == point.ReviewRoute);
        if (point.ReviewRoute != null && review == null) return false;
        if (point.NodeId == null) return !point.SingleLine && point.HistoryPosition == -1 && point.ChoiceCursor == 0;
        if (!Pack.ById.TryGetValue(point.NodeId, out var node) || node.Archived) return false;
        // CommitSingle 也允许已确认路线和共同台词。单句是推进限制，不一定有待核路线；
        // 下面仍须通过 Allowed/CanLocate，不能凭 SingleLine 恢复未选分支。
        if (point.SingleLine && node.Kind != "line") return false;
        var check = new PlaybackEngine(Pack) { Choices = new(point.Choices), Facts = new(point.Facts), Heard = new(point.Heard), ReviewRoute = point.ReviewRoute, ObservedMenus = new(point.ObservedMenus) };
        if (!check.Allowed(node) && !(point.SingleLine && check.CanLocate(node)) && !(point.Mode == RunMode.Original && node.Kind == "line")) return false;
        if (point.Mode == RunMode.Choice && node.Kind != "choice") return false;
        // 不完整连接也可能停在最后一句并进入 Gap，不能仅按节点类型抹去这个暂停状态。
        if (point.Mode == RunMode.Merge && node.Kind != "merge") return false;
        if (point.ReselectOptionId != null && (node.Kind != "choice" || !node.Options.Any(o => o.Id == point.ReselectOptionId))) return false;
        return true;
    }
    bool ValidateVisit(Visit visit, int choiceCount)
    {
        if (visit.Choices == null || !Pack.ById.TryGetValue(visit.NodeId, out var node) || node.Kind != "line") return false;
        return ValidatePoint(new NavigationPoint { NodeId = visit.NodeId, Choices = visit.Choices, Facts = visit.Facts ?? new(), Heard = visit.Heard ?? new(),
            PendingMenu = visit.PendingMenu, SingleLine = visit.SingleLine, ReviewRoute = visit.ReviewRoute, ChoiceCursor = visit.ChoiceCursor, ObservedMenus = visit.ObservedMenus ?? new() }, 0, choiceCount);
    }
    public bool ValidateNavigation(NavigationSnapshot snapshot)
    {
        if (!ValidateNavigationCore(snapshot)) return false;
        if (snapshot.Undo != null && (snapshot.Undo.Undo != null || !ValidateNavigationCore(snapshot.Undo)))
            return FailNavigation("存档中的单步撤销点无效，请重新确认当前台词。");
        return true;
    }
    bool ValidateNavigationCore(NavigationSnapshot snapshot)
    {
        NavigationError = "";
        if (snapshot == null || snapshot.Version is not (1 or 2) || snapshot.PackId != Pack.Id) return FailNavigation("存档不属于当前章节，或使用了不支持的导航版本。");
        string fingerprint = NavigationFingerprint(Pack);
        if (!String.Equals(snapshot.PackFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            !Pack.CompatibleNavigationFingerprints.Contains(snapshot.PackFingerprint, StringComparer.OrdinalIgnoreCase))
            return FailNavigation("章节路线已更新，旧路线状态不能直接恢复。请手动确认游戏当前台词。");
        graphFingerprint = fingerprint;
        if (snapshot.Current == null || snapshot.Visits == null || snapshot.Choices == null || snapshot.Choices.Any(c => c == null || c.Before == null)) return FailNavigation("存档内容不完整。");
        if (snapshot.NextSequence < 1 || snapshot.Choices.Any(c => c.Sequence < 1 || c.Sequence >= snapshot.NextSequence) ||
            snapshot.Choices.Select(c => c.Sequence).Distinct().Count() != snapshot.Choices.Count ||
            !snapshot.Choices.Select(c => c.Sequence).SequenceEqual(snapshot.Choices.Select(c => c.Sequence).OrderBy(x => x))) return FailNavigation("选择历史顺序无效。");
        if (!ValidatePoint(snapshot.Current, snapshot.Visits.Count, snapshot.Choices.Count) || snapshot.Visits.Any(v => v == null || !ValidateVisit(v, snapshot.Choices.Count))) return FailNavigation("存档中的台词、分支范围或历史位置已失效。");
        if (!snapshot.Visits.Select(v => v.ChoiceCursor).SequenceEqual(snapshot.Visits.Select(v => v.ChoiceCursor).OrderBy(x => x)) ||
            snapshot.Current.HistoryPosition >= 0 && snapshot.Visits[snapshot.Current.HistoryPosition].ChoiceCursor > snapshot.Current.ChoiceCursor)
            return FailNavigation("台词履历与选择游标不一致。");
        for (int i = 0; i < snapshot.Choices.Count; i++)
        {
            var choice = snapshot.Choices[i];
            if (choice.Before.NodeId != choice.MenuId || choice.Before.Mode != RunMode.Choice || choice.Before.ChoiceCursor != i ||
                !ValidatePoint(choice.Before, snapshot.Visits.Count, i) || !Pack.ById.TryGetValue(choice.MenuId, out var menu) ||
                !menu.Options.Any(o => o.Id == choice.OptionId && (choice.Before.ObservedMenus.Contains(menu.Id) || o.Requires.All(choice.Before.Facts.Contains) && !o.Excludes.Any(choice.Before.Facts.Contains))))
                return FailNavigation("选择记录与当时的菜单或条件不一致。");
        }
        return true;
    }
    public bool ImportNavigation(NavigationSnapshot snapshot, bool allowBoundaryResume = false)
    {
        if (!ValidateNavigation(snapshot)) return false;
        bool resumedBoundary = false;
        if (allowBoundaryResume && PrepareBoundaryResume(snapshot) is NavigationSnapshot repaired)
        {
            // 原快照先完整通过校验，再校验仅改当前点的候选；失败保留可用旧边界。
            if (ValidateNavigation(repaired)) { snapshot = repaired; resumedBoundary = true; }
            else NavigationError = "";
        }
        StopRequested?.Invoke(); undoCorrection = snapshot.Undo == null ? null : CloneCore(snapshot.Undo);
        if (undoCorrection != null) undoCorrection.PackFingerprint = graphFingerprint!;
        ApplySnapshot(snapshot, true);
        if (resumedBoundary) Notice = "该段衔接已修复，已接回后续台词；确认位置后再播放。";
        Changed?.Invoke(); return true;
    }
    public bool ReselectLastChoice() => choiceCursor > 0 && ReselectChoice(choiceVisits[choiceCursor - 1].Sequence);
    public bool ReselectChoice(long sequence)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能重选分支，请先结束原声时段。");
        int index = choiceVisits.FindIndex(c => c.Sequence == sequence);
        if (index < 0 || index >= choiceCursor) return FailNavigation("这次选择不在当前访问历史中。");
        var choice = choiceVisits[index];
        if (!ValidatePoint(choice.Before, History.Count, index)) return FailNavigation("选择前的位置已失效，请手动定位。");
        BeginCorrection(); StopRequested?.Invoke(); ApplyPoint(choice.Before);
        Mode = RunMode.Choice; PendingMenu = CurrentId; CurrentReselectOptionId = choice.OptionId;
        Notice = "重新选择配音路线；请与游戏当前选择保持一致。";
        Changed?.Invoke(); return true;
    }
    public bool RestoreVisit(int index, bool play = false)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能恢复台词履历。");
        if (index < 0 || index >= History.Count || !ValidateVisit(History[index], choiceVisits.Count)) return FailNavigation("这条台词履历已失效。");
        var v = CloneVisit(History[index]);
        BeginCorrection(); StopRequested?.Invoke();
        ApplyPoint(new NavigationPoint { NodeId = v.NodeId, Mode = play ? RunMode.Following : RunMode.Paused, Choices = v.Choices,
            Facts = v.Facts ?? new(), Heard = v.Heard ?? new(), PendingMenu = v.PendingMenu, SingleLine = v.SingleLine,
            ReviewRoute = v.ReviewRoute, HistoryPosition = index, ChoiceCursor = v.ChoiceCursor, ObservedMenus = v.ObservedMenus ?? new() });
        Notice = play ? "已从这次台词记录继续播放。" : "已回到这次台词记录，等待确认播放。";
        if (play) PlayRequested?.Invoke(Current);
        Changed?.Invoke(); return true;
    }
    public bool UndoCorrection()
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能撤销定位，请先结束原声时段。");
        if (undoCorrection == null) return FailNavigation("没有可撤销的纠偏操作。");
        var snapshot = undoCorrection;
        if (!ValidateNavigation(snapshot)) return false;
        StopRequested?.Invoke(); undoCorrection = null; ApplySnapshot(snapshot, false);
        Notice = "已撤销本次纠偏，配音保持暂停。"; Changed?.Invoke(); return true;
    }
    public void PauseForBrowse()
    {
        StopRequested?.Invoke();
        if (Mode == RunMode.Following) { Mode = RunMode.Paused; Changed?.Invoke(); }
    }
    public NavigationBookmark CreateBookmark(string label)
    {
        if (Current == null) throw new InvalidOperationException("请先选择要保存的剧情位置。");
        return new NavigationBookmark
        {
            Label = string.IsNullOrWhiteSpace(label) ? Current.Text : label.Trim(),
            SectionTitle = Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == Current.SectionId)?.Title ?? "",
            Speaker = Current.Speaker, Text = Current.Text, Snapshot = ExportCore()
        };
    }
    public bool RestoreBookmark(NavigationBookmark bookmark)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能恢复书签。");
        if (bookmark == null || !ValidateNavigation(bookmark.Snapshot)) return false;
        BeginCorrection(); StopRequested?.Invoke(); ApplySnapshot(bookmark.Snapshot, false);
        Notice = "已恢复书签，等待确认；游戏画面不会随之回退。"; Changed?.Invoke(); return true;
    }
}
