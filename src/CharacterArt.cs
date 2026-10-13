using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PgrVoice;

// 角色头像 / 立绘的本地查找（纯文件系统逻辑，不依赖 WPF，便于在 Linux 上测试）。
// 查找顺序：用户本机文件夹（指定 / 自动查找）优先，其次是程序内置图片包（ArtBundle），都没有时显示字标。
public static class CharacterArt
{
    public const string AvatarFolderName = "角色头像";
    public const string StandeeFolderName = "立绘";
    public const string AliasFileName = "aliases.json";
    public const string CoverFolderName = "章节封面";
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };
    static readonly char[] Separators = { '·', '・', '•', '｜', '|' };

    // 查找顺序：设置中指定的文件夹 → 配音总目录内的「角色头像」→ 配音总目录旁（同级）的「角色头像」→ %AppData%\PgrVoice\角色头像。
    // 章节封面使用同一查找链，只是文件夹名为「章节封面」。
    public static List<string> SearchFolders(string? configured, string? libraryRoot, string? appData, string folderName = AvatarFolderName)
    {
        var folders = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
            }
            catch (Exception) { }
        }
        Add(configured);
        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            string root = Path.TrimEndingDirectorySeparator(libraryRoot.Trim());
            Add(Path.Combine(root, folderName));
            try { if (Directory.GetParent(Path.GetFullPath(root)) is { } parent) Add(Path.Combine(parent.FullName, folderName)); }
            catch (Exception) { }
        }
        if (!string.IsNullOrWhiteSpace(appData)) Add(Path.Combine(appData, "PgrVoice", folderName));
        return folders;
    }

    // 把台词说话人转为可用文件名：去掉非法字符、首尾空白与句点；空名返回空串。
    public static string SanitizeName(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker)) return "";
        var invalid = new HashSet<char>("\\/:*?\"<>|".ToCharArray());
        var builder = new StringBuilder(speaker.Length);
        foreach (char c in speaker.Normalize(NormalizationForm.FormC))
            if (!char.IsControl(c) && !invalid.Contains(c)) builder.Append(c);
        return builder.ToString().Trim().Trim('.', ' ', '\u3000');
    }

    // 候选文件名（按优先级）：别名映射 → 完整名 → 去掉括号注释 → 「·」前的基础名。
    public static List<string> CandidateNames(string? speaker, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            string name = SanitizeName(value);
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        string raw = (speaker ?? "").Trim();
        if (raw.Length == 0) return names;
        if (aliases != null)
            foreach (string key in new[] { raw, SanitizeName(raw) })
                if (aliases.TryGetValue(key, out var mapped)) Add(mapped);
        Add(raw);
        string withoutNote = StripNote(raw);
        if (aliases != null && aliases.TryGetValue(withoutNote, out var mappedNote)) Add(mappedNote);
        Add(withoutNote);
        int dot = withoutNote.IndexOfAny(Separators);
        if (dot > 0)
        {
            string baseName = withoutNote[..dot].Trim();
            if (aliases != null && aliases.TryGetValue(baseName, out var mappedBase)) Add(mappedBase);
            Add(baseName);
        }
        return names;
    }

    static string StripNote(string value)
    {
        string text = value;
        foreach (var (open, close) in new[] { ('（', '）'), ('(', ')'), ('【', '】'), ('[', ']') })
        {
            int start = text.IndexOf(open);
            if (start >= 0) { int end = text.IndexOf(close, start); text = (end > start ? text.Remove(start, end - start + 1) : text[..start]).Trim(); }
        }
        return text;
    }

    // 读取 aliases.json：{ "别名": "文件名（不含扩展名）" }。格式错误时返回空表，不抛出。
    public static Dictionary<string, string> ReadAliases(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = Path.Combine(folder, AliasFileName);
            if (!File.Exists(file)) return result;
            using var stream = File.OpenRead(file);
            return ParseAliases(stream);
        }
        catch (Exception) { result.Clear(); }
        return result;
    }

    public static Dictionary<string, string> ParseAliases(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } target)
                    result[property.Name.Trim()] = Path.GetFileNameWithoutExtension(target.Trim());
        }
        catch (Exception) { result.Clear(); }
        return result;
    }

    // 缺图时显示的字标：旁白固定“旁”，否则取第一个字母/汉字（拉丁字母大写）。
    public static string Monogram(string? speaker)
    {
        string name = StripNote((speaker ?? "").Trim());
        if (name.Length == 0) return "声";
        if (name.StartsWith("旁白", StringComparison.Ordinal)) return "旁";
        foreach (var rune in name.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) return rune.ToString().ToUpperInvariant();
        return "声";
    }

    // 简短的“构造体编号”：由名称稳定散列生成，例如 CN-3F2A；仅作装饰。
    public static string Serial(string? speaker)
    {
        string name = SanitizeName(speaker);
        if (name.Length == 0) return "CN-0000";
        uint hash = 2166136261;
        foreach (char c in name) { hash ^= c; hash *= 16777619; }
        return "CN-" + (hash & 0xFFFF).ToString("X4");
    }
}

// 章节封面：标题拆分与候选文件名（纯逻辑）。
public static class ChapterCover
{
    static readonly System.Text.RegularExpressions.Regex Numbered = new(@"^\s*第\s*(?<n>\d+)\s*[章节话幕卷部]\s*[·\-_:：\s]*(?<name>.*)$");
    static readonly System.Text.RegularExpressions.Regex Leading = new(@"^\s*(?<n>(?:[A-Za-z]{1,3}-?)?\d+(?:-\d+)?)\s*[·\-_.:：\s]+(?<name>\S.*)$");

    // 「第43章 远信回响」→（43，远信回响）；「01 涂鸦艺术」→（01，涂鸦艺术）；无编号时编号为空。
    public static (string Number, string Name) Split(string? title)
    {
        string text = (title ?? "").Trim();
        if (text.Length == 0) return ("", "");
        var m = Numbered.Match(text);
        if (!m.Success) m = Leading.Match(text);
        if (m.Success)
        {
            string name = m.Groups["name"].Value.Trim();
            return (m.Groups["n"].Value, name.Length > 0 ? name : text);
        }
        return ("", text);
    }

    // 大号描边编号：纯数字补足两位（7 → 07），其他编号原样。
    public static string DisplayNumber(string? number)
    {
        string n = (number ?? "").Trim();
        return int.TryParse(n, out int value) && value >= 0 && value < 100 && n.Length < 2 ? value.ToString("00") : n;
    }

    // 候选（按优先级）：小节封面 → 章节全名 → 章节名 → 编号 → 包编号。每个候选先查 aliases.json。
    // 小节封面写作「章节名 小节名」「章节名-小节名」、章节同名子文件夹里的「小节名」，或小节编号；
    // 不单独使用小节名，避免「序章」等通用名在不同章节间串用。
    // packFolder：配音包文件夹名（如「主线第01章_MAIN01」），取「_」前的前缀与后缀编号；category 为空时按章节目录推断。
    public static List<string> CandidateNames(string? chapterTitle, string? chapterId, string? sectionTitle = null, string? sectionId = null,
        IReadOnlyDictionary<string, string>? aliases = null, string? packFolder = null, string? category = null)
    {
        var names = new List<string>();
        void Add(string? value, bool path = false, bool alias = true)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string name = path ? string.Join("/", value.Split('/').Select(CharacterArt.SanitizeName).Where(p => p.Length > 0)) : CharacterArt.SanitizeName(value);
            if (name.Length == 0) return;
            if (alias && aliases != null && aliases.TryGetValue(path ? value.Trim() : name, out var mapped)) Add(mapped, false, false);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        string full = (chapterTitle ?? "").Trim();
        var (number, name) = Split(full);
        var chapterNames = new List<string>();
        foreach (string c in new[] { full, name }) if (c.Length > 0 && !chapterNames.Contains(c)) chapterNames.Add(c);
        string section = (sectionTitle ?? "").Trim();
        int dot = section.IndexOf(" · ", StringComparison.Ordinal);
        var sectionNames = new List<string>();
        if (section.Length > 0) sectionNames.Add(section);
        if (dot > 0 && dot + 3 < section.Length) sectionNames.Add(section[(dot + 3)..].Trim());
        foreach (string s in sectionNames)
            foreach (string c in chapterNames)
            {
                Add(c + "/" + s, true);
                Add(c + " " + s); Add(c + "-" + s);
            }
        if (!string.IsNullOrWhiteSpace(sectionId)) Add(sectionId);
        string id = (chapterId ?? "").Trim();
        string shortId = id.StartsWith("voice-v2-", StringComparison.OrdinalIgnoreCase) ? id[9..] : id;
        // 配音包文件夹「主线第01章_MAIN01」：完整名、「_」前的前缀。
        string folder = (packFolder ?? "").Trim();
        int underscore = folder.LastIndexOf('_');
        string folderPrefix = underscore > 0 ? folder[..underscore].Trim() : "";
        if (underscore > 0 && shortId.Length == 0) shortId = folder[(underscore + 1)..].Trim();
        // 分类前缀：主线 / 外篇 / 间章……（去掉「剧情」后缀），用于「主线第35章_覆海归途」「间章_青鸟」「外篇EX00」等命名。
        string kind = (category ?? "").Trim();
        if (kind.Length == 0 && (full.Length > 0 || id.Length > 0)) { try { kind = ChapterCatalog.Category(id, full); } catch (Exception) { kind = ""; } }
        if (kind == ChapterCatalog.Other) kind = "";
        if (kind.EndsWith("剧情", StringComparison.Ordinal)) kind = kind[..^2];
        int mainNumber = -1;
        var idMatch = System.Text.RegularExpressions.Regex.Match(shortId, @"^(?:MAIN|pgr-ch)(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (idMatch.Success) { mainNumber = int.Parse(idMatch.Groups[1].Value); if (kind.Length == 0) kind = "主线"; }
        else if (int.TryParse(number, out int parsed) && kind == "主线") mainNumber = parsed;

        if (full.Length > 0) Add(full);
        // 带章节名的分类命名（「主线第35章_覆海归途」）比配音包前缀（「主线第35章」）更具体，先查。
        string chapterLabel = kind.Length > 0 && mainNumber >= 0 ? $"{kind}第{mainNumber:00}章" : "";
        if (chapterLabel.Length > 0 && name.Length > 0 && name != full) Add(chapterLabel + "_" + name);
        if (folderPrefix.Length > 0) Add(folderPrefix);
        if (folder.Length > 0) Add(folder);
        if (name == "序章" || full == "序章") Add("主线序章");
        if (kind.Length > 0)
        {
            if (chapterLabel.Length > 0) Add(chapterLabel);
            if (name.Length > 0) { Add(kind + "_" + name); Add(kind + name); }
            if (number.Length > 0 && !int.TryParse(number, out _)) Add(kind + number);
        }
        if (name.Length > 0) Add(name);
        if (number.Length > 0)
        {
            Add(number);
            if (int.TryParse(number, out int value)) { Add(value.ToString()); Add(value.ToString("00")); Add($"第{value}章"); }
        }
        if (id.Length > 0) Add(id);
        if (shortId.Length > 0) Add(shortId);
        return names;
    }
}

// 章节封面索引：根目录文件按名称、一级子文件夹内文件按「子文件夹/名称」登记；靠前的文件夹优先。
public sealed class ChapterCoverIndex
{
    readonly Dictionary<string, string> covers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<string> ExistingFolders { get; }
    public int Count => covers.Count + bundled.Keys.Count(k => !covers.ContainsKey(k));
    public IReadOnlyDictionary<string, string> Aliases => aliases;
    public static readonly ChapterCoverIndex Empty = new(Array.Empty<string>());

    // 内置封面（程序自带）：只在本机文件夹都没有命中时使用。
    readonly Dictionary<string, string> bundled = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> bundledAliases = new(StringComparer.OrdinalIgnoreCase);
    public int LocalCount => covers.Count;
    public int BundledCount => bundled.Count;

    public ChapterCoverIndex(IReadOnlyList<string> folders, ArtBundle? bundle = null) : this(folders)
    {
        if (bundle == null) return;
        foreach (var pair in aliases) bundledAliases[pair.Key] = pair.Value;
        foreach (var pair in bundle.Aliases(CharacterArt.CoverFolderName)) bundledAliases.TryAdd(pair.Key, pair.Value);
        foreach (var pair in bundle.Files(CharacterArt.CoverFolderName)) bundled.TryAdd(pair.Key, pair.Value);
    }

    public ChapterCoverIndex(IReadOnlyList<string> folders)
    {
        Folders = folders;
        var existing = new List<string>();
        foreach (string folder in folders)
        {
            try { if (!Directory.Exists(folder)) continue; } catch (Exception) { continue; }
            existing.Add(folder);
            foreach (var pair in CharacterArt.ReadAliases(folder)) aliases.TryAdd(pair.Key, pair.Value);
            CharacterArtIndex.ScanFolder(folder, covers, "");
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    CharacterArtIndex.ScanFolder(sub, covers, Path.GetFileName(sub).Trim() + "/");
            }
            catch (Exception) { }
        }
        ExistingFolders = existing;
    }

    public string? Find(string? chapterTitle, string? chapterId, string? sectionTitle = null, string? sectionId = null, string? packFolder = null, string? category = null)
    {
        if (covers.Count > 0)
            foreach (string name in ChapterCover.CandidateNames(chapterTitle, chapterId, sectionTitle, sectionId, aliases, packFolder, category))
                if (covers.TryGetValue(name, out var path)) return path;
        if (bundled.Count > 0)
            foreach (string name in ChapterCover.CandidateNames(chapterTitle, chapterId, sectionTitle, sectionId, bundledAliases, packFolder, category))
                if (bundled.TryGetValue(name, out var path)) return path;
        return null;
    }
}

// 一次扫描得到的索引：文件名（不含扩展名）→ 路径；先到先得，因此靠前的文件夹优先。
public sealed class CharacterArtIndex
{
    readonly Dictionary<string, string> avatars = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> standees = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<string> ExistingFolders { get; }
    // 内置头像（程序自带）：只在本机文件夹都没有命中时使用。
    readonly Dictionary<string, string> bundled = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> bundledAliases = new(StringComparer.OrdinalIgnoreCase);
    public int AvatarCount => avatars.Count + bundled.Keys.Count(k => !avatars.ContainsKey(k));
    public int LocalAvatarCount => avatars.Count;
    public int BundledAvatarCount => bundled.Count;
    public int StandeeCount => standees.Count;
    public IReadOnlyDictionary<string, string> Aliases => aliases;

    public static readonly CharacterArtIndex Empty = new(Array.Empty<string>());

    public CharacterArtIndex(IReadOnlyList<string> folders, ArtBundle? bundle = null)
    {
        Folders = folders;
        var existing = new List<string>();
        foreach (string folder in folders)
        {
            try { if (!Directory.Exists(folder)) continue; } catch (Exception) { continue; }
            existing.Add(folder);
            foreach (var pair in CharacterArt.ReadAliases(folder)) aliases.TryAdd(pair.Key, pair.Value);
            Scan(folder, avatars);
            Scan(Path.Combine(folder, CharacterArt.StandeeFolderName), standees);
        }
        ExistingFolders = existing;
        if (bundle == null) return;
        foreach (var pair in aliases) bundledAliases[pair.Key] = pair.Value;
        foreach (var pair in bundle.Aliases(CharacterArt.AvatarFolderName)) bundledAliases.TryAdd(pair.Key, pair.Value);
        foreach (var pair in bundle.Files(CharacterArt.AvatarFolderName)) bundled.TryAdd(pair.Key, pair.Value);
    }

    static void Scan(string folder, Dictionary<string, string> target) => ScanFolder(folder, target, "");
    internal static void ScanFolder(string folder, Dictionary<string, string> target, string prefix)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var files = Directory.EnumerateFiles(folder)
                .Where(f => CharacterArt.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Array.FindIndex(CharacterArt.Extensions, e => e.Equals(Path.GetExtension(f), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files) target.TryAdd(prefix + Path.GetFileNameWithoutExtension(file).Trim(), file);
        }
        catch (Exception) { }
    }

    public string? FindAvatar(string? speaker) => Find(avatars, speaker, aliases) ?? Find(bundled, speaker, bundledAliases);
    public string? FindStandee(string? speaker) => Find(standees, speaker, aliases);
    static string? Find(Dictionary<string, string> map, string? speaker, IReadOnlyDictionary<string, string> aliasMap)
    {
        if (map.Count == 0) return null;
        foreach (string name in CharacterArt.CandidateNames(speaker, aliasMap))
            if (map.TryGetValue(name, out var path)) return path;
        return null;
    }
}

// 程序内置图片包：zip 内「角色头像/名称.png」「章节封面/名称.jpg」及各自 aliases.json（由 tools/build_bundled_art.py 生成）。
// 条目路径以 PathPrefix 开头，与本机文件路径区分；读取时加锁（ZipArchive 不支持并发读）。
public sealed class ArtBundle
{
    public const string PathPrefix = "bundled:";
    readonly ZipArchive? zip;
    readonly object gate = new();
    public static readonly ArtBundle None = new(null);

    public ArtBundle(Stream? zipStream)
    {
        if (zipStream == null) return;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false); } catch (Exception) { zip = null; }
    }

    public bool IsEmpty => zip == null;
    public static bool IsBundled(string? path) => path != null && path.StartsWith(PathPrefix, StringComparison.Ordinal);

    // 「folderName/」下一级的图片：文件名（不含扩展名）→ 虚拟路径。同名时按 CharacterArt.Extensions 顺序取第一个。
    public Dictionary<string, string> Files(string folderName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (zip == null) return result;
        string prefix = folderName + "/";
        lock (gate)
        {
            var entries = zip.Entries
                .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.FullName.IndexOf('/', prefix.Length) < 0 && e.Length > 0)
                .Where(e => CharacterArt.Extensions.Contains(Path.GetExtension(e.FullName), StringComparer.OrdinalIgnoreCase))
                .OrderBy(e => Array.FindIndex(CharacterArt.Extensions, x => x.Equals(Path.GetExtension(e.FullName), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                result.TryAdd(Path.GetFileNameWithoutExtension(entry.Name).Trim(), PathPrefix + entry.FullName);
        }
        return result;
    }

    public Dictionary<string, string> Aliases(string folderName)
    {
        byte[]? data = Read(PathPrefix + folderName + "/" + CharacterArt.AliasFileName);
        if (data == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream(data);
        return CharacterArt.ParseAliases(stream);
    }

    public byte[]? Read(string path)
    {
        if (zip == null || !IsBundled(path)) return null;
        lock (gate)
        {
            try
            {
                var entry = zip.GetEntry(path[PathPrefix.Length..]);
                if (entry == null || entry.Length > 32L * 1024 * 1024) return null;
                using var source = entry.Open();
                var buffer = new MemoryStream((int)entry.Length);
                source.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception) { return null; }
        }
    }
}
