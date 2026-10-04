using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    // 只用于隔离验收的场景准备；不发送系统按键，也不在产品观测中途抢回焦点。
    async Task PrepareIsolatedTestForeground(Window window, string description)
    {
        if (!testUi) throw new InvalidOperationException("前台准备只允许隔离验收入口。");
        window.Show(); window.WindowState = WindowState.Normal;
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(handle, out uint processId);
        if (handle == IntPtr.Zero || processId != Environment.ProcessId)
            throw new InvalidOperationException("拒绝激活非本进程验收窗口。");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            window.Activate(); Native.SetForegroundWindow(handle);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            for (int poll = 0; poll < 10 && Native.GetForegroundWindow() != handle; poll++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != handle)
            {
                uint ownThread = GetCurrentThreadId(), foregroundThread = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
                try { window.Activate(); Native.SetForegroundWindow(handle); }
                finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
                // 临时连接只覆盖同步激活，绝不跨 await 或留给后续测试。
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                for (int poll = 0; poll < 10 && Native.GetForegroundWindow() != handle; poll++) await Task.Delay(30);
            }
            // 等待之前的异步窗口切换落定；单次瞬间取得前台不能作为输入场景起点。
            var stable = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() == handle && stable.ElapsedMilliseconds < 180) await Task.Delay(20);
            if (Native.GetForegroundWindow() == handle && stable.ElapsedMilliseconds >= 180) return;
        }
        var actual = Native.GetForegroundWindow(); GetWindowThreadProcessId(actual, out uint actualProcess);
        string processName;
        try { using var process = Process.GetProcessById((int)actualProcess); processName = process.ProcessName; }
        catch { processName = "未知"; }
        throw new InvalidOperationException(description + $"：期望 HWND={handle}/PID={processId}，实际 HWND={actual}/PID={actualProcess}/{processName}；未执行依赖前台的场景。");
    }

    async Task RunShortcutUiTest()
    {
        var results = new List<string>(); Window? scene = null;
        void Check(bool ok, string message) => results.Add((ok ? "PASS: " : "FAIL: ") + message);
        async Task Drain() { await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
        try
        {
            timer.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose(); keyboard = null;
            preferences.Keys = new Preferences().Keys; preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            listeningTestAudio = true;
            var root = Path.Combine(Log.DataDir, "fixtures", "shortcuts"); Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "voice.wav"), new byte[] { 0 });
            var pack = new Pack { Id = "shortcut-fixture", Title = "功能键验收", Root = root,
                Chapters = new() { new() { Id = "chapter", Title = "测试大章", Sections = new() { new() { Id = "section", Title = "测试小节", StartId = "a" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="section", Text="共同线第一句", Audio="voice.wav", NextId="b" },
                    new() { Id="b", SectionId="section", Text="共同线第二句", Audio="voice.wav", NextId="menu" },
                    new() { Id="menu", SectionId="section", Kind="choice", Text="选择路线", Options=new()
                    {
                        new() { Id="one", Label="第一路线", PathId="one", TargetId="r1", MergeId="merge", Verified=true },
                        new() { Id="two", Label="第二路线", PathId="two", TargetId="r2", MergeId="merge", Verified=true }
                    } },
                    new() { Id="r1", SectionId="section", Text="第一路线正文", PathId="one", Audio="voice.wav", NextId="merge" },
                    new() { Id="r2", SectionId="section", Text="第二路线正文", PathId="two", Audio="voice.wav", NextId="merge" },
                    new() { Id="merge", SectionId="section", Kind="merge", NextId="tail" },
                    new() { Id="tail", SectionId="section", Text="共同线末句", Audio="voice.wav" }
                } };
            pack.Validate(); string file = Path.Combine(root, "pack.json"); Json.Save(file, pack); LoadPack(file);
            var own = new WindowInteropHelper(this).Handle;
            KeyEventArgs Local(Key key, UIElement? source = null)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                (source ?? LinesList).RaiseEvent(args); return args;
            }
            void Prepare()
            {
                StopListeningForGame(); HideBranchMenu(); game = null; engine!.Commit("a"); DialoguePositionConfirmed(); Expand(StoryTab); LinesList.Focus();
            }
            Prepare(); int before = playCalls; Local(Key.F5);
            Check(playCalls == before+1 && engine!.CurrentId == "a", "面板 F5 重播当前句一次");
            Prepare(); Local(Key.F7); Check(engine!.Mode == RunMode.Paused, "面板 F7 暂停跟随");
            Local(Key.F7); Check(engine.Mode == RunMode.Following, "面板 F7 恢复跟随");
            Prepare(); Local(Key.PageDown); Check(engine.CurrentId == "b", "面板 PageDown 只把配音推进一句");
            Local(Key.PageUp); Check(engine.CurrentId == "a", "面板 PageUp 回到上一句");
            Prepare(); before=playCalls; Local(Key.Space);
            Check(engine.CurrentId == "a" && playCalls==before, "展开面板的空格不充当游戏推进键");
            Local(Key.F2); Check(Tabs.SelectedItem==historyTab && engine.Mode==RunMode.Paused, "面板 F2 打开历史并暂停游戏跟随");
            Prepare(); Local(Key.F4); await Drain(); Check(Tabs.SelectedItem==StoryTab && LibraryBox.IsKeyboardFocusWithin, "面板 F4 聚焦章节目录");
            Prepare(); Local(Key.F1); Check(branchMenu.IsVisible && branchMenu.IsNavigation && engine.CurrentId=="a", "面板 F1 静音打开手动分支导航");
            HideBranchMenu(); engine.Commit("menu"); engine.SelectBranch(1); Expand(StoryTab); Local(Key.F3);
            Check(engine.CurrentId=="menu" && engine.MenuWaiting, "面板 F3 返回实际选择点");
            Prepare(); Local(Key.F8); Check(engine.Mode==RunMode.Original, "面板 F8 进入原声模式");
            Local(Key.F8); Check(engine.Mode==RunMode.Original && resumeOriginalRequested && expanded, "再次 F8 仅打开续接选择，未确认不恢复配音");
            Local(Key.Escape); Check(engine.Mode==RunMode.Original && !resumeOriginalRequested && !expanded, "Esc 取消续接并保留原声模式");
            Prepare(); Local(Key.F9); Check(Tabs.SelectedItem==LocateTab && OcrStatus.Text.Contains("尚未启用") && !ocr.Running, "面板 F9 未启用时说明原因，不启动 OCR 或误播");
            Prepare(); Local(Key.F6); Check(!expanded, "面板 F6 收起");
            Prepare(); preferences.Keys["replay"]="J"; before=playCalls; Local(Key.J, SearchBox);
            Check(playCalls==before, "在搜索框输入已改绑字母不触发剧情动作");
            Local(Key.J, LinesList); Check(playCalls==before+1, "离开输入框后改绑字母可以重播");
            before=playCalls; Local(Key.F5); Check(playCalls==before, "改绑后面板旧 F5 不再生效"); preferences.Keys["replay"]="F5";

            // 窗口由本测试进程创建，仅直接调用路由入口，不注入系统按键。
            scene = new Window { Title="功能键模拟游戏", Width=360, Height=200, Background=Brushes.DarkSlateGray, ShowInTaskbar=false };
            scene.Show(); var handle = new WindowInteropHelper(scene).Handle;
            Prepare(); BindGame(new(handle, scene.Title, "ShortcutFixture")); Collapse();
            await PrepareIsolatedTestForeground(scene, "功能键模拟游戏无法稳定取得前台");
            using(var listener=new KeyboardListener(false))
            {
                listener.ObservedPressed += input => HandleGlobal(input.Key,input.Foreground,input.Timestamp,input.Modifiers,input.MessageTime);
                void Raw(Key key,bool down=true) => listener.OnRawKey(KeyInterop.VirtualKeyFromKey(key),down,handle);
                before=playCalls; Raw(Key.F5);Raw(Key.F5);Raw(Key.F5,false);
                Check(playCalls==before+1, "模拟游戏 Raw F5 按住不重复重播");
                before=playCalls; Raw(Key.LeftCtrl);Raw(Key.F5);Raw(Key.F5,false);Raw(Key.LeftCtrl,false);
                Check(playCalls==before,"模拟游戏 Ctrl+F5 不误触发单键重播");
                Raw(Key.LeftShift);Raw(Key.Space);Raw(Key.Space,false);Raw(Key.LeftShift,false);
                Check(engine.CurrentId=="a","模拟游戏 Shift+空格不误推进剧情");
                Raw(Key.F7);Raw(Key.F7,false); Check(engine.Mode==RunMode.Paused,"模拟游戏 F7 暂停");
                Raw(Key.F7);Raw(Key.F7,false); Check(engine.Mode==RunMode.Following,"模拟游戏 F7 恢复");
                Raw(Key.Space);Raw(Key.Space);Raw(Key.Space,false);Check(engine.CurrentId=="b","模拟游戏空格按住只推进一句");
                Raw(Key.PageUp);Raw(Key.PageUp,false);Check(engine.CurrentId=="a","模拟游戏 PageUp 返回上一句");
                Raw(Key.PageDown);Raw(Key.PageDown,false);Check(engine.CurrentId=="b","模拟游戏 PageDown 手动推进配音");
                Raw(Key.F6);Raw(Key.F6,false);Check(expanded,"模拟游戏 F6 展开面板");
            }
            var collapseEvent=Local(Key.F6);
            // 模拟 WPF 与 Raw 处理时刻在同一 Windows tick 内相差 8ms；去重必须仍可靠。
            long sameInput=Stopwatch.GetTimestamp()+Stopwatch.Frequency*8/1000;
            await PrepareIsolatedTestForeground(scene, "收起面板后的功能键模拟游戏无法稳定取得前台");
            HandleGlobal(Key.F6,handle,sameInput,messageTime:unchecked((uint)collapseEvent.Timestamp));
            Check(!expanded,"面板 F6 切回游戏后，同一次迟到 Raw 消息不会再次展开");
            HandleGlobal(Key.F6,handle,sameInput,messageTime:unchecked((uint)collapseEvent.Timestamp+1));
            Check(expanded,"紧接着的新一次 F6 消息不被旧面板按键吞掉");
            game=null; Expand(listeningTab); OpenListeningPack(file,"chapter"); StartListening();
            string? gamePosition=engine.CurrentId; before=playCalls;
            Local(Key.F7,listeningPlay); Check(!listeningRunning,"听书页 F7 暂停听书");
            Local(Key.F7,listeningPlay); Check(listeningRunning,"听书页 F7 恢复听书");
            long ticket=listeningTicket; Local(Key.F5,listeningPlay); Check(listeningRunning && listeningTicket!=ticket,"听书页 F5 重播听书当前句");
            Local(Key.PageDown,listeningPlay); Check(listeningSession?.Current?.NodeId=="b" && !listeningRunning,"听书页 PageDown 定位下一句并暂停");
            Local(Key.PageUp,listeningPlay); Check(listeningSession?.Current?.NodeId=="a","听书页 PageUp 定位上一句");
            foreach(var key in new[]{Key.F1,Key.F2,Key.F3,Key.F4,Key.F8,Key.F9}) Local(key,listeningPlay);
            Check(engine.CurrentId==gamePosition && playCalls==before && !branchMenu.IsVisible && !ocr.Running,"听书页游戏快捷键不改变游戏、开分支或启动 OCR");
            Local(Key.F6,listeningPlay); Check(!expanded,"听书页 F6 收起");
            OpenStory();Check(expanded && Tabs.SelectedItem==listeningTab,"听书收起后点悬浮球返回听书页");
            Reveal();Check(Tabs.SelectedItem==listeningTab,"听书中再次打开程序仍显示听书页");
            await PrepareIsolatedTestForeground(scene, "未绑定应用的隔离窗口无法稳定取得前台");
            bool priorExpanded=expanded;HandleGlobal(Key.F6,handle);
            Check(expanded==priorExpanded,"听书时其他未绑定应用的 F6 不操作播放器");
            Expand(StoryTab);Original();Check(!ListeningActive && !listeningRunning && engine.Mode==RunMode.Original,"切回剧情后游戏原声动作停止听书输出");
            keyTestBox.IsChecked=true; before=playCalls; Local(Key.F5);Check(playCalls==before && keyTestStatus.Text.Contains("收到"),"按键诊断只显示输入，不播放或导航");keyTestBox.IsChecked=false;
        }
        catch(Exception ex){results.Add("FAIL: "+ex);}
        finally{listeningTestAudio=false;game=null;scene?.Close();}
        Directory.CreateDirectory(Log.DataDir);File.WriteAllLines(Path.Combine(Log.DataDir,"shortcut-ui-test.txt"),results);Close();
    }
}
