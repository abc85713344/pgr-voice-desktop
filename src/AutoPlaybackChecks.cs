using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PgrVoice;

public partial class MainWindow
{
    // 模拟环境与点击返回值；不调用 SendInput，不连接或激活真实游戏。
    async Task RunAutoPlaybackUiTest()
    {
        var report = new List<string>();
        System.Windows.Window? inputHost = null;
        int clicks = 0;
        bool environmentValid = true, clickSucceeds = true;
        using var locateCancellation = new System.Threading.CancellationTokenSource();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message +
                $" [node={engine?.CurrentId}, mode={engine?.Mode}, active={automaticRunning}, waiting={automaticWaiting}, clicks={clicks}, ticket={automaticTicket}, request={audioRequest}, status={automaticStatus.Text}]");
            report.Add("PASS: " + message);
        }
        async Task Until(Func<bool> predicate, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.ElapsedMilliseconds < 2000) await Task.Delay(10);
            Check(predicate(), message);
        }
        async Task ForegroundInputHost(System.Windows.Window window)
        {
            if (!testUi || !ReferenceEquals(window, inputHost))
                throw new InvalidOperationException("前台重试只允许操作本轮自建的隔离输入窗口。");
            window.Show(); window.WindowState = System.Windows.WindowState.Normal;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            GetWindowThreadProcessId(handle, out uint ownerPid);
            if (handle == IntPtr.Zero || ownerPid != Environment.ProcessId)
                throw new InvalidOperationException($"隔离输入窗口不属于本测试进程：句柄 {handle}，进程 {ownerPid}。");
            async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.Activate(); Native.SetForegroundWindow(handle);
            await Drain();
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != handle)
            {
                uint thread = GetCurrentThreadId(), foregroundThread = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
                try { window.Activate(); Native.SetForegroundWindow(handle); }
                finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
                // No await while attached, and never treat a failed focus request as success.
                await Drain();
                for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            }
            var actual = Native.GetForegroundWindow();
            if (actual != handle)
            {
                GetWindowThreadProcessId(actual, out uint actualPid);
                string processName;
                try { using var process = Process.GetProcessById((int)actualPid); processName = process.ProcessName; }
                catch (Exception ex) { processName = "无法读取（" + ex.GetType().Name + "）"; }
                throw new InvalidOperationException($"本进程隔离输入窗口未取得前台：{window.Title}（期望句柄 {handle}，实际句柄 {actual}，前台进程 {processName}，PID {actualPid}）。");
            }
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("自动播放检查只允许在隔离测试入口运行。");
            timer.Stop(); StopListeningForGame();
            // 手动输入场景直接调用接收入口，避免用户在另一窗口打字影响测试时序。
            rawKeyboard?.Dispose(); keyboard?.Dispose();
            preferences.DialogueGuardEnabled = false;
            preferences.OcrEnabled = false;
            string folder = Path.Combine(Log.DataDir, "fixtures", "desktop-automatic");
            Directory.CreateDirectory(folder);
            // testUi 不会解码或播放这个占位文件；它只满足“录音存在”的门控。
            File.WriteAllBytes(Path.Combine(folder, "fixture.wav"), new byte[] { 0 });
            var fixture = new Pack
            {
                Id = "desktop-automatic-fixture", Title = "自动播放隔离验收", Root = folder,
                Chapters = new() { new() { Id = "chapter", Title = "验收章节", Sections = new()
                    { new() { Id = "section", Title = "共同线与分支边界", StartId = "a" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="section", Speaker="测试角色", Text="共同线第一句。", Audio="fixture.wav", NextId="b" },
                    new() { Id="b", SectionId="section", Speaker="测试角色", Text="共同线第二句，后面是选择。", Audio="fixture.wav", NextId="choice" },
                    new() { Id="choice", SectionId="section", Kind="choice", Text="请选择路线", Options=new()
                    {
                        new() { Id="option-one", Label="路线一", TargetId="r1", PathId="one", MergeId="merge", Verified=true },
                        new() { Id="option-two", Label="路线二", TargetId="r2", PathId="two", MergeId="merge", Verified=true }
                    } },
                    new() { Id="r1", SectionId="section", Speaker="测试角色", Text="路线一内容。", Audio="fixture.wav", PathId="one", NextId="merge" },
                    new() { Id="r2", SectionId="section", Speaker="测试角色", Text="路线二内容。", Audio="fixture.wav", PathId="two", NextId="merge" },
                    new() { Id="merge", SectionId="section", Kind="merge", Text="路线汇合", NextId="tail" },
                    new() { Id="tail", SectionId="section", Speaker="测试角色", Text="共同线末句。", Audio="fixture.wav", NextId="end" },
                    new() { Id="end", SectionId="section", Kind="end", Text="小节结束" }
                }
            };
            fixture.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, fixture);
            LoadPack(file);
            Check(engine?.Pack.Id == fixture.Id, "载入合成共同线与互斥分支，使用隔离进度");
            automaticEnvironmentTest = () => environmentValid;
            automaticClickTest = () => { clicks++; return clickSucceeds; };
            automaticTestImmediate = true;
            automaticAudioStartedTest = true;

            async Task DrainAutomaticUi() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            async Task InvokeAutomaticButton()
            {
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(automaticButton);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await DrainAutomaticUi();
            }
            var locateDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delayedLocateReturned = false;
            automaticLocateTest = async () =>
            {
                ocrCancellation = locateCancellation;
                await locateDone.Task;
                // 即便底层返回已不遵守取消，外层准备代次仍必须拒绝重新开启。
                CandidatesList.ItemsSource = new[] { new MatchCandidate(engine!.Pack.ById["a"], 1, "延迟定位测试", null) };
                CandidatesList.SelectedIndex = 0; delayedLocateReturned = true;
            };
            await InvokeAutomaticButton();
            Check(automaticPreparingOwner == engine && automaticPendingOwner == null && automaticButton.Content?.ToString() == "取消自动播放准备",
                "定位尚未返回时自动按钮已进入可取消准备状态");
            await InvokeAutomaticButton();
            Check(automaticPreparingOwner == null && automaticPendingOwner == null && locateCancellation.IsCancellationRequested && !automaticRunning,
                "准备中再次调用实际自动按钮会取消定位并清除准备状态");
            locateDone.SetResult(true);
            await Until(() => delayedLocateReturned, "模拟取消后仍迟到的定位回调已返回"); await DrainAutomaticUi();
            Check(automaticPreparingOwner == null && automaticPendingOwner == null && !automaticRunning,
                "取消后的旧定位结果不能重新进入自动播放待确认状态");
            ocrCancellation = null; CancelOcr();
            automaticLocateTest = () =>
            {
                CandidatesList.ItemsSource = new[] { new MatchCandidate(engine!.Pack.ById["a"], 1, "正常定位测试", null) };
                CandidatesList.SelectedIndex = 0; return Task.CompletedTask;
            };
            await BeginAutomatic();
            Check(automaticPreparingOwner == null && automaticPendingOwner == engine && CandidatesList.Items.Count == 1,
                "正常定位完成仍进入候选确认，不会被准备状态自身取消");
            StopAutomatic("定位准备回归结束", false); CancelOcr();
            automaticLocateTest = () => Task.CompletedTask;
            await BeginAutomatic();
            Check(automaticPreparingOwner == null && automaticPendingOwner == null && automaticButton.Content?.ToString() == "游戏自动播放（先定位）",
                "没有定位候选时退出准备并恢复启动按钮");
            automaticLocateTest = null;

            void Prepare(string node = "a")
            {
                StopAutomatic("准备隔离场景", false);
                environmentValid = true; clickSucceeds = true;
                automaticTestImmediate = true;
                engine!.Pack.ById["a"].Audio = "fixture.wav";
                engine.Pack.ById["b"].Audio = "fixture.wav";
                engine.Pack.ById["a"].Text = "共同线第一句。";
                engine.Pack.ById["b"].Text = "共同线第二句，后面是选择。";
                engine.Pack.ById["b"].NextId = "choice";
                engine.Commit(node); DialoguePositionConfirmed();
            }
            long Start(string node = "a")
            {
                Prepare(node); StartAutomaticAtConfirmedLine();
                Check(automaticRunning && automaticNode == node && automaticOwner == engine,
                    "确认共同线后绑定当前位置与音频票据：" + node);
                return automaticTicket;
            }

            // Successful start callbacks may precede auto arming; preserve only
            // the exact current request, never a prior clip's callback.
            automaticAudioStartedTest = false;
            Prepare(); OnAutomaticAudioStarted(audioRequest); StartAutomaticAtConfirmedLine();
            Check(automaticCycle.Phase == DesktopAutoPlaybackPhase.Playing,
                "开播成功回调早于自动开启时，当前音频票据仍可正确绑定");
            StopAutomatic("提前开播回调检查结束", false);

            long clock = AutomaticNow(); automaticClockTest = () => clock;
            long unopened = Start(); int unopenedClicks = clicks;
            OnAutomaticCompleted(unopened);
            Check(automaticCycle.Phase == DesktopAutoPlaybackPhase.StartingAudio && !automaticWaiting && clicks == unopenedClicks,
                "自然完成通知抢先到达时，仍等待同票据的开播成功，不能先点游戏");
            clock += 10_000; TickAutomatic(); OnAutomaticAudioStarted(unopened); OnAutomaticCompleted(unopened);
            Check(!automaticRunning && clicks == unopenedClicks && automaticRecoveryText.Text.Contains("10 秒") && automaticRecoveryText.Text.Contains("尚未点击"),
                "音频十秒未开播会停止，迟到成功或完成不复活，明确尚未点击");
            string startupNotice = automaticRecoveryText.Text;
            StopAutomatic("普通界面状态刷新", false);
            Check(automaticRecovery.Visibility == System.Windows.Visibility.Visible && automaticRecoveryText.Text == startupNotice,
                "自动暂停原因长期保留，普通状态更新不会清掉恢复提示");
            automaticClockTest = null; automaticAudioStartedTest = true;

            long first = Start(); int before = clicks; int plays = playCalls;
            OnAutomaticCompleted(first - 1);
            Check(clicks == before && engine!.CurrentId == "a" && !automaticWaiting,
                "过期自然完成票据不能产生点击或推进");
            OnAutomaticCompleted(first); OnAutomaticCompleted(first);
            Check(automaticWaiting && clicks == before, "自然完成先等待，重复回调不能提交第二次点击");
            await Until(() => engine!.CurrentId == "b" && !automaticWaiting, "自然完成后共同线只前进一句");
            Check(clicks == before + 1 && playCalls == plays + 1 && automaticTicket != first,
                "一条音频只对应一次点击和一次新句播放请求");
            OnAutomaticCompleted(first); await Task.Delay(30);
            Check(engine!.CurrentId == "b" && clicks == before + 1, "旧句重复完成不能越过新句");
            OnAutomaticCompleted(automaticTicket); await Task.Delay(30);
            Check(!automaticRunning && engine.CurrentId == "b" && clicks == before + 1 && engine.Choices.Count == 0,
                "共同线末句后遇选择点停止，不代选路线或点击进入选择");

            long stopped = Start(); before = clicks;
            StopAutomatic("测试主动停止"); OnAutomaticCompleted(stopped); await Task.Delay(30);
            Check(!automaticRunning && !automaticWaiting && engine!.CurrentId == "a" && clicks == before,
                "主动停止后迟到自然完成不能恢复自动播放");

            long duringWait = Start(); before = clicks;
            automaticTestImmediate = false; automaticDelay.SelectedIndex = 0;
            OnAutomaticCompleted(duringWait);
            Check(automaticWaiting, "正常等待阶段可见且尚未点击");
            StopAutomatic("测试等待期间取消"); await Task.Delay(700);
            Check(!automaticRunning && !automaticWaiting && engine!.CurrentId == "a" && clicks == before,
                "语音结束后的等待期间取消，会拒绝已经挂起的点击任务");

            long failedClick = Start(); before = clicks; clickSucceeds = false;
            OnAutomaticCompleted(failedClick);
            await Until(() => !automaticRunning, "模拟点击失败会停止自动播放");
            Check(engine!.CurrentId == "a" && clicks == before + 1, "失败点击不推进游标，也不会重试");

            long thrownClick = Start(); before = clicks;
            automaticClickTest = () => { clicks++; throw new IOException("模拟点击适配器异常"); };
            OnAutomaticCompleted(thrownClick);
            await Until(() => !automaticRunning, "点击适配器抛异常会安全收束自动会话");
            OnAutomaticCompleted(thrownClick); await Task.Delay(30);
            Check(engine!.CurrentId == "a" && !automaticWaiting && clicks == before + 1 && automaticStatus.Text.Contains("模拟点击适配器异常"),
                "异常点击不推进、不重试，错误显示在自动播放状态中");
            automaticClickTest = () => { clicks++; return clickSucceeds; };

            long changedEnvironment = Start(); before = clicks; environmentValid = false;
            OnAutomaticCompleted(changedEnvironment); await Task.Delay(30);
            Check(!automaticRunning && engine!.CurrentId == "a" && clicks == before,
                "完成时窗口环境失效会停止，不产生点击");

            long changedAfterClick = Start(); before = clicks;
            automaticClickTest = () => { clicks++; environmentValid = false; return true; };
            OnAutomaticCompleted(changedAfterClick);
            await Until(() => !automaticRunning, "点击提交后环境改变也会结束本次自动会话");
            Check(engine!.CurrentId == "a" && clicks == before + 1,
                "点击后的等待期间失效，不凭旧回调播放下一句，交由用户重新定位");
            Check(automaticRecoveryText.Text.Contains("已发送一次点击") && automaticRecoveryText.Text.Contains("不会补点"),
                "点击已经发送后中断，暂停提示明确告知核对位置且不会补点");
            automaticClickTest = () => { clicks++; return clickSucceeds; };

            long nextClock = AutomaticNow(); automaticClockTest = () => nextClock;
            long missingStart = Start(); before = clicks;
            automaticClickTest = () => { clicks++; automaticAudioStartedTest = false; return true; };
            OnAutomaticCompleted(missingStart);
            await Until(() => engine!.CurrentId == "b" && automaticCycle.Phase == DesktopAutoPlaybackPhase.StartingAudio,
                "点击后已顺序请求下一句，等待独立开播回调");
            nextClock += 10_000; TickAutomatic();
            Check(!automaticRunning && clicks == before + 1 && automaticRecoveryText.Text.Contains("10 秒") && automaticRecoveryText.Text.Contains("已发送一次点击"),
                "下一句开播超时也会停止，保留已点击事实，不重复点击");
            automaticClockTest = null; automaticAudioStartedTest = true;
            automaticClickTest = () => { clicks++; return clickSucceeds; };

            long manuallyStopped = Start(); before = clicks;
            // 主窗前台的按键由 WPF 去重处理。另建本测试进程窗口，验证外部前台的 Raw 入口，
            // 不借用或切换到任何用户窗口，也不发送系统按键。
            inputHost = new System.Windows.Window
            {
                Title = "自动播放输入隔离窗口", Width = 80, Height = 60,
                ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.ToolWindow
            };
            try
            {
                await ForegroundInputHost(inputHost);
                IntPtr inputHandle = new System.Windows.Interop.WindowInteropHelper(inputHost).Handle;
                Check(Native.GetForegroundWindow() == inputHandle, "测试进程自建窗口取得输入前台");
                HandleGlobal(Key.LeftShift, inputHandle, Stopwatch.GetTimestamp());
            }
            finally { inputHost.Close(); inputHost = null; }
            OnAutomaticCompleted(manuallyStopped); await Task.Delay(30);
            Check(!automaticRunning && engine!.CurrentId == "a" && clicks == before,
                "手动按键先停止自动会话，旧完成回调不再推进");

            long mouseStopped = Start(); before = clicks;
            long inputTime = Stopwatch.GetTimestamp();
            HandleMouseGlobal(new ObservedMouseInput("鼠标左键", Native.GetForegroundWindow(), 0, 0, inputTime, inputTime));
            OnAutomaticCompleted(mouseStopped); await Task.Delay(30);
            Check(!automaticRunning && engine!.CurrentId == "a" && clicks == before,
                "手动鼠标入口停止自动会话，旧完成回调不再推进");

            long changedTicket = Start(); before = clicks;
            StopAudio(); TickAutomatic(); OnAutomaticCompleted(changedTicket); await Task.Delay(30);
            Check(!automaticRunning && engine!.CurrentId == "a" && clicks == before,
                "音频停止或替换改变请求后，自动会话立即失效");

            long pausedTicket = Start(); before = clicks;
            engine!.TogglePause(); TickAutomatic(); OnAutomaticCompleted(pausedTicket); await Task.Delay(30);
            Check(!automaticRunning && engine.CurrentId == "a" && clicks == before,
                "剧情暂停会终止自动会话，暂停后的自然完成不产生点击");

            Prepare(); engine!.Pack.ById["a"].Audio = null; before = clicks;
            StartAutomaticAtConfirmedLine();
            Check(!automaticRunning && clicks == before && engine.CurrentId == "a",
                "当前句未配音时拒绝启动自动播放");
            long missingNext = Start(); before = clicks; engine!.Pack.ById["b"].Audio = null;
            OnAutomaticCompleted(missingNext); await Task.Delay(30);
            Check(!automaticRunning && engine.CurrentId == "a" && clicks == before,
                "下一句未配音时留在当前句，不跳过缺音频或点击游戏");
            Check(automaticRecoveryText.Text.Contains("尚未点击") && automaticRecoveryText.Text.Contains("更新配音包"),
                "缺音频暂停给出未点击事实及手动继续或更新配音包的下一步");

            Prepare(); engine!.Commit("choice"); engine.SelectBranch(0); before = clicks;
            StartAutomaticAtConfirmedLine();
            Check(!automaticRunning && engine.CurrentId == "r1" && clicks == before,
                "确认进入已核实支线也不能开启共同线自动播放");

            long endTicket = Start("tail"); before = clicks;
            OnAutomaticCompleted(endTicket); await Task.Delay(30);
            Check(!automaticRunning && engine!.CurrentId == "tail" && clicks == before,
                "小节末句自然结束不跨小节、不补发点击");
            long silentClock = 100_000; automaticClockTest = () => silentClock;
            long StartSilent(string text = "。。。。。。", string? audioPath = null)
            {
                Prepare();
                engine!.Pack.ById["a"].Text = text; engine.Pack.ById["a"].Audio = audioPath;
                engine.Commit("a"); DialoguePositionConfirmed(); StartAutomaticAtConfirmedLine();
                return automaticTicket;
            }
            long silent = StartSilent(); before = clicks;
            Check(automaticRunning && automaticCycle.Phase == DesktopAutoPlaybackPhase.SilentPause,
                "纯标点起点没有录音时进入独立停顿，不误报缺音");
            OnAutomaticCompleted(silent); OnAutomaticAudioStarted(silent);
            silentClock += 1199; TickAutomatic();
            Check(clicks == before && automaticCycle.Phase == DesktopAutoPlaybackPhase.SilentPause,
                "旧音频回调不能结束标点停顿，1199毫秒不点击");
            silentClock++; TickAutomatic(); TickAutomatic();
            await Until(() => engine!.CurrentId == "b" && !automaticWaiting, "标点停顿满1200毫秒后接上下一句配音");
            Check(clicks == before + 1 && automaticCycle.Phase == DesktopAutoPlaybackPhase.Playing,
                "标点到正常配音只点击一次，正常音频已开播");

            silent = StartSilent(); before = clicks;
            engine!.Pack.ById["b"].Text = "？！"; engine.Pack.ById["b"].Audio = null; engine.Pack.ById["b"].NextId = "tail";
            silentClock += 1200; TickAutomatic();
            await Until(() => engine.CurrentId == "b" && !automaticWaiting, "连续标点进入第二句独立停顿");
            Check(automaticCycle.Phase == DesktopAutoPlaybackPhase.SilentPause && clicks == before + 1, "连续标点不合并或连点跳过");
            OnAutomaticCompleted(silent); silentClock += 1199; TickAutomatic();
            Check(clicks == before + 1, "第二句标点同样等待完整1200毫秒");
            silentClock++; TickAutomatic();
            await Until(() => engine.CurrentId == "tail" && !automaticWaiting, "连续标点后恢复正常配音");
            Check(clicks == before + 2 && automaticCycle.Phase == DesktopAutoPlaybackPhase.Playing, "两句标点分别只授权一次点击");

            long intoSilent = Start(); before = clicks;
            engine!.Pack.ById["b"].Text = "……"; engine.Pack.ById["b"].Audio = null;
            OnAutomaticCompleted(intoSilent);
            await Until(() => engine.CurrentId == "b" && !automaticWaiting, "正常配音完成后允许进入无音频标点句");
            silentClock += 1200; TickAutomatic();
            Check(!automaticRunning && clicks == before + 1 && engine.CurrentId == "b", "标点句后遇分支仍停止，不点击进入选项");

            foreach (string change in new[] { "stop", "restart", "navigate", "environment", "audio", "pause" })
            {
                silent = StartSilent(); before = clicks;
                switch (change)
                {
                    case "stop": StopAutomatic("标点停顿期间停止"); break;
                    case "restart": silentClock += 100; StartSilent(); break;
                    case "navigate": engine!.Commit("tail"); break;
                    case "environment": environmentValid = false; break;
                    case "audio": StopAudio(); break;
                    case "pause": engine!.TogglePause(); break;
                }
                OnAutomaticCompleted(silent);
                silentClock += change == "restart" ? 1199 : 1200; TickAutomatic();
                Check(clicks == before, "停顿中状态变化后不由旧任务点击：" + change);
                if (change == "restart")
                {
                    silentClock++; TickAutomatic();
                    await Until(() => engine!.CurrentId == "b" && !automaticWaiting, "停止重启后只有新停顿可推进");
                    Check(clicks == before + 1, "重启后不叠加旧停顿点击");
                }
            }
            StartSilent("……", "fixture.wav"); before = clicks;
            silentClock += 1200; TickAutomatic();
            Check(automaticCycle.Phase == DesktopAutoPlaybackPhase.Playing && clicks == before, "已有录音的标点必须等待真实播完，不能按停顿跳过");
            StartSilent("……", "missing.wav");
            Check(!automaticRunning, "标点声明的音频文件丢失时仍拒绝自动播放");
            foreach (string invalid in new[] { "", " ", "等等……", "（沉默）", "123", "＋" })
            {
                StartSilent(invalid); Check(!automaticRunning, "有文字、空白或非标点符号缺音时仍暂停：" + invalid);
            }
            StartSilent(); before = clicks; engine!.Pack.ById["b"].Audio = null;
            silentClock += 1200; TickAutomatic();
            Check(!automaticRunning && clicks == before, "标点后普通文字缺音也会停止且不点击");
            automaticClockTest = null;
            Check(!ocr.Running, "所有自动播放隔离场景没有启动 OCR 进程");
            Expand(StoryTab); await Task.Delay(60); Screenshot("automatic-playback-ui.png");
            report.Add("INFO: 所有点击均为测试委托计数；环境为测试委托。未调用系统输入、未激活真实游戏、未输出音频。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            StopAutomatic("自动播放隔离检查结束", false);
            await Task.Delay(30);
            automaticEnvironmentTest = null; automaticClickTest = null; automaticTestImmediate = false;
            automaticLocateTest = null; automaticAudioStartedTest = false; automaticClockTest = null;
        }
        Directory.CreateDirectory(Log.DataDir);
        File.WriteAllLines(Path.Combine(Log.DataDir, "automatic-playback-ui-test.txt"), report);
        Close();
    }
}
