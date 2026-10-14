using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>
/// 0.4.7 悬浮快捷窗自检（--test-quick-window-ui --test-state &lt;独立目录&gt;）：窗口不激活、点连接只调用一次连接、
/// 台词条双击事件 / 按钮排除、F10 热键注册 / 不重复触发、改键窗口打开时注销、位置按显示器保存与恢复、16 px 吸附、状态 18b/18c/18d、扇出与置顶。
/// 结果写 Log.DataDir\quick-window-ui-test.txt。真实按键只在本测试窗口处于前台时发送；否则记 SKIP。
/// </summary>
public partial class MainWindow
{
    async Task RunQuickWindowUiTest(string[] args)
    {
        var report = new List<string>
        {
            "SCOPE: 台词条鼠标检查只分派本进程 WPF 预览事件并设置 ClickCount，不移动光标、不发送实体鼠标输入。",
            "SCOPE: 18b 使用隔离游戏文字、时钟及点击计数替身；全屏游戏、实体鼠标命中、手柄仍需实机验证。"
        };
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        async Task Drain() { await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); await Task.Delay(60); }
        bool OwnForeground() { GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid); return pid == (uint)Environment.ProcessId; }
        Button? FindByName(DependencyObject root, string name)
        {
            if (root is FrameworkElement { Visibility: not Visibility.Visible }) return null;
            if (root is Button b && System.Windows.Automation.AutomationProperties.GetName(b) == name) return b;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindByName(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
            return null;
        }
        bool HasText(DependencyObject root, string text)
        {
            if (root is TextBlock t && t.Text.Contains(text)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (HasText(VisualTreeHelper.GetChild(root, i), text)) return true;
            return false;
        }
        TextBlock? FirstText(DependencyObject root)
        {
            if (root is TextBlock text) return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FirstText(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
        void PreviewLeftDown(FrameworkElement target, int count)
        {
            // 与 BranchMouseChecks 的 ClickCount 注入一致；只发预览阶段，避免单击测试进入 DragMove。
            var setter = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))?.GetSetMethod(true)
                ?? throw new InvalidOperationException("当前 WPF 不支持隔离测试设置 ClickCount。");
            var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            setter.Invoke(input, new object[] { count });
            target.RaiseEvent(input);
        }
        async Task<bool> Eventually(Func<bool> condition, int ms = 2000)
        {
            for (var end = DateTime.UtcNow.AddMilliseconds(ms); !condition() && DateTime.UtcNow < end;) { await Task.Delay(50); quick?.UpdateLayout(); }
            return condition();
        }
        async Task PressKey(System.Windows.Input.Key key)
        {
            byte vk = (byte)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero); await Task.Delay(30); keybd_event(vk, 0, 2, UIntPtr.Zero);
            await Task.Delay(450); await Drain();
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("快捷窗自检只允许在隔离测试入口运行。");
            Check(preferences.Keys.GetValueOrDefault("connect") == "F10" && actionLabels.ContainsKey("connect"), "默认键：连接普通对白 = F10，可在按键设置中修改");
            var legacy = new Preferences { QuickWindowPositions = null!, Keys = new() { ["next"] = "Space" } };
            legacy.QuickWindowPositions = null!;
            PreferenceValidation.Normalize(legacy);
            Check(legacy.QuickWindowPositions != null && legacy.QuickWindowPinned, "旧配置缺快捷窗字段：补默认值（位置空、置顶开）");
            var broken = new Preferences(); broken.QuickWindowPositions["X"] = new[] { 1 }; PreferenceValidation.Normalize(broken);
            Check(!broken.QuickWindowPositions.ContainsKey("X"), "非法位置记录被丢弃");

            // 收起 A：台词条
            preferences.CompactMode = "strip"; Collapse(false); UpdateLayout(); await Drain();
            Check(StripView.IsVisible && Math.Abs(Width - Math.Min(QuickStripWidth, SystemParameters.WorkArea.Width - 20)) < 1, "台词条加宽到 720");
            Check(quickStripConnect?.IsVisible == true && HasText(quickStripRow, "F10"), "台词条右侧显示「连接普通对白」及 F10 键帽");

            // 真实控件的预览事件接线：正文双击展开；单击及按钮子元素双击不展开。
            HideQuick();
            var stripOwner = engine; var stripNode = engine?.CurrentId; var stripMode = engine?.Mode;
            int stripCalls = quickConnectCalls, stripPlayCalls = playCalls; bool stripArmed = textArmed;
            var stripForeground = Native.GetForegroundWindow();
            PreviewLeftDown(StripText, 1); await Drain();
            Check(!QuickPanelOpen && !expanded, "台词条正文单击预览事件不展开快捷面板或主窗口");
            var buttonText = FirstText(quickStripConnect!);
            Check(buttonText != null, "台词条连接按钮包含用于验证事件来源的文字子元素");
            PreviewLeftDown(buttonText!, 2); await Drain();
            Check(!QuickPanelOpen && !expanded, "台词条按钮文字子元素双击预览事件不误展开快捷面板");
            PreviewLeftDown(StripText, 2); await Drain();
            Check(QuickPanelOpen && !expanded && Opacity == 0, "台词条正文双击经预览事件展开快捷面板，主窗口仍收起");
            Check(ReferenceEquals(engine, stripOwner) && engine?.CurrentId == stripNode && engine?.Mode == stripMode &&
                playCalls == stripPlayCalls && quickConnectCalls == stripCalls && textArmed == stripArmed,
                "台词条单击 / 双击仅改变展示，不连接、不播放、不推进句子或改变跟随状态");
            Check(quick?.IsActive == false && Native.GetForegroundWindow() == stripForeground, "台词条双击展开快捷面板不抢前台焦点");
            HideQuick(); await Drain();

            // 展开 C：不激活
            var before = Native.GetForegroundWindow();
            ShowQuickPanel(); await Drain(); await Task.Delay(200);
            var q = quick!;
            Check(q.IsVisible && q.Form == QuickWindow.Shape.Panel && q.ActualWidth >= 360, "快捷面板显示（宽 364）");
            Check((GetWindowLong(q.Handle, -20) & 0x08000000) != 0 && !q.ShowActivated, "快捷窗带 WS_EX_NOACTIVATE，显示时不激活");
            Check(!q.IsActive && Native.GetForegroundWindow() == before, "显示快捷窗后前台窗口不变");
            Check(!HasText(q, "主线") && !HasText(q, "闲聊"), "快捷窗不显示主线 / 闲聊标识");

            // 点连接只调用一次
            Button? connect = null;
            for (int i = 0; i < 40 && (connect = FindByName(q, "连接普通对白")) == null; i++) { await Task.Delay(50); q.UpdateLayout(); }
            Check(connect != null, "快捷面板有「连接普通对白」主按钮（状态 " + quickState + "，正文 " + q.Body.Children.Count + " 项，可见 " + q.IsVisible + "）");
            int calls = quickConnectCalls; before = Native.GetForegroundWindow();
            connect!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Check(quickConnectCalls == calls + 1, "点一次连接只调用一次连接");
            Check(engine == null ? !textArmed && quickState == "d" : textArmed, engine == null ? "未打开配音包时不连接，显示 18d「没读到游戏台词」与原因" : "连接后已开启文字监听（同主窗口 BeginTextMonitoring）");
            Check(!q.IsActive && !expanded, "点连接不展开主窗口、不激活快捷窗");
            if (engine == null) Check(HasText(q, "没读到游戏台词") && FindByName(q, "连接 3D 对白") != null && FindByName(q, "打开主窗口手动选句") != null, "18d：标题、连接 3D 对白、打开主窗口手动选句");
            if (textArmed) { PauseTextPlayback("测试结束连接。"); await Drain(); }
            quickFailure = null;

            // HandleGlobal 不重复触发
            calls = quickConnectCalls;
            try { await PrepareIsolatedTestForeground(this, "快捷窗热键"); } catch (Exception ex) { report.Add("SKIP: 前台准备失败：" + ex.Message); }
            TickQuick(); await Drain();
            HandleGlobal(System.Windows.Input.Key.F10, Native.GetForegroundWindow());
            Check(quickConnectCalls == calls, "全局按键路由忽略 connect 键（不与热键重复触发）");

            bool foreground = OwnForeground();
            if (foreground)
            {
                Check(quickHotkeyKey == "F10", "播放器在前台时 F10 已用 RegisterHotKey 注册" + (quickHotkeyError.Length > 0 ? "（" + quickHotkeyError + "）" : ""));
                calls = quickConnectCalls; await PressKey(System.Windows.Input.Key.F10);
                Check(quickConnectCalls == calls + 1, "按 F10 触发一次连接（不重复）");
                if (textArmed) PauseTextPlayback("测试结束连接。"); quickFailure = null;
            }
            else report.Add("SKIP: 测试窗口不在前台（远程启动时 Windows 不允许抢前台），未发送真实 F10；注册条件已由下项覆盖");

            // 改键窗口打开时注销
            recordingAction = "connect"; TickQuick();
            Check(quickHotkeyKey.Length == 0, "改键时注销热键");
            recordingAction = null;

            // 改键后新键生效
            preferences.Keys["connect"] = "F11"; RefreshQuick(true);
            if (!OwnForeground()) try { await PrepareIsolatedTestForeground(this, "快捷窗改键"); } catch { }
            TickQuick(); await Drain();
            Check(HasText(quickStripRow, "F11") && HasText(q, "F11"), "改键后台词条与快捷窗键帽显示新键");
            if (OwnForeground())
            {
                Check(quickHotkeyKey == "F11", "改键后按新键注册");
                calls = quickConnectCalls; await PressKey(System.Windows.Input.Key.F11);
                Check(quickConnectCalls == calls + 1, "按新键 F11 触发一次连接");
                if (textArmed) PauseTextPlayback("测试结束连接。"); quickFailure = null;
                calls = quickConnectCalls; await PressKey(System.Windows.Input.Key.F10);
                Check(quickConnectCalls == calls, "旧键 F10 不再触发");
            }
            else report.Add("SKIP: 测试窗口不在前台，未发送真实 F11 / F10");
            HandleGlobal(System.Windows.Input.Key.F11, Native.GetForegroundWindow());
            preferences.Keys["connect"] = "F10"; TickQuick(); RefreshQuick(true);

            // 位置：按显示器保存与恢复
            var screen = System.Windows.Forms.Screen.FromHandle(q.Handle);
            var area = screen.WorkingArea;
            int x = area.Left + 137, y = area.Top + 93;
            q.MoveTo(x, y); SaveQuickPosition();
            Check(preferences.QuickWindowPositions.TryGetValue(screen.DeviceName, out var saved) && saved[0] == x && saved[1] == y, "拖动结束按显示器保存位置（" + screen.DeviceName + "）");
            HideQuick(); quickHome = null; await Drain();
            ShowQuickPanel(); await Drain();
            Check(q.PixelRect.Left == x && q.PixelRect.Top == y, "重新打开快捷窗恢复到保存的位置");
            var copy = System.Text.Json.JsonSerializer.Deserialize<Preferences>(System.Text.Json.JsonSerializer.Serialize(new Preferences { QuickWindowPinned = false, QuickFullscreenHintShown = true, QuickWindowPositions = new(preferences.QuickWindowPositions) }, Json.Options), Json.Options)!;
            Check(copy.QuickWindowPositions.TryGetValue(screen.DeviceName, out var back) && back[0] == x && back[1] == y && !copy.QuickWindowPinned && copy.QuickFullscreenHintShown, "位置 / 置顶关闭 / 全屏提示已显示 都能写入并读回设置");

            // 16 px 吸附
            int w = q.PixelRect.Width, h = q.PixelRect.Height, near = (int)Math.Round(10 * q.Scale), far = (int)Math.Round(30 * q.Scale);
            var cursor = new QuickWindow.POINT { X = area.Left + 200, Y = area.Top + 200 };
            Check(q.Snap(area.Left + near, area.Top + 300, w, h, cursor) == (area.Left, area.Top + 300), "距工作区左边 16 px 内吸附");
            Check(q.Snap(area.Left + far, area.Top + 300, w, h, cursor) == (area.Left + far, area.Top + 300), "超过 16 px 不吸附");
            Check(q.Snap(area.Right - w - near, area.Bottom - h - near, w, h, cursor) == (area.Right - w, area.Bottom - h), "右下角吸附");

            // 置顶
            q.SetPinned(false); q.PinChanged?.Invoke();
            Check(!q.Topmost && !preferences.QuickWindowPinned, "关闭置顶并保存");
            q.SetPinned(true); q.PinChanged?.Invoke();
            Check(q.Topmost && preferences.QuickWindowPinned, "置顶默认开启");

            // 状态 18c / 默认
            quickRealignedAt = DateTime.UtcNow; quickRealignedText = "已对齐：丽芙「应该就是附近了吧……」 · 继续跟随"; RefreshQuick(false); await Drain();
            Check(await Eventually(() => quickState == "c" && HasText(q, "已对齐") && HasText(q, "3 秒后")), "18c 对齐成功提示");
            quickRealignedAt = DateTime.UtcNow.AddSeconds(-5); TickQuick(); await Drain();
            Check(!q.IsVisible, "对齐成功 3 s 后自动收起");
            ShowQuickPanel(); await Drain();
            bool defaultView = await Eventually(() => quickState is "idle" or "follow" && HasText(q, "章节、分支、设置等在主窗口") && FindByName(q, "连接普通对白") != null && FindByName(q, "对白跟随") != null && FindByName(q, "暂停") != null && FindByName(q, "重播") != null && FindByName(q, "下一句") != null);
            Check(defaultView, "默认态：当前句、主按钮、对白跟随 / 探索面板切换、暂停 / 重播 / 下一句、打开主窗口（状态 " + quickState + "，暂停 " + (FindByName(q, "暂停") != null) + "，重播 " + (FindByName(q, "重播") != null) + "，下一句 " + (FindByName(q, "下一句") != null) + "，链接 " + HasText(q, "章节、分支、设置等在主窗口") + "）");

            // 扇出 B
            preferences.CompactMode = "ball"; ApplyCompactView(); UpdateLayout(); await Drain();
            ShowQuickFan(); await Drain();
            Check(q.IsVisible && q.Form == QuickWindow.Shape.Fan && FindByName(q, "连接普通对白") != null && q.PixelRect.Left >= MainPixelRect().Right, "右键球扇出：主按钮 + 探索面板 + 暂停，位于球右侧");
            calls = quickConnectCalls; FindByName(q, "连接普通对白")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Check(quickConnectCalls == calls + 1 && !q.IsVisible, "扇出点连接调用一次并收起扇出");
            if (textArmed) PauseTextPlayback("测试结束连接。");

            // 键帽跟随 Keys：改 manualNext 后「下一句」键帽文字更新（下一句 = 配音下一句，不点游戏）
            preferences.CompactMode = "strip"; ApplyCompactView(); UpdateLayout();
            ShowQuickPanel(); await Drain();
            string oldNext = preferences.Keys["manualNext"];
            string newNext = oldNext == "End" ? "Home" : "End";
            bool nextFree = !preferences.Keys.Any(k => k.Key != "manualNext" && k.Value == newNext);
            Check(await Eventually(() => FindByName(q, "下一句") is { } nb && HasText(nb, KeyName("manualNext"))), "「下一句」键帽显示 Keys 里的实际绑定（" + KeyName("manualNext") + "）");
            preferences.Keys["manualNext"] = newNext; RefreshKeyHints(); TickQuick(); await Drain();
            if (!q.IsVisible || q.Form != QuickWindow.Shape.Panel) ShowQuickPanel();
            RefreshQuick(false); await Drain();
            bool nextUpdated = nextFree && await Eventually(() => FindByName(q, "下一句") is { } nb && HasText(nb, KeyName("manualNext")));
            string KeyTexts(DependencyObject root) { var list = new List<string>(); void Walk(DependencyObject d) { if (d is TextBlock t) list.Add(t.Text); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i)); } Walk(root); return string.Join("/", list); }
            Check(nextUpdated, "改 manualNext 键后「下一句」键帽更新为 " + KeyName("manualNext") + "（空闲 " + nextFree + "，可见 " + q.IsVisible + "，形态 " + q.Form + "，状态 " + quickState + "，按钮文字 " + (FindByName(q, "下一句") is { } dbg ? KeyTexts(dbg) : "无") + "）");
            preferences.Keys["manualNext"] = oldNext; RefreshKeyHints(); RefreshQuick(false); await Drain();
            Check(await Eventually(() => FindByName(q, "下一句") is { } nb && HasText(nb, KeyName("manualNext"))), "改回后键帽恢复为 " + KeyName("manualNext"));

            // 设计稿 17 / 19：面板打开时球 / 台词条隐去（全透明穿透），收起后恢复
            Check(Opacity == 0 && IsVisible, "快捷面板打开时台词条全透明（不 Hide，热键仍在）");
            HideQuick(); await Drain();
            Check(Opacity == 1 && !q.IsVisible, "收起面板后台词条恢复显示");

            // 18b 终点等待 + 18a 探索列表 + 避让分支窗 / 点击区：需要 --fixtures047 夹具
            int dirArg = Array.IndexOf(args, "--fixtures047");
            string fixtures = dirArg >= 0 && dirArg + 1 < args.Length ? args[dirArg + 1] : Path.Combine(Log.DataDir, "fixtures047");
            string gameEnd = Path.Combine(fixtures, "game-end.json"), ch29 = Path.Combine(fixtures, "ch29.json");
            if (!File.Exists(gameEnd) || !File.Exists(ch29)) report.Add("SKIP: 18b / 18a / 避让 需要 --fixtures047 目录（game-end.json、ch29.json）：" + fixtures);
            else
            {
                // ---- 18b ----
                StopListeningForGame(); LoadPack(gameEnd);
                Check(engine?.Pack.Id == "synthetic-game-end", "载入终点等待夹具");
                string sampleText = "", sampleSpeaker = ""; long fakeNow = 1_000_000;
                int endClicks = 0; bool? previousTextAuto = textAutoBox.IsChecked;
                gameTextSampleTest = p => ReferenceEquals(p, textProbes[0]) && sampleText.Length > 0 ? new GameTextSample(true, sampleText, false, "", true, sampleSpeaker) : new GameTextSample(false, "", false, "隔离测试");
                textAutoClockTest = () => fakeNow;
                textAutoEnvironmentTest = () => true; textAutoForegroundTest = () => true;
                textAutoSampleTest = () => new GameTextSample(true, sampleText, false, "", true, sampleSpeaker);
                textAutoClickTest = () => { endClicks++; return true; };
                try
                {
                    textAutoBox.IsChecked = true;
                    var chapter = engine!.Pack.Chapters.First(c => c.Sections.Any(x => x.Id == "syn-sec"));
                    ChapterBox.SelectedItem = chapter; SectionBox.SelectedItem = chapter.Sections.First(x => x.Id == "syn-sec");
                    ArmTextPlayback(); Check(textArmed, "18b：开启文字跟随");
                    var last = engine.Pack.ById["syn-sec-s9"]; sampleText = last.Text; sampleSpeaker = last.Speaker;
                    Check(await Eventually(() => engine.CurrentId == "syn-sec-s9", 4000), "18b：跟随到小节末句");
                    fakeNow += 60_000;
                    Check(await Eventually(() => GameEndWaiting != null, 4000), "18b：末句停留后进入终点等待");
                    ShowQuickPanel(); await Drain();
                    Check(await Eventually(() => quickState == "b" && HasText(q, "原表终点") && FindByName(q, "停止配音跟随") != null && FindByName(q, "连接普通对白") != null),
                        "18b：快捷面板显示终点等待「原表终点」+ 连接普通对白 + 停止配音跟随（状态 " + quickState + "）");
                    var kept = GameEndWaiting; calls = quickConnectCalls;
                    fakeNow += 60_000; TickGameText(); TickQuick(); await Drain();
                    Check(textAutoBox.IsChecked == true && endClicks == 0 && textAutoLine == null && engine.CurrentId == "syn-sec-s9" && ReferenceEquals(GameEndWaiting, kept),
                        "18b：自动点击已开启，旧末句继续停留仍不点击、不推进，并保留终点等待");
                    Check(!ListeningCounting && !(listeningCountdownTimer?.IsEnabled ?? false) && !HasText(q, "倒计时"),
                        "18b：等待游戏画面不启动听书倒计时，快捷面板不显示倒计时");
                    FindByName(q, "连接普通对白")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                    Check(quickConnectCalls == calls + 1 && textArmed && ReferenceEquals(GameEndWaiting, kept), "18b：快捷窗点连接走 ConnectOrdinaryDialogue，保留终点等待");
                    Check(await Eventually(() => quickState == "b" && FindByName(q, "连接普通对白") != null && FindByName(q, "取消连接") == null),
                        "18b：重新连接后主按钮仍为「连接普通对白」，不变成「取消连接」");
                    fakeNow += 60_000; TickGameText(); TickQuick(); await Drain();
                    Check(textArmed && ReferenceEquals(GameEndWaiting, kept) && engine.CurrentId == "syn-sec-s9" && endClicks == 0 && textAutoLine == null && !ListeningCounting && !HasText(q, "倒计时"),
                        "18b：重新连接后旧末句继续等待，仍不点击、不推进、不增加倒计时");
                    var stop = FindByName(q, "停止配音跟随");
                    Check(stop != null, "18b：重新连接后仍保留「停止配音跟随」按钮");
                    stop!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                    Check(await Eventually(() => !textArmed && GameEndWaiting == null && gameEndWaitCard.Visibility == Visibility.Collapsed && quickState != "b"),
                        "18b：点停止按钮关闭跟随并清除终点等待卡");
                    Check(engine.CurrentId == "syn-sec-s9" && endClicks == 0 && quickConnectCalls == calls + 1,
                        "18b：停止等待不点击游戏、不推进句子、不再次连接");
                    Check(!q.IsActive, "18b：快捷窗未被激活");
                }
                finally
                {
                    PauseTextPlayback("快捷窗 18b 检查结束"); textAutoBox.IsChecked = previousTextAuto;
                    gameTextSampleTest = null; textAutoClockTest = null; textAutoEnvironmentTest = null;
                    textAutoForegroundTest = null; textAutoSampleTest = null; textAutoClickTest = null; sampleText = "";
                }
                HideQuick(); await Drain();

                // ---- 18a ----
                LoadPack(ch29);
                var menu = engine!.Pack.Nodes.FirstOrDefault(n => n.Kind == "choice" && n.Options.Count >= 3 && QuickShape(n) is { Exploration: true } sh && sh.Exits.Count > 0 && engine.OpenGameMenu(n.Id, n.SectionId))
                    ?? engine.Pack.Nodes.FirstOrDefault(n => n.Kind == "choice" && QuickShape(n) is { Exploration: true } && engine.OpenGameMenu(n.Id, n.SectionId));
                Check(menu != null, "18a：ch29 夹具里找到探索菜单");
                var shape = QuickShape(menu!)!;
                FillLines(); HideBranchMenu(); quickExploreView = null;
                ShowQuickPanel(); await Drain();
                Check(await Eventually(() => quickState.StartsWith("a:", StringComparison.Ordinal) && HasText(q, "探索中") && FindByName(q, "探索面板") != null), "18a：停在探索菜单 " + menu!.Id + " 时快捷面板显示探索列表（状态 " + quickState + "）");
                var rows = QuickExploreRows(menu);
                Check(rows.Count > 0 && rows.All(r => FindByName(q, QuickExploreRowName(r, rows)) != null), "18a：每个可见选项一行，A/B/C 键位字（出口 EXIT、空回环 ↻ 不占键位字）+ 选项文字（" + rows.Count + " 行）");
                Check(HasText(q, QuickExploreTitle(menu)), "18a：标题沿用分支窗 14 卡菜单名「" + QuickExploreTitle(menu) + "」");
                Check(rows.Where(r => r.Loop).All(r => r.Heard || HasText(q, "↻") || HasText(q, "未听")) && (shape.Exits.Count == 0 || HasText(q, "[→ EXIT") && HasText(q, "随时可选")), "18a：回环 ↻ / 未听、出口 [→ EXIT · 随时可选");
                bool TagText(DependencyObject root, string text)
                {
                    if (root is TextBlock t && t.Text.Trim() == text) return true;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (TagText(VisualTreeHelper.GetChild(root, i), text)) return true;
                    return false;
                }
                var jumpRow = rows.First(r => !r.Exit);
                DesktopExploration.RecordJump(engine, menu.Id, new DesktopExploration.Jump(jumpRow.Option.Id, jumpRow.Option.Label, "测试台词"));
                Check(await Eventually(() => HasText(q, "已根据游戏画面跳转：" + jumpRow.Option.Label) && HasText(q, "读到：测试台词")), "18a：显示分支窗同一份跳转记录（已根据游戏画面跳转 / 读到）");
                Check(!TagText(q, "主线") && !TagText(q, "闲聊") && !TagText(q, "主线剧情"), "18a：不显示主线 / 闲聊标识（选项文字本身可含这些字）");
                int unheard = QuickExploreUnheard(menu);
                Check(unheard == 0 || HasText(q, "还有 " + unheard + " 段没听"), "18a：未听计数「还有 " + unheard + " 段没听」");
                var loopRow = rows.FirstOrDefault(r => r.Loop && !r.ReturnLoop && !r.Heard) ?? rows.FirstOrDefault(r => r.Loop);
                Check(loopRow != null, "18a：有回环行");
                string rowName = QuickExploreRowName(loopRow!, rows);
                FindByName(q, rowName)!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                Check(engine.CurrentId == menu.Id && quickExploreSelected == loopRow!.Index, "18a：单击只选中，不确认（与分支窗一致）");
                Check(await Eventually(() => FindByName(q, rowName) != null), "18a：选中后行仍在");
                FindByName(q, rowName)!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                Check(await Eventually(() => engine.CurrentId != menu.Id, 3000), "18a：双击确认 → 走分支窗同一条 ConfirmSmallBranch 进入该段（当前 " + engine.CurrentId + "，模式 " + engine.Mode + "）");
                bool playingShown = await Eventually(() => QuickExploreMenu() == menu && QuickExploreRows(menu).Any(r => r.Index == loopRow.Index && r.Playing) && HasText(q, "▶ 正在播放"), 3000);
                if (engine.Mode is RunMode.Following or RunMode.Paused && menu.Options[loopRow.Index] is var chosen && engine.Current is { } now && QuickOptionContains(chosen, now))
                    Check(playingShown, "18a：播放该段时对应行显示 ▶ 正在播放");
                else report.Add("SKIP: 18a ▶ 正在播放：确认后引擎不在该段（模式 " + engine.Mode + "，当前 " + engine.CurrentId + "）");
                // 切回「对白跟随」：隐藏探索列表
                FindByName(q, "对白跟随")?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                Check(await Eventually(() => !quickState.StartsWith("a", StringComparison.Ordinal)), "18a：点「对白跟随」收起探索列表（状态 " + quickState + "）");
                quickExploreView = null;

                // ---- 避让分支窗 / 点击区 ----
                engine.OpenGameMenu(menu.Id, menu.SectionId); FillLines(); HideBranchMenu();
                HideQuick(); quickHome = null;
                ShowQuickPanel(); await Drain();
                var screenArea = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(q).Handle).WorkingArea;
                var homeRect = q.PixelRect;
                int hx = screenArea.Left + (screenArea.Width - homeRect.Width) / 2, hy = screenArea.Top + Math.Max(0, (screenArea.Height - homeRect.Height) / 2);
                q.MoveTo(hx, hy); quickHome = (hx, hy); await Drain();
                string AvoidanceDiagnostic(string stage)
                {
                    string Rect(QuickWindow.RECT r) => $"({r.Left},{r.Top},{r.Width},{r.Height})";
                    QuickWindow.GetWindowRect(branchMenu.Handle, out var branchRect);
                    QuickWindow.GetWindowRect(new WindowInteropHelper(clickZone).Handle, out var clickRect);
                    string detail = $"{stage}: expected=({hx},{hy}), actual={Rect(q.PixelRect)}, home={quickHome}, "
                        + $"visible={q.IsVisible}, form={q.Form}, dragging={q.Dragging}, scale={q.Scale}, state={quickState}, expanded={expanded}, "
                        + $"branchVisible={branchMenu.IsVisible}, branch={Rect(branchRect)}, clickVisible={clickZone.IsVisible}, click={Rect(clickRect)}, "
                        + $"obstacles=[{string.Join(";", QuickObstacles().Select(Rect))}]";
                    report.Add("OBSERVE: " + detail);
                    return detail;
                }
                AvoidanceDiagnostic("分支窗打开前");
                var positionsBeforeAvoid = preferences.QuickWindowPositions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
                bool PositionsUnchanged() => preferences.QuickWindowPositions.Count == positionsBeforeAvoid.Count &&
                    positionsBeforeAvoid.All(pair => preferences.QuickWindowPositions.TryGetValue(pair.Key, out var value) && value.SequenceEqual(pair.Value));
                ShowBranchMenu(); await Drain();
                Check(branchMenu.IsVisible, "避让：分支窗已打开");
                Check(SetQuickCheckWindowPos(branchMenu.Handle, IntPtr.Zero, hx + 20, hy + 20, 0, 0, 0x0001 | 0x0004 | 0x0010),
                    "避让夹具：以物理像素放置分支窗，不借用快捷窗 DPI");
                branchMenu.UpdateLayout(); await Drain();
                AvoidQuickObstacles(); await Drain();
                var obstacles = QuickObstacles().ToList(); var now2 = q.PixelRect;
                AvoidanceDiagnostic("分支窗打开并避让后");
                Check(obstacles.Count > 0 && !obstacles.Any(o => Overlaps(o, now2.Left, now2.Top, now2.Width, now2.Height, 0)) && quickHome == (hx, hy), "避让：分支窗盖住快捷窗时临时移开，不改记忆位置");
                Check(PositionsUnchanged(), "避让：分支窗临时移开不改任何显示器的已保存位置");
                HideBranchMenu(); await Drain(); AvoidQuickObstacles(); await Drain();
                bool returnedImmediately = q.PixelRect.Left == hx && q.PixelRect.Top == hy;
                AvoidanceDiagnostic("分支窗关闭后立即");
                await Task.Delay(350); await Drain();
                string branchDiagnostic = AvoidanceDiagnostic("分支窗关闭后稳定观察");
                Check(returnedImmediately && q.PixelRect.Left == hx && q.PixelRect.Top == hy,
                    "避让：分支窗关闭后回到原位（立即归位=" + returnedImmediately + "；" + branchDiagnostic + "）");
                Check(PositionsUnchanged(), "避让：分支窗关闭回原位后已保存位置仍不变");
                clickZone.Show();
                Check(SetQuickCheckWindowPos(new WindowInteropHelper(clickZone).Handle, IntPtr.Zero, hx + 30, hy + 30, 0, 0, 0x0001 | 0x0004 | 0x0010),
                    "避让夹具：以物理像素放置点击区，不借用快捷窗 DPI");
                clickZone.UpdateLayout(); await Drain();
                AvoidQuickObstacles(); await Drain();
                obstacles = QuickObstacles().ToList(); now2 = q.PixelRect;
                Check(clickZone.IsVisible && obstacles.Count > 0 && !obstacles.Any(o => Overlaps(o, now2.Left, now2.Top, now2.Width, now2.Height, 0)), "避让：点击区盖住快捷窗时临时移开");
                Check(PositionsUnchanged(), "避让：点击区临时移开不改任何显示器的已保存位置");
                clickZone.Hide(); await Drain(); AvoidQuickObstacles(); await Drain();
                bool clickReturnedImmediately = q.PixelRect.Left == hx && q.PixelRect.Top == hy;
                AvoidanceDiagnostic("点击区隐藏后立即");
                await Task.Delay(350); await Drain();
                string clickDiagnostic = AvoidanceDiagnostic("点击区隐藏后稳定观察");
                Check(clickReturnedImmediately && q.PixelRect.Left == hx && q.PixelRect.Top == hy,
                    "避让：点击区隐藏后回到原位（立即归位=" + clickReturnedImmediately + "；" + clickDiagnostic + "）");
                Check(PositionsUnchanged(), "避让：点击区隐藏回原位后已保存位置仍不变");
                HideQuick();
            }
            Check(!q.IsActive, "全程快捷窗未被激活");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir, "quick-window-ui-test.txt"), report);
            Close();
        }
    }
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    static extern bool SetQuickCheckWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
