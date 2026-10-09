using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunCompactFollowUiTest()
    {
        var results = new List<string>(); Window? scene = null;
        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException(label + $" [node={engine?.CurrentId}, mode={engine?.Mode}, expanded={expanded}, plays={playCalls}]");
            results.Add("PASS: " + label);
        }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Click(string action)
        {
            var button = compactFollowActions[action]; UpdateLayout();
            Check(button.IsVisible && button.IsEnabled, "快捷按钮可见且可用：" + button.Content);
            var provider = UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (provider == null) throw new InvalidOperationException("小台词条按钮未提供 Invoke：" + action);
            provider.Invoke(); await Drain();
        }
        async Task OwnForeground(Window window)
        {
            if (!testUi || (window != this && window != scene)) throw new InvalidOperationException("只能聚焦本轮隔离测试窗口");
            window.Show(); window.WindowState = WindowState.Normal; window.UpdateLayout();
            IntPtr handle = new WindowInteropHelper(window).Handle;
            GetWindowThreadProcessId(handle, out uint ownerPid);
            if (handle == IntPtr.Zero || ownerPid != Environment.ProcessId)
                throw new InvalidOperationException($"隔离测试窗口不属于本测试进程：句柄 {handle}，PID {ownerPid}。");
            void RequestForeground()
            {
                window.Activate();
                if (window.Content is Control content) content.Focus();
                Native.SetForegroundWindow(handle);
            }
            RequestForeground(); await Drain();
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != handle)
            {
                uint thread = GetCurrentThreadId(), front = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = front != 0 && front != thread && AttachThreadInput(thread, front, true);
                try { RequestForeground(); }
                finally { if (attached) AttachThreadInput(thread, front, false); }
                // 只同步借用前台线程的输入队列，解除关联后才等待 WPF 消息处理。
                await Drain();
                for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            }
            IntPtr actual = Native.GetForegroundWindow();
            if (actual != handle)
            {
                GetWindowThreadProcessId(actual, out uint actualPid);
                string ProcessName(uint pid)
                {
                    try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
                    catch (Exception ex) { return "无法读取（" + ex.GetType().Name + "）"; }
                }
                throw new InvalidOperationException($"本进程验收窗口未取得前台：{window.Title}（期望 HWND 0x{handle.ToInt64():X}，PID {ownerPid}，进程 {ProcessName(ownerPid)}；实际 HWND 0x{actual.ToInt64():X}，PID {actualPid}，进程 {ProcessName(actualPid)}）。");
            }
            Check(actual == handle, "本进程验收窗口取得前台：" + window.Title);
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("小台词条验收仅允许在隔离测试入口运行");
            timer.Stop(); gamepadTimer?.Stop(); StopGamepadInput(); rawKeyboard?.Dispose(); keyboard?.Dispose(); keyboard = null;
            listeningTestAudio = true; preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            Check(compactControlsReady && compactFollowActions.Count == 6 && compactFollowActions.ContainsKey("continuation"), "构造流程初始化原五个按钮及手动续接入口");
            Check(new Preferences().CompactControlsEnabled, "快捷按钮对新设置默认显示");
            string folder = Path.Combine(Log.DataDir, "fixtures", "compact-follow"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "voice.wav"), new byte[] { 0 });
            var pack = new Pack
            {
                Id = "compact-follow-fixture", Title = "小台词条验收", Root = folder,
                Chapters = new() { new() { Id="chapter", Title="紧凑控制测试章", Sections=new() { new() { Id="section", Title="测试小节", StartId="a" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="section", Speaker="测试角色", Text="共同线第一句", Audio="voice.wav", NextId="b" },
                    new() { Id="b", SectionId="section", Speaker="测试角色", Text="共同线第二句", Audio="voice.wav", NextId="menu" },
                    new() { Id="menu", SectionId="section", Kind="choice", Text="选择路线", Options=new()
                    {
                        new() { Id="one", Label="第一条路线", PathId="one", TargetId="r1", MergeId="merge", Verified=true },
                        new() { Id="two", Label="第二条路线", PathId="two", TargetId="r2", MergeId="merge", Verified=true }
                    } },
                    new() { Id="r1", SectionId="section", PathId="one", Text="第一条路线正文", Audio="voice.wav", NextId="merge" },
                    new() { Id="r2", SectionId="section", PathId="two", Text="第二条路线正文", Audio="voice.wav", NextId="merge" },
                    new() { Id="merge", SectionId="section", Kind="merge", NextId="tail" },
                    new() { Id="tail", SectionId="section", Text="共同线末句", Audio="voice.wav" }
                }
            };
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); LoadPack(file);
            preferences.CompactMode = "strip"; compactControlsSetting.IsChecked = true;
            scene = new Window
            {
                Title="紧凑条验收模拟游戏", Width=400, Height=220, Background=Brushes.DarkSlateGray, ShowInTaskbar=false,
                Content=new Button { Content="隔离验收窗口", Margin=new Thickness(30), Focusable=true }
            };
            scene.Show(); IntPtr gameHandle = new WindowInteropHelper(scene).Handle;
            game = new(gameHandle, scene.Title, "CompactFollowFixture");
            async Task Prepare()
            {
                StopAutomatic("准备小台词条验收", false); StopListeningForGame(); HideBranchMenu();
                engine!.Commit("a"); DialoguePositionConfirmed(); Collapse(); await OwnForeground(scene); RefreshCompactFollowControls();
            }
            await Prepare();
            Check(!expanded && StripView.IsVisible && compactFollowButtons.IsVisible && Math.Abs(Height - PreferredCompactHeight) < .1,
                "小台词条有独立按钮行且窗口高度适配");
            await OwnForeground(this); int before = playCalls; await Click("manualNext");
            Check(engine!.CurrentId == "b" && playCalls == before + 1 && !expanded, "下一句只推进配音一次，保持小台词条");
            Check(Native.GetForegroundWindow() == gameHandle, "紧凑按钮操作后归还绑定模拟游戏焦点");
            await Click("previous"); Check(engine.CurrentId == "a" && !expanded, "上一句纠偏保持收起");
            before = playCalls; await Click("replay"); Check(engine.CurrentId == "a" && playCalls == before + 1 && !expanded, "重播当前句一次且不展开");
            await Click("pause"); Check(engine.Mode == RunMode.Paused && compactFollowActions["pause"].Content?.ToString() == "恢复跟随", "暂停跟随后按钮提供恢复入口");
            await Click("pause"); Check(engine.Mode == RunMode.Following && !expanded, "恢复普通跟随，不启动自动播放或展开面板");
            Check(StripState.Text.Length > 0 && StripState.TextWrapping == TextWrapping.Wrap, "当前跟随状态长期显示并可换行");

            string mode = preferences.CompactMode;
            compactControlsSetting.IsChecked = false; await Drain(); stateSaves.Flush();
            Check(!compactFollowButtons.IsVisible && Height == PreferredCompactHeight && !Json.Read<Preferences>(stateFile).CompactControlsEnabled,
                "隐藏快捷按钮会缩回高度并写入隔离设置");
            Check(preferences.CompactMode == mode, "按钮显隐不改变用户选择的悬浮球或台词条模式");
            compactControlsSetting.IsChecked = true; await Drain(); stateSaves.Flush();
            Check(compactFollowButtons.IsVisible && Json.Read<Preferences>(stateFile).CompactControlsEnabled, "重新显示按钮并持久化设置");

            await Prepare(); int clicks = 0; automaticEnvironmentTest = () => true;
            automaticClickTest = () => { clicks++; return true; }; automaticTestImmediate = true;
            StartAutomaticAtConfirmedLine(); RefreshCompactFollowControls(); long ticket = automaticTicket;
            Check(automaticRunning && StripState.Text.Contains("等待当前句开播"), "小台词条区分等待开播与正在播放");
            OnAutomaticAudioStarted(ticket); RefreshCompactFollowControls();
            Check(StripState.Text.Contains("正在播音"), "配音实际开始后小台词条显示播放阶段");
            await Click("pause"); OnAutomaticCompleted(ticket); await Task.Delay(50);
            Check(!automaticRunning && engine.Mode == RunMode.Paused && engine.CurrentId == "a" && clicks == 0,
                "自动播放时点暂停保持暂停，迟到自然完成不点击或推进");
            automaticEnvironmentTest = null; automaticClickTest = null; automaticTestImmediate = false;

            await Prepare(); engine.Commit("b"); await Click("manualNext");
            Check(engine.MenuWaiting && branchMenu.IsVisible && !expanded, "前进到分支仅打开必要的小菜单，不展开大面板或代选");
            await Prepare(); await Click("ocr");
            Check(expanded && Tabs.SelectedItem == LocateTab && OcrStatus.Text.Contains("尚未启用") && !ocr.Running,
                "定位进入必要候选设置页，未启用 OCR 时说明原因且不运行识别");

            await Prepare(); engine.Commit("b"); string? gameNode = engine.CurrentId; int gameHistory = engine.History.Count;
            OpenListeningPack(file, "chapter"); StartListening(); Collapse(); RefreshCompactFollowControls(); before = playCalls;
            Check(StripText.Text == "共同线第一句" && StripSpeaker.Text.StartsWith("听书") && !compactFollowActions["ocr"].IsEnabled,
                "听书紧凑条显示听书当前句并关闭游戏定位入口");
            await Click("pause"); Check(!listeningRunning && compactFollowActions["pause"].Content?.ToString() == "继续听书", "听书按钮暂停听书");
            await Click("pause"); long listeningBefore = listeningTicket; await Click("replay");
            Check(listeningRunning && listeningTicket != listeningBefore, "听书续播和重播使用独立音频票据");
            await Click("manualNext"); Check(listeningSession?.Current?.NodeId == "b" && !listeningRunning && StripText.Text == "共同线第二句", "听书下一句定位并暂停，条内文字同步更新");
            await Click("previous"); Check(listeningSession?.Current?.NodeId == "a" && !expanded, "听书上一句保持紧凑模式");
            Check(engine.CurrentId == gameNode && engine.History.Count == gameHistory && playCalls == before,
                "全部听书快捷按钮不污染游戏位置、履历和播放次数");
            Screenshot("compact-follow-listening.png");

            await Prepare(); engine.Pack.ById["a"].Text = string.Concat(Enumerable.Repeat("这是一段需要滚动阅读的长对白，用于验证小台词条快捷按钮保持完整可见。", 8));
            RefreshCompactFollowControls(); UpdateLayout();
            bool fits = compactFollowActions.Values.All(button =>
            {
                var rect = button.TransformToAncestor(this).TransformBounds(new Rect(button.RenderSize));
                return rect.Top >= 0 && rect.Left >= 0 && rect.Bottom <= ActualHeight + 1 && rect.Right <= ActualWidth + 1;
            });
            Check(fits, "长对白不会把快捷按钮挤出小台词条窗口");
            Screenshot("compact-follow-controls.png");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally
        {
            automaticEnvironmentTest = null; automaticClickTest = null; StopAutomatic("小台词条验收结束", false);
            StopListeningForGame(); listeningTestAudio = false; HideBranchMenu(); game = null; scene?.Close();
            Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "compact-follow-ui-test.txt"), results);
            Close();
        }
    }
}
