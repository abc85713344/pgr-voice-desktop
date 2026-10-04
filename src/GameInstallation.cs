using System;
using System.IO;
using System.Linq;

namespace PgrVoice;

public static class GameInstallation
{
    public static bool SameExecutable(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public static bool AllowsExecutable(string configured, string? running) => configured.Length == 0 || SameExecutable(configured, running);
    // 玩家明确选择的客户端只校验原生 Unity IL2CPP 目录结构，不绑定服别或程序名。
    // 不据此扫描、自动连接其他 Unity 游戏；自动发现仍只认已知名称或已保存的路径。
    public static bool IsNativeClientExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(executable));
            return folder != null && Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(executable) && Directory.Exists(Path.Combine(folder, Path.GetFileNameWithoutExtension(executable) + "_Data")) &&
                File.Exists(Path.Combine(folder, "GameAssembly.dll")) && File.Exists(Path.Combine(folder, "UnityPlayer.dll"));
        }
        catch { return false; }
    }
    static string[] ClientsInFolder(string folder) => Directory.EnumerateFiles(folder, "*.exe")
        .Where(IsNativeClientExecutable).Take(2).ToArray();

    public static bool MatchesWindowIdentity(string configured, string? running, string processName) => configured.Length > 0
        ? SameExecutable(configured, running)
        : processName.Equals("PGR", StringComparison.OrdinalIgnoreCase);
    public static string ResolveFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new ArgumentException("所选文件夹不存在，请重新选择游戏安装位置。");
        folder = Path.GetFullPath(folder);
        if (Path.TrimEndingDirectorySeparator(folder).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(folder)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请进入该磁盘里的游戏安装文件夹，不要只选择整个磁盘。");
        var direct = ClientsInFolder(folder);
        if (direct.Length == 1) return direct[0];
        if (direct.Length > 1) throw new ArgumentException("这个目录包含多个游戏主程序，请选择仅包含所需客户端的安装目录。");
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选这个 _Data 文件夹的上一级，也就是放着游戏主程序的文件夹。");
        // 只检查所选目录及下一层，不搜索整盘，也不运行任何游戏或启动器文件。
        var candidates = Directory.EnumerateDirectories(folder).Take(128).SelectMany(ClientsInFolder).Take(2).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1) throw new ArgumentException("这里有多份客户端，请进入要使用的那一份游戏目录。");
        throw new ArgumentException("没有找到完整的原生电脑版。请选择包含游戏主程序、同名 _Data 文件夹、GameAssembly.dll 和 UnityPlayer.dll 的目录；只有启动器不够。");
    }
}
