using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice;

/// <summary>Verified story data and imported recordings are independent. Never writes a package.</summary>
public static class BundledPackCorrections
{
    const string Resource = "PgrVoice.PackCorrections.20261012.zip";
    static readonly Lazy<CorrectionIndex> Index = new(ReadIndex);
    static Stream Open() => typeof(BundledPackCorrections).Assembly.GetManifestResourceStream(Resource)
        ?? throw new InvalidDataException("内置剧情修正资源缺失。");
    static CorrectionIndex ReadIndex()
    {
        using var stream = Open(); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var input = (zip.GetEntry("index.json") ?? throw new InvalidDataException("缺少修正目录。" )).Open();
        var index = JsonSerializer.Deserialize<CorrectionIndex>(input, Json.Options) ?? throw new InvalidDataException("修正目录为空。");
        if (index.Version != 2 || index.Packs.Select(p => p.PackId).Distinct().Count() != index.Packs.Count)
            throw new InvalidDataException("修正目录无效。");
        return index;
    }

    public static Pack Apply(Pack original, byte[] originalBytes)
    {
        try
        {
            var entry = Index.Value.Packs.SingleOrDefault(p => p.PackId == original.Id);
            if (entry == null) return original;
            // All known chapter IDs use this exact-version policy, including unknown/new versions.
            // Do not fall through to an older section patch that could affect a newly imported package.
            original.BundledCorrectionHandled = true;
            string hash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
            if (entry.AfterSha256.Length != 64 || !entry.AfterSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("修正文件身份无效。");
            using var stream = Open(); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            using var input = (zip.GetEntry("packs/" + entry.AfterSha256 + ".json") ?? throw new InvalidDataException("修正文件缺失。")).Open();
            using var buffer = new MemoryStream(); input.CopyTo(buffer); byte[] bytes = buffer.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.AfterSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("修正文件校验失败。");
            var candidate = JsonSerializer.Deserialize<Pack>(bytes, Json.Options) ?? throw new InvalidDataException("修正文件为空。");
            if (candidate.Id != original.Id || candidate.SchemaVersion != original.SchemaVersion) throw new InvalidDataException("修正章节不匹配。");
            candidate.Root = original.Root;
            candidate.Validate();
            bool knownLegacy = entry.BeforeSha256.Contains(hash, StringComparer.Ordinal);
            // A package with unknown newer story content must not be silently downgraded.
            if (!knownLegacy && (original.StoryRevision != null && original.StoryRevision != candidate.StoryRevision ||
                original.Nodes.Any(n => n.Kind == "line" && !n.Archived &&
                    (!candidate.ById.TryGetValue(n.Id, out var target) || TextVersion(original.Id, n) != target.TextVersion))))
            {
                original.BundledCorrectionNotice = "配音包含有其他版本的剧情，已保留原包；请核对播放器更新。";
                return original;
            }
            int bound = 0, missing = 0, outdated = 0;
            foreach (var line in candidate.Nodes.Where(n => n.Kind == "line"))
            {
                if (line.TextVersion != TextVersion(candidate.Id, line)) throw new InvalidDataException("内置台词版本无效。");
                if (line.Audio != null) throw new InvalidDataException("内置剧情不应携带声音引用。");
                original.ById.TryGetValue(line.Id, out var source);
                bool match = source != null && !string.IsNullOrWhiteSpace(source.Audio) &&
                    (source.AudioTextVersion ?? TextVersion(original.Id, source)) == line.TextVersion;
                // A finalized text/display correction may explicitly retain the same recording.
                // Reuse that decision only for a byte-exact known legacy manifest, never arbitrary input.
                if (!match && knownLegacy && source?.Audio != null && source.AudioTextVersion == null &&
                    entry.CompatibleTextVersions.TryGetValue(line.Id, out var compatible))
                    match = compatible.Contains(TextVersion(original.Id, source), StringComparer.Ordinal);
                if (match && entry.RetiredAudio.TryGetValue(line.Id, out var retired))
                {
                    // Only known corrected sentences need a wave hash. Normal audio is not rehashed.
                    string? path = original.ResolveAudio(source!);
                    if (path != null && File.Exists(path))
                    {
                        using var sound = File.OpenRead(path);
                        match = !retired.Contains(Convert.ToHexString(SHA256.HashData(sound)).ToLowerInvariant(), StringComparer.Ordinal);
                    }
                }
                if (match)
                {
                    line.Audio = source!.Audio; line.AudioTextVersion = line.TextVersion;
                    line.AudioStatus = source.AudioStatus; line.AudioReason = source.AudioReason; bound++;
                }
                else if (line.AudioStatus is not ("not-spoken" or "subtitle-only"))
                {
                    line.AudioStatus = "draft-missing";
                    line.AudioReason = source?.Audio != null ? "正文已更新，待补配" : "待补配";
                    if (source?.Audio != null) outdated++;
                    if (!line.Archived) missing++;
                }
            }
            candidate.Delivery = original.Delivery;
            // Do not alias old navigation fingerprints: old decisions may describe the wrong route.
            // Existing progress validation reconciles stable IDs or requests a new position as before.
            candidate.BundledCorrectionHandled = true;
            candidate.BundledCorrectionNotice = $"已使用内置剧情 · 已匹配配音 {bound} 句 · 待补配 {missing} 句" +
                (outdated > 0 ? $"（{outdated} 句旧音与正文版本不符）" : "");
            CoreDiagnostics.Write("剧情修正", candidate.Id + "：" + candidate.BundledCorrectionNotice);
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            original.BundledCorrectionHandled = true;
            CoreDiagnostics.Write("剧情修正", "修正未应用，保留原包：" + ex.Message);
            return original;
        }
    }

    public static string TextVersion(string packId, Node node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "pgr-spoken-text-v1\n" + packId + "\n" + node.Id + "\n" + node.Speaker + "\n" + node.Text))).ToLowerInvariant();

    public sealed class CorrectionIndex
    {
        public int Version { get; set; }
        public List<CorrectionEntry> Packs { get; set; } = new();
    }
    public sealed class CorrectionEntry
    {
        public string PackId { get; set; } = "";
        public string AfterSha256 { get; set; } = "";
        public List<string> BeforeSha256 { get; set; } = new();
        public Dictionary<string, List<string>> RetiredAudio { get; set; } = new();
        public Dictionary<string, List<string>> CompatibleTextVersions { get; set; } = new();
    }
}
