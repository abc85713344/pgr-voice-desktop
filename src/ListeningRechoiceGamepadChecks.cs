using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    static extern int GetListeningCheckWindowClass(IntPtr window, StringBuilder name, int capacity);

    // 独立测试目录、假音频和合成手柄快照；不发送系统按键，也不连接实体手柄或游戏。
    async Task RunListeningRechoiceGamepadChecks(Action<bool, string> check, string root)
    {
        if (!testUi || !listeningTestAudio) throw new InvalidOperationException("重选菜单手柄检查仅限隔离假音频入口。");
        string actualState = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgrStoryVoice"));
        string testState = Path.GetFullPath(Log.DataDir);
        if (testState.Equals(actualState, StringComparison.OrdinalIgnoreCase)
            || testState.StartsWith(actualState + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("重选菜单手柄检查不能使用真实用户进度目录。");
        string originalFile = listeningFile, originalChapter = listeningSession!.ChapterId;
        bool originalEnabled = preferences.GamepadEnabled;
        var originalBindings = preferences.GamepadBindings;
        string originalModifier = preferences.GamepadModifier;
        var originalLatest = gamepadLatest; var originalDevice = gamepadHandledDevice; bool originalConnected = gamepadWasConnected;
        bool? originalTest = gamepadTest.IsChecked, originalKeyTest = keyTestBox.IsChecked;
        string gameBefore = engine == null ? "" : JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task ForegroundTestPlayer()
        {
            if (!testUi || !listeningTestAudio) throw new InvalidOperationException("只允许聚焦本轮隔离播放器。");
            Show(); WindowState = WindowState.Normal; Activate();
            var expected = new WindowInteropHelper(this).Handle;
            Native.SetForegroundWindow(expected); await Drain();
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != expected; i++) await Task.Delay(30);
            if (Native.GetForegroundWindow() != expected)
            {
                uint own = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = foreground != 0 && foreground != own && AttachThreadInput(own, foreground, true);
                try { Activate(); Native.SetForegroundWindow(expected); }
                finally { if (attached) AttachThreadInput(own, foreground, false); }
                await Drain();
                for (int i = 0; i < 10 && Native.GetForegroundWindow() != expected; i++) await Task.Delay(30);
            }
            var actual = Native.GetForegroundWindow();
            GetWindowThreadProcessId(actual, out uint processId);
            var windowClass = new StringBuilder(256); GetListeningCheckWindowClass(actual, windowClass, windowClass.Capacity);
            check(actual == expected, $"重选菜单手柄检查仅在隔离播放器前台执行（期望 HWND={expected}，实际 HWND={actual}，PID={processId}，窗口类={windowClass}）");
        }
        string State()
        {
            var snapshot = listeningSession!.Capture(listeningOffset); snapshot.UpdatedUtc = default;
            return JsonSerializer.Serialize(snapshot, Json.Options);
        }
        var device = new GamepadDevice(909, "重选菜单合成手柄", GamepadFamily.Xbox);
        GamepadButtons previous = GamepadButtons.None;
        void Send(GamepadButtons buttons)
        {
            var reading = new GamepadReading(device, true, buttons, buttons & ~previous, previous & ~buttons,
                0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp());
            previous = buttons; gamepadLatest = reading;
            HandleGamepadReading(reading, Native.GetForegroundWindow());
        }
        async Task Tap(GamepadButtons buttons) { Send(buttons); await Drain(); Send(GamepadButtons.None); await Drain(); }
        async Task OpenWithController()
        {
            listeningRechoose.BringIntoView(); UpdateLayout(); listeningRechoose.Focus(); Send(GamepadButtons.None);
            await Tap(GamepadButtons.South); Send(GamepadButtons.None); await Drain();
            check(listeningRechoose.ContextMenu?.IsOpen == true && GetGamepadContext().StartsWith("listening-rechoice:", StringComparison.Ordinal),
                "手柄 A 打开重选列表并进入独立菜单路由");
        }
        var pack = new Pack
        {
            Id = "listening-rechoice-gamepad", Title = "重选菜单手柄检查", Root = root,
            Chapters = new() { new() { Id = "rechoice-book", Title = "重选菜单手柄检查", Sections = new() { new() { Id = "s", Title = "两个已选分支", StartId = "before" } } } },
            Nodes = new()
            {
                new() { Id = "before", SectionId = "s", Text = "第一分支前", Audio = "audio.wav", NextId = "menu1" },
                new() { Id = "menu1", SectionId = "s", Kind = "choice", Options = new() {
                    new() { Id = "one", Label = "第一处路线", TargetId = "line1", PathId = "one", MergeId = "join1", Verified = true } } },
                new() { Id = "line1", SectionId = "s", Text = "第一路线正文", Audio = "audio.wav", PathId = "one", NextId = "join1" },
                new() { Id = "join1", SectionId = "s", Kind = "merge", NextId = "middle" },
                new() { Id = "middle", SectionId = "s", Text = "第二分支前", Audio = "audio.wav", NextId = "menu2" },
                new() { Id = "menu2", SectionId = "s", Kind = "choice", Options = new() {
                    new() { Id = "two", Label = "第二处原路线", TargetId = "line2", PathId = "two", MergeId = "join2", Verified = true },
                    new() { Id = "alternative", Label = "第二处另一路线", TargetId = "alternativeLine", PathId = "alternative", MergeId = "join2", Verified = true } } },
                new() { Id = "line2", SectionId = "s", Text = "第二路线正文", Audio = "audio.wav", PathId = "two", NextId = "join2" },
                new() { Id = "alternativeLine", SectionId = "s", Text = "另一路线正文", Audio = "audio.wav", PathId = "alternative", NextId = "join2" },
                new() { Id = "join2", SectionId = "s", Kind = "merge", NextId = "last" },
                new() { Id = "last", SectionId = "s", Text = "分支后的共同线", Audio = "audio.wav" }
            }
        };
        pack.Validate(); string file = Path.Combine(root, "rechoice-gamepad.json"); Json.Save(file, pack);
        try
        {
            preferences.GamepadEnabled = true; preferences.GamepadBindings = GamepadLayout.Defaults(); preferences.GamepadModifier = "Back";
            gamepadTest.IsChecked = false; keyTestBox.IsChecked = false; gamepadHandledDevice = null; gamepadWasConnected = false;
            OpenListeningPack(file, "rechoice-book");
            listeningSession = new ListeningSession(listeningSession!.Pack, "rechoice-book", ListeningBranchPolicy.Manual);
            var snapshot = listeningSession!.Capture(); snapshot.Choices = new() { ["menu1:0"] = "one", ["menu2:0"] = "two" };
            check(listeningSession.TryRestore(snapshot, out _) && listeningSession.SeekNode("last"), "重选手柄夹具恢复两处已选分支");
            listeningOffset = 912; listeningBrowsingLocation = false; RefreshListening(); StartListening();
            Expand(listeningTab); await ForegroundTestPlayer();
            long cancelledTicket = listeningTicket;
            await OpenWithController();
            var cancelledMenu = listeningRechoose.ContextMenu!;
            var items = cancelledMenu.Items.OfType<MenuItem>().ToArray();
            string beforeCancel = State();
            check(items.Length == 2 && items[0].IsKeyboardFocusWithin && !listeningRunning, "重选菜单首项获得焦点，打开时先暂停且未改路线");
            await Tap(GamepadButtons.DPadDown);
            check(items[1].IsKeyboardFocusWithin && State() == beforeCancel, "手柄下移到第二个选择点只移动焦点，不提交位置");
            await Tap(GamepadButtons.DPadLeft); check(items[0].IsKeyboardFocusWithin, "手柄左移可返回上一选择点");
            await Tap(GamepadButtons.DPadRight); check(items[1].IsKeyboardFocusWithin, "手柄右移可进入下一选择点");
            await Tap(GamepadButtons.East);
            check(!cancelledMenu.IsOpen && expanded && Tabs.SelectedItem == listeningTab && !listeningRunning && State() == beforeCancel,
                "手柄 B 只关闭重选列表，面板保留、播放暂停、路线与句内进度不变");
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); OnListeningCompleted(cancelledTicket); await Drain();
            check(State() == beforeCancel && !listeningRunning, "取消后旧菜单点击和旧完成回调不能提交改选");

            await OpenWithController(); await Tap(GamepadButtons.DPadDown); Send(GamepadButtons.South); await Drain();
            check(!listeningSession.HasPendingChoice && listeningRechoose.ContextMenu!.IsOpen, "手柄 A 按下时不会提前提交改选");
            Send(GamepadButtons.None); await Drain();
            check(listeningSession.HasPendingChoice && listeningSession.Current!.ChoiceKey == "menu2:0" && !listeningRunning
                && expanded && !listeningRechoose.ContextMenu!.IsOpen && listeningSession.Capture().Choices.Count == 1,
                "手柄 A 松开后只打开第二个菜单，保留第一处选择，等待确认具体路线");
            var alternate = listeningChoices.Children.OfType<Button>().Single(b => b.Content?.ToString() == "第二处另一路线");
            alternate.BringIntoView(); alternate.Focus(); Send(GamepadButtons.None); await Tap(GamepadButtons.South);
            check(listeningRunning && listeningSession.Current?.NodeId == "alternativeLine", "重选列表关闭后手柄 A 可确认另一具体路线并开始播放");
            await OpenWithController(); string beforeEscape = State();
            var escapeMenu = listeningRechoose.ContextMenu!;
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeMenu)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            escapeMenu.RaiseEvent(escape); await Drain();
            check(escape.Handled && !escapeMenu.IsOpen && expanded && !listeningRunning && State() == beforeEscape,
                "菜单内部 Escape 路由被消费，只取消列表而不折叠播放器");

            await OpenWithController(); var staleMenu = listeningRechoose.ContextMenu!;
            RefreshListening(); string beforeStale = State(); await Tap(GamepadButtons.South);
            check(!staleMenu.IsOpen && State() == beforeStale && !listeningRunning && expanded,
                "同会话界面版本改变后手柄确认旧菜单只关闭，不重开原分支");
            check(engine == null || JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) == gameBefore,
                "重选菜单方向、取消和确认全过程保持游戏模式进度");
        }
        finally
        {
            if (listeningRechoiceMenuBinding is { } binding) CloseListeningRechoiceMenu(binding, true);
            PauseListening(); OpenListeningPack(originalFile, originalChapter);
            preferences.GamepadEnabled = originalEnabled; preferences.GamepadBindings = originalBindings; preferences.GamepadModifier = originalModifier;
            gamepadLatest = originalLatest; gamepadHandledDevice = originalDevice; gamepadWasConnected = originalConnected;
            gamepadTest.IsChecked = originalTest; keyTestBox.IsChecked = originalKeyTest; ResetGamepadContext(); RefreshGamepadControls();
        }
    }
}
