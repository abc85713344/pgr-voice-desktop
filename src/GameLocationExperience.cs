using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace PgrVoice;

public partial class MainWindow
{
    void RefreshGameLocation() => GameLocationLabel.Text = preferences.GameExecutablePath.Length == 0
        ? "尚未指定目录，可自动查找已打开的战双。每台电脑各自保存选择。"
        : "已选择：" + Path.GetDirectoryName(preferences.GameExecutablePath);

    bool GameMatchesPreference(GameWindow window) => preferences.GameExecutablePath.Length > 0
        ? GameInstallation.AllowsExecutable(preferences.GameExecutablePath, Native.ExecutablePath(window.Handle))
        : window.ProcessName.Equals(preferences.GameProcess, StringComparison.OrdinalIgnoreCase);

    GameWindow[] FindTextGameWindows() => Native.Windows().Where(w => Native.IsGameWindow(w.Handle, preferences.GameExecutablePath)).ToArray();

    bool ApplyGameFolder(string folder)
    {
        string executable;
        try { executable = GameInstallation.ResolveFolder(folder); }
        catch (Exception ex) { Tell(ex.Message); return false; }
        if (!GameInstallation.SameExecutable(preferences.GameExecutablePath, executable))
        {
            StopGameText("游戏位置已更换，请在当前游戏对白中重新连接文本追踪。");
            StopAutomatic("游戏位置已更换，自动播放已停止。");
            game = null; ResetFollowInputSession(); ResetDialogueObservation();
        }
        preferences.GameExecutablePath = executable; preferences.GameProcess = Path.GetFileNameWithoutExtension(executable);
        RefreshGameLocation(); Save();
        if (!testUi) ConnectSelectedGame();
        return true;
    }
    void SelectGameFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要使用的战双客户端文件夹（包含游戏主程序）" };
        string? previous = Path.GetDirectoryName(preferences.GameExecutablePath);
        if (previous != null && Directory.Exists(previous)) dialog.InitialDirectory = previous;
        if (dialog.ShowDialog(this) == true) ApplyGameFolder(dialog.FolderName);
    }
    void ConnectSelectedGameClick(object sender, RoutedEventArgs e) => ConnectSelectedGame();
    void ConnectSelectedGame()
    {
        preferences.GameProcess = preferences.GameExecutablePath.Length > 0 ? Path.GetFileNameWithoutExtension(preferences.GameExecutablePath) : "PGR"; Save();
        var games = FindTextGameWindows();
        if (games.Length == 1) BindGame(games[0]);
        else
        {
            connectionNotice = games.Length > 1 ? "发现多个战双窗口，请在下方选择具体窗口。" :
                preferences.GameExecutablePath.Length > 0 ? "位置已保存，请先从启动器打开所选目录的战双；打开后会自动连接。" : "未发现运行中的战双，请先打开游戏，或选择安装文件夹。";
            GameLabel.Text = connectionNotice; Tell(connectionNotice);
        }
    }
    void ClearGameFolderClick(object sender, RoutedEventArgs e)
    {
        StopGameText("已清除客户端选择，请重新连接游戏文字。"); StopAutomatic("已清除客户端选择，自动播放已停止。");
        game = null; ResetFollowInputSession(); ResetDialogueObservation();
        preferences.GameExecutablePath = ""; preferences.GameProcess = ""; RefreshGameLocation(); Save();
        connectionNotice = "未绑定游戏"; GameLabel.Text = connectionNotice;
        Tell("已清除客户端选择。可以选择另一份战双目录，或自动查找已打开的战双。");
    }
}
