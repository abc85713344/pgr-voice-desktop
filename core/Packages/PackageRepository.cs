using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.Packages;

public sealed class PackageImportLimits
{
    public int MaximumEntries { get; init; } = 150_000;
    public long MaximumArchiveBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public long MaximumEntryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumManifestBytes { get; init; } = 64L * 1024 * 1024;
}

public sealed record PackageImportProgress(string Stage, int CompletedFiles, int TotalFiles, long Bytes);
public sealed record InstalledPackage(string PackId, string Title, int SchemaVersion, string PackFile,
    string NavigationFingerprint, string Revision, DateTimeOffset ImportedUtc,
    bool IsUpdate = false, bool NavigationChanged = false);

/// <summary>
/// ZIP 可以来自 SAF 的非 seekable 流。先流式写入磁盘，再解压、校验；
/// 每次导入生成不可变 revision，唯一提交点是原子替换 current.json。
/// 原包与 ProgressStore 始终独立，失败、取消或进程退出不会覆盖原包或进度。
/// </summary>
public sealed partial class PackageRepository
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    readonly string root;
    readonly PackageImportLimits limits;
    readonly SemaphoreSlim gate;
    public PackageRepository(string root, PackageImportLimits? limits = null)
    {
        this.root = Path.GetFullPath(root);
        this.limits = limits ?? new();
        gate = Gates.GetOrAdd(this.root, _ => new(1, 1));
        Directory.CreateDirectory(this.root);
    }

    static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    string Folder(string id) => Path.Combine(root, Key(id));
    string CurrentFile(string id) => Path.Combine(Folder(id), "current.json");

    public IReadOnlyList<InstalledPackage> List()
    {
        var result = new List<InstalledPackage>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).Length != 64) continue;
            try { var pointer = ReadPointer(directory); result.Add(Resolve(pointer, directory)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { CoreDiagnostics.Write("package", "保留无法读取的配音包：" + ex.Message); }
        }
        return result.OrderBy(p => ChapterCatalog.SortKey(p.PackId, p.Title), StringComparer.Ordinal).ToList();
    }

    public InstalledPackage? Find(string packId)
    {
        string directory = Folder(packId);
        if (!Directory.Exists(directory)) return null;
        var pointer = ReadPointer(directory);
        if (pointer.PackId != packId) throw new InvalidDataException("章节索引编号不符。");
        return Resolve(pointer, directory);
    }

    public Pack Load(string packId) => Pack.Load(Find(packId)?.PackFile ?? throw new FileNotFoundException("尚未导入这个章节。"));

    /// <summary>启动时清理上次被系统中止的导入临时文件。不会清理已发布 revision。</summary>
    public void CleanAbandonedImports()
    {
        if (!gate.Wait(0)) return;
        try
        {
            foreach (string area in new[] { ".incoming", ".deleted" })
            {
                string incoming = Path.Combine(root, area);
                if (!Directory.Exists(incoming)) continue;
                foreach (string directory in Directory.EnumerateDirectories(incoming))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                    try { Directory.Delete(directory, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { CoreDiagnostics.Write("package", "临时章节文件清理失败，已保留：" + ex.Message); }
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<InstalledPackage> ImportAsync(Stream zipStream, CancellationToken cancellationToken = default,
        IProgress<PackageImportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string incoming = Path.Combine(root, ".incoming", Guid.NewGuid().ToString("N"));
        string payload = Path.Combine(incoming, "payload");
        try
        {
            Directory.CreateDirectory(payload);
            string archiveFile = Path.Combine(incoming, "source.zip");
            long lastReport = Environment.TickCount64;
            progress?.Report(new("复制 ZIP", 0, 0, 0));
            using (var output = new FileStream(archiveFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await CopyBoundedAsync(zipStream, output, limits.MaximumArchiveBytes, cancellationToken,
                    bytes =>
                    {
                        long now = Environment.TickCount64;
                        if (now - lastReport >= 250) { lastReport = now; progress?.Report(new("复制 ZIP", 0, 0, bytes)); }
                    }).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            long totalBytes = 0;
            using (var zip = ZipFile.OpenRead(archiveFile))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > limits.MaximumEntries) throw new InvalidDataException("ZIP 文件数量为空或超过限制。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new List<(ZipArchiveEntry Entry, string Name)>();
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = SafeRelativePath(entry.FullName);
                    if (!names.Add(name)) throw new InvalidDataException("ZIP 包含重名或大小写冲突路径：" + name);
                    int unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixKind == 0xA000 || (entry.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("ZIP 不支持符号链接。");
                    bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (isDirectory) continue;
                    if (entry.Length < 0 || entry.Length > limits.MaximumEntryBytes || entry.Length > limits.MaximumExpandedBytes - totalBytes)
                        throw new InvalidDataException("ZIP 解压大小超过限制。");
                    totalBytes += entry.Length;
                    files.Add((entry, name));
                }
                long written = 0; int completed = 0;
                foreach (var (entry, name) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string file = ContainedPath(payload, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    using var input = entry.Open();
                    using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    long copied = await CopyBoundedAsync(input, output, Math.Min(entry.Length, limits.MaximumExpandedBytes - written), cancellationToken).ConfigureAwait(false);
                    if (copied != entry.Length) throw new InvalidDataException("ZIP 文件长度与目录不符：" + name);
                    written += copied;
                    completed++;
                    long now = Environment.TickCount64;
                    if (completed == 1 || completed == files.Count || now - lastReport >= 250)
                    { lastReport = now; progress?.Report(new("解压章节", completed, files.Count, written)); }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var manifests = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                .Where(p => string.Equals(Path.GetFileName(p), "pack.json", StringComparison.Ordinal)).ToArray();
            if (manifests.Length != 1) throw new InvalidDataException("一个章节 ZIP 必须且只能包含一个 pack.json。");
            if (new FileInfo(manifests[0]).Length > limits.MaximumManifestBytes) throw new InvalidDataException("章节剧情清单大小超过限制。");
            progress?.Report(new("校验剧情和音频", 0, 0, totalBytes));
            var pack = Pack.Load(manifests[0]);
            foreach (var node in pack.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Audio == null) continue;
                string audio = pack.ResolveAudio(node)!;
                if (!File.Exists(audio)) throw new InvalidDataException("配音包引用的音频文件缺失：" + node.Audio);
            }
            string fingerprint = PlaybackEngine.NavigationFingerprint(pack);
            string directory = Folder(pack.Id);
            PackagePointer? previous = null;
            if (File.Exists(Path.Combine(directory, "current.json")) || File.Exists(Path.Combine(directory, "current.json.bak"))) previous = ReadPointer(directory);
            string revision = Guid.NewGuid().ToString("N");
            string destination = Path.Combine(directory, "revisions", revision);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var pointer = new PackagePointer
            {
                PackId = pack.Id, Title = pack.Title, SchemaVersion = pack.SchemaVersion,
                ManifestRelativePath = Path.GetRelativePath(payload, manifests[0]).Replace('\\', '/'),
                NavigationFingerprint = fingerprint, Revision = revision, ImportedUtc = DateTimeOffset.UtcNow
            };
            // 取消只在提交前生效，提交之后返回成功，避免界面误报“取消”但索引已经更新。
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(payload, destination);
            string current = CurrentFile(pack.Id), temporary = current + "." + revision + ".tmp";
            try
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(pointer, Json.Options);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(data); stream.Flush(true); }
                if (previous != null)
                {
                    string backup = current + ".bak", backupTemp = backup + ".tmp";
                    // previous 可能来自备份；不能用已经损坏的 current 覆盖可恢复的旧版本。
                    using (var stream = new FileStream(backupTemp, FileMode.Create, FileAccess.Write, FileShare.None))
                    { stream.Write(JsonSerializer.SerializeToUtf8Bytes(previous, Json.Options)); stream.Flush(true); }
                    File.Move(backupTemp, backup, true);
                }
                File.Move(temporary, current, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            progress?.Report(new("导入完成", 1, 1, totalBytes));
            return Resolve(pointer, directory) with
            {
                IsUpdate = previous != null,
                NavigationChanged = previous != null && previous.NavigationFingerprint != fingerprint
            };
        }
        finally
        {
            // 路径只由本仓库 root 和内部 GUID 生成；不删除历史 revision 或外部文件。
            try { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }
            catch (IOException ex) { CoreDiagnostics.Write("package", "导入临时文件将在下次清理：" + ex.Message); }
            catch (UnauthorizedAccessException ex) { CoreDiagnostics.Write("package", "保留导入临时文件：" + ex.Message); }
            gate.Release();
        }
    }

    PackagePointer ReadPointer(string directory)
    {
        string current = Path.Combine(directory, "current.json");
        try { var p = Json.Read<PackagePointer>(current); Resolve(p, directory); return p; }
        catch when (File.Exists(current + ".bak"))
        { var p = Json.Read<PackagePointer>(current + ".bak"); Resolve(p, directory); return p; }
    }

    InstalledPackage Resolve(PackagePointer pointer, string directory)
    {
        if (string.IsNullOrWhiteSpace(pointer.PackId) || Path.GetFileName(directory) != Key(pointer.PackId) ||
            pointer.SchemaVersion is not (1 or 2 or 3) || !Guid.TryParseExact(pointer.Revision, "N", out _) ||
            pointer.NavigationFingerprint == null || pointer.NavigationFingerprint.Length != 64 || !pointer.NavigationFingerprint.All(Uri.IsHexDigit) ||
            pointer.ManifestRelativePath == null)
            throw new InvalidDataException("章节索引内容无效。");
        string path = ContainedPath(Path.Combine(directory, "revisions", pointer.Revision), SafeRelativePath(pointer.ManifestRelativePath));
        if (!File.Exists(path)) throw new FileNotFoundException("章节索引指向的配音包不存在。", path);
        return new(pointer.PackId, pointer.Title, pointer.SchemaVersion, path, pointer.NavigationFingerprint, pointer.Revision, pointer.ImportedUtc);
    }

    static string SafeRelativePath(string name)
    {
        string normal = name.Replace('\\', '/').TrimEnd('/');
        if (normal.Length == 0 || normal.StartsWith('/') || normal.Contains(':') || normal.IndexOf('\0') >= 0 ||
            normal.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("ZIP 含不安全的路径：" + name);
        return normal;
    }

    static string ContainedPath(string directory, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("文件路径越过配音包目录。");
        return path;
    }

    static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        byte[] buffer = new byte[81920]; long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return total;
            if (read > limit - total) throw new InvalidDataException("文件大小超过导入限制。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read; progress?.Invoke(total);
        }
    }

    sealed class PackagePointer
    {
        public string PackId { get; set; } = "";
        public string Title { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string ManifestRelativePath { get; set; } = "";
        public string NavigationFingerprint { get; set; } = "";
        public string Revision { get; set; } = "";
        public DateTimeOffset ImportedUtc { get; set; }
    }
}
