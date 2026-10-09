using System.Text.Json;

namespace PgrVoice;

/// <summary>两端共用的章节分类。旧包按稳定编号匹配，不依赖安装路径或重新导入。</summary>
public static class ChapterCatalog
{
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[]
    {
        "主线", "浮点纪实", "外篇剧情", "间章剧情", "本我回廊", "黄金之涡", "空中花园",
        "多维演绎", "时宇漫纪", "联动", "好感剧情", "调色板战争", "边界公约"
    });
    public const string Other = "其他章节";
    sealed record Entry(string Id, string Title, string Category, int Order);
    static readonly Entry[] Entries = ReadEntries();
    static readonly Dictionary<string, Entry> ById = Entries.Where(e => e.Id.Length > 0)
        .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
    // 同名而分类不同的章节不能凭标题推断。
    static readonly Dictionary<string, Entry> ByTitle = Entries.GroupBy(e => Title(e.Title), StringComparer.Ordinal)
        .Where(g => g.Select(e => e.Category).Distinct().Count() == 1)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    static Entry[] ReadEntries()
    {
        using var stream = typeof(ChapterCatalog).Assembly.GetManifestResourceStream("PgrVoice.ChapterCatalog.json")
            ?? throw new InvalidOperationException("缺少章节分类目录。");
        return JsonSerializer.Deserialize<Entry[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public static string Title(string title)
    {
        var result = title.Trim();
        if (result == "第43章 远信回响（角色、情绪、路线整合）") result = "第43章 远信回响";
        foreach (var suffix in new[] { " · 试用配音包", " · 可用版", " · 自动检查版" })
            if (result.EndsWith(suffix, StringComparison.Ordinal)) result = result[..^suffix.Length].TrimEnd();
        return result.Replace('_', ' ');
    }

    static bool Number(string id, string prefix, out int number)
    {
        number = 0;
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id.AsSpan(prefix.Length), out number) && number >= 0;
    }

    static Entry Resolve(string id, string title)
    {
        if (Number(id, "voice-v2-MAIN", out var main) || Number(id, "pgr-ch", out main))
            return new(id, title, "主线", main);
        if (Number(id, "voice-v2-ER", out var extra)) return new(id, title, "浮点纪实", extra);
        if (ById.TryGetValue(id, out var entry)) return entry;
        if (id.StartsWith("voice-v2-", StringComparison.OrdinalIgnoreCase)
            && ById.TryGetValue(id[9..], out entry)) return entry;
        return ByTitle.TryGetValue(Title(title), out entry) ? entry : new(id, title, Other, int.MaxValue);
    }

    public static string Category(string id, string title) => Resolve(id, title).Category;
    public static int CategoryOrder(string category)
    {
        for (int i = 0; i < Categories.Count; i++) if (Categories[i] == category) return i;
        return Categories.Count;
    }
    public static string SortKey(string id, string title, string fallback = "")
    {
        var entry = Resolve(id, title);
        return $"{CategoryOrder(entry.Category):00}-{entry.Order:0000000000}-{Title(title)}-{fallback}";
    }
}
