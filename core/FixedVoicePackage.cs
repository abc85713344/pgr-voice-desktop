using System.Security.Cryptography;
using System.Text;

namespace PgrVoice;

// 固定角色声线的预留接入。清单只能描述音频，不能自行开启替换。
public sealed class FixedVoiceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string NavigationFingerprint { get; set; } = "";
    public List<FixedVoiceLine> Lines { get; set; } = new();
}

public sealed class FixedVoiceLine
{
    public string NodeId { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string TextSha256 { get; set; } = "";
    public string Audio { get; set; } = "";
}

public sealed class FixedVoicePackage
{
    public const string RelativeManifest = "character-voices/fixed-voices.json";
    readonly Pack owner;
    readonly Dictionary<string, (FixedVoiceLine Line, string Path)> lines;
    public bool Enabled { get; private set; }
    public int LineCount => lines.Count;
    public string Status => Enabled ? $"固定角色声线已开启 · {LineCount} 句" : $"固定角色声线已准备 · {LineCount} 句 · 未开启";

    FixedVoicePackage(Pack owner, Dictionary<string, (FixedVoiceLine, string)> lines)
    { this.owner = owner; this.lines = lines; }

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static FixedVoicePackage Load(Pack owner, string manifestPath)
    {
        var manifest = Json.Read<FixedVoiceManifest>(manifestPath);
        if (manifest.SchemaVersion != 1 || manifest.PackId != owner.Id ||
            manifest.NavigationFingerprint != PlaybackEngine.NavigationFingerprint(owner) || manifest.Lines == null || manifest.Lines.Count == 0)
            throw new InvalidDataException("角色声线包与当前章节版本不对应。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var result = new Dictionary<string, (FixedVoiceLine, string)>(StringComparer.Ordinal);
        var voices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in manifest.Lines)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.NodeId) || !owner.ById.TryGetValue(line.NodeId, out var node) ||
                node.Kind != "line" || node.Archived || line.Speaker != node.Speaker ||
                line.TextSha256 != TextHash(node.Text) || string.IsNullOrWhiteSpace(line.VoiceId))
                throw new InvalidDataException("角色声线台词的编号、角色或原文不对应。");
            if (voices.TryGetValue(line.Speaker, out var voice) && voice != line.VoiceId)
                throw new InvalidDataException("同一角色只能绑定一条固定声线。");
            voices[line.Speaker] = line.VoiceId;
            string audio = (line.Audio ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(audio) || audio.StartsWith('/') || audio.Contains(':') || Path.IsPathRooted(audio))
                throw new InvalidDataException("角色音频必须使用声线包内的相对路径。");
            string path = Path.GetFullPath(Path.Combine(directory, audio.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("角色音频路径越过声线包目录。");
            if (!File.Exists(path)) throw new InvalidDataException("角色声线包尚不完整，缺少音频。");
            if (!result.TryAdd(line.NodeId, (line, path))) throw new InvalidDataException("角色声线台词编号重复。");
        }
        return new(owner, result);
    }

    // 本版两端没有开启入口；留给角色包完成并验证后的接入流程显式调用。
    public void SetEnabled(bool enabled) => Enabled = enabled;

    public string? Resolve(Node node)
    {
        if (!Enabled || !owner.ById.TryGetValue(node.Id, out var actual) || !ReferenceEquals(actual, node) ||
            !lines.TryGetValue(node.Id, out var item) || item.Line.Speaker != node.Speaker ||
            item.Line.TextSha256 != TextHash(node.Text) || !File.Exists(item.Path)) return null;
        return item.Path;
    }
}
