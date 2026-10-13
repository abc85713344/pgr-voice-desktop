using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

/// <summary>安装包携带的已核实路线差量；只改本次加载的内存视图，不写用户章节或音频。</summary>
public static class BundledRouteRepairs
{
    const string ResourceName = "PgrVoice.RouteRepairs.20261009.json.gz";
    static readonly JsonSerializerOptions CanonicalJson = new(Json.Options) { NewLine="\n" };
    static readonly Lazy<RouteRepairCatalog> Catalog = new(() =>
    {
        using var stream = typeof(BundledRouteRepairs).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("内置路线修正资源缺失。");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<RouteRepairCatalog>(gzip, Json.Options)
            ?? throw new InvalidDataException("内置路线修正资源为空。");
    });

    public static Pack Apply(Pack original)
    {
        try { return Apply(original, Catalog.Value); }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
        { CoreDiagnostics.Write("路线兼容", "保留原配音包：" + ex.Message); return original; }
    }

    public static Pack Apply(Pack original, RouteRepairCatalog catalog)
    {
        if (catalog.Version != 1 || original.SchemaVersion != 3) return original;
        var repair = catalog.Packs.SingleOrDefault(p => p.PackId == original.Id);
        if (repair == null) return original;
        var matched = repair.Sections.Where(s => s.BeforeSignatures.Contains(SectionSignature(original, s.SectionId), StringComparer.Ordinal)).ToList();
        string before = PlaybackEngine.NavigationFingerprint(original);
        if (matched.Count == 0 && !repair.CurrentFingerprints.Contains(before, StringComparer.Ordinal)) return original;
        // 在完整副本上验证所有改动；任何一处失败都保留原对象，避免半套路线。
        var candidate = Clone(original); candidate.Root = original.Root;
        foreach (var section in matched)
        {
            var targets = section.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var current = candidate.Nodes.Where(n => n.SectionId == section.SectionId).ToArray();
            if (current.Any(n => !targets.ContainsKey(n.Id))) throw new InvalidDataException("路线修正不能删除原节点。");
            foreach (var node in current)
            {
                var target = targets[node.Id];
                if (node.Kind != target.Kind || node.Text != target.Text || node.Speaker != target.Speaker)
                    throw new InvalidDataException("路线修正不能替换原台词。");
                node.Archived=target.Archived; node.PathId=target.PathId; node.NextId=target.NextId;
                node.MenuType=target.MenuType; node.CompleteRoute=target.CompleteRoute;
                node.SetFacts=new(target.SetFacts); node.ResumeMenuIds=new(target.ResumeMenuIds);
                node.MenuNavigationEvidence=new(target.MenuNavigationEvidence); node.Options=Clone(target.Options);
            }
            foreach (var target in section.Nodes.Where(n => !current.Any(c => c.Id == n.Id)))
            {
                if (target.Kind != "merge" || target.Audio != null || target.Speaker.Length != 0 || target.Options.Count != 0)
                    throw new InvalidDataException("路线修正只能补入无音频汇合节点。");
                candidate.Nodes.Add(Clone(target));
            }
        }
        string after = PlaybackEngine.NavigationFingerprint(candidate);
        candidate.CompatibleNavigationFingerprints = candidate.CompatibleNavigationFingerprints.Append(before)
            .Where(f => !f.Equals(after, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        candidate.NavigationResumeRepairs.RemoveAll(r => r.FromFingerprint.Equals(after, StringComparison.OrdinalIgnoreCase));
        foreach (var section in matched)
        foreach (var rule in section.ResumeRepairs)
        {
            var alias = Clone(rule); alias.FromFingerprint=before;
            if (!candidate.NavigationResumeRepairs.Any(r => r.FromFingerprint==before && r.BoundaryId==alias.BoundaryId && r.TargetId==alias.TargetId && r.PreviousNodeIds.SequenceEqual(alias.PreviousNodeIds)))
                candidate.NavigationResumeRepairs.Add(alias);
        }
        // 发布资料的旧指纹来自 Windows；Android 的历史 JSON 换行不同。
        // 仅在整张图精确等于已核实新版时，补充同一旧图的两种平台指纹。
        if(repair.CurrentFingerprints.Contains(after,StringComparer.Ordinal))
        {
            candidate.CompatibleNavigationFingerprints=candidate.CompatibleNavigationFingerprints.Concat(repair.CompatibleFingerprints)
                .Where(f=>!f.Equals(after,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var rule in repair.ResumeRepairs)
                if(rule.FromFingerprint!=after && !candidate.NavigationResumeRepairs.Any(r=>r.FromFingerprint==rule.FromFingerprint && r.BoundaryId==rule.BoundaryId && r.TargetId==rule.TargetId && r.PreviousNodeIds.SequenceEqual(rule.PreviousNodeIds)))
                    candidate.NavigationResumeRepairs.Add(Clone(rule));
        }
        if(after==before && JsonSerializer.Serialize(candidate.CompatibleNavigationFingerprints,Json.Options)==JsonSerializer.Serialize(original.CompatibleNavigationFingerprints,Json.Options)
            && JsonSerializer.Serialize(candidate.NavigationResumeRepairs,Json.Options)==JsonSerializer.Serialize(original.NavigationResumeRepairs,Json.Options))return original;
        candidate.Validate();
        // 保留加载时已验证的固定声线对象及其原文件绑定，正文/音频引用没有变化。
        original.Nodes=candidate.Nodes;
        original.CompatibleNavigationFingerprints=candidate.CompatibleNavigationFingerprints;
        original.NavigationResumeRepairs=candidate.NavigationResumeRepairs;
        original.Validate();
        CoreDiagnostics.Write("路线兼容", $"{original.Id}：已应用内置路线兼容（{matched.Count} 个小节），原配音文件保留。");
        return original;
    }

    public static string SectionSignature(Pack pack, string sectionId)
    {
        var section=pack.Chapters.SelectMany(c=>c.Sections).SingleOrDefault(s=>s.Id==sectionId);
        if(section==null)return "";
        var subset=new Pack { Id=pack.Id, SchemaVersion=pack.SchemaVersion,
            Chapters=new(){new Chapter{Id="section",Sections=new(){new Section{Id=section.Id,StartId=section.StartId}}}},
            Nodes=pack.Nodes.Where(n=>n.SectionId==sectionId).ToList() };
        // 音频文件名、补音状态和证据注释不参与匹配；所有连接、条件、选项顺序、正文与角色必须一致。
        var identity=new { Graph=FingerprintForNewLine(subset,"\n"),
            Content=subset.Nodes.OrderBy(n=>n.Id,StringComparer.Ordinal).Select(n=>new {n.Id,n.Text,n.Speaker,
                Options=n.Options.Select(o=>new{o.Id,o.Label})}) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity,CanonicalJson))));
    }

    // Catalog v1 明确冻结的导航身份字段。构建检查将两种换行逐包对照共享核心，不能据此修改全局存档格式。
    public static string FingerprintForNewLine(Pack pack,string newLine)
    {
        var graph=new {pack.Id,pack.SchemaVersion,
            Chapters=pack.Chapters.Select(c=>new{c.Id,Sections=c.Sections.Select(s=>new{s.Id,s.StartId})}),
            Nodes=pack.Nodes.OrderBy(n=>n.Id,StringComparer.Ordinal).Select(n=>new{
                n.Id,n.Kind,n.Archived,n.SectionId,n.PathId,n.NextId,n.MenuType,n.CompleteRoute,n.SetFacts,n.ResumeMenuIds,
                Options=n.Options.Select(o=>new{o.Id,o.PathId,o.TargetId,o.ReturnId,o.MergeId,o.Verified,o.BodyVerified,o.ExitVerified,o.SegmentIds,o.BoundaryId,o.Requires,o.Excludes,o.LineIds})})};
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(graph,new JsonSerializerOptions(Json.Options){NewLine=newLine}))));
    }

    static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,Json.Options),Json.Options)!;
}

public sealed class RouteRepairCatalog
{
    public int Version { get; set; } = 1;
    public List<RouteRepairPack> Packs { get; set; } = new();
}
public sealed class RouteRepairPack
{
    public string PackId { get; set; } = "";
    public List<RouteRepairSection> Sections { get; set; } = new();
    public List<string> CurrentFingerprints { get; set; } = new();
    public List<string> CompatibleFingerprints { get; set; } = new();
    public List<NavigationResumeRepair> ResumeRepairs { get; set; } = new();
}
public sealed class RouteRepairSection
{
    public string SectionId { get; set; } = "";
    public List<string> BeforeSignatures { get; set; } = new();
    public List<Node> Nodes { get; set; } = new();
    public List<NavigationResumeRepair> ResumeRepairs { get; set; } = new();
}
