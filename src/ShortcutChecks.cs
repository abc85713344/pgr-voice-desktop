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
using PgrVoice.Listening;

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
        var results = new List<string>(); var controlDiagnostics = new List<object>(); Window? scene = null;
        void Check(bool ok, string message) => results.Add((ok ? "PASS: " : "FAIL: ") + message);
        async Task Drain() { await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
        void Diagnostic(string phase, Control control, KeyEventArgs? input = null)
        {
            controlDiagnostics.Add(new { phase, control = control.GetType().Name, control.IsLoaded, control.IsVisible,
                control.IsKeyboardFocusWithin, dropdownOpen = (control as ComboBox)?.IsDropDownOpen,
                selectedIndex = (control as ComboBox)?.SelectedIndex, itemCount = (control as ComboBox)?.Items.Count,
                expanded, current = engine?.CurrentId, mode = engine?.Mode.ToString(), playCalls,
                listeningRunning, listeningTicket, listeningNode = listeningSession?.Current?.NodeId,
                sessionPolicy = listeningSession?.Policy.ToString(), savedPolicy = ListeningProgress?.Resume?.Policy.ToString(),
                listeningSaveWarning,
                foreground = Native.GetForegroundWindow().ToInt64(), own = new WindowInteropHelper(this).Handle.ToInt64(),
                handled = input?.Handled, originalSource = input?.OriginalSource?.GetType().Name });
        }
        void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException(message + "；详细前提见 shortcut-control-diagnostics.json"); Check(true, message); }
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
            pack.Validate(); string file = Path.Combine(root, "pack.json"); Json.Save(file, pack); SetLibrary(root); LoadPack(file);
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
            Prepare(); await PrepareIsolatedTestForeground(this, "F4目录检查无法取得播放器前台");
            Local(Key.F4); await Drain(); Diagnostic("catalog-after-F4", chapterPickerButtons[LibraryBox].Chapter);
            Check(Tabs.SelectedItem==StoryTab && chapterPickerButtons[LibraryBox].Chapter.IsKeyboardFocusWithin, "面板 F4 聚焦章节目录");
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

            // 真实控件上的隧道事件检查窗口路由；再发冒泡事件检查原生调整仍然可用。
            var appearance = SettingsContent.Children.OfType<Expander>().Single(x => Equals(x.Header, "外观与声音"));
            var scaleBox = AppearanceSettingsContent.Children.OfType<ComboBox>().First();
            double priorScale = preferences.ReadingScale, priorVolume = VolumeSlider.Value;
            async Task PrepareSettings()
            {
                Prepare(); Expand(SettingsTab); appearance.IsExpanded = true; scaleBox.BringIntoView(); scaleBox.Focus();
                await PrepareIsolatedTestForeground(this, "设置控件检查无法取得播放器前台");
                UpdateLayout(); await Drain(); scaleBox.BringIntoView(); scaleBox.Focus(); await Drain();
                Diagnostic("settings-loaded", scaleBox);
                Require(scaleBox.IsLoaded && scaleBox.IsVisible && scaleBox.IsKeyboardFocusWithin, "设置下拉已加载可见且实际取得键盘焦点");
            }
            KeyEventArgs NativeControlKey(Key key, UIElement source)
            {
                var args = Local(key, source);
                if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; source.RaiseEvent(args); }
                return args;
            }
            var controlKeys = new[] { Key.PageUp, Key.PageDown, Key.Up, Key.Down, Key.Home, Key.End, Key.Space, Key.Enter };
            foreach (bool open in new[] { false, true })
            {
                await PrepareSettings(); scaleBox.IsDropDownOpen = open; await Drain();
                Diagnostic(open ? "settings-open-before-keys" : "settings-closed-before-keys", scaleBox);
                Require(scaleBox.IsDropDownOpen == open, open ? "设置下拉实际展开后检查按键" : "设置下拉实际收起后检查按键");
                before = playCalls;
                foreach (var key in controlKeys)
                {
                    var routed = Local(key, scaleBox);
                    Check(!routed.Handled && engine.CurrentId == "a" && playCalls == before && expanded && Tabs.SelectedItem == SettingsTab,
                        $"设置下拉{(open ? "展开" : "收起")}时 {key} 交原控件，不播放或切换剧情");
                }
                preferences.Keys["replay"] = "J";
                var letter = Local(Key.J, scaleBox);
                Check(!letter.Handled && engine.CurrentId == "a" && playCalls == before,
                    $"设置下拉{(open ? "展开" : "收起")}时改绑 J 不抢文字检索");
                preferences.Keys["replay"] = "F5";
                if (open)
                {
                    Diagnostic("settings-before-escape", scaleBox);
                    Require(scaleBox.IsDropDownOpen, "设置Esc场景发键前下拉仍实际展开");
                    var escape = Local(Key.Escape, scaleBox);
                    Diagnostic("settings-after-escape", scaleBox, escape);
                    Check(escape.Handled && !scaleBox.IsDropDownOpen && expanded && engine.CurrentId == "a" && playCalls == before,
                        "设置下拉 Esc 仅关闭下拉，不收起面板或改变剧情");
                }
                scaleBox.IsDropDownOpen = false;
            }
            await PrepareSettings(); scaleBox.SelectedIndex = 0; before = playCalls;
            NativeControlKey(Key.Down, scaleBox);
            Check(scaleBox.SelectedIndex == 1 && preferences.ReadingScale == 1.25 && engine.CurrentId == "a" && playCalls == before,
                "阅读比例原生 Down 仍调整并保存设置，不播放");
            VolumeSlider.Focus(); before = playCalls;
            foreach (var key in controlKeys.Concat(new[] { Key.Left, Key.Right }))
            {
                var routed = Local(key, VolumeSlider);
                Check(!routed.Handled && engine.CurrentId == "a" && playCalls == before && expanded,
                    $"音量滑块 {key} 不被剧情快捷键抢走");
            }
            VolumeSlider.Value = 50; NativeControlKey(Key.Right, VolumeSlider);
            Check(VolumeSlider.Value > 50 && preferences.Volume == VolumeSlider.Value && engine.CurrentId == "a" && playCalls == before,
                "音量滑块原生 Right 仍调整并保存音量，不播放");
            Local(Key.F5, VolumeSlider);
            Check(playCalls == before + 1 && engine.CurrentId == "a", "设置滑块仍保留 F5 明确重播快捷键");
            scaleBox.SelectedIndex = Array.IndexOf(new[] { 1d, 1.25, 1.5, 2d }, priorScale);
            VolumeSlider.Value = priorVolume;

            game = null; Expand(listeningTab); OpenListeningPack(file, "chapter"); StartListening();
            listeningOptions.IsExpanded = true;
            await PrepareIsolatedTestForeground(this, "听书策略检查无法取得播放器前台");
            UpdateLayout(); await Drain(); listeningPolicy.BringIntoView(); listeningPolicy.Focus(); await Drain();
            Diagnostic("listening-policy-loaded", listeningPolicy);
            Require(listeningPolicy.IsLoaded && listeningPolicy.IsVisible && listeningPolicy.IsKeyboardFocusWithin, "听书策略已加载可见且实际取得键盘焦点");
            long policyTicket = listeningTicket; string? policyPosition = listeningSession?.Current?.NodeId;
            before = playCalls;
            foreach (var key in controlKeys)
            {
                var routed = Local(key, listeningPolicy);
                Check(!routed.Handled && listeningTicket == policyTicket && listeningRunning && listeningSession?.Current?.NodeId == policyPosition && playCalls == before,
                    $"听书策略下拉 {key} 先交原控件，不误调用听书上一句或下一句");
            }
            listeningPolicy.IsDropDownOpen = true; await Drain(); Diagnostic("listening-before-escape", listeningPolicy);
            Require(listeningPolicy.IsDropDownOpen, "听书Esc场景发键前下拉实际展开");
            var policyEscape = Local(Key.Escape, listeningPolicy); Diagnostic("listening-after-escape", listeningPolicy, policyEscape);
            Check(!listeningPolicy.IsDropDownOpen && expanded && listeningRunning && listeningTicket == policyTicket && listeningSession?.Current?.NodeId == policyPosition,
                "听书策略下拉 Esc 仅关闭下拉，保播放票据和当前句");
            // Popup关闭和焦点还原由Dispatcher完成，下一次实体按键也会发生在其后。
            await Drain(); listeningPolicy.Focus(); await Drain(); Diagnostic("listening-before-native-policy-key", listeningPolicy);
            Require(listeningPolicy.IsLoaded && listeningPolicy.IsVisible && listeningPolicy.IsKeyboardFocusWithin && !listeningPolicy.IsDropDownOpen,
                "听书原生策略调整前下拉已关闭并稳定取得焦点");
            int priorPolicy = listeningPolicy.SelectedIndex;
            int expectedPolicyIndex = priorPolicy == 0 ? 1 : priorPolicy - 1;
            var expectedPolicy = expectedPolicyIndex switch { 1 => ListeningBranchPolicy.First, 2 => ListeningBranchPolicy.All, _ => ListeningBranchPolicy.Manual };
            var policyKey = NativeControlKey(priorPolicy == 0 ? Key.Down : Key.Up, listeningPolicy);
            Diagnostic("listening-after-native-policy-key", listeningPolicy, policyKey); await Drain();
            Diagnostic("listening-after-native-policy-drain", listeningPolicy);
            Check(listeningPolicy.SelectedIndex == expectedPolicyIndex && listeningSession?.Policy == expectedPolicy && !listeningRunning && listeningSession?.Current?.NodeId == policyPosition && playCalls == before,
                "听书策略原生调整按既有规则暂停并保存策略，不跳句或重播");
            var savedPolicy = listeningStore!.Load(pack.Id).ForChapter("chapter").Resume;
            Check(savedPolicy?.Policy == expectedPolicy && savedPolicy.NodeId == policyPosition && listeningProgressWarning.Length == 0,
                "听书原生键选中的具体策略和原当前句实际保存并可重读");
            Prepare();

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
        Directory.CreateDirectory(Log.DataDir);File.WriteAllLines(Path.Combine(Log.DataDir,"shortcut-ui-test.txt"),results);
        Json.Save(Path.Combine(Log.DataDir,"shortcut-control-diagnostics.json"),controlDiagnostics);Close();
    }
}
