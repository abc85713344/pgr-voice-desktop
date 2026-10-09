using System.Text.Json;

namespace PgrVoice;

// 这些记录证明游戏配置引用了原声且核验时本地有资源，不证明当前游戏已开启或正在播放原声。
public sealed record NativeVoiceEntry(string PackId, string NodeId, string SectionId, string PathId,
    string Text, string? RawText, string Speaker, string PackSha256, string CueId, string EvidenceId,
    string MovieId, string ActionId, string TextField, IReadOnlyList<string> ConfiguredLanguages,
    IReadOnlyList<string> LocalResourceLanguages);
public sealed record NativeVoiceStatistics(int ConfiguredEntries, int Packs, int ExcludedCandidates,
    int RejectedEntries, int AmbiguousIdentities);

/// <summary>未来游戏适配层必须逐项提供当前实测状态；null 表示未知，不能替代为默认安全值。</summary>
public sealed record NativeVoiceRuntimeContext(string GameVersion, string Language, string PackSha256,
    bool IsGameFollow, bool? GameVoiceEnabled, bool? GameVoiceMuted, bool? SpeedingUp,
    bool? CurrentCueAudible, string? CurrentCueId);
/// <summary>两端共用的台词标记策略：定位层已确认具体节点，用户已选择使用游戏原声；不要求读取实时音轨。</summary>
public sealed record NativeVoiceLineContext(string GameVersion, string Language, string PackSha256,
    bool IsGameFollow, bool CurrentLineConfirmed, bool GameVoiceEnabled);
public sealed record NativeVoiceDecision(bool CanSkip, string Reason, NativeVoiceEntry? ConfiguredEntry);

/// <summary>原声避让的只读数据与预留门控。本版没有调用端或界面开关，实例始终默认关闭。</summary>
public sealed class NativeVoiceCatalog
{
    public const string ResourceName = "PgrVoice.NativeVoiceCatalog.json";
    readonly Dictionary<(string PackId, string NodeId), NativeVoiceEntry> entries;
    readonly Dictionary<string, int> packCounts;
    public string SourceVersion { get; }
    public bool Enabled { get; private set; }
    public NativeVoiceStatistics Statistics { get; }
    public string Status => $"原声配置 {SourceVersion} · {Statistics.ConfiguredEntries} 句 · " +
        (Enabled ? "已允许按已确认台词核对原声标记" : "未开启避让");

    NativeVoiceCatalog(string version, Dictionary<(string, string), NativeVoiceEntry> entries,
        int candidates, int rejected, int ambiguous)
    {
        SourceVersion = version; this.entries = entries;
        packCounts = entries.Values.GroupBy(e => e.PackId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Statistics = new(entries.Count, packCounts.Count, candidates, rejected, ambiguous);
    }
    public static NativeVoiceCatalog LoadEmbedded()
    {
        using var stream = typeof(NativeVoiceCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("没有预装原声配置清单。");
        return Load(stream);
    }
    public static NativeVoiceCatalog Load(string path)
    {
        using var stream = File.OpenRead(path); return Load(stream);
    }
    public static NativeVoiceCatalog Load(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("enabledByDefault").GetBoolean() ||
            Get(root, "scope") is not ("game-follow" or "desktop-game-follow-only") || string.IsNullOrWhiteSpace(Get(root, "sourceVersion")))
            throw new InvalidDataException("原声清单格式、默认开关或适用范围不支持。");
        string version = Get(root, "sourceVersion")!;
        var entries = new Dictionary<(string, string), NativeVoiceEntry>(); var ambiguous = new HashSet<(string, string)>();
        int candidates = 0, rejected = 0;
        foreach (var row in root.GetProperty("entries").EnumerateArray())
        {
            if (Get(row, "status") != "confirmed") { candidates++; continue; }
            string? packId = Get(row, "packId"), nodeId = Get(row, "nodeId"), sectionId = Get(row, "sectionId"), pathId = Get(row, "pathId"),
                text = Get(row, "text"), speaker = Get(row, "speaker"), sha = Get(row, "packSha256"), cue = Get(row, "cueId"), evidence = Get(row, "evidenceId"),
                movie = Get(row, "movieId"), action = Get(row, "actionId"), field = Get(row, "textField");
            var configured = Languages(row, "configuredLanguages"); var local = Languages(row, "localResourceLanguages");
            if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(sectionId) ||
                pathId == null || string.IsNullOrWhiteSpace(text) || speaker == null || !HashIsValid(sha) ||
                string.IsNullOrWhiteSpace(cue) || string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(movie) ||
                string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(field) ||
                !row.TryGetProperty("rawText", out var rawText) || rawText.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || configured.Length == 0 || local.Length == 0 ||
                !local.All(language => configured.Contains(language, StringComparer.Ordinal)) ||
                !row.TryGetProperty("autoSkipDefault", out var autoSkip) || autoSkip.ValueKind != JsonValueKind.False ||
                !row.TryGetProperty("runtimeVerified", out var runtimeVerified) || runtimeVerified.ValueKind != JsonValueKind.False)
            { rejected++; continue; }
            var key = (packId, nodeId);
            if (ambiguous.Contains(key)) { rejected++; continue; }
            if (entries.ContainsKey(key)) { entries.Remove(key); ambiguous.Add(key); rejected += 2; continue; }
            entries.Add(key, new(packId, nodeId, sectionId, pathId, text, Get(row, "rawText"), speaker, sha!, cue, evidence,
                movie, action, field, Array.AsReadOnly(configured), Array.AsReadOnly(local)));
        }
        return new(version, entries, candidates, rejected, ambiguous.Count);
    }
    static string? Get(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string[] Languages(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    static bool HashIsValid(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);

    /// <summary>仅供统计和查阅，不代表当前可跳过。不能使用正文相似度或同文节点替代精确身份。</summary>
    public NativeVoiceEntry? FindConfiguredVoice(string packId, string nodeId) => entries.GetValueOrDefault((packId, nodeId));
    public int CountConfiguredVoices(string packId) => packCounts.GetValueOrDefault(packId);
    public IReadOnlyDictionary<string, int> ConfiguredCountsByPack() => new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(packCounts, StringComparer.Ordinal));

    /// <summary>仅未来适配流程明确调用；只允许清单核准版本。Enable 本身绝不表示可以跳过。</summary>
    public bool Enable(string verifiedGameVersion)
    { Enabled = string.Equals(verifiedGameVersion, SourceVersion, StringComparison.Ordinal); return Enabled; }
    public void Disable() => Enabled = false;

    // 兼容读取早期调查清单的旧scope；数据来源是PC，不代表使用方只能是PC。
    // 此策略可由桌面定位或安卓OCR/人工确认后的节点共同调用，本版仍未接播放阻断。
    public NativeVoiceDecision EvaluateConfirmedLine(Pack? pack, Node? node, NativeVoiceLineContext? context)
    {
        if (pack == null || node == null || context == null) return new(false, "尚未取得当前包、节点或跟随状态。", null);
        var entry = FindConfiguredVoice(pack.Id, node.Id);
        NativeVoiceDecision No(string reason) => new(false, reason, entry);
        if (!Enabled) return No("原声避让尚未开启。");
        if (!context.IsGameFollow) return No("只用于游戏跟随，不作用于离线听书或试听。");
        if (!context.CurrentLineConfirmed) return No("尚未确认具体章节、小节及当前台词。");
        if (!context.GameVoiceEnabled) return No("尚未选择使用游戏原声。");
        if (context.GameVersion != SourceVersion || entry == null) return No("此版本或节点没有确认的原声配置。");
        if (!MatchesIdentity(pack, node, entry, context.PackSha256)) return No("配音包、节点、路线、角色或正文不对应。");
        if (!entry.ConfiguredLanguages.Contains(context.Language, StringComparer.Ordinal)) return No("没有当前语言的原声配置。");
        return new(true, "已确认当前台词命中原声标记；按用户的游戏原声设置避让外部配音，不代表已检测到实时声音。", entry);
    }

    static bool MatchesIdentity(Pack pack, Node node, NativeVoiceEntry entry, string sha) =>
        pack.ById.TryGetValue(node.Id, out var actual) && ReferenceEquals(actual, node) && !node.Archived && node.Kind == "line" &&
        node.SectionId == entry.SectionId && node.PathId == entry.PathId && node.Text == entry.Text && node.RawText == entry.RawText && node.Speaker == entry.Speaker &&
        HashIsValid(sha) && string.Equals(sha, entry.PackSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>本版没有实时调用方。任何缺失证据、未知状态或非游戏跟随场景均不能跳过。</summary>
    public NativeVoiceDecision CanSkip(Pack? pack, Node? node, NativeVoiceRuntimeContext? context)
    {
        if (pack == null || node == null || context == null) return new(false, "尚未取得当前包、节点或游戏状态。", null);
        var entry = FindConfiguredVoice(pack.Id, node.Id);
        NativeVoiceDecision No(string reason) => new(false, reason, entry);
        if (!Enabled) return No("原声避让尚未开启。");
        if (!context.IsGameFollow) return No("只适用于游戏跟随，不作用于听书。");
        if (context.GameVersion != SourceVersion) return No("游戏版本与原声配置不一致。");
        if (entry == null) return No("此节点没有确认的原声配置。");
        if (!MatchesIdentity(pack, node, entry, context.PackSha256))
            return No("配音包、节点、路线、角色或正文不对应。");
        if (!entry.ConfiguredLanguages.Contains(context.Language, StringComparer.Ordinal) || !entry.LocalResourceLanguages.Contains(context.Language, StringComparer.Ordinal))
            return No("没有当前语言的已核原声资源。");
        if (context.GameVoiceEnabled != true || context.GameVoiceMuted != false || context.SpeedingUp != false)
            return No("当前游戏原声开关、静音或加速状态未满足条件。");
        if (context.CurrentCueAudible != true || context.CurrentCueId != entry.CueId)
            return No("尚未确认当前正在播放对应原声。");
        return new(true, "配置与调用方提供的当前原声证据一致；仅可避免重复AI配音，不得推进游戏。", entry);
    }
}
