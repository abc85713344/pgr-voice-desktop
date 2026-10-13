using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PgrVoice.Following;

namespace PgrVoice;

/// <summary>
/// 0.4.7 桌面悬浮快捷窗（设计稿 17–19）的主窗口接线。
/// · 收起 A（台词条）：台词条加宽到 720，右侧加「连接普通对白 / 探索 / 暂停 / ▾」；▾ 打开快捷面板。
/// · 收起 B（悬浮球）：悬停球 600 ms 打开快捷面板；右键球扇出「连接普通对白 + 探索面板 + 暂停」。
/// · 展开 C（快捷面板，宽 364）：按状态 18a 探索中 / 18b 结尾等待 / 18c 对齐成功 / 18d 没读到台词 / 默认（等待连接 · 跟随中）填充。
/// 「连接普通对白」直接调用现有 BeginTextMonitoring(普通对白探针)，与主窗口「游戏配音 › 文字跟随」同一操作；不调用 Expand()。
/// 热键（默认 F10，动作键 "connect"）用 RegisterHotKey 注册：只在游戏或本播放器窗口在前台时注册，改键窗口打开时注销，避免 F10 激活菜单栏；
/// HandleGlobal 忽略 "connect"，不会触发两次。快捷窗不激活（WS_EX_NOACTIVATE），位置按显示器记住（物理像素），遇到分支窗 / 点击区徽标时避让 16 px。
/// </summary>
public partial class MainWindow
{
    public const double QuickStripWidth = 720;
    const int QuickHotkeyId = 0x4717;
    QuickWindow? quick;
    bool quickReady, quickAwaitAlign;
    int quickConnectCalls;
    string quickHotkeyKey = "";          // 当前已注册的键（"" 表示未注册）
    string quickHotkeyError = "";
    string quickState = "", quickKeySignature = "";
    string? quickFailure, quickFullscreenHint;
    DateTime quickRealignedAt = DateTime.MinValue, quickFanLeftAt = DateTime.MinValue;
    string quickRealignedText = "";
    (int X, int Y)? quickHome;           // 用户放置的位置；避让只临时偏移
    readonly DispatcherTimer quickTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer ballHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly StackPanel quickStripRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
    Button? quickStripConnect, quickStripPause;
    Border? quickStripKey;
    TextBlock? quickStripPauseText;
    // 面板内可刷新的元素
    TextBlock? qStatus, qSection, qSpeaker, qLine, qLineSub, qConnectTitle, qConnectSub;
    Border? qConnectKey;
    SpeakerPortrait? qPortrait;
    QuickUi.PauseButton? qPause;
    QuickUi.Segment? qSegment;

    bool QuickPanelOpen => quick?.IsVisible == true && quick.Form == QuickWindow.Shape.Panel;
    bool QuickFanOpen => quick?.IsVisible == true && quick.Form == QuickWindow.Shape.Fan;
    TextProbe OrdinaryProbe => textProbes.First(p => p.Kind == GameTextKind.Ordinary);
    bool QuickConnecting => textArmed && textProbes.All(p => p.Reader == null);
    /// <summary>主按钮是否处于「取消连接」：终点等待中不切换——再点仍是 ConnectOrdinaryDialogue（保留等待），停止用 18b 的「停止配音跟随」。</summary>
    bool QuickCancelable => QuickConnecting && GameEndWaiting == null;
    string QuickKeyLabel => (preferences.Keys.GetValueOrDefault("connect") ?? "None") is "None" or "" ? "" : KeyName("connect");

    void InitializeQuickExperience()
    {
        if (quickReady) return;
        quickReady = true;
        BuildQuickStrip();
        // 双击台词条（非按钮处）展开快捷面板（设计稿 19：展开 = 点 ▾ / 双击标题栏 / 悬停球 600 ms）
        StripView.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            for (var element = e.OriginalSource as DependencyObject; element != null && element != StripView; element = VisualTreeHelper.GetParent(element))
                if (element is Button || element is System.Windows.Controls.Primitives.ScrollBar) return;
            e.Handled = true; ShowQuickPanel();
        };
        SizeChanged += (_, _) => { if (expanded && Opacity < 1) Opacity = 1; };
        BallView.MouseEnter += (_, _) => { if (!expanded && !QuickPanelOpen) ballHoverTimer.Start(); };
        BallView.MouseLeave += (_, _) => ballHoverTimer.Stop();
        BallView.PreviewMouseLeftButtonDown += (_, _) => ballHoverTimer.Stop();
        BallView.MouseRightButtonUp += (_, e) => { e.Handled = true; ballHoverTimer.Stop(); ShowQuickFan(); };
        ballHoverTimer.Tick += (_, _) =>
        {
            ballHoverTimer.Stop();
            if (!expanded && BallView.IsMouseOver && Mouse.LeftButton != MouseButtonState.Pressed && !closing) ShowQuickPanel();
        };
        quickTimer.Tick += (_, _) => TickQuick();
        quickTimer.Start();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(QuickHotkeyHook);
        Closed += (_, _) => { quickTimer.Stop(); UnregisterQuickHotkey(); if (quick != null) { quick.Close(); quick = null; } };
    }

    // ---------------- 「连接普通对白」 ----------------
    /// <summary>快捷窗按钮、台词条按钮、扇出按钮与热键共用：直接调用现有 BeginTextMonitoring(普通对白)。连接中再点为「取消连接」。</summary>
    void QuickConnect(string source)
    {
        quickConnectCalls++;
        Log.Write("quick-connect", source);
        if (closing) return;
        if (ListeningActive) { quickFailure = "听书进行中：先在主窗口停止听书，再连接游戏对白。"; Tell(quickFailure); RefreshQuick(true); return; }
        if (QuickCancelable) { PauseTextPlayback("已取消连接普通对白。"); quickAwaitAlign = false; RefreshQuick(true); return; }
        quickFailure = null;
        quickAwaitAlign = true;
        ConnectOrdinaryDialogue(); // 与结束等待卡同一入口：BeginTextMonitoring(普通对白)，终点等待中保留等待票据
        if (!textArmed) { quickAwaitAlign = false; quickFailure = gameTextNotice.Text.Length > 0 ? gameTextNotice.Text : "请先打开游戏，或在设置中选择所用客户端的游戏文件夹。"; }
        else CheckQuickFullscreen();
        if (QuickFanOpen) HideQuick();
        RefreshQuick(true);
    }

    void CheckQuickFullscreen()
    {
        if (preferences.QuickFullscreenHintShown || testUi) return;
        // QUNS_RUNNING_D3D_FULL_SCREEN = 3：独占全屏时置顶窗口可能被盖住。只提示一次。
        if (SHQueryUserNotificationState(out int state) != 0 || state != 3) return;
        preferences.QuickFullscreenHintShown = true; Save();
        quickFullscreenHint = "检测到游戏为独占全屏：悬浮窗可能被游戏盖住。请把游戏改为「无边框窗口」或「窗口化」。（只提示这一次）";
        Tell(quickFullscreenHint);
    }

    // ---------------- 热键（RegisterHotKey） ----------------
    bool QuickHotkeyWanted()
    {
        if (closing || keyEditor != null || recordingAction != null) return false;
        var key = preferences.Keys.GetValueOrDefault("connect") ?? "";
        if (!Enum.TryParse<Key>(key, out var parsed) || parsed == Key.None) return false;
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (game != null && foreground == game.Handle) return true;
        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;
        return !testUi && Native.IsGameWindow(foreground, preferences.GameExecutablePath);
    }
    void SyncQuickHotkey()
    {
        string want = QuickHotkeyWanted() ? preferences.Keys["connect"] : "";
        if (want == quickHotkeyKey) return;
        UnregisterQuickHotkey();
        if (want.Length == 0) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Enum.TryParse<Key>(want, out var key)) return;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(handle, QuickHotkeyId, 0x4000 /* MOD_NOREPEAT */, (uint)vk)) { quickHotkeyKey = want; quickHotkeyError = ""; }
        else
        {
            string error = "连接普通对白热键 " + want + " 注册失败（可能被其他程序占用，错误 " + Marshal.GetLastWin32Error() + "），可在按键设置里改键。";
            if (error != quickHotkeyError) { quickHotkeyError = error; Log.Write("quick-hotkey", error); }
            quickHotkeyKey = "";
        }
    }
    void UnregisterQuickHotkey()
    {
        if (quickHotkeyKey.Length == 0) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, QuickHotkeyId);
        quickHotkeyKey = "";
    }
    IntPtr QuickHotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == QuickHotkeyId)
        {
            handled = true;
            if (quickHotkeyKey.Length > 0 && keyEditor == null && recordingAction == null) Dispatcher.BeginInvoke(() => QuickConnect("hotkey"));
        }
        return IntPtr.Zero;
    }
    /// <summary>HandleGlobal 用：「连接普通对白」只由 RegisterHotKey 触发，Raw Input / 钩子看到同一键时忽略，避免触发两次。</summary>
    /// 热键未能注册（被占用）而游戏在前台时，退回由 Raw Input 触发一次。
    bool HandleQuickConnectKey(Key key, ModifierKeys modifiers, IntPtr foreground)
    {
        if (modifiers != ModifierKeys.None || !KeyBindingWindow.Matches(preferences.Keys.GetValueOrDefault("connect") ?? "None", key)) return false;
        if (quickHotkeyKey.Length == 0 && game != null && foreground == game.Handle && keyEditor == null) QuickConnect("raw-fallback");
        return true;
    }

    // ---------------- 收起 A：台词条右侧的快捷按钮 ----------------
    void BuildQuickStrip()
    {
        if (StripView.Child is not Grid grid) return;
        var expand = grid.Children.OfType<Button>().FirstOrDefault(b => Grid.GetColumn(b) == 1 && Grid.GetRow(b) == 0);
        quickStripConnect = QuickUi.Connect("连接普通对白", "", QuickKeyLabel, 42, () => QuickConnect("strip"), out _, out _, out var keycap);
        quickStripKey = keycap; quickStripConnect.Width = 182; quickStripConnect.Margin = new Thickness(8, 0, 0, 0);
        quickStripRow.Children.Add(quickStripConnect);
        quickStripRow.Children.Add(StripMini("explore", "探索", "#7F6223", "打开探索 / 手动分支窗（F1 同一窗口）", () => QuickExplore(true), out _));
        quickStripPause = StripMini("pause", "暂停", "#4F6B8A", "暂停 / 继续配音跟随（" + KeyName("pause") + "）", () => RunCompactFollowAction("pause"), out var pauseText);
        quickStripPauseText = pauseText;
        quickStripRow.Children.Add(quickStripPause);
        quickStripRow.Children.Add(StripMini("down", "", "#5A646E", "展开快捷面板", ShowQuickPanel, out _, 26));
        if (expand != null)
        {
            grid.Children.Remove(expand); expand.Margin = new Thickness(6, 0, 0, 0); expand.VerticalAlignment = VerticalAlignment.Top;
            expand.ToolTip = "打开主窗口（" + KeyName("panel") + "）";
            quickStripRow.Children.Add(expand);
        }
        Grid.SetColumn(quickStripRow, 1); Grid.SetRow(quickStripRow, 0); grid.Children.Add(quickStripRow);
    }
    Button StripMini(string icon, string label, string color, string tip, Action click, out TextBlock text, double width = 52)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var glyph = QuickUi.Icon(icon, Hud.Solid(color), 15); glyph.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(glyph);
        text = new TextBlock { Text = label, FontSize = 11, Foreground = Hud.Solid(color), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
        stack.Children.Add(text);
        var frame = new CutFrame { BorderBrush = Theme.Brush("Stroke"), Cut = 6, Corners = CutCorners.BottomRight, Width = width, Height = 42, Child = stack };
        var button = QuickUi.Plate(frame, tip, click, label.Length > 0 ? label : tip);
        button.Margin = new Thickness(6, 0, 0, 0);
        QuickUi.Hover(button, frame, Brushes.White, Theme.Brush("Surface"), Theme.Brush("SlateFill"));
        return button;
    }

    // ---------------- 快捷窗（展开 C / 扇出 B） ----------------
    QuickWindow EnsureQuick()
    {
        if (quick != null) return quick;
        quick = new QuickWindow { GameHandle = () => game?.Handle ?? IntPtr.Zero };
        quick.SetPinned(preferences.QuickWindowPinned);
        quick.OpenMain = () => { HideQuick(); OpenStory(); };
        quick.Minimize = HideQuick;
        quick.PinChanged = () => { preferences.QuickWindowPinned = quick.Pinned; Save(); };
        quick.Moved = SaveQuickPosition;
        quick.MouseLeave += (_, _) => quickFanLeftAt = DateTime.UtcNow;
        quick.MouseEnter += (_, _) => quickFanLeftAt = DateTime.MinValue;
        return quick;
    }
    void ShowQuickPanel()
    {
        if (closing) return;
        var window = EnsureQuick();
        bool wasFan = QuickFanOpen;
        window.SetForm(QuickWindow.Shape.Panel);
        quickState = ""; RefreshQuick(true);
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        // 设计稿 17 / 19：球 / 台词条 ⇄ 展开面板是互相替换的两种形态（▾ 提示「收起回台词条 / 悬浮球」），
        // 面板打开时把球 / 台词条设成全透明（分层窗口全透明处点击穿透），不 Hide()，热键、原始输入和主窗计时照常；收起时恢复。
        if (!expanded) { ballHoverTimer.Stop(); Opacity = 0; }
        if (wasFan || quickHome == null) quickHome = QuickRestorePosition(window);
        window.MoveTo(quickHome.Value.X, quickHome.Value.Y);
        AvoidQuickObstacles();
    }
    void ShowQuickFan()
    {
        if (closing || expanded) return;
        var window = EnsureQuick();
        window.SetForm(QuickWindow.Shape.Fan);
        BuildQuickFan(window);
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        var ball = MainPixelRect();
        int gap = (int)Math.Round(8 * window.Scale);
        window.MoveTo(ball.Right + gap, ball.Top);
        quickFanLeftAt = DateTime.UtcNow.AddSeconds(2);
    }
    void HideQuick()
    {
        if (Opacity < 1) Opacity = 1;
        if (quick == null || !quick.IsVisible) return;
        quick.Hide();
        quickState = "";
    }
    QuickWindow.RECT MainPixelRect() { QuickWindow.GetWindowRect(new WindowInteropHelper(this).Handle, out var r); return r; }

    // ---------------- 位置记忆（按显示器，物理像素） ----------------
    static string QuickMonitorKey(int x, int y) => System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).DeviceName;
    (int X, int Y) QuickRestorePosition(QuickWindow window)
    {
        var main = MainPixelRect();
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((main.Left + main.Right) / 2, (main.Top + main.Bottom) / 2));
        var rect = window.PixelRect; int width = Math.Max(1, rect.Width), height = Math.Max(1, rect.Height);
        int x, y;
        if (preferences.QuickWindowPositions.TryGetValue(screen.DeviceName, out var saved) && saved is { Length: 2 }) { x = saved[0]; y = saved[1]; }
        else { x = main.Right + (int)Math.Round(8 * window.Scale); y = main.Top; }
        var area = screen.WorkingArea;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }
    void SaveQuickPosition()
    {
        if (quick == null || quick.Form != QuickWindow.Shape.Panel) return;
        var r = quick.PixelRect;
        quickHome = (r.Left, r.Top);
        preferences.QuickWindowPositions[QuickMonitorKey((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)] = new[] { r.Left, r.Top };
        Save();
    }

    // ---------------- 避让分支窗 / 点击区徽标（16 px） ----------------
    IEnumerable<QuickWindow.RECT> QuickObstacles()
    {
        foreach (Window w in new Window[] { branchMenu, clickZone })
        {
            if (!w.IsVisible) continue;
            var handle = new WindowInteropHelper(w).Handle;
            if (handle != IntPtr.Zero && QuickWindow.GetWindowRect(handle, out var r) && r.Width > 0) yield return r;
        }
    }
    static bool Overlaps(QuickWindow.RECT a, int x, int y, int w, int h, int margin) =>
        x < a.Right + margin && x + w > a.Left - margin && y < a.Bottom + margin && y + h > a.Top - margin;
    void AvoidQuickObstacles()
    {
        if (!QuickPanelOpen || quick == null || quick.Dragging || quickHome is not { } home) return;
        var rect = quick.PixelRect; int w = rect.Width, h = rect.Height, margin = (int)Math.Round(16 * quick.Scale);
        var obstacles = QuickObstacles().ToList();
        (int X, int Y) target = home;
        var blocking = obstacles.FirstOrDefault(o => Overlaps(o, home.X, home.Y, w, h, 0));
        if (blocking.Width > 0)
        {
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(home.X + w / 2, home.Y + h / 2)).WorkingArea;
            var candidates = new[] { (X: blocking.Right + margin, Y: home.Y), (X: blocking.Left - margin - w, Y: home.Y), (X: home.X, Y: blocking.Bottom + margin), (X: home.X, Y: blocking.Top - margin - h) }
                .Where(c => c.X >= area.Left && c.Y >= area.Top && c.X + w <= area.Right && c.Y + h <= area.Bottom && !obstacles.Any(o => Overlaps(o, c.X, c.Y, w, h, 0)))
                .OrderBy(c => Math.Abs(c.X - home.X) + Math.Abs(c.Y - home.Y)).ToList();
            if (candidates.Count > 0) target = candidates[0];
        }
        if (rect.Left != target.X || rect.Top != target.Y) quick.MoveTo(target.X, target.Y);
    }

    // ---------------- 状态 ----------------
    string QuickStateKey()
    {
        if (quickFailure != null && !textArmed) return "d";
        if (OrdinaryProbe.ExportConnection.Visibility == Visibility.Visible && QuickConnecting) return "d";
        // 终点等待优先于「正在连接」：等待中点连接保留等待票据和 18b 卡，主按钮仍为「连接普通对白」。
        if (GameEndWaiting != null) return "b";
        if (QuickConnecting) return "connecting";
        if (quickRealignedAt != DateTime.MinValue && DateTime.UtcNow - quickRealignedAt < TimeSpan.FromSeconds(3.4)) return "c";
        if (QuickExploreMenu() == null) quickExploreMenuId = null;
        else if (quickExploreView != false) return "a:" + QuickExploreSignature();
        if (branchMenu.IsVisible && branchMenu.IsNavigation && quickExploreView != false) return "a-nav";
        return textArmed ? "follow" : "idle";
    }
    void TickQuick()
    {
        if (closing) return;
        SyncQuickHotkey();
        if (quickAwaitAlign && textArmed && lastTextPlayed.Length > 0)
        {
            quickAwaitAlign = false; quickRealignedAt = DateTime.UtcNow;
            var node = engine?.Current;
            string text = node?.Text ?? ""; if (text.Length > 12) text = text[..12] + "……";
            quickRealignedText = "已对齐：" + (node?.Speaker is { Length: > 0 } s ? s : "当前句") + "「" + text + "」 · 继续跟随";
        }
        if (!textArmed && quickAwaitAlign && !QuickConnecting) quickAwaitAlign = false;
        // 对齐成功 3 s 后自动收起（3.4 s 含淡出）
        if (QuickPanelOpen && quickState == "c" && DateTime.UtcNow - quickRealignedAt >= TimeSpan.FromSeconds(3.4)) { quickRealignedAt = DateTime.MinValue; HideQuick(); }
        if (QuickFanOpen && quick != null && !quick.IsMouseOver && !BallView.IsMouseOver && quickFanLeftAt != DateTime.MinValue && DateTime.UtcNow - quickFanLeftAt > TimeSpan.FromMilliseconds(700)) HideQuick();
        if (expanded && quick?.IsVisible == true) HideQuick();
        RefreshQuick(false);
        AvoidQuickObstacles();
    }
    void RefreshQuick(bool force)
    {
        RefreshQuickStrip();
        if (quick == null || !quick.IsVisible || quick.Form != QuickWindow.Shape.Panel) return;
        string state = QuickStateKey();
        // 键帽随 Keys 实际绑定变化：改键后整块重建
        string keys = QuickKeyLabel + "|" + KeyName("pause") + "|" + KeyName("replay") + "|" + KeyName("manualNext") + "|" + KeyName("panel");
        if (force || state != quickState || keys != quickKeySignature) { quickState = state; quickKeySignature = keys; BuildQuickPanel(state.Split(':')[0]); }
        UpdateQuickTexts();
    }
    void RefreshQuickStrip()
    {
        if (quickStripKey?.Child is TextBlock key) { key.Text = QuickKeyLabel; quickStripKey.Visibility = QuickKeyLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        if (quickStripConnect != null) AutomationName(quickStripConnect, QuickCancelable ? "取消连接" : "连接普通对白");
        if (quickStripPauseText != null) quickStripPauseText.Text = QuickPaused ? "继续" : "暂停";
    }
    static void AutomationName(UIElement element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
    bool QuickPaused => ListeningActive ? !listeningRunning : engine?.Mode == RunMode.Paused && !automaticRunning;

    void BuildQuickPanel(string state)
    {
        if (quick == null) return;
        var body = quick.Body; body.Children.Clear();
        qStatus = qSection = qSpeaker = qLine = qLineSub = qConnectTitle = qConnectSub = null; qConnectKey = null; qPortrait = null; qPause = null; qSegment = null;
        if (quickFullscreenHint != null) { body.Children.Add(Spaced(QuickUi.Banner("wait", "warn", "FULLSCREEN · 独占全屏", "请把游戏改为无边框窗口 / 窗口化", quickFullscreenHint), 8)); }
        if (quickHotkeyError.Length > 0 && state is "idle" or "d") body.Children.Add(Spaced(new TextBlock { Text = quickHotkeyError, FontSize = 10.5, Foreground = Theme.Brush("SubtleText"), TextWrapping = TextWrapping.Wrap }, 6));
        switch (state)
        {
            case "d":
                body.Children.Add(Spaced(QuickUi.Banner("bad", "warn", "", "没读到游戏台词", "未连接：" + (quickFailure ?? OrdinaryProbe.Status.Text)), 8));
                body.Children.Add(Spaced(QuickConnectButton("重试", 46), 8));
                body.Children.Add(Spaced(QuickUi.Secondary("连接 3D 对白", "scene", "", () => { HideQuick(); Expand(gameTextTab); }, "3D 对白需要在主窗口填写当前正文（≥6 字）", "战斗 / 营地"), 6));
                body.Children.Add(Spaced(QuickUi.Secondary("打开主窗口手动选句", "open", KeyName("panel"), () => { HideQuick(); OpenStory(); }), 6));
                if (OrdinaryProbe.Diagnostics.Length > 0) body.Children.Add(Spaced(QuickUi.Link("导出连接诊断", "diag", "", ExportDiagnostics), 4));
                body.Children.Add(Note("画面上没有对白框（过场、战斗、菜单）时也会这样，等台词出现后再点。"));
                break;
            case "b":
                var wait = GameEndWaiting!;
                body.Children.Add(Spaced(QuickUi.Banner("wait", "scene", GameEndWaitPolicy.Eyebrow, wait.Hint, GameEndWaitPolicy.Detail), 8));
                body.Children.Add(Spaced(QuickCurrentCard(), 8));
                body.Children.Add(Spaced(QuickConnectButton("按游戏当前对白定位后续", 44), 6));
                body.Children.Add(Spaced(QuickUi.Secondary("停止配音跟随", "stop", "", () => PauseTextPlayback("已停止终点等待，文字跟随已暂停。")), 6));
                body.Children.Add(Spaced(QuickUi.Link("在主窗口手动选句", "open", KeyName("panel"), () => { HideQuick(); OpenStory(); }), 2));
                break;
            case "c":
                body.Children.Add(Spaced(QuickUi.Banner("ok", "check", "", quickRealignedText, "正文与启用状态已连接，全程只读。"), 8));
                body.Children.Add(Spaced(QuickCurrentCard(), 8));
                body.Children.Add(Spaced(SegmentView(false), 6));
                body.Children.Add(Spaced(ControlRow(), 6));
                body.Children.Add(Note("提示条 3 秒后淡出；快捷窗随后自动收起回台词条。"));
                break;
            case "a":
            {
                var menu = QuickExploreMenu()!;
                body.Children.Add(Spaced(StatusRow("◆ 探索中 · 自动跟随", "#7F6223"), 6));
                int unheard = QuickExploreUnheard(menu);
                var title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (unheard > 0)
                {
                    var chip = new Border { BorderBrush = Theme.Brush("Brass"), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "还有 " + unheard + " 段没听", FontSize = 10.5, Foreground = Theme.Brush("BrassText") } };
                    DockPanel.SetDock(chip, Dock.Right); title.Children.Add(chip);
                }
                title.Children.Add(new TextBlock { Text = QuickExploreTitle(menu), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Brush("Fg"), VerticalAlignment = VerticalAlignment.Center });
                body.Children.Add(title);
                // 与分支窗 14 卡同一份跳转记录（分支监听按游戏对话框接上时记下）与兜底标记
                if (DesktopExploration.LastJump(engine!, menu.Id) is { } jump)
                    body.Children.Add(Spaced(QuickUi.Banner("ok", "check", "", "已根据游戏画面跳转：" + jump.OptionLabel, "读到：" + jump.Quote), 6));
                if (DesktopExploration.FollowUnavailable(engine!, menu.Id))
                    body.Children.Add(Spaced(QuickUi.Banner("wait", "scene", "", "没能从游戏画面认出是谁", "请点选正在交谈的人物（双击确认）。"), 6));
                body.Children.Add(Spaced(QuickExploreList(menu), 4));
                body.Children.Add(Spaced(QuickConnectButton("离开营地、回到普通对白时点", 46), 6));
                body.Children.Add(Spaced(SegmentView(true), 6));
                body.Children.Add(Spaced(ControlRow(false), 2));
                body.Children.Add(Note("单击选中，双击确认（与分支窗相同）。"));
                break;
            }
            case "a-nav":
                body.Children.Add(Spaced(StatusRow("◆ 探索中 · 分支窗已打开", "#7F6223"), 6));
                body.Children.Add(Spaced(new TextBlock { Text = "在旁边的分支窗里选择要听的人物 / 话题；离开营地、回到普通对白时点下面的红色按钮。", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Fg") }, 8));
                body.Children.Add(Spaced(QuickConnectButton("离开营地、回到普通对白时点", 46), 6));
                body.Children.Add(Spaced(SegmentView(true), 6));
                body.Children.Add(Spaced(ControlRow(false), 2));
                break;
            default: // idle / follow / connecting
                body.Children.Add(Spaced(StatusRow("", "#A3283A"), 6));
                body.Children.Add(Spaced(QuickCurrentCard(), 8));
                body.Children.Add(Spaced(QuickConnectButton("读取游戏当前对白，接着跟随", 46), 6));
                body.Children.Add(Spaced(SegmentView(false), 6));
                body.Children.Add(Spaced(ControlRow(), 8));
                body.Children.Add(MoreRow());
                break;
        }
    }
    static FrameworkElement Spaced(FrameworkElement element, double bottom) { element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right, bottom); return element; }
    static TextBlock Note(string text) => new() { Text = text, FontSize = 10.5, Foreground = Theme.Brush("SubtleText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    FrameworkElement StatusRow(string fixedText, string color)
    {
        var grid = new Grid();
        qStatus = new TextBlock { Text = fixedText, FontSize = 11.5, Foreground = Hud.Solid(color), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) };
        qSection = new TextBlock { FontSize = 11, Foreground = Theme.Brush("SubtleText"), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        if (fixedText.Length > 0) qStatus.Tag = "fixed";
        grid.Children.Add(qStatus); grid.Children.Add(qSection);
        return grid;
    }
    FrameworkElement QuickCurrentCard()
    {
        qPortrait = new SpeakerPortrait { Width = 34, Height = 38, ShowSerial = false, VerticalAlignment = VerticalAlignment.Top };
        qSpeaker = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Fg") };
        qLine = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Brush("Fg"), Margin = new Thickness(0, 1, 0, 1) };
        qLineSub = new TextBlock { FontSize = 10.5, Foreground = Theme.Brush("SubtleText"), TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { qSpeaker, qLine, qLineSub } };
        var dock = new DockPanel(); DockPanel.SetDock(qPortrait, Dock.Left); dock.Children.Add(qPortrait); dock.Children.Add(texts);
        return new CutFrame { Background = Brushes.White, BorderBrush = Theme.Brush("Stroke"), EdgeWidth = 3, OutlineBrush = Theme.Brush("Stroke"), Cut = 8, Corners = CutCorners.BottomRight, Padding = new Thickness(9, 7, 9, 7), Child = dock };
    }
    Button QuickConnectButton(string sub, double height)
    {
        var button = QuickUi.Connect("连接普通对白", sub, QuickKeyLabel, height, () => QuickConnect("panel"), out var title, out var subText, out var keycap);
        qConnectTitle = title; qConnectSub = subText; qConnectKey = keycap; qConnectSub.Tag = sub;
        return button;
    }
    FrameworkElement SegmentView(bool explore)
    {
        qSegment = new QuickUi.Segment(QuickExplore);
        qSegment.Set(explore);
        return qSegment.View;
    }
    FrameworkElement ControlRow(bool withNext = true)
    {
        var grid = new Grid();
        int columns = withNext ? 3 : 2;
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        qPause = new QuickUi.PauseButton(KeyName("pause"), () => { RunCompactFollowAction("pause"); RefreshQuick(false); });
        qPause.Button.Margin = new Thickness(0, 0, 3, 0);
        var replay = QuickUi.Secondary("重播", "replay", KeyName("replay"), () => RunCompactFollowAction("replay"), "重播当前句");
        replay.Margin = new Thickness(3, 0, withNext ? 3 : 0, 0); Grid.SetColumn(replay, 1);
        grid.Children.Add(qPause.Button); grid.Children.Add(replay);
        if (withNext)
        {
            var next = QuickUi.Secondary("下一句", "next", KeyName("manualNext"), () => RunCompactFollowAction("manualNext"), "配音下一句（只校正配音，不点击游戏）");
            next.Margin = new Thickness(3, 0, 0, 0); Grid.SetColumn(next, 2); grid.Children.Add(next);
        }
        return grid;
    }
    FrameworkElement MoreRow()
    {
        var grid = new Grid();
        grid.Children.Add(QuickUi.Link("章节、分支、设置等在主窗口", "open", KeyName("panel"), () => { HideQuick(); OpenStory(); }));
        var gridIcon = QuickUi.Icon("grid", Theme.Brush("SubtleText"), 13); gridIcon.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(gridIcon);
        return grid;
    }
    void QuickExplore(bool explore)
    {
        quickExploreView = explore;
        if (explore) { if (QuickExploreMenu() == null && !(branchMenu.IsVisible && branchMenu.IsNavigation)) OpenInteractions(); }
        else if (branchMenu.IsVisible && branchMenu.IsNavigation) DismissSmallMenu();
        if (game != null && Native.IsWindow(game.Handle) && !expanded) Native.SetForegroundWindow(game.Handle);
        RefreshQuick(true);
    }
    void UpdateQuickTexts()
    {
        if (qStatus != null && qStatus.Tag as string != "fixed")
            qStatus.Text = QuickConnecting ? "◆ 正在寻找对白框…" : textArmed ? "◆ 文字跟随中" + (engine?.Mode == RunMode.Paused ? " · 已暂停" : "") : "◆ 等待连接游戏对白";
        if (qSection != null)
            qSection.Text = engine != null && SectionBox.SelectedItem is Section section ? SectionDisplayTitle(section.Id, engine.Pack) + " · 文字跟随" : "未选择小节";
        if (qSpeaker != null) qSpeaker.Text = StripSpeaker.Text;
        if (qLine != null) qLine.Text = StripText.Text;
        if (qLineSub != null)
            qLineSub.Text = !textArmed ? "游戏画面上的当前句（尚未接上配音）" : GameEndWaiting != null ? "本小节台词已播完 · 不点击游戏" : "◆ 配音跟随中";
        if (qPortrait != null) { qPortrait.Speaker = StripPortrait.Speaker; qPortrait.Source = StripPortrait.Source; }
        if (qConnectTitle != null) qConnectTitle.Text = QuickCancelable ? "取消连接" : "连接普通对白";
        if (qConnectSub != null) qConnectSub.Text = QuickCancelable ? "正在寻找对白框；再点一次取消" : qConnectSub.Tag as string ?? "";
        if (qConnectKey?.Child is TextBlock key) { key.Text = QuickKeyLabel; qConnectKey.Visibility = QuickKeyLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        qPause?.Set(QuickPaused, KeyName("pause"));
        qSegment?.Set(quickState.StartsWith("a", StringComparison.Ordinal));
    }

    void BuildQuickFan(QuickWindow window)
    {
        var body = window.FanBody; body.Children.Clear();
        var connect = QuickUi.Connect("连接普通对白", "", QuickKeyLabel, 38, () => QuickConnect("fan"), out _, out _, out _);
        connect.Width = 186; connect.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(Spaced(connect, 6));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var explore = QuickUi.Secondary("探索面板", "explore", "", () => { HideQuick(); QuickExplore(true); }); explore.Width = 92;
        var pause = new QuickUi.PauseButton(KeyName("pause"), () => { HideQuick(); RunCompactFollowAction("pause"); });
        pause.Set(QuickPaused, KeyName("pause")); pause.Button.Width = 88; pause.Button.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(explore); row.Children.Add(pause.Button);
        body.Children.Add(Spaced(row, 6));
        body.Children.Add(new Border { Background = Hud.Solid("#E62A3139"), Padding = new Thickness(7, 3, 7, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = "单击球：查看台词（原有）· 拖动移动 · " + KeyName("panel") + " 展开主窗口", FontSize = 10.5, Foreground = Brushes.White } });
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
