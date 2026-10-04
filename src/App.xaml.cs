using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
namespace PgrVoice;
public partial class App : Application
{
    Mutex? instanceMutex;
    EventWaitHandle? revealSignal;
    RegisteredWaitHandle? revealWait;
    bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CoreDiagnostics.Message += (category, message) => Log.Write(category, message);
        DispatcherUnhandledException += (_, args) => { Log.Write("error", args.Exception.ToString()); MessageBox.Show(args.Exception.Message, "剧情配音播放器"); args.Handled = true; };
        bool draftPlayer = e.Args.Contains("--draft-player");
        bool dialogueTrial = e.Args.Contains("--dialogue-trial");
        bool test = e.Args.Any(a => a.StartsWith("--test-", StringComparison.Ordinal));
        if ((draftPlayer || dialogueTrial) && !test)
        {
            string previous = Path.Combine(Log.DataDir, "preferences.json");
            Log.DataDir = Path.Combine(Log.DataDir, dialogueTrial ? "dialogue-trial" : "draft-player");
            string local = Path.Combine(Log.DataDir, "preferences.json");
            if (!File.Exists(local) && File.Exists(previous))
            {
                try { Directory.CreateDirectory(Log.DataDir); File.Copy(previous, local, false); }
                catch (Exception ex) { Log.Write("draft-settings", ex.Message); }
            }
        }
        if (e.Args.Any(a => a.StartsWith("--test-", StringComparison.Ordinal))) Log.DataDir = Path.Combine(AppContext.BaseDirectory, "ui-test-state");
        int stateArgument = Array.IndexOf(e.Args, "--test-state");
        if (stateArgument >= 0 && stateArgument + 1 < e.Args.Length) Log.DataDir = Path.GetFullPath(e.Args[stateArgument + 1]);
        if (!test)
        {
            try
            {
                string scope = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                if (dialogueTrial) scope += ".DialogueTrial";
                else if (draftPlayer) scope += ".Draft";
                revealSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PgrStoryVoice.Reveal." + scope);
                instanceMutex = new Mutex(true, @"Local\PgrStoryVoice.Instance." + scope, out ownsInstance);
                if (!ownsInstance) { revealSignal.Set(); Shutdown(); return; }
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("可能已有以管理员权限运行的播放器。请先使用或退出那个播放器，避免重复启动。", "剧情配音播放器");
                Shutdown(); return;
            }
        }
        try
        {
            Log.Write("startup", $"开始启动，PID={Environment.ProcessId}，程序={AppContext.BaseDirectory}");
            var window = new MainWindow(e.Args); MainWindow = window; window.Show();
            if (!test) revealWait = ThreadPool.RegisterWaitForSingleObject(revealSignal!, (_, _) =>
                Dispatcher.BeginInvoke(() => window.Reveal()), null, Timeout.Infinite, false);
        }
        catch (Exception ex)
        {
            Log.Write("startup-error", ex.ToString());
            MessageBox.Show("播放器启动失败：" + ex.Message + "\n日志保存在：" + Path.Combine(Log.DataDir, "player.log"), "剧情配音播放器");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        revealWait?.Unregister(null); revealSignal?.Dispose();
        if (ownsInstance) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose(); base.OnExit(e);
    }
}
