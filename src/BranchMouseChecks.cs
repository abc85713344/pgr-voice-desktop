using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    // 定向分派本测试进程的 WPF 事件与 UIA Invoke，不移动光标、不生成系统输入。
    // 它验证真实控件事件接线和状态，不能代替实体鼠标命中与真实游戏实测。
    async Task RunBranchMouseUiTest()
    {
        const string fixture = @"packs\第29章_源解信标\pack.json";
        var results = new List<string>
        {
            "SCOPE: isolated WPF routed mouse events, reflected ClickCount, and UIA button Invoke; not physical input.",
            "SCOPE: simulated game and menu belong to this test process; no input is sent to real games or user processes."
        };
        Window? scene = null;
        IntPtr sceneHandle = IntPtr.Zero;
        int sceneMouseEvents = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message +
                $" [node={engine?.CurrentId}, mode={engine?.Mode}, calls={playCalls}, selected={branchMenu.Options.SelectedIndex}, epoch={branchMenu.Epoch}]");
            results.Add("PASS: " + message);
        }
        void FocusCheck() => Check(Native.GetForegroundWindow() == sceneHandle, "模拟游戏持续持有前台，菜单操作不抢焦点");
        FrameworkElement ItemTarget(int index)
        {
            var item = (ListBoxItem)branchMenu.Options.Items[index];
            branchMenu.Options.ScrollIntoView(item); branchMenu.UpdateLayout();
            return BranchMouseElements<TextBlock>(item).FirstOrDefault() ?? (FrameworkElement)item;
        }
        void Pointer(FrameworkElement target, bool down, int count)
        {
            var setter = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))?.GetSetMethod(true)
                ?? throw new InvalidOperationException("当前 WPF 不支持隔离测试设置 ClickCount。");
            foreach (var routedEvent in down ? new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent }
                : new[] { Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent })
            {
                var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = routedEvent };
                setter.Invoke(input, new object[] { count });
                target.RaiseEvent(input);
            }
        }
        async Task Click(FrameworkElement target, int count = 1)
        { Pointer(target, true, count); await Task.Delay(5); Pointer(target, false, count); await Task.Delay(25); }
        async Task InvokeButton(string startsWith)
        {
            var button = BranchMouseElements<Button>(branchMenu).First(b => b.Content?.ToString()?.StartsWith(startsWith, StringComparison.Ordinal) == true);
            var peer = new ButtonAutomationPeer(button);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("菜单按钮没有 Invoke 模式：" + startsWith);
            invoke.Invoke(); await Task.Delay(45);
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("分支鼠标验收只能在隔离测试模式运行。");
            if (!File.Exists(fixture)) throw new FileNotFoundException("缺少第29章验收配音包。", fixture);
            preferences.DialogueGuardEnabled = false;
            preferences.ClickZoneEnabled = false;
            LoadPack(fixture);
            Check(engine != null && engine.Pack.SchemaVersion >= 2, "载入真实第29章配音包，使用隔离状态且不输出声音");
            preferences.MenuSelections.Clear(); Left = 40; Top = 50;
            scene = new Window
            {
                Title = "分支鼠标隔离验收 · 模拟游戏", Width = 900, Height = 520, Left = 160, Top = 100,
                Background = Brushes.DarkSlateGray,
                Content = new TextBlock { Text = "模拟游戏窗口\n仅用于验证菜单不抢焦点，不接收测试鼠标输入。", FontSize = 24, Margin = new Thickness(28) }
            };
            scene.PreviewMouseDown += (_, _) => sceneMouseEvents++;
            scene.PreviewMouseUp += (_, _) => sceneMouseEvents++;
            scene.Show(); sceneHandle = new WindowInteropHelper(scene).Handle;
            game = new GameWindow(sceneHandle, scene.Title, "BranchMouseFixture");
            Collapse(false); scene.Activate(); Native.SetForegroundWindow(sceneHandle); await Task.Delay(100);
            if (Native.GetForegroundWindow() != sceneHandle)
            {
                uint thread = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                bool attached = AttachThreadInput(thread, foreground, true);
                try { scene.Activate(); Native.SetForegroundWindow(sceneHandle); }
                finally { if (attached) AttachThreadInput(thread, foreground, false); }
                await Task.Delay(80);
            }
            FocusCheck();
            string prefix = engine!.Pack.Chapters[0].Sections[0].Id, menuId = prefix + "-menu-000";
            async Task OpenInitialMenu()
            {
                preferences.MenuSelections[menuId] = 0;
                engine.Restore(menuId, new(), new(), new(), engine.Pack.SchemaVersion);
                engine.Commit(menuId); await Task.Delay(30); branchMenu.UpdateLayout();
                Check(branchMenu.IsVisible && !branchMenu.IsNavigation && !expanded && engine.Mode == RunMode.Choice,
                    "显示人物选择菜单，等待明确确认");
            }

            await OpenInitialMenu();
            Check(branchMenu.Options.Items.Count > 2 && engine.AvailableOptions[1].BodyVerified, "第二项是可验证正文的真实选项");
            string secondTarget = engine.AvailableOptions[1].TargetId;
            int calls = playCalls;
            var second = ItemTarget(1);
            await Click(second);
            Check(branchMenu.Options.SelectedIndex == 1 && engine.CurrentId == menuId && playCalls == calls,
                "单击第二项只选中，不进入路线、不播放");
            InteractionScreenshot("branch-mouse-menu.png");
            FocusCheck();

            preferences.ClickZoneEnabled = true; UpdateClickZone(); clickZone.UpdateLayout();
            clickZone.PlaceAt(scene.Left + 100, scene.Top + 100);
            var hotPoint = clickZone.PointToScreen(new Point(clickZone.ActualWidth / 2, clickZone.ActualHeight / 2));
            Task ObservedAt(long timestamp) => ObserveMouseTapForTest(new("鼠标左键", sceneHandle, (int)hotPoint.X, (int)hotPoint.Y,
                timestamp, Stopwatch.GetTimestamp()));
            await ObservedAt(Stopwatch.GetTimestamp());
            Check(branchMenu.IsVisible && engine.CurrentId == menuId && playCalls == calls, "菜单可见时忽略模拟 Raw Input 跟随点击");

            Pointer(second, true, 2); await Task.Delay(10);
            Check(branchMenu.IsVisible && engine.CurrentId == menuId && playCalls == calls, "双击第二次按下仍不确认，等待同项松开");
            Pointer(second, false, 2); await Task.Delay(40);
            Check(!branchMenu.IsVisible && engine.CurrentId == secondTarget && playCalls == calls + 1,
                "双击第二次松开后进入第二项正确目标，仅请求播放一次");
            Check(branchMenu.LastPointerInputTimestamp > 0, "真实菜单预览事件记录了指针输入时间");
            FocusCheck();
            long menuPointerTime = branchMenu.LastPointerInputTimestamp;
            await ObservedAt(menuPointerTime); await ObservedAt(menuPointerTime - 1); await Task.Delay(20);
            Check(engine.CurrentId == secondTarget && playCalls == calls + 1,
                "菜单收起后的同次及更早鼠标消息不被当成下一句");
            await Task.Delay(5); await ObservedAt(Stopwatch.GetTimestamp()); await Task.Delay(20);
            Check(engine.CurrentId != secondTarget, "正向对照：菜单之外的新点击仍可推进，迟到消息断言不是因跟随整体失效");
            preferences.ClickZoneEnabled = false; UpdateClickZone();

            await OpenInitialMenu(); calls = playCalls; await Click(ItemTarget(1));
            await InvokeButton("确认进入");
            Check(!branchMenu.IsVisible && engine.CurrentId == secondTarget && playCalls == calls + 1,
                "确认按钮 Invoke 接线可用，选择第二项并且只播放一次");
            FocusCheck();

            await OpenInitialMenu(); calls = playCalls; await Click(ItemTarget(1));
            await InvokeButton("收起，保持待选");
            Check(!branchMenu.IsVisible && engine.CurrentId == menuId && engine.Mode == RunMode.Choice && playCalls == calls,
                "取消按钮保持待选和原剧情位置，不播放");
            OpenStory(); await Task.Delay(30);
            Check(branchMenu.IsVisible && branchMenu.Options.SelectedIndex == 1 && playCalls == calls,
                "重新打开菜单保留鼠标选中的第二项");
            Check(keyboard != null && keyboard.ProbeMenuKey(40, true, sceneHandle), "方向键仍由菜单处理");
            keyboard!.ProbeMenuKey(40, false, sceneHandle); await Task.Delay(35);
            Check(branchMenu.Options.SelectedIndex == 2 && playCalls == calls && engine.CurrentId == menuId,
                "鼠标选中后下方向键继续浏览，不确认或播放");
            keyboard.ProbeMenuKey(38, true, sceneHandle); keyboard.ProbeMenuKey(38, false, sceneHandle); await Task.Delay(35);
            Check(branchMenu.Options.SelectedIndex == 1 && playCalls == calls, "上方向键可回到第二项，继续保持静音");

            second = ItemTarget(1); Pointer(second, true, 2); Pointer(ItemTarget(0), false, 2); await Task.Delay(25);
            Check(branchMenu.IsVisible && engine.CurrentId == menuId && playCalls == calls,
                "双击第二次按下后在另一项松开，不误确认");

            OpenInteractions(); await Task.Delay(35);
            string navigationTarget = prefix + "-menu-006";
            int navigationIndex = branchMenu.Options.Items.OfType<ListBoxItem>().ToList().FindIndex(item => (string?)item.Tag == navigationTarget);
            Check(branchMenu.IsNavigation && navigationIndex >= 0, "手动分支导航包含戴里克话题入口");
            string? previousNode = engine.CurrentId;
            var navigationItem = ItemTarget(navigationIndex);
            await Click(navigationItem);
            Check(branchMenu.IsNavigation && branchMenu.SelectedNavigationId == navigationTarget && engine.CurrentId == previousNode && playCalls == calls,
                "手动导航单击只选中对应菜单，保持原位置和静音");
            await Click(navigationItem, 2);
            Check(branchMenu.IsVisible && !branchMenu.IsNavigation && engine.CurrentId == navigationTarget && engine.Mode == RunMode.Choice && playCalls == calls,
                "手动导航双击打开正确话题菜单，不播放正文");
            FocusCheck();
            Check(sceneMouseEvents == 0, "模拟游戏未收到任何测试鼠标事件，所有事件定向到本进程菜单");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally
        {
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir, "branch-mouse-ui-test.txt"), results);
            game = null; scene?.Close(); Close();
        }
    }

    static IEnumerable<T> BranchMouseElements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T matched) yield return matched;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in BranchMouseElements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
