using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.Listening;

/// <summary>独立听书文件。使用散列文件名，包编号不能越过目录；保存中断保留上一份完整备份。</summary>
public sealed class ListeningProgressStore
{
    static readonly ConcurrentDictionary<string, object> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    readonly string root;
    public ListeningProgressStore(string root) => this.root = Path.GetFullPath(root);

    public string GetPath(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("缺少配音包编号", nameof(packId));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packId))).ToLowerInvariant();
        return Path.Combine(root, "listening-" + key + ".json");
    }

    public ListeningProgressDocument Load(string packId) => Load(packId, out _);

    public ListeningProgressDocument Load(string packId, out string notice)
    {
        string path = GetPath(packId); notice = "";
        lock (Gates.GetOrAdd(path, _ => new()))
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    var document = Json.Read<ListeningProgressDocument>(candidate);
                    bool repaired = Normalize(document, packId);
                    if (candidate != path) notice = "听书存档已从备份恢复；请确认上次位置。";
                    if (repaired) notice += "听书存档的部分字段损坏，已修复可读记录；原文件保留，保存时会另存损坏原件。";
                    return document;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                { notice = "听书存档无法读取，已保留原文件；可重新选择收听位置。"; }
            }
            return new() { PackId = packId };
        }
    }

    public void Save(ListeningProgressDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Normalize(document, document.PackId);
        string path = GetPath(document.PackId);
        lock (Gates.GetOrAdd(path, _ => new()))
        {
            bool validPrevious = false;
            if (File.Exists(path))
                try { validPrevious = !Normalize(Json.Read<ListeningProgressDocument>(path), document.PackId); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { }
            // 先留原件，再替换坏主文件；即使重新启动后才保存，也不会丢失诊断原件。
            // 归档复制失败时让保存失败，原主文件与有效备份均保持不动。
            if (File.Exists(path) && !validPrevious)
            {
                string archive = path + ".unreadable-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                File.Copy(path, archive, overwrite: false);
            }
            // 不让语法正确但字段损坏的主文件覆盖有效备份。
            Json.Save(path, document, preservePrevious: validPrevious);
        }
    }

    /// <returns>是否修复过字段。修复后的对象可交给界面；原文件不能视为有效备份。</returns>
    static bool Normalize(ListeningProgressDocument document, string packId)
    {
        if (document == null || document.Version != 1 || string.IsNullOrWhiteSpace(packId) || document.PackId != packId || document.Chapters == null)
            throw new InvalidDataException("听书存档无效");
        bool repaired = false;
        foreach (var (chapterId, chapter) in document.Chapters.ToArray())
        {
            if (string.IsNullOrWhiteSpace(chapterId) || chapter == null)
            { document.Chapters.Remove(chapterId); repaired = true; continue; }
            if (chapter.Resume != null && !NormalizeSnapshot(chapter.Resume, packId, chapterId, ref repaired))
            { chapter.Resume = null; repaired = true; }
            if (chapter.Bookmarks == null) { chapter.Bookmarks = new(); repaired = true; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bookmark in chapter.Bookmarks.ToArray())
            {
                // 一条坏书签不应使同章其余有效书签一并退回旧备份。
                if (bookmark == null || !NormalizeSnapshot(bookmark.Snapshot, packId, chapterId, ref repaired))
                { chapter.Bookmarks.Remove(bookmark!); repaired = true; continue; }
                if (bookmark.Label == null) { bookmark.Label = "未命名书签"; repaired = true; }
                if (string.IsNullOrWhiteSpace(bookmark.Id) || !ids.Add(bookmark.Id))
                { bookmark.Id = Guid.NewGuid().ToString("N"); ids.Add(bookmark.Id); repaired = true; }
            }
        }
        return repaired;
    }

    static bool NormalizeSnapshot(ListeningSnapshot? snapshot, string packId, string chapterId, ref bool repaired)
    {
        if (snapshot == null || snapshot.Version != 1 || snapshot.PackId != packId || snapshot.ChapterId != chapterId || !Enum.IsDefined(snapshot.Policy))
            return false;
        if (snapshot.Fingerprint == null) { snapshot.Fingerprint = ""; repaired = true; }
        if (snapshot.SectionId == null) { snapshot.SectionId = ""; repaired = true; }
        if (snapshot.SectionTitle == null) { snapshot.SectionTitle = ""; repaired = true; }
        if (snapshot.Speaker == null) { snapshot.Speaker = ""; repaired = true; }
        if (snapshot.Text == null) { snapshot.Text = ""; repaired = true; }
        if (snapshot.LineNumber < 0) { snapshot.LineNumber = 0; repaired = true; }
        if (snapshot.PositionMs < 0) { snapshot.PositionMs = 0; repaired = true; }
        if (snapshot.Choices == null) { snapshot.Choices = new(); repaired = true; }
        foreach (var choice in snapshot.Choices.Where(p => string.IsNullOrEmpty(p.Key) || p.Value == null).ToArray())
        { snapshot.Choices.Remove(choice.Key); repaired = true; }
        return true;
    }
}
