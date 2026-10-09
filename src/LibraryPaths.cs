using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PgrVoice;

// 只识别总目录、约定的分类目录和单章，避免递归读入同步备份及历史修订。
internal static class LibraryPaths
{
    internal static bool IsCategory(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return ChapterCatalog.Categories.Select((category, index) => (category, index))
            .Any(item => name == item.category || name == $"{item.index + 1:00}_{item.category}");
    }

    static IEnumerable<string> Children(string folder) => Directory.EnumerateDirectories(folder)
        .Where(path =>
        {
            string name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            return !name.StartsWith('.') && !name.StartsWith('_') &&
                !name.Contains("备份", StringComparison.Ordinal) && !name.Contains("暂存", StringComparison.Ordinal) &&
                !name.Equals("backup", StringComparison.OrdinalIgnoreCase) && !name.Equals("backups", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("staging", StringComparison.OrdinalIgnoreCase) && !name.Equals("temp", StringComparison.OrdinalIgnoreCase) &&
                (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;
        });

    internal static string Root(string folder)
    {
        string root = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(root, "pack.json"))) root = Directory.GetParent(root)?.FullName ?? root;
        if (IsCategory(root)) root = Directory.GetParent(root)?.FullName ?? root;
        return root;
    }

    internal static List<string> Packs(string folder)
    {
        string root = Root(folder);
        if (!Directory.Exists(root)) return new();
        var files = new List<string>();
        foreach (string child in Children(root))
        {
            if (IsCategory(child))
                files.AddRange(Children(child).Select(path => Path.Combine(path, "pack.json")).Where(File.Exists));
            else
            {
                string file = Path.Combine(child, "pack.json");
                if (File.Exists(file)) files.Add(file);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 整章目录移动时，包编号与存档不变；只恢复名称唯一的原章节路径。
    internal static string Restore(string file, string? library = null)
    {
        if (string.IsNullOrWhiteSpace(file) || File.Exists(file)) return file;
        try
        {
            if (!Path.GetFileName(file).Equals("pack.json", StringComparison.OrdinalIgnoreCase)) return file;
            string? chapter = Path.GetDirectoryName(Path.GetFullPath(file));
            if (chapter == null) return file;
            string? root = library ?? Directory.GetParent(chapter)?.FullName;
            if (root == null || !Directory.Exists(root)) return file;
            var candidates = Packs(root).Where(candidate =>
                string.Equals(Path.GetFileName(Path.GetDirectoryName(candidate)), Path.GetFileName(chapter), StringComparison.OrdinalIgnoreCase)).ToArray();
            return candidates.Length == 1 ? candidates[0] : file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return file; }
    }
}
