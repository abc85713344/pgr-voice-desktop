using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PgrVoice;

public partial class MainWindow
{
    bool textDiscoveryBusy;
    Func<TextProbe, int, CancellationToken, Task<GameTextReader>>? textDiscoveryTest;

    bool ResolveTextGame()
    {
        if (game == null || !Native.IsGameWindow(game.Handle, preferences.GameExecutablePath))
        {
            var windows = FindTextGameWindows();
            if (windows.Length != 1) return false;
            BindGame(windows[0]);
        }
        return game != null && GameInstallation.IsNativeClientExecutable(Native.ExecutablePath(game.Handle));
    }
    void BeginTextMonitoring(TextProbe? preferred = null)
    {
        if (engine == null || SectionBox.SelectedItem is not Section) { TextNotice("请先选好配音章节和小节。"); return; }
        if (!testUi && !ResolveTextGame()) { TextNotice("请先打开游戏，或在设置中选择所用客户端的游戏文件夹。"); return; }
        PauseTextPlayback("正在开启游戏文字监听。");
        ArmTextPlayback();
        if (preferred != null)
            foreach (var probe in textProbes) probe.NextConnectAttempt = probe == preferred ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(1);
    }
    void MaintainTextConnections()
    {
        if (testUi || closing || !textArmed || textDiscoveryBusy || textProbes.Any(p => p.Binding != null)) return;
        var probe = textProbes.FirstOrDefault(p => p.AutoConnect && DateTime.UtcNow >= p.NextConnectAttempt);
        if (probe == null) return;
        // 只在尚未找到或暂时没有对白时重查，且一次只扫描一路；正常跟随只读已找到的字段。
        probe.NextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
        if (!ResolveTextGame()) { probe.Status.Text = "等待所选游戏启动或出现可连接的窗口。"; return; }
        _ = RefreshTextConnection(probe);
    }
    async Task RefreshTextConnection(TextProbe probe)
    {
        if (textDiscoveryBusy || game == null || engine == null || SectionBox.SelectedItem is not Section section) return;
        textDiscoveryBusy = true;
        var owner = engine; var window = game.Handle;
        int gameId = GameTextReader.WindowProcessId(window), revision = ++probe.Revision;
        using var cancel = new CancellationTokenSource(); probe.Binding = cancel;
        probe.Status.Text = "正在寻找对白框；找到后持续监听。";
        string diagnostics = "";
        GameTextReader? candidate = null;
        bool Current() => !closing && !cancel.IsCancellationRequested && probe.AutoConnect && textArmed &&
            revision == probe.Revision && owner == engine && textOwner == owner &&
            game?.Handle == window && GameTextReader.WindowProcessId(window) == gameId &&
            SectionBox.SelectedItem is Section selected && selected.Id == section.Id && textSection == section.Id;
        try
        {
            candidate = testUi && textDiscoveryTest != null ? await textDiscoveryTest(probe, gameId, cancel.Token) : await Task.Run(() =>
            {
                var reader = new GameTextReader(gameId);
                try { reader.Bind("", probe.Kind, cancel.Token, allowWaiting: true); return reader; }
                catch { reader.Dispose(); throw; }
                finally { diagnostics = reader.ConnectionDiagnostics; }
            }, cancel.Token);
            if (!Current()) return;
            probe.Reader?.Dispose(); probe.Reader = candidate; candidate = null;
            // 扫描期间旧探针可能已读到新句。保留观察状态，避免同一句被误当成换句而切断录音。
            probe.Status.Text = probe.Active ? "已重新核对当前对白。" : "对白框已连接，等待台词出现。";
            probe.ExportConnection.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Current())
            {
                probe.Status.Text = "正在等待对白，将自动重新寻找。";
                if (diagnostics.Length == 0) diagnostics = ex.Message;
                probe.ExportConnection.Visibility = System.Windows.Visibility.Visible;
            }
        }
        finally
        {
            candidate?.Dispose();
            if (revision == probe.Revision && diagnostics.Length > 0) probe.Diagnostics = diagnostics;
            if (ReferenceEquals(probe.Binding, cancel)) probe.Binding = null;
            if (revision == probe.Revision) probe.NextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
            textDiscoveryBusy = false;
        }
    }
}
