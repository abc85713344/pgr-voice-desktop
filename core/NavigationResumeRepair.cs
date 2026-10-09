using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PgrVoice;

/// <summary>经核对的旧段尾恢复声明；仅用于自动恢复当前游戏配音断点，不替代路线验证。</summary>
public sealed class NavigationResumeRepair
{
    public string FromFingerprint { get; set; } = "";
    public string BoundaryId { get; set; } = "";
    public List<string> PreviousNodeIds { get; set; } = new();
    public string TargetId { get; set; } = "";
    public List<string> Evidence { get; set; } = new();

    public static void Validate(Pack pack)
    {
        if (pack.NavigationResumeRepairs == null) throw new InvalidDataException("段尾恢复声明不能为空。");
        if (pack.NavigationResumeRepairs.Count == 0) return;
        if (pack.SchemaVersion != 3) throw new InvalidDataException("段尾恢复声明仅适用于已核对路线的配音包。");
        string currentFingerprint = PlaybackEngine.NavigationFingerprint(pack);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in pack.NavigationResumeRepairs)
        {
            if (rule == null || rule.FromFingerprint == null || rule.FromFingerprint.Length != 64
                || !rule.FromFingerprint.All(Uri.IsHexDigit)
                || !pack.CompatibleNavigationFingerprints.Contains(rule.FromFingerprint, StringComparer.OrdinalIgnoreCase)
                || rule.FromFingerprint.Equals(currentFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("段尾恢复声明必须绑定明确兼容的旧导航指纹。");
            if (string.IsNullOrWhiteSpace(rule.BoundaryId) || string.IsNullOrWhiteSpace(rule.TargetId)
                || rule.PreviousNodeIds == null || rule.PreviousNodeIds.Count == 0
                || rule.PreviousNodeIds.Any(string.IsNullOrWhiteSpace)
                || rule.PreviousNodeIds.Distinct(StringComparer.Ordinal).Count() != rule.PreviousNodeIds.Count
                || rule.Evidence == null || rule.Evidence.Count == 0 || rule.Evidence.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("段尾恢复声明缺少位置或核对证据。");
            if (!pack.ById.TryGetValue(rule.BoundaryId, out var boundary) || boundary.Archived || boundary.Kind != "gap"
                || string.IsNullOrEmpty(boundary.PathId)
                || !pack.ById.TryGetValue(rule.TargetId, out var target) || target.Archived || target.Kind != "line"
                || target.SectionId != boundary.SectionId || target.PathId != boundary.PathId)
                throw new InvalidDataException("段尾恢复只能接回同小节、同父路线的正文。");
            var owners = pack.Nodes.Where(n => !n.Archived && n.SectionId == boundary.SectionId)
                .SelectMany(n => n.Options).Where(o => o.PathId == boundary.PathId).ToArray();
            if (owners.Length != 1 || !owners[0].BodyVerified || !owners[0].ExitVerified
                || owners[0].BoundaryId != boundary.Id || !owners[0].SegmentIds.Contains(target.Id))
                throw new InvalidDataException("段尾恢复要求保留原边界并完整核实父路线与目标正文。");
            foreach (string previous in rule.PreviousNodeIds)
            {
                string key = rule.FromFingerprint.ToUpperInvariant() + "\n" + rule.BoundaryId + "\n" + previous;
                if (!sources.Add(key)) throw new InvalidDataException("同一旧段尾与前句存在重复的恢复声明。");
                if (!rule.HasVerifiedContinuation(pack, previous, null))
                    throw new InvalidDataException("段尾恢复声明没有唯一且已核对的返回正文链。");
            }
        }
    }

    // 只穿过唯一 NextId 的 return/merge；任何其它正文、菜单、未知边界、循环或跨节均停止。
    internal bool HasVerifiedContinuation(Pack pack, string previousId, Func<Node, bool>? allowed)
    {
        if (!pack.ById.TryGetValue(previousId, out var previous) || previous.Archived || previous.Kind != "line"
            || !pack.ById.TryGetValue(TargetId, out var target) || target.Archived || target.Kind != "line"
            || previous.SectionId != target.SectionId || previous.Id == target.Id || allowed?.Invoke(previous) == false)
            return false;
        bool BodyContains(Node node, bool requireExit)
        {
            if (node.PathId.Length == 0) return !requireExit;
            var owners = pack.Nodes.Where(n => !n.Archived && n.SectionId == node.SectionId).SelectMany(n => n.Options)
                .Where(o => o.PathId == node.PathId).ToArray();
            return owners.Length == 1 && owners[0].BodyVerified && owners[0].SegmentIds.Contains(node.Id)
                && (!requireExit || owners[0].ExitVerified);
        }
        if (!BodyContains(previous, false)) return false;
        string? next = previous.NextId;
        var seen = new HashSet<string>(StringComparer.Ordinal) { previous.Id };
        for (int step = 0; step < 128 && next != null; step++)
        {
            if (!seen.Add(next) || !pack.ById.TryGetValue(next, out var node) || node.Archived
                || node.SectionId != target.SectionId || allowed?.Invoke(node) == false) return false;
            if (node.Kind == "line") return node.Id == target.Id && BodyContains(node, false);
            if (node.Kind is not ("return" or "merge") || !BodyContains(node, node.Kind == "return")) return false;
            next = node.NextId;
        }
        return false;
    }
}

public sealed partial class PlaybackEngine
{
    NavigationSnapshot? PrepareBoundaryResume(NavigationSnapshot snapshot)
    {
        var current = snapshot.Current;
        if (current.Mode == RunMode.Original || current.SingleLine || current.NodeId == null
            || !Pack.ById.TryGetValue(current.NodeId, out var boundary) || boundary.Kind != "gap" || boundary.Archived
            || current.HistoryPosition < 0 || current.HistoryPosition >= snapshot.Visits.Count) return null;
        var previous = snapshot.Visits[current.HistoryPosition];
        // 边界没有新增访次；必须使用当前历史指针，不能把用户后退后的未来履历拿来推断。
        if (previous.SingleLine || previous.ChoiceCursor != current.ChoiceCursor) return null;
        var rules = Pack.NavigationResumeRepairs.Where(r => r.BoundaryId == boundary.Id
            && r.FromFingerprint.Equals(snapshot.PackFingerprint, StringComparison.OrdinalIgnoreCase)
            && r.PreviousNodeIds.Contains(previous.NodeId)).ToArray();
        if (rules.Length != 1) return null;
        var check = new PlaybackEngine(Pack)
        {
            Choices = new(current.Choices), Facts = new(current.Facts), Heard = new(current.Heard),
            ObservedMenus = new(current.ObservedMenus), ReviewRoute = current.ReviewRoute
        };
        if (!rules[0].HasVerifiedContinuation(Pack, previous.NodeId, check.Allowed)) return null;
        var result = CloneCore(snapshot);
        if (snapshot.Undo != null) result.Undo = CloneCore(snapshot.Undo);
        result.Current.NodeId = rules[0].TargetId;
        result.Current.Mode = RunMode.Ready;
        result.Current.ReviewRoute = null;
        result.Current.ReselectOptionId = null;
        return result;
    }
}
