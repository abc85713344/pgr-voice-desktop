namespace PgrVoice.Packages;

public sealed record PackageRemovalResult(bool CleanupPending);
public sealed record PackageFileEntry(string Name, string RelativePath, bool IsDirectory, long Bytes);
public sealed record PackageFolder(string FullPath, string RelativePath, string Revision,
    IReadOnlyList<PackageFileEntry> Entries);

public sealed partial class PackageRepository
{
    /// <summary>先原子移出章节索引，再回收所有 revision。进度、书签和外部 ZIP 不在仓库内。</summary>
    public async Task<PackageRemovalResult> RemoveAsync(string packId, string expectedRevision)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var installed = Find(packId) ?? throw new FileNotFoundException("章节已被删除。");
            if (installed.Revision != expectedRevision)
                throw new InvalidOperationException("章节已更新，请重新打开管理页后再删除。");
            string directory = Folder(packId);
            EnsureNoLinks(root, directory);
            string trash = Path.Combine(root, ".deleted");
            Directory.CreateDirectory(trash);
            EnsureNoLinks(root, trash);
            string destination = Path.Combine(trash, Guid.NewGuid().ToString("N"));
            Directory.Move(directory, destination);
            try { Directory.Delete(destination, true); return new(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CoreDiagnostics.Write("package", "章节已移除，残留文件将在下次启动清理：" + ex.Message);
                return new(true);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>只浏览已安装章节的当前解压目录；不接受绝对路径或符号链接。</summary>
    public PackageFolder Browse(string packId, string relativePath = "", string? expectedRevision = null)
    {
        var installed = Find(packId) ?? throw new FileNotFoundException("章节已被删除。");
        if (expectedRevision != null && installed.Revision != expectedRevision)
            throw new InvalidOperationException("章节已更新，请重新打开文件位置。");
        string folder = Path.GetDirectoryName(installed.PackFile)!;
        string current = relativePath.Length == 0 ? folder : ContainedPath(folder, SafeRelativePath(relativePath));
        EnsureNoLinks(root, current);
        var entries = new DirectoryInfo(current).EnumerateFileSystemInfos()
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(f => new PackageFileEntry(f.Name, Path.GetRelativePath(folder, f.FullName).Replace('\\', '/'),
                f is DirectoryInfo, f is FileInfo file ? file.Length : 0))
            .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCulture).ToArray();
        return new(current, relativePath, installed.Revision, entries);
    }

    static void EnsureNoLinks(string boundary, string target)
    {
        string relative = Path.GetRelativePath(boundary, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("文件位置不在章节目录内。");
        for (string? path = target; path != null; path = Path.GetDirectoryName(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("章节目录不支持符号链接。");
            if (path == boundary) return;
        }
        throw new InvalidDataException("无法确认章节文件位置。");
    }
}
