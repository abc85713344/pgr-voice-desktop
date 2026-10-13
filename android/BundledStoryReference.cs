using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

/// <summary>只读剧情资料。节点身份与正文逐次校验；不提供导航授权，不保存用户状态。</summary>
public static class BundledStoryReference
{
    const string ResourceName = "PgrVoice.StoryReference.20261009.zip";
    const int MaxCachedSections = 3;
    const string Unavailable = "内置剧情资料暂不可用。";
    const string Mismatch = "当前小节的正文或选项与内置资料不一致，暂不展示已绑定资料。";
    static readonly object Sync = new();
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly Lazy<ZipArchive> Archive = new(() =>
    {
        var stream = typeof(BundledStoryReference).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("内置剧情资料缺失。");
        if (!stream.CanSeek) { stream.Dispose(); throw new InvalidDataException("内置剧情资料不支持按小节读取。"); }
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch { stream.Dispose(); throw; }
    });
    static readonly Lazy<ReferenceIndex> Index = new(() =>
    {
        var index = ReadJson<ReferenceIndex>("index.json");
        if (index.Version != 1) throw new InvalidDataException("内置剧情资料版本不支持。");
        return index;
    });
    static string cachedPackId = "";
    static PackIndex? cachedPack;
    static string? cachedPreamble;
    static readonly Dictionary<string, SectionCache> Sections = new(StringComparer.Ordinal);
    static readonly LinkedList<string> RecentSections = new();

    public static bool TryGetNode(Pack pack, Node node, [NotNullWhen(true)] out StoryReferenceNode? reference)
    {
        reference = null;
        if (pack == null || node == null || node.Archived) return false;
        lock (Sync)
        {
            try
            {
                var section = GetSection(pack.Id, node.SectionId);
                var current = FindCurrent(pack, node.Id);
                if (section == null || current == null || current.Archived || !section.Nodes.TryGetValue(node.Id, out var found) ||
                    !Matches(pack.Id, node, found) || !Matches(pack.Id, current, found)) return false;
                reference = found.Node;
                return true;
            }
            catch (Exception ex) when (IsDataError(ex)) { return false; }
        }
    }

    public static string GetSectionText(Pack pack, string sectionId)
    {
        if (pack == null || string.IsNullOrEmpty(sectionId)) return "请先选择要查阅的小节。";
        lock (Sync)
        {
            try
            {
                var section = GetSection(pack.Id, sectionId);
                if (section == null) return "内置资料暂未收录这个小节。";
                // 允许旧包只有导航边界不同；正文与菜单身份/选项必须双向一致。
                var current = pack.Nodes.Where(n => !n.Archived && n.SectionId == sectionId && n.Kind is "line" or "choice").ToArray();
                var reference = section.Nodes.Values.Where(n => n.Node.Kind is "line" or "choice").ToArray();
                if (current.Length == 0 || current.Length != reference.Length || current.Any(n =>
                    !section.Nodes.TryGetValue(n.Id, out var r) || !Matches(pack.Id, n, r))) return Mismatch;
                section.Text ??= ReadText(section.Metadata.TextEntry);
                cachedPreamble ??= ReadText(cachedPack!.PreambleEntry);
                return "内置剧情文本 · 只读参考\n正文和选项已与当前小节逐条对应；资料中的现稿连接不代替实际游戏路线。\n\n" + cachedPreamble + section.Text;
            }
            catch (Exception ex) when (IsDataError(ex)) { return Unavailable; }
        }
    }

    public static string GetSummary(Pack pack)
    {
        if (pack == null) return "请先打开配音章节。";
        lock (Sync)
        {
            try
            {
                var item = Index.Value.Packs.FirstOrDefault(p => p.Id == pack.Id);
                if (item == null) return "内置资料暂未收录这个章节。";
                return $"{item.Title}\n内置 {item.SectionCount} 个小节、{item.NodeCount} 条节点资料。\n按当前小节查阅全文；游戏节点号与稿内查找号分开标注。资料仅供查阅，不改变播放位置、配音或自动续播资格。";
            }
            catch (Exception ex) when (IsDataError(ex)) { return Unavailable; }
        }
    }

    static Node? FindCurrent(Pack pack, string id) => pack.ById.TryGetValue(id, out var n) ? n : pack.Nodes.FirstOrDefault(n => n.Id == id);
    static bool Matches(string packId, Node node, ReferenceDocument reference)
    {
        var r = reference.Node;
        if (r.PackId != packId || r.NodeId != node.Id || r.SectionId != node.SectionId || r.Kind != node.Kind ||
            r.Speaker != node.Speaker || r.Text != node.Text || node.Options.Count != reference.Bindings.Count) return false;
        for (int i = 0; i < node.Options.Count; i++)
            if (node.Options[i].Id != reference.Bindings[i].OptionId || node.Options[i].Label != reference.Bindings[i].Label) return false;
        return true;
    }

    static SectionCache? GetSection(string packId, string sectionId)
    {
        if (cachedPackId != packId)
        {
            cachedPackId = packId; cachedPack = null; cachedPreamble = null;
            Sections.Clear(); RecentSections.Clear();
        }
        var item = Index.Value.Packs.FirstOrDefault(p => p.Id == packId);
        if (item == null) return null;
        cachedPack ??= ReadJson<PackIndex>(item.IndexEntry);
        if (cachedPack.Id != packId) throw new InvalidDataException("内置章节身份不一致。");
        var metadata = cachedPack.Sections.FirstOrDefault(s => s.Id == sectionId);
        if (metadata == null) return null;
        if (Sections.TryGetValue(sectionId, out var cached))
        {
            RecentSections.Remove(sectionId); RecentSections.AddLast(sectionId);
            return cached;
        }
        var nodes = new Dictionary<string, ReferenceDocument>(StringComparer.Ordinal);
        using (var stream = OpenEntry(metadata.NodesEntry))
        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var row = JsonSerializer.Deserialize<ReferenceDocument>(line, JsonOptions)
                    ?? throw new InvalidDataException("内置节点为空。");
                if (row.Node.PackId != packId || row.Node.SectionId != sectionId || string.IsNullOrEmpty(row.Node.NodeId))
                    throw new InvalidDataException("内置节点身份不一致。");
                row.Node = row.Node with { Options = Array.AsReadOnly(row.Node.Options.ToArray()) };
                nodes.Add(row.Node.NodeId, row);
            }
        }
        if (nodes.Count != metadata.NodeCount) throw new InvalidDataException("内置小节节点数不一致。");
        // 开启新小节前淘汰旧小节，最多常驻当前章节的三份资料。
        while (Sections.Count >= MaxCachedSections && RecentSections.First is { } oldest)
        { Sections.Remove(oldest.Value); RecentSections.RemoveFirst(); }
        var section = new SectionCache(metadata, nodes);
        Sections.Add(sectionId, section); RecentSections.AddLast(sectionId);
        return section;
    }

    static Stream OpenEntry(string entry) => (Archive.Value.GetEntry(entry)
        ?? throw new InvalidDataException("内置资料条目缺失。")).Open();
    static T ReadJson<T>(string entry)
    {
        using var stream = OpenEntry(entry);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException("内置资料为空。");
    }
    static string ReadText(string entry)
    {
        using var stream = OpenEntry(entry);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    static bool IsDataError(Exception ex) => ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
    sealed class ReferenceIndex { public int Version { get; set; } public List<PackSummary> Packs { get; set; } = new(); }
    sealed class PackSummary
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string IndexEntry { get; set; } = "";
        public int SectionCount { get; set; }
        public int NodeCount { get; set; }
    }
    sealed class PackIndex { public string Id { get; set; } = ""; public string PreambleEntry { get; set; } = ""; public List<SectionIndex> Sections { get; set; } = new(); }
    sealed class SectionIndex
    {
        public string Id { get; set; } = "";
        public string TextEntry { get; set; } = "";
        public string NodesEntry { get; set; } = "";
        public int NodeCount { get; set; }
    }
    sealed class ReferenceDocument
    {
        public StoryReferenceNode Node { get; set; } = new();
        public List<OptionBinding> Bindings { get; set; } = new();
    }
    sealed class OptionBinding { public string OptionId { get; set; } = ""; public string Label { get; set; } = ""; }
    sealed class SectionCache(SectionIndex metadata, Dictionary<string, ReferenceDocument> nodes)
    {
        public SectionIndex Metadata { get; } = metadata;
        public Dictionary<string, ReferenceDocument> Nodes { get; } = nodes;
        public string? Text { get; set; }
    }
}

public sealed record StoryReferenceNode
{
    public string PackId { get; init; } = "";
    public string SectionId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public string ShortId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Speaker { get; init; } = "";
    public string Text { get; init; } = "";
    public string LocationText { get; init; } = "";
    public string DetailText { get; init; } = "";
    public IReadOnlyList<StoryReferenceOption> Options { get; init; } = Array.Empty<StoryReferenceOption>();
}

public sealed record StoryReferenceOption
{
    public int Number { get; init; }
    public string OptionId { get; init; } = "";
    public string Label { get; init; } = "";
    public string SourceMenuAction { get; init; } = "";
    public string TargetAction { get; init; } = "";
    public string FirstVisibleAction { get; init; } = "";
    public string FirstVisibleSpeaker { get; init; } = "";
    public string FirstVisibleText { get; init; } = "";
    public string MergeAction { get; init; } = "";
    public string TargetMechanism { get; init; } = "";
    public string ConditionRaw { get; init; } = "";
    public string StopReason { get; init; } = "";
    public string Movie { get; init; } = "";
    public string SourceHash { get; init; } = "";
}
