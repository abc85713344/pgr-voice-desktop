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
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    // 合成输入经过实际业务路由；只聚焦本进程夹具，不发送系统按键/点击，不启动设备或音频。
    async Task RunHotSwitchUiTest()
    {
        var report = new List<string>();
        Window? scene = null;
        int scenarios = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message +
                $" [node={engine?.CurrentId}, mode={engine?.Mode}, source={followInputs.ActiveSource}, plays={playCalls}, pendingMouse={pendingMouseFollow != null}, foreground={Native.GetForegroundWindow()}]");
            report.Add("PASS: " + message);
        }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Foreground(Window window)
        {
            if (!testUi || (window != this && window != scene))
                throw new InvalidOperationException("前台准备只允许本轮隔离验收窗口。");
            window.Show(); window.WindowState = WindowState.Normal;
            var handle = new WindowInteropHelper(window).Handle;
            GetWindowThreadProcessId(handle, out uint processId);
            if (handle == IntPtr.Zero || processId != Environment.ProcessId)
                throw new InvalidOperationException("拒绝激活非本进程窗口。");
            window.Activate(); Native.SetForegroundWindow(handle); await Drain();
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != handle)
            {
                uint own = GetCurrentThreadId(), front = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = front != 0 && front != own && AttachThreadInput(own, front, true);
                try { window.Activate(); Native.SetForegroundWindow(handle); }
                finally { if (attached) AttachThreadInput(own, front, false); }
                // 输入线程连接只包住同步激活，不跨 await。
                await Drain();
                for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            }
            if (Native.GetForegroundWindow() != handle)
            {
                var actual = Native.GetForegroundWindow(); GetWindowThreadProcessId(actual, out uint actualProcess);
                string name;
                try { using var process = Process.GetProcessById((int)actualProcess); name = process.ProcessName; }
                catch { name = "未知"; }
                throw new InvalidOperationException($"隔离窗口未取得前台：期望 HWND={handle}/PID={processId}，实际 HWND={actual}/PID={actualProcess}/{name}");
            }
        }
        async Task Scenario(string name, Func<Task> test)
        {
            scenarios++;
            try { await test(); }
            catch (Exception ex) { report.Add("FAIL: " + name + "：" + ex); }
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("热切换验收只能从隔离测试入口运行。");
            timer.Stop(); gamepadTimer?.Stop(); listeningSaveTimer?.Stop();
            rawKeyboard?.Dispose(); keyboard?.Dispose(); keyboard = null; StopGamepadInput();
            preferences.Keys = new Preferences().Keys;
            preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            preferences.GamepadEnabled = true; preferences.GamepadFollowEnabled = true;
            preferences.MouseFollowEnabled = true; preferences.ClickZoneEnabled = true;
            preferences.GamepadBindings = GamepadLayout.Defaults(); preferences.GamepadModifier = "Back";
            preferences.GamepadAdvanceButton = "South"; listeningTestAudio = true;
            string folder = Path.Combine(Log.DataDir, "fixtures", "input-switch"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "voice.wav"), new byte[] { 0 });
            var pack = new Pack
            {
                Id = "input-switch-fixture", Title = "输入热切换隔离验收", Root = folder,
                Chapters = new() { new() { Id = "chapter", Title = "热切换测试", Sections = new() { new() { Id = "section", Title = "共同线与分支", StartId = "a" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="section", Text="第一句", Audio="voice.wav", NextId="b" },
                    new() { Id="b", SectionId="section", Text="第二句", Audio="voice.wav", NextId="c" },
                    new() { Id="c", SectionId="section", Text="第三句", Audio="voice.wav", NextId="d" },
                    new() { Id="d", SectionId="section", Text="第四句", Audio="voice.wav", NextId="e" },
                    new() { Id="e", SectionId="section", Text="第五句", Audio="voice.wav", NextId="f" },
                    new() { Id="f", SectionId="section", Text="第六句", Audio="voice.wav", NextId="menu" },
                    new() { Id="menu", SectionId="section", Kind="choice", Text="请选择路线", Options=new()
                    {
                        new() { Id="one", Label="路线一", PathId="one", TargetId="r1", MergeId="merge", Verified=true },
                        new() { Id="two", Label="路线二", PathId="two", TargetId="r2", MergeId="merge", Verified=true }
                    } },
                    new() { Id="r1", SectionId="section", Text="路线一正文", PathId="one", Audio="voice.wav", NextId="merge" },
                    new() { Id="r2", SectionId="section", Text="路线二正文", PathId="two", Audio="voice.wav", NextId="merge" },
                    new() { Id="merge", SectionId="section", Kind="merge", NextId="tail" },
                    new() { Id="tail", SectionId="section", Text="共同线结束", Audio="voice.wav" }
                }
            };
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); LoadPack(file);
            var fixtureButton = new Button { Content = "隔离场景：只合成观察事件，不点击游戏", Margin = new Thickness(35) };
            int realClicks = 0; fixtureButton.Click += (_, _) => realClicks++;
            scene = new Window { Title = "输入热切换隔离场景", Left = 150, Top = 120, Width = 900, Height = 600,
                Background = Brushes.DarkSlateGray, Content = fixtureButton, ShowInTaskbar = false };
            scene.Show(); var handle = new WindowInteropHelper(scene).Handle;
            var device = new GamepadDevice(7101, "热切换合成 Xbox", GamepadFamily.Xbox);
            gamepadDevices = new[] { device }; RefreshGamepadControls();
            GamepadButtons previous = GamepadButtons.None;
            void Send(GamepadButtons buttons, bool connected = true, long timestamp = 0)
            {
                var reading = new GamepadReading(connected ? device : null, connected, buttons, buttons & ~previous,
                    previous & ~buttons, 0, 0, 0, 0, 0, 0, timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp);
                previous = buttons; gamepadLatest = reading; HandleGamepadReading(reading, Native.GetForegroundWindow());
            }
            void KeyNext() => HandleGlobal(Key.Space, handle, Stopwatch.GetTimestamp());
            ObservedMouseInput Down()
            {
                var point = clickZone.PointToScreen(new Point(32, 32)); long now = Stopwatch.GetTimestamp();
                return new("鼠标左键", handle, (int)point.X, (int)point.Y, now, now, new IntPtr(7102));
            }
            void End(ObservedMouseInput down)
            {
                long now = Stopwatch.GetTimestamp();
                HandleMouseGesture(new(down, down with { Timestamp = now, ReceivedTimestamp = now }, true, ""));
            }
            async Task MouseNext() => await ObserveMouseTapForTest(Down());
            async Task PadNext() { Send(GamepadButtons.South); await Drain(); Send(GamepadButtons.None); await Drain(); }
            async Task Reset(string node = "a")
            {
                CancelPendingMouseFollow(); StopAutomatic("准备热切换验收", false); StopListeningForGame(); HideBranchMenu();
                gamepadTest.IsChecked = false; keyTestBox.IsChecked = false;
                preferences.MouseFollowEnabled = true; preferences.GamepadEnabled = true; preferences.GamepadFollowEnabled = true;
                preferences.ClickZoneEnabled = true; game = new(handle, scene.Title, "InputSwitchFixture");
                Send(GamepadButtons.None); engine!.Commit(node);
                Collapse(false); UpdateClickZone(); clickZone.PlaceAt(scene.Left + 140, scene.Top + 160);
                clickZone.SetDraggable(false); clickZone.UpdateLayout();
                await Foreground(scene); fixtureButton.Focus();
                Send(GamepadButtons.None); ResetGamepadContext(); TickGamepad();
                DialoguePositionConfirmed(); await Drain();
                if (engine.Mode != RunMode.Following || followInputs.ActiveSource.ToString() != "None")
                    throw new InvalidOperationException("场景没有从已对齐且无输入来源的位置开始。");
            }

            await Scenario("在线手柄不独占与正常热切换", async () =>
            {
                await Reset(); int plays = playCalls;
                Check(GamepadConnected && preferences.GamepadFollowEnabled && preferences.MouseFollowEnabled, "手柄在线且手柄、鼠标跟随同时启用");
                KeyNext(); await Drain();
                Check(engine!.CurrentId == "b" && playCalls == plays + 1 && followInputs.ActiveSource.ToString() == "Keyboard", "在线手柄不屏蔽键盘下一句");
                await Task.Delay(270); await MouseNext();
                Check(engine.CurrentId == "c" && playCalls == plays + 2 && followInputs.ActiveSource.ToString() == "Mouse", "键盘后可直接用热区鼠标接管一次");
                await Task.Delay(270); await PadNext();
                Check(engine.CurrentId == "d" && playCalls == plays + 3 && followInputs.ActiveSource.ToString() == "Gamepad", "鼠标后可直接用手柄接管一次");
                await Task.Delay(270); KeyNext(); await Drain();
                Check(engine.CurrentId == "e" && playCalls == plays + 4 && followInputs.ActiveSource.ToString() == "Keyboard", "手柄后可直接切回键盘且没有补推进");
                Check(preferences.GamepadFollowEnabled && preferences.MouseFollowEnabled && engine.Mode == RunMode.Following, "正常互切全程无需改设置或再次确认位置");
            });
            await Scenario("手柄先到的近邻映射", async () =>
            {
                await Reset(); int plays = playCalls;
                Send(GamepadButtons.South); KeyNext(); await MouseNext(); await Drain();
                Check(engine!.CurrentId == "b" && playCalls == plays + 1, "手柄按下后的键盘与鼠标近邻副本合计只推进一次");
                Send(GamepadButtons.None); await Drain();
                Check(engine.CurrentId == "b" && engine.Mode == RunMode.Following, "近邻副本和手柄松开不会误暂停跟随");
            });
            await Scenario("键盘映射先于手柄", async () =>
            {
                await Reset(); int plays = playCalls;
                KeyNext(); Send(GamepadButtons.South); Send(GamepadButtons.None); await Drain();
                Check(engine!.CurrentId == "b" && playCalls == plays + 1, "键盘先推进后再收到手柄副本仍只有一次");
                Check(engine.Mode == RunMode.Following, "较晚手柄副本不会把正常跟随误暂停");
            });
            await Scenario("鼠标按下与松开之间收到手柄副本", async () =>
            {
                await Reset(); int plays = playCalls; var down = Down(); HandleMouseGlobal(down); await Drain();
                Check(engine!.CurrentId == "a" && pendingMouseFollow != null && playCalls == plays, "鼠标按下只预约本次输入");
                Send(GamepadButtons.South); await Drain();
                Check(engine.CurrentId == "a" && playCalls == plays, "鼠标未松开时近邻手柄副本不能提前推进");
                End(down); await Drain(); Send(GamepadButtons.None); await Drain();
                Check(engine.CurrentId == "b" && playCalls == plays + 1, "合法鼠标松开完成原票据且只推进一次");
                End(down); await Drain();
                Check(engine.CurrentId == "b" && playCalls == plays + 1, "重复旧鼠标松开没有第二次推进");
            });
            await Scenario("手柄长按与松开映射", async () =>
            {
                await Reset(); int plays = playCalls; Send(GamepadButtons.South);
                await Task.Delay(680); KeyNext(); await MouseNext(); Send(GamepadButtons.South); await Drain();
                Check(engine!.CurrentId == "b" && playCalls == plays + 1, "手柄按住 680ms 后的键鼠镜像仍只算一次");
                Send(GamepadButtons.None); KeyNext(); await MouseNext(); await Drain();
                Check(engine.CurrentId == "b" && playCalls == plays + 1, "手柄实际松开后的近邻键鼠镜像不再计数");
                await Task.Delay(90); Send(GamepadButtons.None);
                await Task.Delay(90); Send(GamepadButtons.None);
                await Task.Delay(90); Send(GamepadButtons.None); KeyNext(); await Drain();
                Check(engine.CurrentId == "c" && playCalls == plays + 2 && followInputs.ActiveSource.ToString() == "Keyboard", "重复 neutral 不延长保护窗，松开 270ms 后键盘可接管");
            });
            await Scenario("未完成鼠标轻点遇到新的真实来源", async () =>
            {
                await Reset(); int plays = playCalls; var down = Down(); HandleMouseGlobal(down);
                await Task.Delay(270); KeyNext(); await Drain();
                Check(engine!.CurrentId == "a" && playCalls == plays && engine.Mode == RunMode.Paused, "鼠标按住超过关联窗再按键盘会暂停核对，不补推进");
                End(down); await Drain();
                Check(engine.CurrentId == "a" && playCalls == plays && engine.Mode == RunMode.Paused, "冲突后的旧鼠标松开不能恢复或推进");
            });
            foreach (string source in new[] { "Keyboard", "Mouse" })
            {
                await Scenario(source + "接管后闲置手柄断开及故障", async () =>
                {
                    await Reset(); if (source == "Keyboard") { KeyNext(); await Drain(); } else await MouseNext();
                    int plays = playCalls; Send(GamepadButtons.None, false); await Drain();
                    Check(engine!.CurrentId == "b" && engine.Mode == RunMode.Following && playCalls == plays && followInputs.ActiveSource.ToString() == source,
                        source + "接管后拔掉闲置手柄不暂停、不改位置");
                    PauseForGamepadChange("合成手柄监听故障"); await Drain();
                    Check(engine.Mode == RunMode.Following && engine.CurrentId == "b" && playCalls == plays,
                        source + "接管后手柄故障不会暂停当前来源");
                    Send(GamepadButtons.None); await Drain();
                    Check(engine.Mode == RunMode.Following && followInputs.ActiveSource.ToString() == source, "neutral 重连不抢走 " + source + " 来源");
                    await Task.Delay(270); KeyNext(); await Drain();
                    Check(engine.CurrentId == "c" && playCalls == plays + 1, source + "场景在手柄重连后仍可直接键盘推进");
                });
            }
            await Scenario("活跃手柄断开与重新对齐", async () =>
            {
                await Reset(); await PadNext(); int plays = playCalls; Send(GamepadButtons.None, false); await Drain();
                Check(engine!.CurrentId == "b" && engine.Mode == RunMode.Paused && playCalls == plays, "活跃手柄断开暂停核对，保留当前句");
                Send(GamepadButtons.None); await Drain();
                Check(engine.CurrentId == "b" && engine.Mode == RunMode.Paused && playCalls == plays, "neutral 重连不自动恢复被暂停的手柄会话");
                engine.ConfirmCurrentPosition(); DialoguePositionConfirmed(); KeyNext(); await Drain();
                Check(engine.CurrentId == "c" && engine.Mode == RunMode.Following && playCalls == plays + 1, "核对位置后可立即改用键盘，无旧手柄保护残留");
            });
            await Scenario("没有当前来源时手柄连接故障", async () =>
            {
                await Reset(); int plays = playCalls;
                Send(GamepadButtons.None, false); Send(GamepadButtons.None); PauseForGamepadChange("合成闲置手柄故障"); await Drain();
                Check(engine!.Mode == RunMode.Following && engine.CurrentId == "a" && playCalls == plays && followInputs.ActiveSource.ToString() == "None",
                    "尚未操作时的 neutral 接入、断开和故障不抢来源也不暂停");
            });
            await Scenario("听书不受闲置手柄断开故障影响", async () =>
            {
                await Reset(); await PadNext();
                OpenListeningPack(file, "chapter"); StartListening(); await Drain();
                Check(ListeningActive && listeningRunning && listeningTicket != 0, "听书以合成音频票据开始，不打开真实声音设备");
                long ticket = listeningTicket; string? node = listeningSession?.Current?.NodeId;
                Send(GamepadButtons.None, false); PauseForGamepadChange("合成听书期间手柄故障"); await Drain();
                Check(ListeningActive && listeningRunning && listeningTicket == ticket && listeningSession?.Current?.NodeId == node,
                    "此前使用过手柄也不使听书因手柄断开或故障暂停");
                Send(GamepadButtons.None); await Drain();
                Check(listeningRunning && listeningTicket == ticket, "neutral 重连保持当前听书票据");
            });
            await Scenario("旧监听队列不污染新会话", async () =>
            {
                await Reset(); KeyNext(); await Drain(); await Task.Delay(270);
                int plays = playCalls; var expectedReading = gamepadLatest;
                var savedInput = gamepadInput;
                // 构造本身只保存工厂；从不 Start，若误启动后端则立即失败。
                using var obsolete = new GamepadInput(() => throw new InvalidOperationException("旧队列测试禁止启动设备后端"));
                using var replacement = new GamepadInput(() => throw new InvalidOperationException("旧队列测试禁止启动设备后端"));
                try
                {
                    var stale = new GamepadReading(device, true, GamepadButtons.South, GamepadButtons.South,
                        GamepadButtons.None, 0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp());
                    gamepadQueue.Enqueue((obsolete, stale, handle));
                    gamepadInput = replacement; DrainGamepadInput(); await Drain();
                    Check(engine!.CurrentId == "b" && engine.Mode == RunMode.Following && playCalls == plays,
                        "旧监听已经排队的手柄按下不能推进新会话");
                    Check(ReferenceEquals(gamepadLatest, expectedReading) && followInputs.ActiveSource.ToString() == "Keyboard",
                        "丢弃旧监听队列时不替换最新手柄快照或当前键盘来源");
                }
                finally { gamepadInput = savedInput; while (gamepadQueue.TryDequeue(out _)) { } }
            });
            await Scenario("人工核对使排队旧输入失效", async () =>
            {
                await Reset(); var down = Down(); HandleMouseGlobal(down);
                long oldKeyboardTime = Stopwatch.GetTimestamp(); int plays = playCalls;
                await Task.Delay(5); DialoguePositionConfirmed();
                HandleGlobal(Key.Space, handle, oldKeyboardTime); End(down); await Drain();
                Check(engine!.CurrentId == "a" && playCalls == plays && engine.Mode == RunMode.Following,
                    "重新确认位置后旧键盘消息与旧鼠标松开均不能推进或暂停新位置");
            });
            await Scenario("分支边界不因副本代选", async () =>
            {
                await Reset("f"); Send(GamepadButtons.South); await Drain();
                Check(engine!.Mode == RunMode.Choice && engine.CurrentId == "menu" && branchMenu.IsVisible, "下一句到达分支时保持待选");
                KeyNext(); await MouseNext(); Send(GamepadButtons.None); await Drain();
                Check(engine.Mode == RunMode.Choice && engine.CurrentId == "menu" && engine.Choices.Count == 0, "近邻键鼠副本及旧手柄松开不会代选路线");
            });
            await Scenario("绑定完成后首个输入及同会话迟到消息", async () =>
            {
                await Reset(); int plays = playCalls;
                followInputWindow = IntPtr.Zero; // 模拟上一绑定不同；真正通过绑定入口更新会话。
                BindGame(new(handle, scene.Title, "InputSwitchFixture"));
                KeyNext(); await Drain();
                Check(engine!.CurrentId == "b" && playCalls == plays + 1 && engine.Mode == RunMode.Following,
                    "重新绑定在接收输入前建立会话，首个键盘操作不被静默丢掉");
                await Reset(); plays = playCalls;
                long delayed = Stopwatch.GetTimestamp(); await Task.Delay(280);
                HandleGlobal(Key.Space, handle, delayed); await Drain();
                Check(engine.CurrentId == "a" && playCalls == plays && engine.Mode == RunMode.Paused && mouseFollowNotice.Contains("过晚"),
                    "关闭画面检查时迟到键盘输入也停住提示，不悄悄追加推进");
            });
            await Scenario("长时间积压的手柄输入", async () =>
            {
                await Reset(); KeyNext(); await Drain(); int plays = playCalls;
                long delayed = Stopwatch.GetTimestamp(); await Task.Delay(540);
                Send(GamepadButtons.None, timestamp: delayed); await Drain();
                Check(engine!.CurrentId == "b" && engine.Mode == RunMode.Following && playCalls == plays,
                    "迟到超过半秒的闲置手柄快照不暂停键盘跟随");
                Send(GamepadButtons.None); delayed = Stopwatch.GetTimestamp(); await Task.Delay(540);
                Send(GamepadButtons.South, timestamp: delayed); await Drain();
                Check(engine.CurrentId == "b" && engine.Mode == RunMode.Paused && playCalls == plays && mouseFollowNotice.Contains("过晚"),
                    "迟到超过半秒的有效手柄推进停住核对，不静默丢弃后继续跟随");
            });
            Check(realClicks == 0, "全部场景仅合成观察事件，没有点击模拟窗口或真实游戏");
            Check(!ocr.Running, "热切换验收未启动 OCR");
            report.Add("INFO: 对同进程 WPF 场景调用真实输入路由；截图检查关闭，音频使用 testUi/listeningTestAudio。没有系统输入、SDL 监听或真实游戏操作。");
        }
        catch (Exception ex) { report.Add("FAIL: 验收准备或收尾：" + ex); }
        finally
        {
            try
            {
                CancelPendingMouseFollow(); StopAutomatic("热切换隔离验收结束", false); StopListeningForGame();
                listeningTestAudio = false; HideBranchMenu(); scene?.Close();
            }
            catch (Exception ex) { report.Add("FAIL: 验收清理：" + ex); }
            int passed = report.Count(line => line.StartsWith("PASS:"));
            int failed = report.Count(line => line.StartsWith("FAIL:"));
            report.Add($"SUMMARY: scenarios={scenarios}, passed={passed}, failed={failed}");
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir, "input-switch-ui-test.txt"), report);
            // Check 的异常已经落入报告；不向全局未处理异常弹窗传播，以便隔离进程正常退出。
            Close();
        }
    }
}
