using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunGameLocationUiTest()
    {
        var report = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); report.Add("PASS: " + name); }
        string Fixture(string name, string executableName = "PGR")
        {
            string folder = Path.Combine(Log.DataDir, "game-folder-fixtures", name);
            Directory.CreateDirectory(Path.Combine(folder, executableName + "_Data"));
            foreach (string file in new[] { executableName + ".exe", "GameAssembly.dll", "UnityPlayer.dll" }) File.WriteAllText(Path.Combine(folder, file), "fixture: never executed");
            return folder;
        }
        bool Rejected(string folder) { try { GameInstallation.ResolveFolder(folder); return false; } catch (ArgumentException) { return true; } }
        try
        {
            Check(new Preferences().GameExecutablePath == "", "通用版默认不带制作者游戏目录");
            string first = Fixture(Path.Combine("磁盘甲", "任意中文游戏目录"));
            string second = Fixture(Path.Combine("磁盘乙", "启动器目录", "自定义游戏文件夹"));
            Check(GameInstallation.SameExecutable(GameInstallation.ResolveFolder(first), Path.Combine(first, "PGR.exe")), "直接识别含 PGR.exe 的游戏目录");
            Check(GameInstallation.SameExecutable(GameInstallation.ResolveFolder(Path.GetDirectoryName(second)!), Path.Combine(second, "PGR.exe")), "选启动器外层目录时定位下一层实际游戏");
            Check(Rejected(Path.Combine(first, "PGR_Data")), "选错 PGR_Data 时提示选择上一级");
            string launcherOnly = Path.Combine(Log.DataDir, "game-folder-fixtures", "只有启动器");
            Directory.CreateDirectory(launcherOnly); File.WriteAllText(Path.Combine(launcherOnly, "launcher.exe"), "fixture");
            Check(Rejected(launcherOnly), "仅有 launcher.exe 不能冒充游戏本体");
            Fixture(Path.Combine("磁盘甲", "另一份游戏"));
            Check(Rejected(Path.GetDirectoryName(first)!), "同一外层目录有多份游戏时不猜测选择");
            // 名称仅为测试夹具，不声称是真实国际服或渠道服的文件名。
            string alternate = Fixture(Path.Combine("另一客户端", "游戏目录"), "AlternateClient");
            string alternateExe = Path.Combine(alternate, "AlternateClient.exe");
            Check(GameInstallation.SameExecutable(GameInstallation.ResolveFolder(alternate), alternateExe), "明确选择目录后支持非 PGR 名称的客户端");
            Check(GameInstallation.SameExecutable(GameInstallation.ResolveFolder(Path.GetDirectoryName(alternate)!), alternateExe), "不同名称客户端也支持启动器外层目录");
            Check(Rejected(Path.Combine(alternate, "AlternateClient_Data")), "不同名称的 _Data 目录也提示返回上一级");
            string mixed = Fixture("同目录多个主程序", "ClientOne"); Fixture("同目录多个主程序", "ClientTwo");
            Check(Rejected(mixed), "同目录出现两个完整客户端时不猜测");
            string incomplete = Fixture("缺少游戏组件", "IncompleteClient");
            File.Delete(Path.Combine(incomplete, "GameAssembly.dll"));
            Check(Rejected(incomplete), "取消名称限制后仍拒绝缺少游戏组件的目录");
            Check(GameInstallation.MatchesWindowIdentity(alternateExe, alternateExe, "AlternateClient") &&
                !GameInstallation.MatchesWindowIdentity(alternateExe, Path.Combine(first, "PGR.exe"), "PGR") &&
                !GameInstallation.MatchesWindowIdentity(alternateExe, null, "AlternateClient"), "指定客户端后只接受其完整路径，不串到其他客户端或无法核实的进程");
            Check(GameInstallation.MatchesWindowIdentity("", null, "PGR") &&
                !GameInstallation.MatchesWindowIdentity("", alternateExe, "AlternateClient"), "未选择目录时保留已知名称自动发现，不自动连接任意 Unity 程序");
            Check(ApplyGameFolder(first) && preferences.GameProcess == "PGR" && game == null, "选择游戏目录可独立保存，不要求游戏已经启动");
            var restored = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences, Json.Options), Json.Options)!;
            Check(GameInstallation.SameExecutable(restored.GameExecutablePath, Path.Combine(first, "PGR.exe")), "保存和重载保留每位用户自己的游戏位置");
            Check(!ApplyGameFolder(launcherOnly) && GameInstallation.SameExecutable(preferences.GameExecutablePath, restored.GameExecutablePath), "无效选择不覆盖已保存位置");
            Check(GameInstallation.AllowsExecutable(restored.GameExecutablePath, Path.Combine(first, "PGR.exe")) &&
                !GameInstallation.AllowsExecutable(restored.GameExecutablePath, Path.Combine(second, "PGR.exe")) &&
                !GameInstallation.AllowsExecutable(restored.GameExecutablePath, null), "指定目录后拒绝其他安装和无法核实路径的同名进程");
            textArmed = true; textProbes[0].Reader = new GameTextReader(Environment.ProcessId);
            Check(ApplyGameFolder(second) && !textArmed && textProbes[0].Reader == null, "更换安装位置取消旧文本探测，防止串到上一份游戏");
            Check(ApplyGameFolder(alternate) && preferences.GameProcess == "AlternateClient", "保存不同名称客户端时记录实际主程序名称");
            restored = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences, Json.Options), Json.Options)!;
            Check(restored.GameProcess == "AlternateClient" && GameInstallation.SameExecutable(restored.GameExecutablePath, alternateExe), "重载设置保留不同名称的客户端选择");
            textArmed = true; textProbes[0].Reader = new GameTextReader(Environment.ProcessId);
            ClearGameFolderClick(this, new System.Windows.RoutedEventArgs());
            Check(preferences.GameExecutablePath == "" && preferences.GameProcess == "" && game == null && !textArmed && textProbes[0].Reader == null &&
                GameLabel.Text == "未绑定游戏", "清除选择会释放旧文本连接、进程名和窗口绑定");
            var ownWindow = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string? ownExecutable = Native.ExecutablePath(ownWindow);
            Check(ownExecutable is { Length: > 0 }, "能只读取得窗口所属程序的完整路径");
            Check(Native.IsGameWindow(ownWindow, ownExecutable!) && !Native.IsGameWindow(ownWindow, alternateExe) &&
                !Native.IsGameWindow(ownWindow, "") && !Native.IsGameWindow(IntPtr.Zero, ownExecutable!), "真实窗口身份检查接受指定路径、拒绝其他路径及无选择的非 PGR 程序");
            Check(!DesktopAdvanceInput.TryClick(ownWindow, default, 0, 0, alternateExe, out _), "实际点击入口拒绝另一客户端的窗口，未发送点击");
            Expand(SettingsTab); SelectGameFolderButton.BringIntoView(); await Task.Delay(120); Screenshot("game-location-ui.png");
            Check(SelectGameFolderButton.IsVisible && GameLocationLabel.Text.Contains("每台电脑"), "设置页提供游戏文件夹选择与本机保存说明");
            report.Add("INFO: 使用隔离设置与虚构目录；未执行夹具文件，未操作或读取真实游戏台词。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "game-location-ui-test.txt"), report); Close(); }
    }
}
