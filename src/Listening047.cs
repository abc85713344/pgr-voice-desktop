using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 听书停下处（与安卓 release/0.4.7 android/ListeningSession.cs 同规则、同文字）：
/// 待选支线 / 结局分歧、等待确认续接、无文本死角——「分支自动选择」开启时 15 秒倒计时后自动处理，用户点击优先；关闭后停下等待。
/// 无音频台词显示字幕并按字数停留后继续（待配音 / 无配音签）；单选项与探索菜单由核心计划自动越过；全部写入播放记录。
/// </summary>
public partial class MainWindow
{
    const string ListeningLogMode = "听书";
    ListeningStop? listeningPendingStop; ListeningItem? listeningStopItem;
    int listeningCountdown; long listeningCountdownEpoch; ListeningItem? listeningCountdownItem;
    DispatcherTimer? listeningCountdownTimer, listeningDwellTimer, listeningNoticeTimer;
    string? listeningAutoNotice; string listeningAutoNoticeEnglish = "AUTO · ROUTE"; DateTime listeningAutoNoticeAt;
    readonly HashSet<string> listeningLoggedChoicePoints = new(StringComparer.Ordinal);
    readonly List<TextBlock> listeningCountdownNumbers = new();
    readonly List<ScaleTransform> listeningCountdownRails = new();
    readonly List<CountdownRing> listeningCountdownRings = new();
    /// <summary>「暂停 / 继续」行固定在台词滚动区之外（主控件正上方），任何窗口尺寸下不滚动即可见。</summary>
    readonly StackPanel listeningPauseDock = new();
    /// <summary>设计稿 16：倒计时已暂停（保留剩余秒数，不自动处理）。</summary>
    bool listeningCountdownPaused;
    readonly WrapPanel listeningTags = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    readonly CutFrame listeningNoticeStrip = new() { Visibility = Visibility.Collapsed, Cut = 6, Corners = CutCorners.BottomRight, EdgeWidth = 3, Padding = new Thickness(9, 4, 9, 5), Margin = new Thickness(0, 5, 0, 0) };
    readonly TextBlock listeningNoticeEnglish = new() { FontSize = 9.5, FontWeight = FontWeights.SemiBold };
    readonly TextBlock listeningNoticeText = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    // 设计稿 02 / 07 / 09（桌面）：「已自动选择」提示——左侧分支标、副标题「倒计时结束 · 默认分支…」，右侧 ✓ 环「DONE · 已选择」。
    readonly TextBlock listeningNoticeSub = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly TextBlock listeningNoticeDoneEnglish = new() { FontSize = 9, FontWeight = FontWeights.SemiBold };
    readonly TextBlock listeningNoticeDoneText = new() { FontSize = 10.5 };
    readonly CutFrame listeningNoticeDone = new() { Visibility = Visibility.Collapsed, Cut = 5, Corners = CutCorners.Diagonal, Padding = new Thickness(5, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    string? listeningAutoNoticeSub, listeningAutoNoticeDone;
    /// <summary>测试：无音频停留按此比例缩短（1 = 实际时长）。</summary>
    double listeningDwellScale = 1;
    int listeningPendingVoiceItems, listeningSubtitleOnlyItems;

    ListeningStop? ListeningPendingStop => listeningSession != null && listeningStopItem != null && ReferenceEquals(listeningSession.Current, listeningStopItem) ? listeningPendingStop : null;
    int ListeningDefaultNumber => preferences.DefaultBranchOption == 2 ? 2 : 1;
    double ListeningCountdownFraction => listeningCountdown <= 0 ? 0 : listeningCountdown / (double)ListeningText.CountdownSeconds;
    string? ListeningCountdownOptionId => listeningCountdown > 0 && ListeningPendingStop is { Action: ListeningStopAction.Choose } stop ? stop.OptionId : null;
    bool ListeningCounting => listeningCountdown > 0 && ListeningPendingStop != null;

    void InitializeListening047()
    {
        try { Directory.CreateDirectory(Log.DataDir); PlaybackLog.Configure(System.IO.Path.Combine(Log.DataDir, "playback-log.csv")); }
        catch (Exception ex) { Log.Write("playback-log", ex.Message); }
        listeningNoticeStrip.Background = Theme.Brush("BranchFill"); listeningNoticeStrip.BorderBrush = Theme.Brush("Brass");
        listeningNoticeEnglish.Foreground = Theme.Brush("BrassText"); listeningNoticeText.Foreground = Theme.Brush("Fg");
        listeningNoticeSub.Foreground = Theme.Brush("MutedText");
        listeningNoticeDoneEnglish.Foreground = Theme.Brush("BrassText"); listeningNoticeDoneText.Foreground = Theme.Brush("Fg");
        listeningNoticeDone.Background = Brushes.White; listeningNoticeDone.BorderBrush = Theme.Brush("Brass");
        var check = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        check.Children.Add(new Ellipse { Stroke = Theme.Brush("Stroke"), StrokeThickness = 1.4 });
        check.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M8,13.5 L11.5,17 L18.5,9.5"), Stroke = Theme.Brush("Brass"), StrokeThickness = 1.8, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round });
        var doneWords = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { listeningNoticeDoneEnglish, listeningNoticeDoneText } };
        listeningNoticeDone.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, doneWords } };
        var fork = new System.Windows.Shapes.Path { Data = ActionIcons.Fork, Stroke = Theme.Brush("BrassText"), StrokeThickness = 1.4, Width = 14, Height = 14, Stretch = Stretch.None, Margin = new Thickness(0, 3, 9, 0), VerticalAlignment = VerticalAlignment.Top };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { listeningNoticeEnglish, listeningNoticeText, listeningNoticeSub } };
        var strip = new DockPanel();
        DockPanel.SetDock(listeningNoticeDone, Dock.Right); strip.Children.Add(listeningNoticeDone);
        DockPanel.SetDock(fork, Dock.Left); strip.Children.Add(fork);
        strip.Children.Add(words);
        // 设计稿 02：底部「AUTO-HIDE · 3 秒后收起」细进度条，随剩余时间缩短，3 秒后提示条收起。
        var hide = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        hide.Children.Add(Docked047(new TextBlock { Text = "AUTO-HIDE · 3 秒后收起", FontFamily = Theme.Font("LabelFont"), FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BrassText"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Right));
        var hideTrack = new Grid { Height = 2, VerticalAlignment = VerticalAlignment.Center };
        hideTrack.Children.Add(new Rectangle { Fill = Theme.Brush("Stroke") });
        hideTrack.Children.Add(new Rectangle { Fill = Theme.Brush("Brass"), RenderTransform = listeningNoticeHideScale, RenderTransformOrigin = new Point(0, .5) });
        hide.Children.Add(hideTrack);
        listeningNoticeStrip.Child = new StackPanel { Children = { strip, hide } };
        listeningNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        listeningNoticeTimer.Tick += (_, _) =>
        {
            if (ListeningAutoNotice == null) { listeningNoticeTimer!.Stop(); RefreshListeningNotice(); return; }
            listeningNoticeHideScale.ScaleX = ListeningNoticeRemaining;
        };
        BuildBranchSettings();
    }

    // ---------------- 播放记录 ----------------
    void ListeningLog(string type, ListeningItem? item, string reason, string option = "")
    {
        if (listeningSession == null) return;
        PlaybackLog.Write(type, ListeningLogMode, listeningSession.Pack, listeningSession.Chapter.Title, item?.SectionTitle ?? "", item?.NodeId ?? "", reason, option);
    }
    /// <summary>计划中已经越过的自动选择点（单选项 / 探索菜单 / 默认分支）各记一条，选择点按键 + 所选项去重。</summary>
    void LogListeningPassedChoicePoints()
    {
        if (listeningSession == null) return;
        int position = listeningSession.Position;
        foreach (var point in listeningSession.ChoicePoints)
        {
            if (point.AutoKind == null || point.Position > position || point.SelectedOptionId == null) continue;
            if (!listeningLoggedChoicePoints.Add(point.ChoiceKey + "\u001f" + point.SelectedOptionId)) continue;
            string type = point.AutoKind;
            ListeningLog(type, point.Item, type == PlaybackLog.Types.AutoSelect ? "默认分支" + ListeningText.OptionOrdinal(ListeningDefaultNumber) : type == PlaybackLog.Types.SingleOption ? "仅一个选项" : "探索菜单逐项读完后离开", point.AutoLabel ?? "");
            if (point.EmptyExitLabel != null) ListeningLog(PlaybackLog.Types.Exploration, point.Item, ListeningText.EmptyExitContinue, point.EmptyExitLabel);
        }
    }

    // ---------------- 停下处与倒计时 ----------------
    /// <summary>同一停下处按新设置重新解析（改默认分支 / 自动选择开关 / 保留暂停）时不再多记一条「停下」（复审应该改 5）。</summary>
    bool listeningRestartingStop;
    void RestartListeningStop((string Id, int Seconds)? held = null)
    {
        held ??= HoldListeningPause();
        CancelListeningCountdown();
        listeningRestartingStop = true;
        try { BeginListeningStop(); } finally { listeningRestartingStop = false; }
        RestoreListeningPause(held);
    }
    /// <summary>PauseListening 取消了倒计时后，在同一停下处恢复「已暂停 + 剩余秒数」。</summary>
    void ReinstateListeningPause((string Id, int Seconds)? held)
    {
        if (held is not { } h || listeningSession?.Current?.Id != h.Id) return;
        listeningRestartingStop = true;
        try { BeginListeningStop(); } finally { listeningRestartingStop = false; }
        RestoreListeningPause(held);
    }
    void CancelListeningCountdown() { listeningCountdownEpoch++; listeningCountdownPaused = false; listeningCountdownItem = null; listeningCountdown = 0; listeningCountdownTimer?.Stop(); }

    /// <summary>停在待选支线 / 未核续接 / 无文本死角：记录停下；「分支自动选择」开启时 15 秒倒计时后自动处理，关闭则等待。</summary>
    void BeginListeningStop()
    {
        if (listeningSession?.Current is not { } item) { RefreshListening(); return; }
        var stop = listeningSession.ResolveStop(ListeningDefaultNumber);
        bool sameStop = ReferenceEquals(listeningCountdownItem, item) && listeningCountdown > 0;
        listeningPendingStop = stop; listeningStopItem = stop == null ? null : item;
        if (stop == null) { CancelListeningCountdown(); RefreshListening(); return; }
        // 最后一个小节已读完（出口未核实）：正常结束本章，不倒计时、不提示反馈。
        if (stop is { Action: ListeningStopAction.ChapterEnd, SectionComplete: true }) { ApplyListeningStop(true); return; }
        if (!sameStop && !listeningRestartingStop) ListeningLog(PlaybackLog.Types.Stop, item, ListeningStopReason(stop), stop.OptionLabel ?? "");
        if (!preferences.BranchAutoSelect)
        { CancelListeningCountdown(); listeningMessage = ListeningStopText(stop, 0); RefreshListening(); return; }
        if (sameStop) { listeningMessage = ListeningStopText(stop, listeningCountdown); RefreshListening(); return; }
        long token = ++listeningCountdownEpoch; listeningCountdownItem = item; listeningCountdown = ListeningText.CountdownSeconds;
        listeningMessage = ListeningStopText(stop, listeningCountdown);
        listeningCountdownTimer?.Stop();
        listeningCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(listeningCountdownTickMs) };
        var timer = listeningCountdownTimer;
        timer.Tick += (_, _) =>
        {
            if (token != listeningCountdownEpoch || listeningSession == null || !ReferenceEquals(listeningSession.Current, item)) { timer.Stop(); return; }
            if (listeningCountdownPaused) return; // 设计稿 16：暂停期间停住秒数，不自动处理
            listeningCountdown--;
            if (listeningCountdown <= 0) { timer.Stop(); ApplyListeningStop(true); return; }
            listeningMessage = ListeningStopText(stop, listeningCountdown);
            UpdateListeningCountdown(); RefreshListeningStatus();
        };
        timer.Start();
        RefreshListening();
    }
    /// <summary>测试可缩短倒计时的每秒间隔；实际为 1000 毫秒。</summary>
    int listeningCountdownTickMs = 1000;

    static string ListeningStopReason(ListeningStop stop) => stop.Action switch
    {
        ListeningStopAction.Choose => "支线待选",
        ListeningStopAction.OrderContinue => "分支续接未核实：" + stop.Reason,
        _ when stop.SectionComplete => "小节已读完，出口未核实：" + stop.Reason,
        ListeningStopAction.NextSection => "后续没有配音文本：" + stop.Reason,
        _ => "后续没有配音文本（最后一个小节）：" + stop.Reason,
    };

    string ListeningStopText(ListeningStop stop, int seconds)
    {
        if (seconds > 0 && listeningCountdownPaused)
            return stop.Action == ListeningStopAction.Choose ? ListeningPausedHint.TrimEnd('。') + "，或直接点选项。" : ListeningPausedHint;
        string wait = seconds > 0 ? $"，{seconds} 秒后" : "";
        // 互动菜单（等待互动）保留原有措辞：请选择互动选项，确认后继续本节。
        bool interaction = listeningSession?.Current is { Kind: ListeningItemKind.Choice } cur && cur.Notice.StartsWith("等待互动", StringComparison.Ordinal);
        if (interaction && stop.Action == ListeningStopAction.Choose)
            return seconds > 0 ? $"等待互动，{seconds} 秒后自动选择：{stop.OptionLabel}（分支自动选择；可直接点选项，确认后继续本节）"
                : "等待互动：请选择下面的互动选项，确认后继续本节。";
        return stop.Action switch
        {
            ListeningStopAction.Choose => seconds > 0
                ? $"遇到支线，{seconds} 秒后自动选择：{stop.OptionLabel}" + ((stop.OptionLabel ?? "").Contains("开启结局") ? "" : "（分支自动选择；可直接点选项）")
                : "遇到支线，请手动选择后继续收听（分支自动选择已关闭）。",
            ListeningStopAction.OrderContinue => seconds > 0
                ? $"此处分支续接未核实{wait}按顺序继续（可点「按顺序继续」或暂停）。"
                : "此处分支续接未核实。可点「按顺序继续」从本节下一句接着听（分支自动选择已关闭）。",
            ListeningStopAction.NextSection when stop.SectionComplete => seconds > 0
                ? $"本小节已读完（出口未核实）{wait}进入下一小节（可点「进入下一小节」或暂停）。"
                : "本小节已读完（出口未核实）。可点「进入下一小节」继续（分支自动选择已关闭）。",
            ListeningStopAction.ChapterEnd when stop.SectionComplete => "本章已听完。",
            ListeningStopAction.NextSection => ListeningText.DeadEndHint + (seconds > 0 ? $"。{seconds} 秒后跳到下一小节。" : "。可点「跳到下一小节」继续（分支自动选择已关闭）。"),
            _ => ListeningText.DeadEndChapterEnd + (seconds > 0 ? $"。{seconds} 秒后结束本章。" : "。可点「结束本章」（分支自动选择已关闭）。"),
        };
    }

    static string ListeningStopAction047(ListeningStop stop) => stop.Action switch
    {
        ListeningStopAction.OrderContinue => "按顺序继续",
        ListeningStopAction.NextSection when stop.SectionComplete => "进入下一小节",
        ListeningStopAction.NextSection => "跳到下一小节",
        _ => "结束本章",
    };

    /// <summary>执行停下处的处理（倒计时到点，或用户点「按顺序继续」/「跳到下一小节」/「结束本章」/ 下一句）。</summary>
    void ApplyListeningStop(bool automatic)
    {
        if (listeningSession == null || ListeningPendingStop is not { } stop) return;
        var session = listeningSession; var item = session.Current;
        CancelListeningCountdown(); listeningPendingStop = null; listeningStopItem = null;
        if (!PauseListening()) { listeningMessage = "当前位置暂未保存，未自动继续。"; RefreshListeningStatus(); return; }
        if (!ReferenceEquals(session, listeningSession) || !session.ApplyStop(stop)) { listeningMessage = "当前位置已变化，请重新核对。"; RefreshListening(); return; }
        listeningOffset = 0; MarkExplicitListeningPosition(); listeningPreserveResume = false; listeningBrowsingLocation = false; SaveListeningProgress();
        string how = automatic ? "倒计时结束" : "用户确认";
        string shown;
        switch (stop.Action)
        {
            case ListeningStopAction.Choose:
                ListeningLog(PlaybackLog.Types.AutoSelect, item, how + "·默认分支" + ListeningText.OptionOrdinal(ListeningDefaultNumber), stop.OptionLabel ?? "");
                shown = ListeningText.AutoSelected + stop.OptionLabel; ShowListeningAutoNotice(shown, automatic ? "AUTO · SELECTED" : "ROUTE · SELECTED",
                    how + " · " + (ListeningAtEndingItem(item) ? "选入结局 · " : "") + "默认分支" + ListeningText.OptionOrdinal(ListeningDefaultNumber), automatic ? "已按默认继续" : "已按所选继续"); break;
            case ListeningStopAction.OrderContinue:
                ListeningLog(PlaybackLog.Types.OrderContinue, item, how + "·" + stop.Reason, stop.LineId ?? "");
                shown = ListeningText.OrderContinued; ShowListeningAutoNotice(shown, "ROUTE · IN ORDER", how + " · 从本节下一句接着听", "已按顺序继续"); break;
            case ListeningStopAction.NextSection when stop.SectionComplete:
                ListeningLog(PlaybackLog.Types.OrderContinue, item, how + "·小节已读完·" + stop.Reason, session.Current?.SectionTitle ?? "");
                shown = "已进入下一小节：" + (session.Current?.SectionTitle ?? ""); ShowListeningAutoNotice(shown, "SECTION · NEXT", how + " · 本小节已读完", "已进入下一小节"); break;
            case ListeningStopAction.ChapterEnd when stop.SectionComplete:
                listeningMessage = "本章已听完。"; RefreshListening(); return;
            case ListeningStopAction.NextSection:
                ListeningLog(PlaybackLog.Types.DeadEndJump, item, how + "·" + stop.Reason, session.Current?.SectionTitle ?? "");
                shown = "已跳到下一小节：" + (session.Current?.SectionTitle ?? ""); ShowListeningAutoNotice(shown, "SECTION · JUMP", how + " · 后续没有配音文本", "已跳到下一小节"); break;
            default:
                ListeningLog(PlaybackLog.Types.DeadEndChapterEnd, item, how + "·" + stop.Reason);
                listeningMessage = ListeningText.DeadEndChapterEnd + "。本章已听完。"; RefreshListening(); return;
        }
        listeningMessage = shown;
        StartListening();
        if (listeningRunning) { listeningMessage = shown + " · " + listeningMessage; RefreshListeningStatus(); }
    }

    /// <summary>「反馈」：写一条玩家反馈记录（配音包、章节 + 小节、节点、时间）并提示；不取消倒计时。</summary>
    void ListeningStopFeedback()
    {
        if (listeningSession?.Current is not { } item) return;
        ListeningLog(PlaybackLog.Types.PlayerFeedback, item, "后续没有配音文本，游戏里可能还有内容：" + item.Notice);
        ShowListeningAutoNotice("已记录反馈 · " + item.SectionTitle, "FEEDBACK · LOGGED");
        listeningMessage = "已记录反馈（" + item.SectionTitle + "）。可在「设置 · 分支与播放记录」导出播放记录发给作者。"
            + (listeningCountdown > 0 && ListeningPendingStop is { } stop ? " " + ListeningStopText(stop, listeningCountdown) : "");
        RefreshListeningStatus();
    }
    bool CanListeningFeedback => ListeningPendingStop is { } stop && (stop.DeadEnd || stop.Action == ListeningStopAction.OrderContinue);

    /// <summary>「下一句」在等待确认续接处保持可点：未核出口 → 立刻按顺序续接；死角 / 小节已读完 → 立刻跳下一小节或结束本章；普通句仍 MoveNext（正在收听时继续播放）。</summary>
    void ListeningNextClick()
    {
        if (listeningSession == null) return;
        if (listeningSession.HasBlockingNotice)
        {
            if (ListeningPendingStop == null) { listeningPendingStop = listeningSession.ResolveStop(ListeningDefaultNumber); listeningStopItem = listeningPendingStop == null ? null : listeningSession.Current; }
            if (ListeningPendingStop is { Action: ListeningStopAction.OrderContinue or ListeningStopAction.NextSection or ListeningStopAction.ChapterEnd })
            { ApplyListeningStop(false); return; }
            listeningMessage = ListeningWaitText; RefreshListeningStatus(); return;
        }
        bool wasPlaying = listeningRunning;
        MoveListening(() => listeningSession!.MoveNext());
        if (wasPlaying && !listeningRunning && listeningSession?.Current?.Kind == ListeningItemKind.Line) StartListening();
    }

    // ---------------- 无音频停留 ----------------
    /// <summary>当前句没有可用音频时的签：「待配音」/「无配音」；有音频、不需要停留或仅字幕时为 null。</summary>
    string? ListeningVoiceTag(Node? node)
    {
        if (listeningSession == null || node == null || !PendingVoice.IsFlagged(node)) return null;
        string? file = null; try { file = listeningSession.Pack.ResolveAudio(node); } catch { }
        if (file != null && File.Exists(file)) return null;
        return PendingVoice.VoiceTag(node);
    }
    /// <summary>无音频台词：不跳过、不暂停——显示字幕并按字数停留（每字 0.25 秒，至少 2 秒）后进入下一句。</summary>
    void StartListeningDwell(long request, ListeningItem item)
    {
        var node = item.Node!;
        ListeningLog(PendingVoice.PassType(node), item, node.AudioStatus ?? "无音频"); // 待配音越过 / 无配音越过 / 仅字幕越过
        if (PendingVoice.IsSubtitleOnly(node)) listeningSubtitleOnlyItems++; else listeningPendingVoiceItems++;
        listeningTicket = 0; listeningOffset = 0; listeningStarting = false;
        listeningMessage = PendingVoice.Tag(node) + "正在收听" + (item.DraftContinuation ? ListeningText.DraftStatusPart : "") + (item.BranchLabel.Length > 0 ? " · " + item.BranchLabel : "");
        SaveListeningProgress(); RefreshListening();
        int dwell = Math.Max(1, (int)(PendingVoice.DwellMilliseconds(node.Text) * listeningDwellScale));
        listeningDwellTimer?.Stop();
        var timer = listeningDwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dwell) };
        var session = listeningSession;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!listeningRunning || request != listeningRequest || !ReferenceEquals(session, listeningSession) || !ReferenceEquals(listeningSession?.Current?.Node, node)) return;
            listeningSession!.MoveNext(); listeningOffset = 0; SaveListeningProgress(); PlayListeningCurrent(request);
        };
        timer.Start();
    }

    // ---------------- 17-A 提示条（设计稿 02：AUTO-HIDE · 3 秒后收起） ----------------
    const double ListeningNoticeSeconds = 3;
    readonly ScaleTransform listeningNoticeHideScale = new(1, 1);
    double ListeningNoticeRemaining => Math.Clamp(1 - (DateTime.UtcNow - listeningAutoNoticeAt).TotalSeconds / ListeningNoticeSeconds, 0, 1);
    static Color C047(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static UIElement Docked047(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }
    string? ListeningAutoNotice => listeningAutoNotice != null && DateTime.UtcNow - listeningAutoNoticeAt < TimeSpan.FromSeconds(ListeningNoticeSeconds) ? listeningAutoNotice : null;
    void ShowListeningAutoNotice(string text, string english, string? sub = null, string? done = null)
    {
        listeningAutoNotice = text; listeningAutoNoticeEnglish = english; listeningAutoNoticeAt = DateTime.UtcNow;
        listeningAutoNoticeSub = sub; listeningAutoNoticeDone = done;
        Log.Write("listening-auto", text);
        listeningNoticeTimer?.Start(); RefreshListeningNotice();
    }
    void RefreshListeningNotice()
    {
        string? notice = ListeningAutoNotice;
        listeningNoticeStrip.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;
        listeningNoticeEnglish.Text = listeningAutoNoticeEnglish; listeningNoticeText.Text = notice ?? "";
        listeningNoticeSub.Text = listeningAutoNoticeSub ?? ""; listeningNoticeSub.Visibility = string.IsNullOrEmpty(listeningAutoNoticeSub) ? Visibility.Collapsed : Visibility.Visible;
        listeningNoticeDoneEnglish.Text = "DONE · 已选择"; listeningNoticeDoneText.Text = listeningAutoNoticeDone ?? "";
        listeningNoticeDone.Visibility = string.IsNullOrEmpty(listeningAutoNoticeDone) ? Visibility.Collapsed : Visibility.Visible;
        listeningNoticeHideScale.ScaleX = ListeningNoticeRemaining;
        UpdateListeningViewport();
    }

    // ---------------- 视觉（设计稿 01 / 03 / 04 / 11 / 16：黄铜纸面卡、倒计时环、细轨与「暂停 / 继续」） ----------------
    const string ListeningPauseLogType = "倒计时暂停", ListeningResumeLogType = "倒计时继续";
    const string ListeningPauseHint = "点「暂停」停住倒计时，不自动续接；再点「继续」恢复。";
    const string ListeningPausedHint = "已暂停：倒计时停住，不自动续接；点「继续」恢复。";
    static readonly Brush CountdownPausedBrush = Hud.Solid("#9AA3AD");

    /// <summary>倒计时环（SEC · AUTO）：环 + 两位秒数 + 英文眉标 / 中文说明；暂停后环与字转灰，标「PAUSED · 已暂停 / 倒计时已停住」。</summary>
    FrameworkElement CountdownBadge(int seconds, string caption = "秒后自动选择")
    {
        bool paused = listeningCountdownPaused;
        var ring = new CountdownRing { Width = 34, Height = 34, Fraction = ListeningCountdownFraction, Paused = paused, VerticalAlignment = VerticalAlignment.Center };
        listeningCountdownRings.Add(ring);
        var number = new TextBlock { Text = Math.Max(0, seconds).ToString("00"), FontFamily = Theme.Font("NumberFont"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = paused ? Hud.Solid("#7A8591") : Theme.Brush("BrassText"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        listeningCountdownNumbers.Add(number);
        var dial = new Grid { Width = 34, Height = 34, Children = { ring, number } };
        var words = new StackPanel { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = paused ? "PAUSED · 已暂停" : "SEC · AUTO", FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = paused ? Theme.Brush("Slate") : Theme.Brush("BrassText") });
        words.Children.Add(new TextBlock { Text = paused ? "倒计时已停住" : caption, FontSize = 11, Foreground = paused ? Hud.Solid("#4B5560") : Theme.Brush("Fg") });
        return new CutFrame
        {
            Background = paused ? Hud.Solid("#ECEFF2") : Brushes.White, BorderBrush = paused ? Hud.Solid("#AEB6BF") : Theme.Brush("Brass"), Cut = 6, Corners = CutCorners.Diagonal, Padding = new Thickness(5, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Top, ToolTip = paused ? ListeningPausedHint : "倒计时（秒）",
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dial, words } },
        };
    }
    FrameworkElement CountdownRail(double fraction, string caption)
    {
        bool paused = listeningCountdownPaused;
        var row = new DockPanel { Margin = new Thickness(2, 2, 2, 6) };
        var label = new TextBlock { Text = paused ? "PAUSED · 已暂停" : caption, FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = paused ? Theme.Brush("Slate") : Theme.Brush("BrassText"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Right); row.Children.Add(label);
        var scale = new ScaleTransform(Math.Clamp(fraction, 0, 1), 1); listeningCountdownRails.Add(scale);
        var rail = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        rail.Children.Add(new Rectangle { Fill = Theme.Brush("Stroke"), Height = 1, VerticalAlignment = VerticalAlignment.Center });
        // 设计稿 rail47：底轨 3 段刻度（0 / ⅓ / ⅔ / 1 处 1×6 竖刻）。
        var ticks = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, IsHitTestVisible = false };
        for (int i = 0; i < 3; i++) ticks.Children.Add(new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1, 0, i == 2 ? 1 : 0, 0), Height = 6, VerticalAlignment = VerticalAlignment.Center });
        rail.Children.Add(ticks);
        rail.Children.Add(new Rectangle { Fill = paused ? CountdownPausedBrush : Theme.Brush("Brass"), Height = 2, VerticalAlignment = VerticalAlignment.Center, RenderTransform = scale, RenderTransformOrigin = new Point(0, 0.5) });
        // 进度头的小菱形：随细轨推进（与设计稿 01 相同）。
        var head = new Rectangle { Width = 7, Height = 7, Fill = paused ? CountdownPausedBrush : Theme.Brush("Brass"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45) };
        var track = new Grid { Children = { rail, head } };
        track.SizeChanged += (_, _) => PlaceRailHead(track, head, scale);
        scale.Changed += (_, _) => PlaceRailHead(track, head, scale);
        row.Children.Add(track);
        return row;
    }
    static void PlaceRailHead(Grid track, FrameworkElement head, ScaleTransform scale) => head.Margin = new Thickness(Math.Max(0, track.ActualWidth * scale.ScaleX - 3.5), 0, 0, 0);
    void UpdateListeningCountdown()
    {
        foreach (var number in listeningCountdownNumbers) number.Text = Math.Max(0, listeningCountdown).ToString("00");
        foreach (var rail in listeningCountdownRails) rail.ScaleX = ListeningCountdownFraction;
        foreach (var ring in listeningCountdownRings) ring.Fraction = ListeningCountdownFraction;
    }

    /// <summary>「暂停 / 继续」（设计稿 16）：冻结倒计时（保留剩余秒数），暂停期间不自动选择 / 续接 / 跳小节；用户仍可直接点选项或处理按钮。</summary>
    void ToggleListeningCountdownPause()
    {
        if (!ListeningCounting || ListeningPendingStop is not { } stop) return;
        listeningCountdownPaused = !listeningCountdownPaused;
        // 暂停 / 恢复单独记类型（不记作「停下」，导出 CSV 的停下数不虚高）；类型字串只在桌面端定义，不改 core。
        ListeningLog(listeningCountdownPaused ? ListeningPauseLogType : ListeningResumeLogType, listeningSession?.Current, (listeningCountdownPaused ? "倒计时已暂停（剩 " : "倒计时继续（剩 ") + listeningCountdown + " 秒）", stop.OptionLabel ?? "");
        listeningMessage = ListeningStopText(stop, listeningCountdown);
        RefreshListening();
    }
    /// <summary>
    /// 主播放按钮（设计稿 16）：停下处倒计时进行或已暂停时，与卡片「暂停 / 继续」同一状态、同一逻辑（ToggleListeningCountdownPause）——
    /// 进行中显示「暂停」并冻结秒数，已暂停显示「继续」并从停住的秒数接着倒计时，不取消倒计时。其余情况保持原行为（试听中停止、收听中暂停、否则播放）。
    /// </summary>
    bool ListeningPlayTogglesCountdown => listeningPreviewTicket == 0 && !listeningRunning && ListeningCounting;
    string ListeningPlayCaption => listeningPreviewTicket != 0 ? "停止试听"
        : ListeningPlayTogglesCountdown ? (listeningCountdownPaused ? "继续" : "暂停")
        : listeningRunning ? "暂停" : "播放 / 续听";
    /// <summary>F5 / 手柄 West「重播」：停下处倒计时进行或已暂停时没有可重播的台词——不重开倒计时、不解除暂停，状态不变。</summary>
    void ListeningReplay()
    {
        if (listeningSession == null) return;
        if (ListeningPlayTogglesCountdown)
        {
            return; // 复审应该改 3：停下处没有可重播的台词——状态不变（定稿无附加说明）
        }
        PauseListening(); listeningOffset = 0; StartListening();
    }
    /// <summary>紧凑小窗的暂停键文字：与主播放按钮同一状态。</summary>
    string CompactListeningPauseCaption => ListeningPlayTogglesCountdown ? (listeningCountdownPaused ? "继续" : "暂停") : listeningRunning ? "暂停听书" : "继续听书";
    void ListeningPlayClick()
    {
        if (ListeningPlayTogglesCountdown) { ToggleListeningCountdownPause(); return; }
        if (listeningRunning || listeningPreviewTicket != 0 || ListeningCounting) PauseListening(listeningPreviewTicket != 0); else StartListening();
    }
    /// <summary>「已暂停」三字加粗（黄铜字），其余照常（设计稿 16 / 11）。</summary>
    static TextBlock PausedBold(TextBlock block, string text)
    {
        block.Inlines.Clear();
        if (text.StartsWith("已暂停", StringComparison.Ordinal))
        {
            block.Inlines.Add(new System.Windows.Documents.Run("已暂停") { FontWeight = FontWeights.Bold, Foreground = Theme.Brush("BrassText") });
            block.Inlines.Add(new System.Windows.Documents.Run(text[3..]));
        }
        else block.Inlines.Add(new System.Windows.Documents.Run(text));
        return block;
    }
    /// <summary>暂停键：未暂停为石板 ‖ 图标，已暂停为黄铜 ▶ 图标；文字仍取 Content（「暂停」/「继续」）。</summary>
    static DataTemplate PauseButtonTemplate(bool paused)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        icon.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse(paused ? "M3,1.5 L11,6.5 L3,11.5 Z" : "M3,1.5 L3,11.5 M9,1.5 L9,11.5"));
        if (paused) icon.SetValue(System.Windows.Shapes.Shape.FillProperty, Theme.Brush("BrassText"));
        icon.SetValue(System.Windows.Shapes.Shape.StrokeProperty, paused ? Theme.Brush("BrassText") : Theme.Brush("Slate"));
        icon.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, paused ? 1.0 : 2.0);
        icon.SetValue(FrameworkElement.WidthProperty, 13.0); icon.SetValue(FrameworkElement.HeightProperty, 13.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)); icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon); panel.AppendChild(label);
        return new DataTemplate { VisualTree = panel };
    }
    /// <summary>暂停行（.pzRow）：石板描边「暂停」，暂停后为黄铜「继续」；右侧一句说明。</summary>
    FrameworkElement ListeningPauseRow()
    {
        bool on = listeningCountdownPaused;
        var button = new Button
        {
            Content = on ? "继续" : "暂停", MinWidth = 86, ContentTemplate = PauseButtonTemplate(on), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = on ? Theme.Brush("BranchFill") : Brushes.White, BorderBrush = on ? Theme.Brush("Brass") : Theme.Brush("Slate"), BorderThickness = new Thickness(1.4),
            Foreground = on ? Theme.Brush("BrassText") : Theme.Brush("Plate"), FontWeight = FontWeights.Bold,
            ToolTip = on ? "从停住的秒数接着倒计时" : "停住倒计时，不自动续接",
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, "ListeningCountdownPause");
        button.Click += (_, _) => ToggleListeningCountdownPause();
        var text = PausedBold(new TextBlock { FontSize = 11.5, Foreground = Hud.Solid("#3A4450"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, on ? ListeningPausedHint : ListeningPauseHint);
        var row = new DockPanel { LastChildFill = true };
        // 设计稿 pzBtn：右下角 6×6 角标（黄铜 / 石板）。
        var corner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,0 L6,6 L0,6"), Stroke = on ? Theme.Brush("Brass") : Theme.Brush("Slate"), StrokeThickness = 1.4, Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 13, 3), IsHitTestVisible = false };
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { button, corner } };
        DockPanel.SetDock(host, Dock.Left); row.Children.Add(host); row.Children.Add(text);
        return new CutFrame { Background = on ? Theme.Brush("RouteFill") : Theme.Brush("Surface"), BorderBrush = on ? Theme.Brush("Brass") : Theme.Brush("Slate"), OutlineBrush = Theme.Brush("Stroke"), EdgeWidth = 3, Cut = 7, Corners = CutCorners.BottomRight, Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(0, 4, 0, 6), Child = row };
    }

    /// <summary>将被自动选择的选项（设计稿 01 / 03 opt47 auto）：黄铜淡底 + 黄铜描边 + 金色左边，编号块实心黄铜，右侧「DEFAULT · 默认分支」（结局为「ENDING · 结局 · 名称」），下方挂倒计时细轨。暂停时边框保持黄铜。</summary>
    void MarkListeningAutoOption(Button button)
    {
        bool ending = ListeningAtEnding;
        button.Background = Theme.Brush("BranchFill"); button.BorderBrush = Theme.Brush("Brass"); button.BorderThickness = new Thickness(3, 1, 1, 1);
        button.Foreground = Theme.Brush("Fg"); button.FontWeight = FontWeights.SemiBold;
        button.ToolTip = button.Content + (ending ? "（倒计时结束后选入这一结局）" : "（倒计时结束后自动选择）");
        button.ContentTemplate = ListeningOptionTemplate(button.Tag is int n ? n : 1, true, ending ? EndingName(button.Content as string) : null);
        int at = listeningChoices.Children.IndexOf(button);
        listeningChoices.Children.Insert(at + 1, CountdownRail(ListeningCountdownFraction, ending ? "AUTO-PICK · 将选入结局" : "AUTO-PICK · 将自动选择"));
    }
    /// <summary>结局名：取选项文字里「（开启结局·明天）」的「明天」；没有时为空串（标签只写「结局」）。</summary>
    static string EndingName(string? label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label ?? "", @"[（(]开启结局\s*[·・]\s*([^）)]+)[）)]");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
    /// <summary>选项行模板（设计稿 opt47）：左侧 01 / 02 编号块，中间选项文字（「（开启结局·X）」用强调色加粗），右侧箭头或 DEFAULT / ENDING 标。</summary>
    static DataTemplate ListeningOptionTemplate(int number, bool auto, string? endingName)
    {
        bool ending = endingName != null;
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var no = new FrameworkElementFactory(typeof(Border));
        no.SetValue(DockPanel.DockProperty, Dock.Left); no.SetValue(FrameworkElement.WidthProperty, 22.0); no.SetValue(FrameworkElement.HeightProperty, 22.0);
        no.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0)); no.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        no.SetValue(Border.BackgroundProperty, auto ? Theme.Brush("Brass") : Brushes.Transparent);
        no.SetValue(Border.BorderBrushProperty, Hud.Solid("#AEB6BF")); no.SetValue(Border.BorderThicknessProperty, new Thickness(auto ? 0 : 1));
        var noText = new FrameworkElementFactory(typeof(TextBlock)); noText.SetValue(TextBlock.TextProperty, number.ToString("00"));
        noText.SetValue(TextBlock.FontFamilyProperty, Theme.Font("LabelFont")); noText.SetValue(TextBlock.FontSizeProperty, 10.5); noText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        noText.SetValue(TextBlock.ForegroundProperty, auto ? Brushes.White : Hud.Solid("#5C6670"));
        noText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); noText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        no.AppendChild(noText); dock.AppendChild(no);
        if (auto)
        {
            var tag = new FrameworkElementFactory(typeof(StackPanel)); tag.SetValue(DockPanel.DockProperty, Dock.Right); tag.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0)); tag.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var brush = ending ? Theme.Brush("AccentText") : Theme.Brush("BrassText");
            var en = new FrameworkElementFactory(typeof(TextBlock)); en.SetValue(TextBlock.TextProperty, ending ? "ENDING" : "DEFAULT"); en.SetValue(TextBlock.FontSizeProperty, 9.0); en.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            en.SetValue(TextBlock.FontFamilyProperty, Theme.Font("LabelFont")); en.SetValue(TextBlock.ForegroundProperty, brush); en.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            var zh = new FrameworkElementFactory(typeof(TextBlock)); zh.SetValue(TextBlock.TextProperty, ending ? (endingName!.Length > 0 ? "结局 · " + endingName : "结局") : "默认分支"); zh.SetValue(TextBlock.FontSizeProperty, 11.0); zh.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
            zh.SetValue(TextBlock.ForegroundProperty, brush); zh.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            tag.AppendChild(en); tag.AppendChild(zh); dock.AppendChild(tag);
        }
        else
        {
            var chev = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path)); chev.SetValue(DockPanel.DockProperty, Dock.Right);
            chev.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M6,3 L11,8 L6,13")); chev.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Hud.Solid("#8A96A3"));
            chev.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5); chev.SetValue(FrameworkElement.WidthProperty, 16.0); chev.SetValue(FrameworkElement.HeightProperty, 16.0);
            chev.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0)); chev.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.AppendChild(chev);
        }
        var label = new FrameworkElementFactory(typeof(OptionLabelBlock)); label.SetBinding(OptionLabelBlock.LabelProperty, new System.Windows.Data.Binding());
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dock.AppendChild(label);
        return new DataTemplate { VisualTree = dock };
    }
    /// <summary>普通选项行（编号块 + 箭头）；「跳过本处选择」等非选项按钮不加编号。</summary>
    static void ApplyListeningOptionRow(Button button)
    {
        if (button.Tag is int number) button.ContentTemplate = ListeningOptionTemplate(number, false, null);
    }
    bool ListeningAtEndingItem(ListeningItem? item) => item is { Kind: ListeningItemKind.Choice, Node: { Kind: "choice" } menu } && SafeShape(menu)?.Ending == true;
    bool ListeningAtEnding => listeningSession?.Current is { Kind: ListeningItemKind.Choice, Node: { Kind: "choice" } menu } && SafeShape(menu)?.Ending == true;
    MenuShapeInfo? SafeShape(Node menu) { try { return listeningSession == null ? null : MenuShape.Of(listeningSession.Pack, menu); } catch { return null; } }

    /// <summary>待选支线 / 结局分歧的卡头（设计稿 01 / 03）与卡尾（提示 / 结局锁定说明 / 暂停行）。只在倒计时进行或已暂停时出现。</summary>
    void ListeningChoiceHead()
    {
        if (!ListeningCounting) return;
        bool ending = ListeningAtEnding;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var badge = CountdownBadge(listeningCountdown, ending ? "秒后选入结局" : "秒后自动选择"); DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = ending ? "ENDING · 结局分歧" : "BRANCH · 待选互动", FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = ending ? Hud.Solid("#F0B8C0") : Theme.Brush("BrassText") });
        words.Children.Add(new TextBlock { Text = ending ? "此处是结局分歧 · 只收听所选结局" : "分支自动选择 · 倒计时结束按默认项继续", FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 0), Foreground = ending ? Brushes.White : Theme.Brush("Fg") });
        head.Children.Add(words);
        if (ending)
        {
            // 设计稿 03：深红卡头带（深酒红渐变 + 左侧红→黄铜竖条），环与标题压在带上。
            head.Margin = new Thickness(0);
            var band = new CutFrame
            {
                Background = new LinearGradientBrush(new GradientStopCollection { new(C047("#3A2A30"), 0), new(C047("#2A2228"), .6), new(C047("#272D35"), 1) }, 90),
                BorderBrush = new LinearGradientBrush(C047("#E0485C"), C047("#B08A3E"), 90), OutlineBrush = Hud.Solid("#3A2A30"), EdgeWidth = 3,
                Cut = 10, Corners = CutCorners.BottomRight, Padding = new Thickness(12, 9, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = head,
            };
            listeningChoices.Children.Insert(0, band);
        }
        else listeningChoices.Children.Insert(0, head);
        if (ending)
            listeningChoices.Children.Add(new CutFrame
            {
                Background = Theme.Brush("AccentFill"), BorderBrush = Theme.Brush("Accent"), OutlineBrush = Theme.Brush("AccentFill"), EdgeWidth = 3, Cut = 0, Corners = CutCorners.None, Padding = new Thickness(9, 5, 9, 6), Margin = new Thickness(0, 4, 0, 2),
                Child = new TextBlock { Text = "选入结局后只读这一结局，读完即停：「" + ListeningText.EndingFinished + "」", FontSize = 11.5, Foreground = Theme.Brush("AccentText"), TextWrapping = TextWrapping.Wrap },
            });
        else
        {
            var hint = new DockPanel { Margin = new Thickness(2, 6, 0, 2) };
            hint.Children.Add(Docked047(new System.Windows.Shapes.Path { Data = ActionIcons.Fork, Stroke = Theme.Brush("Brass"), StrokeThickness = 1.3, Width = 13, Height = 13, Stretch = Stretch.Uniform, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top }, Dock.Left));
            hint.Children.Add(new TextBlock { Text = "点任一选项立即选择；默认分支在分支页设定（当前：" + ListeningText.OptionOrdinal(ListeningDefaultNumber) + "）", FontSize = 11.5, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap });
            listeningChoices.Children.Add(hint);
        }
        listeningPauseDock.Children.Add(ListeningPauseRow());
    }
    /// <summary>未核续接 / 小节已读完 / 无文本死角卡（设计稿 04 / 11）：倒计时环、标题、说明、细轨、处理按钮、「反馈」与「暂停 / 继续」。</summary>
    void ListeningStopCard(ListeningStop stop)
    {
        bool dead = stop.DeadEnd, counting = listeningCountdown > 0, paused = counting && listeningCountdownPaused;
        string action = ListeningStopAction047(stop);
        var card = new CutFrame { Background = Theme.Brush("RouteFill"), BorderBrush = Theme.Brush("Brass"), OutlineBrush = Theme.Brush("BranchStroke"), EdgeWidth = 3, Cut = 10, Corners = CutCorners.Diagonal, BracketBrush = Theme.Brush("Brass"), BracketLength = 8, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 2, 0, 6) };
        var body = new StackPanel(); card.Child = body;
        var head = new DockPanel();
        string ringCaption = stop.Action == ListeningStopAction.OrderContinue ? "秒后按顺序继续" : "秒后" + action;
        if (counting) { var badge = CountdownBadge(listeningCountdown, ringCaption); DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge); }
        string eyebrow = stop.Action == ListeningStopAction.OrderContinue ? "ROUTE · 续接未核实" : stop.SectionComplete ? "SECTION · 小节已读完" : "END OF TEXT · 后续没有配音文本";
        var headWords = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headWords.Children.Add(new TextBlock { Text = eyebrow, FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BrassText") });
        string title = stop.Action switch
        {
            ListeningStopAction.OrderContinue => "此处分支续接未核实",
            _ when stop.SectionComplete => "本小节已读完 · 出口未核实",
            ListeningStopAction.NextSection => ListeningText.DeadEndHint,
            _ => ListeningText.DeadEndChapterEnd,
        };
        headWords.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 0) });
        head.Children.Add(headWords);
        body.Children.Add(head);
        // 复审应该改 2：「也可直接点…」只有 11 有；04 正文暂停时不变，暂停说明只在暂停行。
        string detail = paused && stop.Action == ListeningStopAction.OrderContinue
            ? ListeningPausedHint + "也可直接点「" + action + "」或「下一句」。"
            : paused && counting
            ? "倒计时结束后" + action + "。"
            : counting
            ? (stop.Action == ListeningStopAction.OrderContinue ? "倒计时结束后从本节下一句接着听，并显示「" + ListeningText.OrderContinued + "」。" + ListeningPauseHint : "倒计时结束后" + action + "。")
            : preferences.BranchAutoSelect ? "倒计时已停止，停在这里；点「" + action + "」或「下一句」继续，点播放重新倒计时。"
            : "分支自动选择已关闭，停在这里等待；点「" + action + "」或「下一句」继续。";
        body.Children.Add(PausedBold(new TextBlock { FontSize = 11.5, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) }, detail));
        if (counting) body.Children.Add(CountdownRail(ListeningCountdownFraction, stop.Action == ListeningStopAction.OrderContinue ? "IN ORDER · 按顺序" : "NEXT · " + action));
        var row = new WrapPanel(); body.Children.Add(row);
        var go = ActionButton.Apply(new Button { Content = action, Margin = new Thickness(0, 3, 6, 3) }, stop.Action == ListeningStopAction.OrderContinue ? "IN ORDER" : stop.Action == ListeningStopAction.NextSection ? "NEXT SECTION" : "END", ActionIcons.Confirm, "go");
        go.Click += (_, _) => ApplyListeningStop(false); row.Children.Add(go);
        if (CanListeningFeedback)
        {
            var fb = ActionButton.Apply(new Button { Content = "反馈", Margin = new Thickness(0, 3, 6, 3), ToolTip = "反馈：记录这里后续没有配音文本，游戏里可能还有内容" }, "FEEDBACK", ActionIcons.Menu);
            fb.Click += (_, _) => ListeningStopFeedback(); row.Children.Add(fb);
        }
        if (counting) listeningPauseDock.Children.Add(ListeningPauseRow());
        // 复审小问题：脚注在最后（定稿在暂停行之下）。
        if (dead)
            (counting ? (Panel)listeningPauseDock : body).Children.Add(new TextBlock { Text = "反馈会记录这一处：后续没有配音文本，游戏里可能还有内容", FontSize = 10.5, Foreground = Theme.Brush("SubtleText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, counting ? 3 : 2, 0, 0) });
        if (dead) System.Windows.Automation.AutomationProperties.SetName(card, title);
        listeningChoices.Children.Add(card);
    }
    /// <summary>当前句的签：待配音 / 无配音、现稿续接。</summary>
    void RefreshListeningTags(ListeningItem? current)
    {
        listeningTags.Children.Clear();
        if (current?.Kind == ListeningItemKind.Line)
        {
            if (ListeningVoiceTag(current.Node) is { } voice) listeningTags.Children.Add(VoiceLegendCard(voice));
            if (current.DraftContinuation) listeningTags.Children.Add(DraftTagChip());
        }
        listeningTags.Visibility = listeningTags.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>设计稿 05：「VOICE · 配音状态」卡——右侧图例（待配音 / 无配音，当前项高亮），下方一句说明。</summary>
    static FrameworkElement VoiceLegendCard(string voice)
    {
        bool draft = voice == "待配音";
        var head = new DockPanel();
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, isDraft) in new[] { ("待配音", true), ("无配音", false) })
        {
            var chip = (Border)VoiceTagChip(text, isDraft); chip.Margin = new Thickness(6, 0, 0, 0);
            if (text != voice) { chip.Opacity = .45; chip.Background = Brushes.White; }
            legend.Children.Add(chip);
        }
        DockPanel.SetDock(legend, Dock.Right); head.Children.Add(legend);
        head.Children.Add(new TextBlock { Text = "VOICE · 配音状态", FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BrassText"), VerticalAlignment = VerticalAlignment.Center });
        var line = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.Brush("Fg") };
        line.Inlines.Add(new System.Windows.Documents.Run(voice + "：") { FontWeight = FontWeights.Bold, Foreground = draft ? Theme.Brush("BrassText") : Theme.Brush("NormalAccent") });
        line.Inlines.Add(new System.Windows.Documents.Run((draft ? "本句尚未配音" : "本句没有可用录音") + "——照常显示字幕，按字数停留（约每字 0.25 秒，至少 2 秒）后自动进入下一句；点「下一句」可立即越过。"));
        var card = new CutFrame { Background = Theme.Brush("Surface"), BorderBrush = draft ? Theme.Brush("Brass") : Theme.Brush("Slate"), OutlineBrush = Theme.Brush("Stroke"), EdgeWidth = 3, Cut = 6, Corners = CutCorners.BottomRight, Padding = new Thickness(9, 5, 9, 6), Margin = new Thickness(0, 0, 0, 2),
            Child = new StackPanel { Children = { head, line } } };
        System.Windows.Automation.AutomationProperties.SetName(card, voice);
        return card;
    }
    static FrameworkElement VoiceTagChip(string text, bool draft) => new Border
    {
        Background = draft ? Theme.Brush("BranchFill") : Theme.Brush("SlateFill"), BorderBrush = draft ? Theme.Brush("Brass") : Theme.Brush("Slate"), BorderThickness = new Thickness(1),
        Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = draft ? "待配音：这句还没有录音，显示字幕并按字数停留后继续" : "无配音：这句没有可用录音，显示字幕并按字数停留后继续",
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = draft ? Theme.Brush("BrassText") : Theme.Brush("NormalAccent") },
    };
    static FrameworkElement DraftTagChip() => new Border
    {
        Background = Theme.Brush("TealFill"), BorderBrush = Theme.Brush("Teal"), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = ListeningText.DraftExitUnverified,
        Child = new TextBlock { Text = ListeningText.DraftTag, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("TealText") },
    };

    // ---------------- 设置：分支与播放记录 ----------------
    readonly CheckBox branchAutoSelectBox = new() { Content = "分支自动选择", Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold };
    readonly RadioButton defaultBranchFirst = new() { Content = "第一个选项", GroupName = "DefaultBranch047", Margin = new Thickness(0, 0, 14, 0) };
    readonly RadioButton defaultBranchSecond = new() { Content = "第二个选项", GroupName = "DefaultBranch047" };
    readonly TextBlock playbackLogStatus = new() { FontSize = 11, Foreground = Theme.Brush("Fg"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    // 导出结果卡：「LOG · EXPORTED / 导出失败」眉标 + 结果（文件路径、条数）。
    readonly TextBlock playbackLogEyebrow = new() { FontSize = 9.5, FontWeight = FontWeights.SemiBold };
    readonly CutFrame playbackLogCard = new() { Visibility = Visibility.Collapsed, EdgeWidth = 3, Cut = 6, Corners = CutCorners.BottomRight, Padding = new Thickness(9, 5, 9, 6), Margin = new Thickness(0, 6, 0, 0) };
    void ShowPlaybackLogResult(bool ok, string text)
    {
        playbackLogStatus.Text = text;
        playbackLogEyebrow.Text = ok ? "LOG · EXPORTED · 已导出" : "LOG · FAILED · 导出失败";
        playbackLogEyebrow.Foreground = ok ? Theme.Brush("TealText") : Theme.Brush("AccentText");
        playbackLogCard.Background = ok ? Theme.Brush("TealFill") : Theme.Brush("AccentFill"); playbackLogCard.BorderBrush = ok ? Theme.Brush("Teal") : Theme.Brush("Accent");
        playbackLogCard.Visibility = Visibility.Visible;
    }
    Expander branchSettingsExpander = null!;
    public const string BranchAutoSelectCaption = "听书遇到待选支线、续接未核实或后续没有配音文本时，15 秒倒计时后自动处理：支线按上方默认项选择并显示“已自动选择：…”，未核续接按本节顺序继续，无文本处跳到下一小节（最后一个小节则结束本章）。关闭后停下等待。只有一个选项的菜单、探索菜单不受此开关影响，始终自动继续。";

    void BuildBranchSettings()
    {
        var body = new StackPanel { Margin = new Thickness(4, 6, 4, 8) };
        body.Children.Add(new TextBlock { Text = "默认分支", Style = (Style)FindResource("SectionLabel") });
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) }; row.Children.Add(defaultBranchFirst); row.Children.Add(defaultBranchSecond); body.Children.Add(row);
        body.Children.Add(new TextBlock { Text = "听书的“默认分支”与倒计时自动选择共用此设置；只有一个选项的地方按第一个。", FontSize = 11, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(branchAutoSelectBox);
        body.Children.Add(new TextBlock { Text = BranchAutoSelectCaption, FontSize = 11, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "播放记录", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 14, 0, 4) });
        body.Children.Add(new TextBlock { Text = "自动选择、按顺序续接、无配音越过、停下与反馈都会记一条（时间、配音包、章节、小节、节点、原因、所选项）。", FontSize = 11, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap });
        var export = ActionButton.Apply(new Button { Content = "导出播放记录", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), ToolTip = "导出为 CSV（UTF-8，可用表格软件打开），保存到桌面" }, "EXPORT LOG", ActionIcons.Menu);
        export.Click += (_, _) => ExportPlaybackLog(); body.Children.Add(export);
        playbackLogCard.Child = new StackPanel { Children = { playbackLogEyebrow, playbackLogStatus } }; body.Children.Add(playbackLogCard);
        branchAutoSelectBox.IsChecked = preferences.BranchAutoSelect;
        (preferences.DefaultBranchOption == 2 ? defaultBranchSecond : defaultBranchFirst).IsChecked = true;
        branchAutoSelectBox.Checked += (_, _) => SetBranchAutoSelect(true);
        branchAutoSelectBox.Unchecked += (_, _) => SetBranchAutoSelect(false);
        defaultBranchFirst.Checked += (_, _) => SetDefaultBranchOption(1);
        defaultBranchSecond.Checked += (_, _) => SetDefaultBranchOption(2);
        branchSettingsExpander = new Expander { Header = "分支与播放记录", Margin = new Thickness(0, 0, 0, 8), Content = body };
        int at = SettingsContent.Children.OfType<Expander>().Select(x => SettingsContent.Children.IndexOf(x)).DefaultIfEmpty(SettingsContent.Children.Count - 1).First() + 1;
        SettingsContent.Children.Insert(Math.Min(at, SettingsContent.Children.Count), branchSettingsExpander);
    }
    void SetBranchAutoSelect(bool enabled)
    {
        if (preferences.BranchAutoSelect == enabled) return;
        preferences.BranchAutoSelect = enabled; Save();
        // 停在停下处时立即按新设置重新提示（关闭则取消倒计时并等待）。
        if (listeningSession != null && !listeningRunning && ListeningPendingStop != null)
            RestartListeningStop();
    }
    /// <summary>暂停期间改设置 / 重新解析停下处：记下「已暂停 + 剩余秒数 + 停下处」，处理后恢复，不解除暂停。</summary>
    // 按停下处 Id 记（改默认分支会 SetPolicy 重建计划，条目对象换新但 Id 不变）。
    (string Id, int Seconds)? HoldListeningPause() => listeningCountdownPaused && ListeningCounting && listeningSession?.Current is { } item ? (item.Id, listeningCountdown) : null;
    void RestoreListeningPause((string Id, int Seconds)? held)
    {
        if (held is not { } h || !ListeningCounting || listeningSession?.Current?.Id != h.Id) return;
        listeningCountdown = h.Seconds; listeningCountdownPaused = true;
        if (ListeningPendingStop is { } stop) listeningMessage = ListeningStopText(stop, listeningCountdown);
        RefreshListening();
    }
    void SetDefaultBranchOption(int number)
    {
        number = number == 2 ? 2 : 1;
        if (preferences.DefaultBranchOption == number) return;
        preferences.DefaultBranchOption = number; Save();
        RefreshListeningPolicyLabels();
        var held = HoldListeningPause();
        if (held != null)
        {
            // 复审应该改 4：暂停时也要换成新默认分支的计划（SetPolicy），再在同一停下处恢复暂停。
            if (listeningSession?.Policy is ListeningBranchPolicy.First or ListeningBranchPolicy.Second)
            {
                listeningSession.SetPolicy(ListeningSelectedPolicy);
                // SetPolicy 按节点重新定位，可能落到停下提示之前的台词；新计划里仍有同一停下处时回到它。
                if (listeningSession.Current?.Id != held.Value.Id) listeningSession.SeekItem(held.Value.Id);
                listeningOffset = 0; MarkExplicitListeningPosition(); listeningPreserveResume = false; SaveListeningProgress();
            }
            RestartListeningStop(held); return;
        }
        if (listeningSession?.Policy is ListeningBranchPolicy.First or ListeningBranchPolicy.Second && PauseListening())
        { listeningSession.SetPolicy(ListeningSelectedPolicy); listeningOffset = 0; MarkExplicitListeningPosition(); listeningPreserveResume = false; SaveListeningProgress(); }
        else if (listeningSession != null && !listeningRunning && ListeningPendingStop != null) { RestartListeningStop(); return; }
        RefreshListening();
    }
    void RefreshListeningPolicyLabels()
    {
        bool was = listeningRefreshing; listeningRefreshing = true;
        try
        {
            int selected = listeningPolicy.SelectedIndex;
            listeningPolicy.ItemsSource = new[] { "手动选择：遇分支暂停，点选后继续（可倒计时自动选择）", "默认分支：按" + (ListeningDefaultNumber == 2 ? "第二个" : "第一个") + "选项继续（在设置中更改）", "全部听取：依次听互斥及条件路线" };
            listeningPolicy.SelectedIndex = Math.Max(0, selected);
        }
        finally { listeningRefreshing = was; }
    }
    string? ExportPlaybackLog(string? folder = null)
    {
        try
        {
            folder ??= testUi ? Log.DataDir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Log.DataDir;
            string file = System.IO.Path.Combine(folder, "战双配音播放记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            File.WriteAllText(file, PlaybackLog.Export(), new System.Text.UTF8Encoding(false));
            int rows = Math.Max(0, File.ReadAllLines(file).Length - 1);
            ShowPlaybackLogResult(true, "已导出：" + file + "\n共 " + rows + " 条记录（CSV，可用表格软件打开）。"); Tell("播放记录已导出：" + file);
            return file;
        }
        catch (Exception ex) { ShowPlaybackLogResult(false, "导出失败：" + ex.Message); Tell(playbackLogStatus.Text); return null; }
    }
}

/// <summary>倒计时环（设计稿 01 / 16）：细灰底环 + 黄铜进度弧（12 点起顺时针）；暂停后弧转灰 #9AA3AD。</summary>
public sealed class CountdownRing : FrameworkElement
{
    static readonly Brush PausedBrush = Hud.Solid("#9AA3AD");
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(CountdownRing), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PausedProperty = DependencyProperty.Register(nameof(Paused), typeof(bool), typeof(CountdownRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public bool Paused { get => (bool)GetValue(PausedProperty); set => SetValue(PausedProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight); if (size <= 4) return;
        double thickness = 2.6, r = size / 2 - thickness / 2; var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Theme.Brush("Stroke"), 1.2), c, r, r);
        // 设计稿 ring47：12 / 4 / 8 点三处刻度（细灰短线，跨环内外）。
        var tick = new Pen(Hud.Solid("#AEB6BF"), 1);
        for (int i = 0; i < 3; i++)
        {
            double t = -Math.PI / 2 + i * 2 * Math.PI / 3;
            dc.DrawLine(tick, new Point(c.X + Math.Cos(t) * (r - 3), c.Y + Math.Sin(t) * (r - 3)), new Point(c.X + Math.Cos(t) * (r + 2.5), c.Y + Math.Sin(t) * (r + 2.5)));
        }
        double f = Math.Clamp(Fraction, 0, 1); if (f <= 0) return;
        var pen = new Pen(Paused ? PausedBrush : Theme.Brush("Brass"), thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (f >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = f * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r); var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open()) { ctx.BeginFigure(start, false, false); ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false); }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>选项文字：「（开启结局·X）」部分用强调色加粗（设计稿 03），其余照常。</summary>
public sealed class OptionLabelBlock : TextBlock
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(object), typeof(OptionLabelBlock), new PropertyMetadata(null, (d, _) => ((OptionLabelBlock)d).Build()));
    public object? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    void Build()
    {
        Inlines.Clear();
        string text = Label as string ?? Label?.ToString() ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[（(]开启结局[^）)]*[）)]");
        if (!m.Success) { Inlines.Add(new System.Windows.Documents.Run(text)); return; }
        if (m.Index > 0) Inlines.Add(new System.Windows.Documents.Run(text[..m.Index]));
        Inlines.Add(new System.Windows.Documents.Run(m.Value) { Foreground = Theme.Brush("AccentText"), FontWeight = FontWeights.Bold });
        if (m.Index + m.Length < text.Length) Inlines.Add(new System.Windows.Documents.Run(text[(m.Index + m.Length)..]));
    }
}
