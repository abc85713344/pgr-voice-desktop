using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    // 合成设备快照进入实际路由。只聚焦本测试进程自己的 WPF 窗口，不发送系统输入。
    async Task RunGamepadUiTest()
    {
        var report = new List<string>();
        Window? scene = null;
        TabItem? fixtureTab = null;
        void Check(bool valid, string message)
        {
            report.Add((valid ? "PASS: " : "FAIL: ") + message + (valid ? "" :
                $" [node={engine?.CurrentId}, mode={engine?.Mode}, plays={playCalls}, context={gamepadContext}, foreground={Native.GetForegroundWindow()}]"));
        }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Foreground(Window window)
        {
            if (!testUi || (window != this && window != scene))
                throw new InvalidOperationException("前台重试只允许操作本轮隔离验收窗口。");
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
            var handle = new WindowInteropHelper(window).Handle;
            Native.SetForegroundWindow(handle);
            await Drain();
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != handle)
            {
                uint thread = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = foreground != 0 && foreground != thread && AttachThreadInput(thread, foreground, true);
                try { window.Activate(); Native.SetForegroundWindow(handle); }
                finally { if (attached) AttachThreadInput(thread, foreground, false); }
                // 不跨 await 保留输入线程连接；成功与否仍以实际前台 HWND 为准。
                await Drain();
                for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) await Task.Delay(30);
            }
            if (Native.GetForegroundWindow() != handle)
                throw new InvalidOperationException($"本进程验收窗口未取得前台：{window.Title}（期望 {handle}，实际 {Native.GetForegroundWindow()}）");
        }
        async Task Scenario(string name, Func<Task> run)
        {
            try { await run(); }
            catch (Exception ex) { report.Add("FAIL: " + name + "：" + ex); }
        }
        void Capture(Window window, string name)
        {
            window.UpdateLayout();
            var image = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(output);
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("手柄验收只允许在隔离测试入口运行。");
            timer.Stop(); gamepadTimer?.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose(); keyboard = null;
            StopGamepadInput(); preferences.Keys = new Preferences().Keys;
            preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            preferences.GamepadEnabled = true; preferences.GamepadFollowEnabled = true;
            preferences.GamepadBindings = GamepadLayout.Defaults(); preferences.GamepadModifier = "Back";
            preferences.GamepadAdvanceButton = "South"; preferences.GamepadGlyphStyle = "auto";
            listeningTestAudio = true;
            string folder = Path.Combine(Log.DataDir, "fixtures", "gamepad"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "voice.wav"), new byte[] { 0 });
            var pack = new Pack
            {
                Id = "gamepad-fixture", Title = "手柄隔离验收", Root = folder,
                Chapters = new() { new() { Id = "chapter", Title = "手柄测试大章", Sections = new() { new() { Id = "section", Title = "共同线与分支", StartId = "a" } } } },
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
                }
            };
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); LoadPack(file);
            var xbox = new GamepadDevice(101, "Xbox 合成手柄", GamepadFamily.Xbox);
            var sony = new GamepadDevice(202, "DualSense 合成手柄", GamepadFamily.PlayStation);
            GamepadDevice device = xbox;
            gamepadDevices = new[] { xbox, sony }; RefreshGamepadControls();
            GamepadButtons previous = GamepadButtons.None;
            void Send(GamepadButtons buttons, bool connected = true, float leftX = 0, float leftY = 0, long? timestamp = null)
            {
                var reading = new GamepadReading(connected ? device : null, connected, buttons, buttons & ~previous, previous & ~buttons,
                    leftX, leftY, 0, 0, 0, 0, timestamp ?? Stopwatch.GetTimestamp());
                previous = buttons; gamepadLatest = reading;
                HandleGamepadReading(reading, Native.GetForegroundWindow());
            }
            async Task Tap(GamepadButtons buttons)
            { Send(buttons); await Drain(); Send(GamepadButtons.None); await Drain(); }
            int externalClicks = 0;
            var externalButton = new Button { Content = "模拟游戏按钮（不会发送系统输入）" };
            externalButton.Click += (_, _) => externalClicks++;
            scene = new Window { Title = "手柄验收模拟游戏", Width = 420, Height = 230, Background = Brushes.DarkSlateGray, Content = externalButton, ShowInTaskbar = false };
            scene.Show(); var gameHandle = new WindowInteropHelper(scene).Handle;
            game = new(gameHandle, scene.Title, "GamepadFixture");
            async Task Story(bool panel)
            {
                StopAutomatic("准备手柄验收", false); StopListeningForGame(); HideBranchMenu();
                gamepadTest.IsChecked = false; keyTestBox.IsChecked = false;
                game = new(gameHandle, scene.Title, "GamepadFixture");
                Send(GamepadButtons.None);
                engine!.Commit("a"); DialoguePositionConfirmed();
                if (panel) { Expand(StoryTab); await Foreground(this); BrowseCurrent(); LinesList.Focus(); }
                else { Collapse(); await Foreground(scene); }
                Send(GamepadButtons.None); await Drain();
            }
            async Task Focus(Control control)
            {
                control.BringIntoView(); UpdateLayout();
                if (!control.Focus()) throw new InvalidOperationException("无法聚焦验收控件：" + control.GetType().Name);
                await Drain(); Send(GamepadButtons.None);
            }

            await Scenario("面板确认与列表", async () =>
            {
                await Story(true);
                LinesList.SelectedItem = LinesList.Items.OfType<LineRow>().First(row => row.Node.Id == "a");
                int before = playCalls, selected = LinesList.SelectedIndex;
                await Tap(GamepadButtons.DPadDown);
                Check(LinesList.SelectedIndex > selected && playCalls == before && engine!.CurrentId == "a", "方向键浏览真实台词列表，滚入选中项且不播放");
                LinesList.SelectedItem = LinesList.Items.OfType<LineRow>().First(row => row.Node.Id == "b");
                await Focus(LinesList); before = playCalls;
                Send(GamepadButtons.South); Send(GamepadButtons.South); await Drain();
                Check(engine!.CurrentId == "a" && playCalls == before, "面板 A 按下并按住不提前确认");
                Send(GamepadButtons.None); await Drain();
                Check(engine.CurrentId == "b" && playCalls == before + 1, "面板 A 松开只确认所选台词一次");
                Send(GamepadButtons.None); await Drain();
                Check(playCalls == before + 1, "重复中性快照不重复播放");
            });

            await Scenario("字形自动识别", async () =>
            {
                await Story(true); Expand(gamepadTab); await Foreground(this); Send(GamepadButtons.None);
                Check(gamepadHelp.Text.Contains("A 确认") && !GamepadSony, "Xbox 设备自动显示 A/B/X/Y 提示");
                device = sony; Send(GamepadButtons.None);
                Check(gamepadHelp.Text.Contains("× 叉 确认") && GamepadSony && gamepadStatus.Text.Contains("DualSense"), "切换索尼设备显示叉圆方三角与当前设备名称");
                gamepadGlyphBox.SelectedIndex = 1;
                Check(!GamepadSony && gamepadHelp.Text.Contains("A 确认"), "字形设置可覆盖自动识别");
                gamepadGlyphBox.SelectedIndex = 0; await Drain(); Capture(this, "gamepad-tab.png");
                device = xbox; Send(GamepadButtons.None);
            });

            await Scenario("真实手动选分支按钮的延迟调用", async () =>
            {
                await Story(true); await Focus(InteractionsButton);
                int before = playCalls, clicks = 0;
                RoutedEventHandler observedClick = (_, _) => clicks++;
                InteractionsButton.Click += observedClick;
                try
                {
                    Send(GamepadButtons.South); await Drain();
                    Check(clicks == 0 && !branchMenu.IsVisible, "真实手动选分支按钮按住 A 时不打开菜单");
                    // 此路径进入 ActivateFocused -> ButtonAutomationPeer.Invoke，Click 被 WPF 排队执行。
                    Send(GamepadButtons.None); await Drain(); await Task.Delay(40); await Drain();
                    Check(clicks == 1 && branchMenu.IsVisible && branchMenu.IsNavigation && engine!.CurrentId == "a" && playCalls == before,
                        "真实手动选分支按钮的 UIA 延迟 Click 只打开静音导航菜单一次");
                    Check(branchMenu.GamepadNavigation && Native.GetForegroundWindow() == branchMenu.Handle,
                          $"UIA 延迟打开的小菜单仍进入手柄导航并取得前台焦点（主窗={new WindowInteropHelper(this).Handle}，菜单={branchMenu.Handle}，游戏={gameHandle}，导航={branchMenu.GamepadNavigation}）");
                    if (branchMenu.IsVisible) Capture(branchMenu, "gamepad-button-branch.png");
                }
                finally { InteractionsButton.Click -= observedClick; HideBranchMenu(); }
            });

            await Scenario("同名设备刷新和断连选择", async () =>
            {
                await Story(true); Expand(gamepadTab); await Foreground(this);
                var savedDevices = gamepadDevices; var savedInput = gamepadInput;
                string savedName = preferences.GamepadDeviceName, savedNotice = gamepadDeviceNotice;
                uint? savedId = gamepadChosenId; bool savedAmbiguous = gamepadAmbiguousSelection;
                // 从不 Start；工厂若意外被调用会失败，保证这一场景不接触 SDL 或真实设备。
                using var source = new GamepadInput(() => throw new InvalidOperationException("合成选择测试不允许启动设备后端"));
                var first = new GamepadDevice(303, "同名 Xbox 手柄", GamepadFamily.Xbox);
                var second = new GamepadDevice(404, first.Name, GamepadFamily.Xbox);
                var reconnected = new GamepadDevice(405, first.Name, GamepadFamily.Xbox);
                void RefreshDevices(params GamepadDevice[] devices)
                { gamepadDevices = devices; SelectRememberedGamepad(source, devices); RefreshGamepadControls(); }
                void Select(uint id) => gamepadDeviceBox.SelectedItem = gamepadDeviceBox.Items.OfType<GamepadDeviceOption>().First(option => option.Id == id);
                uint? SelectedId() => (gamepadDeviceBox.SelectedItem as GamepadDeviceOption)?.Id;
                try
                {
                    gamepadInput = source; preferences.GamepadDeviceName = ""; gamepadChosenId = null; gamepadAmbiguousSelection = false;
                    RefreshDevices(first, second); Select(second.Id);
                    Check(source.SelectedDeviceId == second.Id && SelectedId() == second.Id, "同名设备可以明确选择第二只的设备 ID");
                    RefreshDevices(second, first);
                    Check(source.SelectedDeviceId == second.Id && SelectedId() == second.Id, "同名设备列表顺序刷新仍保留所选 ID");
                    RefreshDevices(first);
                    Check(source.SelectedDeviceId == 0 && SelectedId() == 0 && gamepadDeviceNotice.Length > 0,
                        "所选同名设备断开时显示待重新选择，不自动接管剩余第一只");
                    RefreshDevices(first, reconnected);
                    Check(source.SelectedDeviceId == 0 && SelectedId() == 0, "同名设备重新接入并取得新 ID 后仍等待明确选择");
                    Select(reconnected.Id);
                    Check(source.SelectedDeviceId == reconnected.Id && SelectedId() == reconnected.Id, "明确重选后才启用重连设备的新 ID");

                    preferences.GamepadDeviceName = ""; gamepadChosenId = null; gamepadAmbiguousSelection = false; gamepadDeviceNotice = "";
                    RefreshDevices(first); Select(first.Id); RefreshDevices(first, second);
                    Check(source.SelectedDeviceId == first.Id && SelectedId() == first.Id, "先指定唯一设备后接入同名第二只，保留原设备");
                    RefreshDevices(second);
                    Check(source.SelectedDeviceId == 0 && SelectedId() == 0 && gamepadDeviceNotice.Length > 0,
                        "后加入的同名设备也会记录歧义，拔掉原设备不得自动切给新设备");
                }
                finally
                {
                    gamepadInput = savedInput; gamepadDevices = savedDevices; preferences.GamepadDeviceName = savedName;
                    gamepadChosenId = savedId; gamepadAmbiguousSelection = savedAmbiguous; gamepadDeviceNotice = savedNotice;
                    RefreshGamepadControls();
                }
            });

            var combo = new ComboBox { ItemsSource = new[] { "第一项", "第二项", "第三项" }, SelectedIndex = 0, Margin = new Thickness(4) };
            var check = new CheckBox { Content = "验收开关", Margin = new Thickness(4) };
            var radio = new RadioButton { Content = "验收单选", Margin = new Thickness(4) };
            var toggle = new ToggleButton { Content = "验收切换", Margin = new Thickness(4) };
            var text = new TextBox { Text = "保留文本", Margin = new Thickness(4) };
            var button = new Button { Content = "验收按钮", Margin = new Thickness(4) };
            var hidden = new Button { Content = "隐藏按钮", Visibility = Visibility.Collapsed };
            var disabled = new Button { Content = "禁用按钮", IsEnabled = false };
            int buttonClicks = 0, invalidClicks = 0;
            button.Click += (_, _) => buttonClicks++; hidden.Click += (_, _) => invalidClicks++; disabled.Click += (_, _) => invalidClicks++;
            var controls = new StackPanel();
            foreach (var control in new Control[] { combo, check, radio, toggle, text, button, hidden, disabled }) controls.Children.Add(control);
            fixtureTab = new TabItem { Header = "验收控件", Content = controls }; Tabs.Items.Add(fixtureTab);
            await Scenario("WPF 控件语义", async () =>
            {
                await Story(true); Expand(fixtureTab); await Foreground(this); await Focus(combo);
                await Tap(GamepadButtons.South); Check(combo.IsDropDownOpen, "A 展开下拉框");
                await Tap(GamepadButtons.DPadDown); Check(combo.SelectedIndex == 1 && combo.IsDropDownOpen, "展开时方向键选择下一项");
                await Tap(GamepadButtons.East); Check(!combo.IsDropDownOpen && expanded, "B 优先关闭下拉框，保留面板");
                await Tap(GamepadButtons.South); await Tap(GamepadButtons.South); Check(!combo.IsDropDownOpen, "下拉框也可用 A 收起");
                await Focus(VolumeSlider); VolumeSlider.Value = 50; double value = VolumeSlider.Value;
                await Tap(GamepadButtons.DPadRight); Check(VolumeSlider.Value > value, "滑块右方向调高音量");
                await Tap(GamepadButtons.DPadLeft); Check(Math.Abs(VolumeSlider.Value - value) < .001, "滑块左方向调低音量");
                await Focus(check); await Tap(GamepadButtons.South); Check(check.IsChecked == true, "A 通过控件接口切换复选框");
                await Focus(radio); await Tap(GamepadButtons.South); Check(radio.IsChecked == true, "A 选择单选框");
                await Focus(toggle); await Tap(GamepadButtons.South); Check(toggle.IsChecked == true, "A 切换 ToggleButton");
                await Focus(text); await Tap(GamepadButtons.South); Check(text.Text == "保留文本" && text.IsKeyboardFocusWithin, "文本框 A 不输入或触发剧情确认");
                await Tap(GamepadButtons.East); Check(!text.IsKeyboardFocusWithin && expanded, "文本框 B 离开输入而不收起面板");
                await Focus(button); int before = buttonClicks;
                Send(GamepadButtons.South); await Drain(); Check(buttonClicks == before, "普通按钮按住时不执行");
                Send(GamepadButtons.None); await Drain(); Check(buttonClicks == before + 1, "普通按钮松开时只执行一次 Click");
                await Focus(button); button.IsEnabled = false;
                await Tap(GamepadButtons.South); Check(buttonClicks == before + 1, "已经禁用的焦点按钮不会被激活"); button.IsEnabled = true;
                await Focus(button); button.Visibility = Visibility.Collapsed;
                await Tap(GamepadButtons.South); Check(buttonClicks == before + 1, "已经隐藏的焦点按钮不会被激活"); button.Visibility = Visibility.Visible;
                await Focus(button); gamepadNavigation!.Move(FocusNavigationDirection.Next);
                Check(!hidden.IsKeyboardFocusWithin && !disabled.IsKeyboardFocusWithin && invalidClicks == 0, "窗口内导航排除隐藏和禁用控件");
                game = null; await Foreground(scene); externalButton.Focus(); before = externalClicks;
                bool handled = gamepadNavigation.ActivateFocused(); await Tap(GamepadButtons.South);
                Check(!handled && externalClicks == before, "外部窗口焦点不被主窗口导航助手激活");
            });

            await Scenario("游戏按下跟随及键鼠去重", async () =>
            {
                preferences.GamepadFollowEnabled = true; await Story(false); int before = playCalls;
                Send(GamepadButtons.South);
                Check(engine!.CurrentId == "b" && playCalls == before + 1, "游戏前台对白继续在按下时跟随，不等松开");
                Send(GamepadButtons.South); Send(GamepadButtons.None);
                Check(engine.CurrentId == "b" && playCalls == before + 1, "游戏继续键按住和松开均不重复推进");
                preferences.ClickZoneEnabled = true; UpdateClickZone(); clickZone.SetDraggable(false); clickZone.UpdateLayout();
                clickZone.PlaceAt(scene.Left + 100, scene.Top + 100);
                var point = clickZone.PointToScreen(new Point(clickZone.ActualWidth / 2, clickZone.ActualHeight / 2));
                Check(clickZone.ContainsCursor((int)point.X, (int)point.Y), "鼠标去重场景使用有效热区坐标");
                HandleGlobal(Key.Space, gameHandle, Stopwatch.GetTimestamp());
                long now = Stopwatch.GetTimestamp(); await ObserveMouseTapForTest(new("鼠标左键", gameHandle, (int)point.X, (int)point.Y, now, now));
                Check(engine.CurrentId == "b" && playCalls == before + 1 && !branchMenu.IsVisible, "手柄推进后的短时键鼠副本合并一次，不误开分支");
                await Task.Delay(260);
                HandleGlobal(Key.Space, gameHandle, Stopwatch.GetTimestamp());
                Check(engine.Mode == RunMode.Choice, "手柄仍启用时，关联期后的键盘新输入可接管推进");
                preferences.GamepadFollowEnabled = false; await Story(false); before = playCalls;
                await Tap(GamepadButtons.South); Check(engine.CurrentId == "a" && playCalls == before, "未开启手柄跟随时游戏裸 A 不推进");
                HandleGlobal(Key.Space, gameHandle, Stopwatch.GetTimestamp());
                Check(engine.CurrentId == "b", "关闭手柄跟随后原空格跟随仍可推进（去重对照）");
                engine.Commit("a"); DialoguePositionConfirmed(); await Task.Delay(320);
                now = Stopwatch.GetTimestamp(); await ObserveMouseTapForTest(new("鼠标左键", gameHandle, (int)point.X, (int)point.Y, now, now));
                Check(engine.CurrentId == "b", "关闭手柄跟随后有效鼠标热区仍可推进（去重对照）");
                preferences.ClickZoneEnabled = false; UpdateClickZone(); preferences.GamepadFollowEnabled = true;
            });

            await Scenario("接入、重连、切设备归中", async () =>
            {
                await Story(false); Send(GamepadButtons.None, false); engine!.Commit("a"); DialoguePositionConfirmed(); int before = playCalls;
                Send(GamepadButtons.South); Send(GamepadButtons.South);
                Check(engine.CurrentId == "a" && playCalls == before, "连接时已按住 A 不产生推进");
                Send(GamepadButtons.None); Send(GamepadButtons.South);
                Check(engine.CurrentId == "b", "接入后先松开归中再按可推进"); Send(GamepadButtons.None);
                engine.Commit("a"); DialoguePositionConfirmed(); Send(GamepadButtons.South); Send(GamepadButtons.None); before = playCalls;
                device = sony; Send(GamepadButtons.South);
                Check(engine.Mode == RunMode.Paused && engine.CurrentId == "b" && playCalls == before, "活动手柄切换设备先暂停，携带的按住状态不执行");
                engine.Commit("a"); DialoguePositionConfirmed(); before = playCalls; Send(GamepadButtons.South);
                Check(engine.CurrentId == "a" && playCalls == before, "切设备重新确认后仍须松开归中");
                Send(GamepadButtons.None); Send(GamepadButtons.South); Check(engine.CurrentId == "b", "新设备归中后恢复跟随"); Send(GamepadButtons.None);
                Send(GamepadButtons.None, false); Check(engine.Mode == RunMode.Paused, "断连使游戏跟随暂停");
                engine.Commit("a"); DialoguePositionConfirmed(); before = playCalls;
                Send(GamepadButtons.South); Send(GamepadButtons.None);
                Check(engine.CurrentId == "a" && playCalls == before, "重连时按住再松开不会补发旧操作");
                Send(GamepadButtons.South); Check(engine.CurrentId == "b", "重连后新按下可跟随"); Send(GamepadButtons.None);
            });

            await Scenario("失焦与摇杆归中", async () =>
            {
                await Story(true); LinesList.SelectedItem = LinesList.Items.OfType<LineRow>().First(row => row.Node.Id == "b"); await Focus(LinesList);
                int before = playCalls; Send(GamepadButtons.South);
                await Foreground(scene); Send(GamepadButtons.South);
                await Foreground(this); await Focus(LinesList); // 下面重新制造按住状态跨越窗口切换。
                Send(GamepadButtons.South); await Foreground(scene); Send(GamepadButtons.South); await Foreground(this);
                Send(GamepadButtons.South); Send(GamepadButtons.None); await Drain();
                Check(engine!.CurrentId == "a" && playCalls == before, "按住 A 离开并返回面板后，松开不提交旧确认");
                await Tap(GamepadButtons.South); Check(engine.CurrentId == "b", "失焦归中后新的 A 可正常确认");
                await Story(true); int selected = LinesList.SelectedIndex;
                Send(GamepadButtons.None, false); Send(GamepadButtons.None, leftY: .9f);
                Check(LinesList.SelectedIndex == selected, "接入时摇杆未归中不移动选择");
                Send(GamepadButtons.None); Send(GamepadButtons.None, leftY: .9f);
                Check(LinesList.SelectedIndex > selected, "摇杆归中后再次推动可浏览列表"); Send(GamepadButtons.None);
            });

            await Scenario("游戏分支确认与焦点归还", async () =>
            {
                await Story(false); engine!.Commit("menu"); ShowBranchMenu(); await Foreground(scene); Send(GamepadButtons.None);
                int selected = branchMenu.Options.SelectedIndex, before = playCalls;
                await Tap(GamepadButtons.South); await Tap(GamepadButtons.DPadDown);
                Check(engine.MenuWaiting && branchMenu.Options.SelectedIndex == selected && playCalls == before, "游戏前台裸 A 和方向键不代选配音分支");
                await Tap(GamepadButtons.Back | GamepadButtons.Start); await Drain();
                Check(branchMenu.GamepadNavigation && Native.GetForegroundWindow() == branchMenu.Handle, "展开组合把配音分支菜单切为可聚焦手柄操作");
                Send(GamepadButtons.None); branchMenu.Options.SelectedIndex = 0; await Tap(GamepadButtons.DPadDown);
                Check(branchMenu.Options.SelectedIndex == 1 && playCalls == before, "菜单方向选择第二路线但保持静音");
                Capture(branchMenu, "gamepad-branch.png"); Send(GamepadButtons.South); Send(GamepadButtons.South);
                Check(engine.MenuWaiting && playCalls == before, "分支 A 按住不确认且不归还游戏焦点");
                Send(GamepadButtons.None); await Drain();
                Check(engine.CurrentId == "r2" && playCalls == before + 1 && !branchMenu.IsVisible && Native.GetForegroundWindow() == gameHandle, "分支松开 A 后确认第二路线一次并归还模拟游戏焦点");
            });

            await Scenario("听书手柄操作独立进度", async () =>
            {
                await Story(true); string? gameNode = engine!.CurrentId; int gameHistory = engine.History.Count;
                OpenListeningPack(file, "chapter"); StartListening(); Expand(listeningTab); await Foreground(this); await Focus(listeningPlay);
                int before = playCalls; Send(GamepadButtons.Start);
                Check(listeningRunning, "听书 Start 按下不提前暂停"); Send(GamepadButtons.None); await Drain();
                Check(!listeningRunning, "听书 Start 松开暂停"); await Tap(GamepadButtons.Start); Check(listeningRunning, "听书 Start 再次松开续播");
                long ticket = listeningTicket; await Tap(GamepadButtons.West);
                Check(listeningRunning && listeningTicket != ticket && listeningSession?.Current?.NodeId == "a", "听书 X 重播当前句并更新请求票据");
                int bookmarks = ListeningProgress!.Bookmarks.Count; listeningBookmarkName.Text = "手柄书签"; await Tap(GamepadButtons.North);
                Check(ListeningProgress.Bookmarks.Count == bookmarks + 1 && ListeningProgress.Bookmarks.Last().Label == "手柄书签", "听书 Y 保存独立命名书签");
                Check(engine.CurrentId == gameNode && engine.History.Count == gameHistory && playCalls == before, "听书暂停、重播和书签不改变游戏位置、履历或游戏播放次数");
                Capture(this, "gamepad-listening.png"); StopListeningForGame();
            });

            await Scenario("设置冲突与测试模式", async () =>
            {
                await Story(true); Expand(gamepadTab); await Foreground(this); Send(GamepadButtons.None);
                string old = preferences.GamepadBindings["pause"]; int saves = saveGeneration;
                var box = gamepadBindingBoxes["pause"];
                box.SelectedItem = box.Items.OfType<GamepadButtonOption>().First(option => option.Button == GamepadLayout.Parse(preferences.GamepadBindings["panel"]));
                Check(preferences.GamepadBindings["pause"] == old && saveGeneration == saves && gamepadInputStatus.Text.Contains("已被使用"), "重复组合键明确提示冲突且不写入设置");
                var panelBox = gamepadBindingBoxes["panel"]; saves = saveGeneration;
                panelBox.SelectedItem = panelBox.Items.OfType<GamepadButtonOption>().First(option => option.Button == GamepadButtons.None);
                Check(preferences.GamepadBindings["panel"] != "None" && saveGeneration == saves, "展开面板组合不能被清空而失去手柄入口");
                await Story(false); gamepadTest.IsChecked = true; int before = playCalls; string? node = engine!.CurrentId;
                Send(GamepadButtons.None); await Tap(GamepadButtons.South); await Tap(GamepadButtons.Back | GamepadButtons.West);
                Check(engine.CurrentId == node && playCalls == before && gamepadInputStatus.Text.Contains("测试中"), "手柄测试模式显示输入，裸键和组合均不推进或播放");
                gamepadTest.IsChecked = false;
                await Story(true); keyTestBox.IsChecked = true; before = playCalls;
                await Tap(GamepadButtons.South); Check(playCalls == before && keyTestStatus.Text.Contains("手柄测试中"), "原按键诊断开启时手柄同样只显示输入"); keyTestBox.IsChecked = false;
            });

            await Scenario("关闭诊断后首个新按钮", async () =>
            {
                await Story(true); Expand(fixtureTab); await Foreground(this); await Focus(button);
                int before = buttonClicks;
                gamepadTest.IsChecked = true; Send(GamepadButtons.None); await Drain();
                // 触发真实 Checked/Unchecked 路径，模拟用户用界面关闭测试，期间手柄已归中。
                gamepadTest.IsChecked = false; await Tap(GamepadButtons.South);
                Check(buttonClicks == before + 1, "手柄测试期间已归中，用界面关闭测试后的第一次新 A 正常执行");
                await Focus(button); before = buttonClicks;
                keyTestBox.IsChecked = true; Send(GamepadButtons.None); await Drain();
                keyTestBox.IsChecked = false; await Tap(GamepadButtons.South);
                Check(buttonClicks == before + 1, "键盘诊断期间已归中，关闭诊断后的第一次新 A 不被吞掉");
            });

            await Scenario("手柄停止自动播放及过期回调", async () =>
            {
                await Story(false); int clicks = 0;
                automaticEnvironmentTest = () => true; automaticClickTest = () => { clicks++; return true; }; automaticTestImmediate = true;
                StartAutomaticAtConfirmedLine(); Check(automaticRunning, "自动播放取消测试已成功开启合成环境");
                long ticket = automaticTicket; int before = playCalls;
                Send(GamepadButtons.South);
                Check(!automaticRunning && engine!.CurrentId == "a" && playCalls == before, "手柄按下立即停止自动播放，同一输入不接着推进配音");
                Send(GamepadButtons.None); OnAutomaticCompleted(ticket); await Task.Delay(80);
                Check(!automaticRunning && engine!.CurrentId == "a" && clicks == 0, "停止后迟到的自然完成票据不能点击或恢复自动推进");
                long stale = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
                Send(GamepadButtons.Back | GamepadButtons.West, timestamp: stale); Send(GamepadButtons.None); await Drain();
                Check(playCalls == before, "过期手柄动作不能补发重播");
                automaticEnvironmentTest = null; automaticClickTest = null; automaticTestImmediate = false;
            });
            report.Add($"SUMMARY: {report.Count(line => line.StartsWith("PASS:"))} passed, {report.Count(line => line.StartsWith("FAIL:"))} failed");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            automaticEnvironmentTest = null; automaticClickTest = null; StopAutomatic("手柄验收结束", false);
            StopListeningForGame(); listeningTestAudio = false; gamepadTest.IsChecked = false; keyTestBox.IsChecked = false;
            gamepadNavigation?.ReleaseHighlight(); HideBranchMenu(); game = null; scene?.Close();
            if (fixtureTab != null) Tabs.Items.Remove(fixtureTab);
            preferences.GamepadEnabled = false; preferences.ClickZoneEnabled = false; UpdateClickZone();
            Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "gamepad-ui-test.txt"), report);
            Close();
        }
    }
}
