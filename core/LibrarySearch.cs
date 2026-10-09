using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice;

// 搜索结果是只读快照。身份包括文件、内容指纹与节点，不按正文或包编号合并分支。
public sealed record LibrarySearchResult(string PackFile, string SourceHash, string PackId, string PackTitle,
    string ChapterId, string ChapterTitle, string SectionId, string SectionTitle, string NodeId,
    string PathId, string Route, string Speaker, string Text)
{
    public override string ToString() => $"{PackTitle} · {SectionTitle}\n{(Speaker.Length == 0 ? "旁白" : Speaker)}：{Text}\n{Route}";
}
public sealed record LibrarySearchReport(IReadOnlyList<LibrarySearchResult> Results, int TotalMatches,
    int SearchedPacks, IReadOnlyList<string> Problems);
public sealed record LibrarySearchProgress(int Completed, int Total);
public sealed record LibrarySearchTarget(Pack Pack, Chapter Chapter, Section Section, Node Node);

/// <summary>只读清单的跨包搜索；不访问音频、不构造播放会话、不保存路线。调用方提供当前可用包清单。</summary>
public sealed class LibrarySearchIndex
{
    sealed record Entry(LibrarySearchResult Result, string SearchText);
    sealed record Cached(long Length, DateTime Modified, Entry[] Entries);
    readonly Dictionary<string, Cached> cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task<LibrarySearchReport> SearchAsync(IEnumerable<string> packFiles, string query,
        CancellationToken cancellationToken = default, IProgress<LibrarySearchProgress>? progress = null,
        bool refresh = false, int maximumResults = 300)
    {
        if (maximumResults < 1 || maximumResults > 5000) throw new ArgumentOutOfRangeException(nameof(maximumResults));
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize)
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0) return new(Array.Empty<LibrarySearchResult>(), 0, 0, Array.Empty<string>());
        var files = packFiles.Select(Path.GetFullPath).Distinct(cache.Comparer).ToArray();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var results = new List<LibrarySearchResult>(); var problems = new List<string>();
                int total = 0, searched = 0;
                var available = files.ToHashSet(cache.Comparer);
                foreach (var old in cache.Keys.Where(p => !available.Contains(p)).ToArray()) cache.Remove(old);
                for (int i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string file = files[i];
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) throw new FileNotFoundException("配音包已移走");
                        if (refresh || !cache.TryGetValue(file, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc)
                        {
                            byte[] bytes = File.ReadAllBytes(file);
                            string hash = Convert.ToHexString(SHA256.HashData(bytes));
                            var pack = Parse(file, bytes);
                            var sections = pack.Chapters.SelectMany(c => c.Sections.Select(s => (Chapter: c, Section: s)))
                                .ToDictionary(x => x.Section.Id, StringComparer.Ordinal);
                            var routes = pack.Nodes.Where(n => !n.Archived && n.Kind == "choice")
                                .SelectMany(n => n.Options.Select(o => (n.SectionId, o.PathId, o.Label)))
                                .GroupBy(x => (x.SectionId, x.PathId)).ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(x => x.Label).Distinct()));
                            var entries = new List<Entry>();
                            foreach (var node in pack.Nodes)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (node.Archived || node.Kind != "line") continue;
                                var location = sections[node.SectionId];
                                string route = node.PathId.Length == 0 ? "未附分支编号；打开后按原路线确认" :
                                    "路线：" + routes.GetValueOrDefault((node.SectionId, node.PathId), node.PathId);
                                var result = new LibrarySearchResult(file, hash, pack.Id, pack.Title,
                                    location.Chapter.Id, location.Chapter.Title, node.SectionId, location.Section.Title,
                                    node.Id, node.PathId, route, node.Speaker, node.Text);
                                entries.Add(new(result, Normalize(node.Speaker + " " + node.Text)));
                            }
                            cached = new(info.Length, info.LastWriteTimeUtc, entries.ToArray()); cache[file] = cached;
                        }
                        searched++;
                        foreach (var entry in cached.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!terms.All(t => entry.SearchText.Contains(t, StringComparison.Ordinal))) continue;
                            total++; if (results.Count < maximumResults) results.Add(entry.Result);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                    { cache.Remove(file); problems.Add(Path.GetFileName(Path.GetDirectoryName(file)) + "：" + ex.Message); }
                    progress?.Report(new(i + 1, files.Length));
                }
                return new LibrarySearchReport(results, total, searched, problems);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // 确认时重读完整清单并核对哈希：同尺寸、同时间戳的替换也不能把旧结果定位到新内容。
    public static LibrarySearchTarget Resolve(LibrarySearchResult result)
    {
        byte[] bytes = File.ReadAllBytes(result.PackFile);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != result.SourceHash)
            throw new InvalidDataException("配音包已经更新，请重新读取配音库后搜索。");
        var pack = Parse(result.PackFile, bytes);
        var chapter = pack.Chapters.SingleOrDefault(c => c.Id == result.ChapterId);
        var section = chapter?.Sections.SingleOrDefault(s => s.Id == result.SectionId);
        if (pack.Id != result.PackId || section == null || !pack.ById.TryGetValue(result.NodeId, out var node) ||
            node.Archived || node.Kind != "line" || node.SectionId != result.SectionId || node.PathId != result.PathId ||
            node.Speaker != result.Speaker || node.Text != result.Text)
            throw new InvalidDataException("搜索结果已失效，请重新搜索。");
        return new(pack, chapter!, section, node);
    }
    static Pack Parse(string file, byte[] bytes)
    {
        var pack = JsonSerializer.Deserialize<Pack>(bytes, Json.Options) ?? throw new InvalidDataException("配音包为空");
        pack.Root = Path.GetDirectoryName(file)!; pack.Validate(); return pack;
    }
    static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))).ToUpperInvariant();
}
