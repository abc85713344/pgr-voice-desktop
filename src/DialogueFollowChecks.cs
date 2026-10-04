using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunDialogueFollowUiTest()
    {
        var results = new List<string>();
        Window? scene = null;
        int pattern = 0, captures = 0;
        bool useScreen = false;
        string report = System.IO.Path.Combine(Log.DataDir, "dialogue-follow-ui-test.txt");
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description +
                $" [node={engine?.CurrentId}, mode={engine?.Mode}, held={dialogueHeld}, checking={dialogueChecking}, calls={playCalls}, message={dialogueMessage}]");
            results.Add("PASS: " + description);
        }
        async Task WaitUntil(Func<bool> condition, string description, int timeoutMs = 1800)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
            if (!condition())
            {
                IntPtr foreground = Native.GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint ownerPid);
                string name;
                try { using var process = Process.GetProcessById((int)ownerPid); name = process.ProcessName; }
                catch { name = "无法读取"; }
                throw new TimeoutException(description + $" [expected={game?.Handle}, foreground={foreground}, process={name}, pid={ownerPid}, held={dialogueHeld}, expanded={expanded}, message={dialogueMessage}]");
            }
        }

        byte[] Mask(int variant)
        {
            var pixels = new byte[200 * 80];
            void Block(int x, int y, int width = 20, int height = 20)
            {
                for (int row = y; row < y + height; row++)
                    for (int column = x; column < x + width; column++) pixels[row * 200 + column] = 1;
            }
            if (variant is 0 or 1) Block(20, 20);
            if (variant == 1) Block(45, 20); // 原白字完整保留，同时增加白字。
            if (variant is 2 or 3) Block(105, 35);
            if (variant == 3) Block(130, 35);
            if (variant == 4) Block(55, 5);
            return pixels;
        }

        var masks = Enumerable.Range(0, 5).Select(Mask).ToArray();
        var canvas = new Canvas { Background = Brushes.Black, Width = 800, Height = 480 };
        void PaintScene(int variant)
        {
            canvas.Children.Clear();
            var white = new Rectangle { Width = 44, Height = 28, Fill = Brushes.White };
            Canvas.SetLeft(white, variant == 0 ? 150 : 460);
            Canvas.SetTop(white, variant == 0 ? 155 : 275);
            canvas.Children.Add(white);
            canvas.UpdateLayout();
        }

        Pack Fixture()
        {
            var pack = new Pack
            {
                Id = "dialogue-follow-ui-fixture", Title = "对白跟随隔离验收", Root = Log.DataDir,
                Chapters = new()
                {
                    new() { Id = "chapter", Title = "测试章节", Sections = new()
                    { new() { Id = "section", Title = "小区域变化测试", StartId = "a" } } }
                },
                Nodes = new()
                {
                    new() { Id = "a", SectionId = "section", Speaker = "测试角色", Text = "第一句尚在逐字显示。", NextId = "b" },
                    new() { Id = "b", SectionId = "section", Speaker = "测试角色", Text = "第二句后面才是选择点。", NextId = "c" },
                    new() { Id = "c", SectionId = "section", Kind = "choice", Text = "选择测试路线", Options = new()
                    { new() { Id = "route-option", Label = "进入已核实路线", PathId = "route", TargetId = "r", MergeId = "merge", Verified = true } } },
                    new() { Id = "r", SectionId = "section", PathId = "route", Speaker = "测试角色", Text = "分支第一句需要重新核对。", NextId = "merge" },
                    new() { Id = "merge", SectionId = "section", Kind = "merge", Text = "回到共同线", NextId = "common" },
                    new() { Id = "common", SectionId = "section", Speaker = "测试角色", Text = "共同线首句对应一次真实换句。", NextId = "end" },
                    new() { Id = "end", SectionId = "section", Kind = "end", Text = "测试结束" }
                }
            };
            pack.Validate();
            return pack;
        }

        async Task FocusScene()
        {
            if (scene == null || game == null) throw new InvalidOperationException("模拟游戏尚未建立。");
            var handle = new WindowInteropHelper(scene).Handle;
            if (game.Handle != handle) throw new InvalidOperationException("绑定窗口已不是本轮模拟游戏，拒绝改变其他窗口前台。");
            Collapse(false);
            await PrepareIsolatedTestForeground(scene, "对白跟随模拟游戏无法稳定取得前台");
            if (!GameIsForeground()) throw new InvalidOperationException("WPF 模拟游戏未能获得真实前台，不能验证跟随保护。");
            UpdateDialogueMonitor();
        }

        async Task WarmBaseline()
        {
            // 每个新输入场景先将本进程模拟窗口置于前台，等同玩家核对后返回游戏。
            // 观测进行中不重试焦点，仍由产品检测并拒绝中途失焦。
            await FocusScene();
            int before = Volatile.Read(ref captures);
            UpdateDialogueMonitor();
            await WaitUntil(() => Volatile.Read(ref captures) > before, "等待新鲜前帧超时");
            // 确保输入晚于截图完成，不能拿跨过输入时刻的帧当基线。
            await Task.Delay(20);
            if (!GameIsForeground()) throw new InvalidOperationException("准备新鲜前帧时模拟游戏失去前台，尚未开始本次对白观测。");
        }

        async Task ResetAtA(int initialPattern = 0)
        {
            dialogueMonitor.Reset(); dialogueChecking = false; dialogueHeld = false;
            dialogueDeferredNode = null; previousDialogueMode = null;
            HideBranchMenu(); CancelOcr();
            Volatile.Write(ref pattern, initialPattern);
            var pack = Fixture();
            engine = new PlaybackEngine(pack);
            engine.PlayRequested += PlayNode;
            engine.StopRequested += () => { StopAudio(); playingId = null; };
            engine.Changed += EngineChanged;
            ChapterBox.ItemsSource = pack.Chapters;
            ChapterBox.SelectedIndex = 0;
            SectionBox.ItemsSource = pack.Chapters[0].Sections;
            SectionBox.SelectedIndex = 0;
            PackLabel.Text = pack.Title;
            engine.Commit("a"); FillLines(); BrowseCurrent();
            DialoguePositionConfirmed();
            await FocusScene();
            await WarmBaseline();
        }

        async Task ObservePattern(int nextPattern)
        {
            RequestObservedNext(Stopwatch.GetTimestamp());
            Volatile.Write(ref pattern, nextPattern);
            await WaitUntil(() => !dialogueChecking, "对白观测没有完成");
            await Task.Delay(30);
        }

        void SaveScene(string file)
        {
            scene!.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)scene.ActualWidth, (int)scene.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(scene);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(Log.DataDir, file)); encoder.Save(stream);
        }

        try
        {
            if (!testUi) throw new InvalidOperationException("这个入口必须使用隔离 testUi 配置。");
            timer.Stop(); gamepadTimer?.Stop(); StopGamepadInput(); rawKeyboard?.Dispose(); keyboard?.Dispose(); keyboard = null;
            Directory.CreateDirectory(Log.DataDir);
            Check(!ocr.Running, "测试开始时 OCR 未运行");
            preferences.DialogueGuardEnabled = true;
            preferences.OcrEnabled = false;
            preferences.ClickZoneEnabled = false;
            preferences.Keys["next"] = "Space"; preferences.Keys["manualNext"] = "PageDown";
            preferences.Keys["previous"] = "PageUp";
            preferences.DialogueRegion = new(.10, .20, .80, .60);
            dialogueGuardBox.IsChecked = true;
            OcrEnabledBox.IsChecked = false;
            UpdateClickZone();
            var work = SystemParameters.WorkArea;
            Left = work.Left + 12; Top = work.Top + 12;
            PaintScene(0);
            scene = new Window
            {
                Title = "对白跟随隔离模拟游戏", Width = 800, Height = 480,
                Left = work.Left + 120, Top = work.Top + 80,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, Content = canvas, ShowInTaskbar = true
            };
            scene.Show();
            game = new GameWindow(new WindowInteropHelper(scene).Handle, scene.Title, "DialogueFollowFixture");
            dialogueMonitor.Dispose();
            dialogueMonitor = new DialogueFrameMonitor((handle, region) =>
            {
                CapturedDialogueFrame frame;
                if (Volatile.Read(ref useScreen)) frame = DialogueFrameCapture.Capture(handle, region);
                else frame = new(new(200, 80, 0, masks[Volatile.Read(ref pattern)]), "stable-fixture", Stopwatch.GetTimestamp(), 0);
                Interlocked.Increment(ref captures);
                return frame;
            });

            await ResetAtA();
            int calls = playCalls;
            await ObservePattern(1);
            Check(engine!.CurrentId == "a" && playCalls == calls && !dialogueHeld,
                "旧白字保留并补字时不推进、不重复播放，不把补字当换句");
            await WarmBaseline();
            await ObservePattern(1);
            Check(engine.CurrentId == "a" && playCalls == calls && dialogueHeld,
                "无变化时保持当前句并进入 Hold");
            ConfirmDialogueAnchor();
            Check(!dialogueHeld && engine.CurrentId == "a" && playCalls == calls,
                "确认游戏仍是当前句后恢复跟随，不额外重播");
            await WarmBaseline();
            await ObservePattern(2);
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "明确稳定的文字替换只推进一次到第二句");
            await Task.Delay(180);
            Check(engine.CurrentId == "b" && playCalls == calls + 1, "检测完成后的旧任务不会再推进一次");
            await WarmBaseline();
            await ObservePattern(3);
            Check(engine.CurrentId == "b" && engine.Mode == RunMode.Following && playCalls == calls + 1,
                "选择点前一句的补字不会提前进入分支菜单");
            await WarmBaseline();
            await ObservePattern(4);
            Check(engine.CurrentId == "c" && engine.Mode == RunMode.Choice && branchMenu.IsVisible,
                "第二句确有替换后才进入 choice");
            int beforeChoice = playCalls;
            HandleGlobal(Key.Space, game.Handle, Stopwatch.GetTimestamp());
            RequestObservedNext(Stopwatch.GetTimestamp());
            await Task.Delay(120);
            Check(engine.CurrentId == "c" && playCalls == beforeChoice && !dialogueChecking,
                "choice 状态的普通推进键不选择路线、不播放");
            branchMenu.Options.SelectedIndex = 0;
            ConfirmSmallBranch();
            Check(engine.CurrentId == "r" && dialogueHeld && dialogueDeferredNode == "r" && playCalls == beforeChoice,
                "选路首句先静音 Hold，等待玩家核对");
            ConfirmDialogueAnchor();
            Check(!dialogueHeld && dialogueDeferredNode == null && playCalls == beforeChoice + 1,
                "确认分支首句后只触发一次模拟播放");
            ConfirmDialogueAnchor();
            Check(playCalls == beforeChoice + 1, "重复确认分支首句不会重复触发播放");
            await WarmBaseline();
            await ObservePattern(0);
            Check(engine.CurrentId == "common" && playCalls == beforeChoice + 2 && !dialogueHeld,
                "一次真实换句跨过虚拟 merge 并播放共同线首句，不落后一行");

            await ResetAtA(); calls = playCalls;
            RequestObservedNext(Stopwatch.GetTimestamp());
            Volatile.Write(ref pattern, 2);
            await Task.Delay(25);
            RequestObservedNext(Stopwatch.GetTimestamp());
            await Task.Delay(800);
            Check(engine.CurrentId == "a" && dialogueHeld && playCalls == calls,
                "连续快按进入 Hold，旧观测不能随后推进");
            await FocusScene();
            HandleGlobal(Key.PageDown, game.Handle);
            Check(engine.CurrentId == "b" && !dialogueHeld && playCalls == calls + 1,
                "Hold 时手动 PageDown 可纠偏一行并重新确认位置");
            HoldDialogue("测试手动回退");
            HandleGlobal(Key.PageUp, game.Handle);
            Check(engine.CurrentId == "a" && !dialogueHeld && playCalls == calls + 2,
                "Hold 时 Previous 可回退并重新确认位置");

            await ResetAtA(); calls = playCalls;
            RequestObservedNext(Stopwatch.GetTimestamp());
            Volatile.Write(ref pattern, 2);
            await Task.Delay(25);
            engine.TogglePause();
            await Task.Delay(800);
            Check(engine.CurrentId == "a" && engine.Mode == RunMode.Paused && playCalls == calls,
                "检查中暂停使旧结果失效，不在暂停后补推进");
            ConfirmDialogueAnchor();
            Check(engine.Mode == RunMode.Following && !dialogueHeld && engine.CurrentId == "a",
                "确认当前句可以从暂停恢复，不改变位置");

            await ResetAtA(); calls = playCalls;
            RequestObservedNext(Stopwatch.GetTimestamp());
            Volatile.Write(ref pattern, 2);
            await Task.Delay(25);
            Expand(StoryTab); FillLines();
            LinesList.SelectedItem = rows.First(row => row.Node.Id == "b");
            ConfirmLine();
            await Task.Delay(800);
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "检查中手动确认另一句后，过期检测不能继续推到 choice");

            await ResetAtA(); calls = playCalls;
            RequestObservedNext(Stopwatch.GetTimestamp() - Stopwatch.Frequency);
            await Task.Delay(50);
            Check(engine.CurrentId == "a" && dialogueHeld && playCalls == calls,
                "一秒前的迟到输入被拒绝，不猜测或推进");

            await ResetAtA(); calls = playCalls;
            preferences.ClickZoneEnabled = true; UpdateClickZone();
            clickZone.PlaceAt(scene.Left + 100, scene.Top + 100);
            Check(clickZone.IsClickThrough, "普通鼠标热区始终穿透，不需要等前台切换");
            long mouseTime = Stopwatch.GetTimestamp();
            await ObserveMouseTapForTest(new("鼠标左键", game.Handle, int.MinValue, int.MinValue, mouseTime, mouseTime));
            Check(engine.CurrentId == "a" && !dialogueChecking, "热区外的鼠标消息不触发对白检查");
            var center = clickZone.PointToScreen(new Point(clickZone.ActualWidth / 2, clickZone.ActualHeight / 2));
            mouseTime = Stopwatch.GetTimestamp();
            await ObserveMouseTapForTest(new("鼠标左键", game.Handle, (int)center.X, (int)center.Y, mouseTime, mouseTime));
            Volatile.Write(ref pattern, 2);
            await WaitUntil(() => !dialogueChecking, "热区内点击检测没有完成");
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "热区内使用消息坐标和多帧检查，确认替换后只推进一次");

            await ResetAtA(); calls = playCalls;
            RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Volatile.Write(ref pattern, 2);
            RequestFollowNext(FollowInputSource.Gamepad, Stopwatch.GetTimestamp());
            Check(dialogueChecking && !dialogueHeld, "键盘与手柄近邻副本在画面检查前合并，不误判连续快按");
            await WaitUntil(() => !dialogueChecking, "热切换合并后的对白检查没有完成");
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "开启对白检查时混合来源的一次操作仍只推进和播放一次");

            await ResetAtA(); calls = playCalls;
            center = clickZone.PointToScreen(new Point(clickZone.ActualWidth / 2, clickZone.ActualHeight / 2));
            mouseTime = Stopwatch.GetTimestamp();
            var heldDown = new ObservedMouseInput("鼠标左键", game.Handle, (int)center.X, (int)center.Y, mouseTime, mouseTime);
            HandleMouseGlobal(heldDown);
            RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Check(dialogueChecking && !dialogueHeld && pendingMouseFollow != null,
                "鼠标未松开时的近邻键盘副本不会取消或重复启动画面检查");
            await Task.Delay(680);
            Check(engine.CurrentId == "a" && playCalls == calls && dialogueChecking && !dialogueHeld,
                "按住期间保留按下前画面，尚未松开不误报无变化、不推进");
            Volatile.Write(ref pattern, 2); // 模拟只在松开时换字的游戏。
            long releaseTime = Stopwatch.GetTimestamp();
            HandleMouseGesture(new(heldDown, heldDown with { Timestamp = releaseTime, ReceivedTimestamp = releaseTime }, true, ""));
            await WaitUntil(() => !dialogueChecking, "松开后对白检查没有完成");
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "延后松开再换字也从按下前画面比较，恰好跟随一次");

            await ResetAtA(); calls = playCalls;
            mouseTime = Stopwatch.GetTimestamp();
            heldDown = heldDown with { Timestamp = mouseTime, ReceivedTimestamp = mouseTime };
            HandleMouseGlobal(heldDown); Volatile.Write(ref pattern, 2);
            releaseTime = Stopwatch.GetTimestamp();
            HandleMouseGesture(new(heldDown, heldDown with { Timestamp = releaseTime, ReceivedTimestamp = releaseTime }, false, "拖动不是轻点"));
            await Task.Delay(750);
            Check(engine.CurrentId == "a" && playCalls == calls && engine.Mode == RunMode.Paused && !dialogueChecking,
                "拒绝拖动时取消整次画面检查，旧采样不会补推进");
            Expand(SettingsTab); editingClickZone = true; UpdateClickZone();
            Check(!clickZone.IsClickThrough, "只有显式调整热区时可以拖动");
            Collapse(false);
            Check(clickZone.IsClickThrough && !editingClickZone, "收起面板立刻结束调整并恢复穿透");
            preferences.ClickZoneEnabled = false; UpdateClickZone();

            Volatile.Write(ref useScreen, true); PaintScene(0);
            await ResetAtA(); calls = playCalls;
            SaveScene("dialogue-follow-fixture-before.png");
            RequestObservedNext(Stopwatch.GetTimestamp()); PaintScene(1);
            await WaitUntil(() => !dialogueChecking, "真实模拟窗口截图检测没有完成", 2500);
            await Task.Delay(30);
            SaveScene("dialogue-follow-fixture-after.png");
            Check(engine.CurrentId == "b" && playCalls == calls + 1 && !dialogueHeld,
                "真实 WPF 窗口截图的稳定白字图形替换可完成一次跟随");
            Check(!ocr.Running && !locating, "全部自动跟随检查都没有启动 OCR");
            Check(!audio.Playing && !audioStarting, "testUi 全程没有启动真实音频播放");
            if (stateSaves.Flush() != null) throw new IOException("测试存档准备失败");
            var oldEngine = engine;
            var otherPack = Fixture(); otherPack.Id = "other-dialogue-test";
            string otherFile = System.IO.Path.Combine(Log.DataDir, "other-pack.json"); Json.Save(otherFile, otherPack);
            using (var blocked = File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LoadPack(otherFile);
                Check(ReferenceEquals(engine, oldEngine) && SaveWarning.Visibility == Visibility.Visible,
                    "存档写入失败时拒绝切章，保留当前引擎与错误提示");
                var close = new CancelEventArgs(); OnClosing(this, close);
                Check(close.Cancel && !closing, "退出保存失败会取消关闭，不静默丢失进度");
            }
            Save();
            Check(stateSaves.Flush() == null, "写入障碍解除后可以成功重试保存");
            HoldDialogue("隔离验收已完成"); Expand(StoryTab); BrowseCurrent();
            await Task.Delay(80); Screenshot("dialogue-follow-recovery-ui.png");
            Expand(SettingsTab); await Task.Delay(80); Screenshot("dialogue-follow-settings-ui.png");
            results.Add("INFO: 确定用例使用白字掩膜；另含真实本机窗口截图。未向系统或游戏注入键盘。播放次数仅为 testUi 模拟计数。");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally
        {
            dialogueMonitor.Dispose();
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(report, results);
            scene?.Close();
        }
        Close();
    }
}
