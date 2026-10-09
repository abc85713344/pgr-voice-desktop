using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PgrVoice;

public sealed class ChapterProgress
{
    public int Version { get; set; } = 1;
    public string PackId { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public string Summary { get; set; } = "";
    public NavigationSnapshot? Navigation { get; set; }
    public List<NavigationBookmark> Bookmarks { get; set; } = new();
}
// 每章独立文件；某章损坏不会使其他章节的进度丢失。
public sealed class ProgressStore
{
    // 后台保存和界面读取/书签操作共享缓存与备份保护状态。
    // Monitor 可重入，迁移与保存内部调用 Load/HasProgress 时仍在同一事务内。
    readonly object storeGate = new();
    readonly string directory;
    readonly Dictionary<string, ChapterProgress> documents = new(StringComparer.Ordinal);
    readonly HashSet<string> protectedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> readErrors = new(StringComparer.OrdinalIgnoreCase);
    bool loaded;
    string lastError = "";
    bool recoveredFromBackup;
    public string LastError { get => Volatile.Read(ref lastError); private set => Volatile.Write(ref lastError, value); }
    public bool RecoveredFromBackup { get => Volatile.Read(ref recoveredFromBackup); private set => Volatile.Write(ref recoveredFromBackup, value); }
    public ProgressStore(string directory) { this.directory = Path.GetFullPath(directory); }
    string FileFor(string packId) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packId)))[..24] + ".progress.json");
    static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json.Options), Json.Options)!;
    static ChapterProgress ReadDocument(string file)
    {
        var document = Json.Read<ChapterProgress>(file);
        if (document.Version != 1 || string.IsNullOrWhiteSpace(document.PackId) || document.Bookmarks == null ||
            document.Navigation != null && document.Navigation.PackId != document.PackId ||
            document.Bookmarks.Any(b => b == null || b.Snapshot == null || b.Snapshot.PackId != document.PackId || string.IsNullOrWhiteSpace(b.Id)))
            throw new InvalidDataException("章节存档内容无效");
        return document;
    }
    public void Load()
    {
        lock (storeGate) LoadCore();
    }
    void LoadCore()
    {
        documents.Clear(); protectedFiles.Clear(); readErrors.Clear(); LastError = ""; RecoveredFromBackup = false; loaded = true;
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.progress.json").Concat(
            Directory.EnumerateFiles(directory, "*.progress.json.bak").Select(p => p[..^4])).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                ChapterProgress document;
                try { document = ReadDocument(file); }
                catch when (File.Exists(file + ".bak")) { document = ReadDocument(file + ".bak"); RecoveredFromBackup = true; protectedFiles.Add(file); }
                // 只存书签的文档可以没有 Navigation；但若旧备份仍有进度，不能把
                // 主文件丢失导航的情况当作首次使用，再由旧版迁移覆盖这份有效备份。
                if (document.Navigation == null && File.Exists(file + ".bak"))
                {
                    try
                    {
                        var backup = ReadDocument(file + ".bak");
                        if (backup.PackId != document.PackId) throw new InvalidDataException("章节备份编号不符");
                        if (backup.Navigation != null)
                        {
                            document.Navigation = backup.Navigation; document.Summary = backup.Summary; document.UpdatedUtc = backup.UpdatedUtc;
                            RecoveredFromBackup = true; protectedFiles.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = "章节备份无法读取，已保留原文件：" + ex.Message;
                        readErrors[file] = LastError; protectedFiles.Add(file);
                    }
                }
                if (!String.Equals(FileFor(document.PackId), file, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("章节存档文件与编号不符");
                documents[document.PackId] = document;
            }
            catch (Exception ex) { LastError = "部分章节进度无法读取，已保留原文件：" + ex.Message; readErrors[file] = LastError; protectedFiles.Add(file); }
        }
    }
    void EnsureLoaded() { if (!loaded) Load(); }
    public bool HasProgress(string packId)
    {
        lock (storeGate)
        {
            EnsureLoaded();
            // 文件损坏也属于已有存档，不能自动当作首次使用并套入旧版唯一进度。
            return documents.TryGetValue(packId, out var d) ? d.Navigation != null || protectedFiles.Contains(FileFor(packId)) : File.Exists(FileFor(packId)) || File.Exists(FileFor(packId) + ".bak");
        }
    }
    public string GetSummary(Pack pack) => GetSummary(pack.Id);
    public string GetSummary(string packId)
    {
        lock (storeGate) { EnsureLoaded(); return documents.TryGetValue(packId, out var document) ? document.Summary : ""; }
    }
    public bool TryRestore(PlaybackEngine engine)
    {
        lock (storeGate) return TryRestoreCore(engine);
    }
    bool TryRestoreCore(PlaybackEngine engine)
    {
        EnsureLoaded();
        string file = FileFor(engine.Pack.Id);
        if (!documents.TryGetValue(engine.Pack.Id, out var document) || document.Navigation == null) { LastError = readErrors.GetValueOrDefault(file, ""); return false; }
        bool restored = engine.ImportNavigation(Copy(document.Navigation), allowBoundaryResume: true);
        LastError = restored ? "" : engine.NavigationError;
        if (!restored)
        {
            protectedFiles.Add(file);
            // 可解析的 JSON 也可能包含损坏的游标；有效备份仍可恢复，但不能跳过路线校验。
            try
            {
                var backup = ReadDocument(file + ".bak");
                if (backup.PackId == engine.Pack.Id && backup.Navigation != null && engine.ImportNavigation(Copy(backup.Navigation), allowBoundaryResume: true))
                {
                    // 仅回退损坏的导航；主文档仍可读的最新书签不属于这次损坏，不能一起抹去。
                    document.Navigation = backup.Navigation; document.Summary = backup.Summary; document.UpdatedUtc = backup.UpdatedUtc;
                    documents[engine.Pack.Id] = document; RecoveredFromBackup = true; LastError = ""; return true;
                }
            }
            catch { }
        }
        return restored;
    }
    public void Save(Pack pack, NavigationSnapshot snapshot)
    {
        lock (storeGate) SaveCore(pack, snapshot);
    }
    void SaveCore(Pack pack, NavigationSnapshot snapshot)
    {
        EnsureLoaded();
        if (snapshot.PackId != pack.Id || snapshot.Version is not (1 or 2)) throw new InvalidDataException("不能将其他章节进度保存到当前章");
        if (snapshot.Current.NodeId == null && HasProgress(pack.Id)) throw new InvalidDataException("尚未确认新的剧情位置，旧进度已保留。");
        var document = documents.TryGetValue(pack.Id, out var previous) ? Copy(previous) : new ChapterProgress { PackId = pack.Id };
        document.Navigation = Copy(snapshot); document.UpdatedUtc = DateTimeOffset.UtcNow;
        var node = snapshot.Current.NodeId == null ? null : pack.ById.GetValueOrDefault(snapshot.Current.NodeId);
        var section = pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == node?.SectionId);
        var line = node == null ? "尚未开始" : (string.IsNullOrWhiteSpace(node.Speaker) ? "" : node.Speaker + "：") + node.Text;
        if (line.Length > 42) line = line[..42] + "…";
        document.Summary = (section == null ? "" : section.Title + " · ") + line;
        Write(document);
    }
    void Write(ChapterProgress document)
    {
        string file = FileFor(document.PackId);
        try
        {
            if (protectedFiles.Contains(file))
            {
                string recovery = Path.Combine(directory, "recovery"); Directory.CreateDirectory(recovery);
                string name = Path.GetFileName(file) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..6];
                if (File.Exists(file)) File.Copy(file, Path.Combine(recovery, name + ".original"));
                if (File.Exists(file + ".bak")) File.Copy(file + ".bak", Path.Combine(recovery, name + ".backup"));
            }
            Json.Save(file, document, !protectedFiles.Contains(file)); documents[document.PackId] = document; protectedFiles.Remove(file); readErrors.Remove(file); LastError = "";
        }
        catch (Exception ex) { LastError = "本次章节进度尚未保存：" + ex.Message; throw; }
    }
    public bool MigrateLegacy(Pack pack, string? nodeId, Dictionary<string, string>? choices,
        HashSet<string>? facts, HashSet<string>? heard, int schema, List<Visit>? visits, int position)
    {
        lock (storeGate) return MigrateLegacyCore(pack, nodeId, choices, facts, heard, schema, visits, position);
    }
    bool MigrateLegacyCore(Pack pack, string? nodeId, Dictionary<string, string>? choices,
        HashSet<string>? facts, HashSet<string>? heard, int schema, List<Visit>? visits, int position)
    {
        EnsureLoaded();
        if (HasProgress(pack.Id)) return false;
        var engine = new PlaybackEngine(pack);
        engine.Restore(nodeId, choices, facts, heard, schema);
        if (nodeId != null && engine.CurrentId == null) { LastError = "旧进度位置已失效，请手动选择起始台词。"; return false; }
        if (schema == pack.SchemaVersion) engine.RestoreHistory(visits, position);
        var snapshot = engine.ExportNavigation();
        if (!engine.ValidateNavigation(snapshot)) { LastError = engine.NavigationError; return false; }
        Save(pack, snapshot); return true;
    }
    public IReadOnlyList<NavigationBookmark> Bookmarks(string packId)
    {
        lock (storeGate)
        {
            EnsureLoaded();
            return documents.TryGetValue(packId, out var document)
                ? document.Bookmarks.OrderByDescending(b => b.CreatedUtc).Select(Copy).ToList()
                : Array.Empty<NavigationBookmark>();
        }
    }
    public void SaveBookmark(string packId, NavigationBookmark bookmark)
    {
        lock (storeGate)
        {
            EnsureLoaded();
            if (bookmark.Snapshot.PackId != packId || string.IsNullOrWhiteSpace(bookmark.Id)) throw new InvalidDataException("书签与当前章节不符");
            var document = documents.TryGetValue(packId, out var previous) ? Copy(previous) : new ChapterProgress { PackId = packId };
            document.Bookmarks.RemoveAll(b => b.Id == bookmark.Id); document.Bookmarks.Add(Copy(bookmark)); Write(document);
        }
    }
    public void RemoveBookmark(string packId, string id)
    {
        lock (storeGate)
        {
            EnsureLoaded();
            if (!documents.TryGetValue(packId, out var previous)) return;
            var document = Copy(previous); document.Bookmarks.RemoveAll(b => b.Id == id); Write(document);
        }
    }
}
