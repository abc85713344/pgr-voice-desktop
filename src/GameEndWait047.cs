using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PgrVoice.Following;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 游戏配音的终点规则（与安卓 d13260c 相同，共享 core/Following/GameEndWait.cs）：
/// 文字跟随（含「播完自动下一句」）到达未归档 end 节点（选项出口或小节正文末尾）时不关闭，
/// 提示终点自己的文字（兜底「请按游戏画面定位后续」），不点击游戏，继续用 GameTextReader 读取对白；
/// 读到下一句（含下一小节首句，自动切换小节）后自动接上配音。每次进入等待写一条「终点等待定位」记录。听书模式不受影响。
/// </summary>
public partial class MainWindow
{
    readonly CutFrame gameEndWaitCard = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock gameEndWaitTitle = new() { FontSize = 14.5, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock gameEndWaitDetail = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
    GameEndWait? textEndWait;
    string? textEndPendingNode;
    long textEndPendingTicket, textEndPendingSince;
    long? textEndPendingSilentMs;
    /// <summary>正在终点等待时的票据；未等待时为 null。</summary>
    GameEndWait? GameEndWaiting => textEndWait;

    void InitializeGameEndWait(Panel panel)
    {
        gameEndWaitCard.Background = Theme.Brush("RouteFill"); gameEndWaitCard.BorderBrush = Theme.Brush("Brass"); gameEndWaitCard.OutlineBrush = Theme.Brush("BranchStroke");
        gameEndWaitCard.EdgeWidth = 3; gameEndWaitCard.Cut = 10; gameEndWaitCard.Corners = CutCorners.Diagonal; gameEndWaitCard.BracketBrush = Theme.Brush("Brass"); gameEndWaitCard.BracketLength = 8;
        gameEndWaitCard.Padding = new Thickness(12, 9, 12, 9); gameEndWaitCard.Margin = new Thickness(0, 2, 0, 8);
        var body = new StackPanel(); gameEndWaitCard.Child = body;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Theme.Brush("Brass"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .25, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        head.Children.Add(dot);
        head.Children.Add(new TextBlock { Text = GameEndWaitPolicy.Eyebrow, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BrassText"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head); body.Children.Add(gameEndWaitTitle);
        gameEndWaitDetail.Foreground = Theme.Brush("MutedText"); body.Children.Add(gameEndWaitDetail);
        var row = new WrapPanel(); body.Children.Add(row);
        var stop = new Button { Content = "停止配音跟随", Margin = new Thickness(0, 3, 6, 3), Padding = new Thickness(10, 5, 10, 5), ToolTip = "停止等待并暂停文字跟随；不会点击游戏。" };
        stop.Click += (_, _) => PauseTextPlayback("已停止终点等待，文字跟随已暂停。");
        row.Children.Add(stop);
        // 「连接普通对白」：与「文字跟随」里的同名按钮同一操作（BeginTextMonitoring(普通对白)，只读、无需 OCR），保留本次终点等待。
        var connect = ActionButton.Apply(new Button { Content = "连接普通对白", Margin = new Thickness(0, 3, 6, 3), ToolTip = "重新开启游戏文字监听并优先连接普通对白（只读，不点击游戏）；继续等待下一句。" }, "CONNECT", ActionIcons.Link, "go");
        System.Windows.Automation.AutomationProperties.SetAutomationId(connect, "GameEndWaitConnect");
        connect.Click += (_, _) => ConnectOrdinaryDialogue();
        row.Children.Insert(0, connect);
        panel.Children.Add(gameEndWaitCard);
        audio.PlaybackCompleted += ticket => Dispatcher.BeginInvoke(() => OnTextEndAudioCompleted(ticket));
    }

    /// <summary>文字跟随已确认一句后调用：前方是 end 节点时，本句播完（或无音频停留结束）即进入终点等待。</summary>
    void ArmTextEndWait(PlaybackEngine owner, Node node)
    {
        textEndPendingNode = null;
        if (GameEndWaitPolicy.EndAhead(owner) == null) return;
        textEndPendingNode = node.Id; textEndPendingTicket = audioRequest; textEndPendingSince = TextAutoNow();
        textEndPendingSilentMs = AutoPlaybackLinePolicy.SilentMilliseconds(owner.Pack, node);
    }
    void OnTextEndAudioCompleted(long ticket)
    {
        if (textEndPendingNode == null || ticket != textEndPendingTicket) return;
        BeginTextEndWait();
    }
    void TickTextEndWait()
    {
        if (textEndPendingNode == null) return;
        if (!textArmed || engine?.CurrentId != textEndPendingNode) { textEndPendingNode = null; return; }
        if (textEndPendingSilentMs is long silent && TextAutoNow() - textEndPendingSince >= silent) BeginTextEndWait();
    }
    void BeginTextEndWait()
    {
        var owner = engine; string? node = textEndPendingNode; textEndPendingNode = null;
        if (owner == null || node == null || !textArmed || owner.CurrentId != node || owner != textOwner) return;
        if (!GameEndWaitPolicy.TryBegin(owner, owner.EvaluateCommonAutoPlayNext(), out var wait) || wait == null) return;
        textEndWait = wait; UpdateState();
        CancelTextAutoAdvance();
        textAutoStatus.Text = wait.Hint + " · 不点击游戏，等待游戏画面出现下一句。";
        GameLog(PlaybackLog.Types.EndWait, owner.Current, GameEndWaitPolicy.LogReason(wait));
        Log.Write("game-end-wait", $"{wait.SourceId} → {wait.EndId ?? "（无终点节点）"}\t{wait.Hint}\t搜索={string.Join(",", wait.SearchSections)}");
        ShowGameEndWaitCard(wait, null);
        TextNotice(wait.Hint + " · 等待游戏画面定位，读到下一句后自动继续（含下一小节首句）。");
        if (!expanded) { routeToast.PlacementTarget = BallView; routeToast.Content = GameEndWaitPolicy.Eyebrow + "\n" + wait.Hint; routeToast.IsOpen = true; }
    }
    void ShowGameEndWaitCard(GameEndWait wait, string? extra)
    {
        gameEndWaitTitle.Text = wait.Hint;
        gameEndWaitDetail.Text = GameEndWaitPolicy.Detail + (extra == null ? "" : "\n" + extra);
        System.Windows.Automation.AutomationProperties.SetName(gameEndWaitCard, GameEndWaitPolicy.Eyebrow + " " + wait.Hint);
        gameEndWaitCard.Visibility = Visibility.Visible;
    }
    /// <summary>「连接普通对白」（结束等待卡；第二步快捷窗 / F10 复用）：调用现有 BeginTextMonitoring(普通对白)。正在终点等待时保留等待票据，读到下一句（含下一小节首句）仍自动接上。</summary>
    void ConnectOrdinaryDialogue()
    {
        var wait = textEndWait; var owner = engine;
        var ordinary = textProbes.FirstOrDefault(p => p.Kind == GameTextKind.Ordinary);
        BeginTextMonitoring(ordinary);
        if (wait != null && textArmed && ReferenceEquals(engine, owner))
        {
            textEndWait = wait; UpdateState();
            ShowGameEndWaitCard(wait, "已重新开启监听，优先连接普通对白；读到下一句后自动接上。");
            TextNotice(wait.Hint + " · 已重新开启监听，等待游戏画面出现下一句。");
        }
    }
    void ClearGameEndWait(string? resumedAt = null)
    {
        textEndPendingNode = null;
        if (textEndWait is { } wait && resumedAt != null) Log.Write("game-end-wait-resume", $"{wait.EndId ?? wait.SourceId} → {resumedAt}");
        textEndWait = null; UpdateState();
        gameEndWaitCard.Visibility = Visibility.Collapsed;
        if (routeToast.IsOpen && routeToast.Content is string s && s.StartsWith(GameEndWaitPolicy.Eyebrow, StringComparison.Ordinal)) routeToast.IsOpen = false;
    }

    /// <summary>
    /// 顺序自动播放（截图定位 + 点击）本身不持续读取游戏画面：到达终点时写记录，交给「游戏文字跟随」以同一终点票据继续等待，
    /// 读到下一句（含下一小节首句）后自动接上配音；若「播完自动下一句」开启，之后照常自动点击。
    /// </summary>
    void HandOverAutomaticToEndWait(GameEndWait wait)
    {
        var owner = engine!;
        GameLog(PlaybackLog.Types.EndWait, owner.Current, GameEndWaitPolicy.LogReason(wait));
        Log.Write("game-end-wait", $"顺序自动播放 {wait.SourceId} → {wait.EndId ?? "（无终点节点）"}\t{wait.Hint}");
        StopAutomatic(wait.Hint + "\n已转为游戏文字跟随，等待游戏画面出现下一句后自动接上；不会点击游戏。", pauseSound: false);
        var chapter = owner.Pack.Chapters.First(c => c.Sections.Any(s => s.Id == wait.SectionId));
        if (ChapterBox.SelectedItem != chapter) ChapterBox.SelectedItem = chapter;
        SectionBox.SelectedItem = chapter.Sections.First(s => s.Id == wait.SectionId);
        BeginTextMonitoring();
        if (!textArmed)
        {
            ShowGameEndWaitCard(wait, "未能开启游戏文字读取（需要原生电脑版客户端）；请在游戏中继续，读到新台词后用定位重新开启。");
            return;
        }
        textEndWait = wait; UpdateState();
        ShowGameEndWaitCard(wait, null);
        TextNotice(wait.Hint + " · 等待游戏画面定位，读到下一句后自动继续（含下一小节首句）。");
        if (!expanded) { routeToast.PlacementTarget = BallView; routeToast.Content = GameEndWaitPolicy.Eyebrow + "\n" + wait.Hint; routeToast.IsOpen = true; }
    }

    /// <summary>
    /// 文字跟随匹配：平时只在所选小节；终点等待中忽略旧末句，并在下一小节里找（同文多句只认下一小节首句），
    /// 导航器确认后自动切到该小节。
    /// </summary>
    Node? MatchGameText(PlaybackEngine owner, string text, string speaker, out string reason)
    {
        // 0.4.8：同文同角色的重复短句按当前句位置消歧；终点等待中旧末句不再作为位置依据。
        var here = GameTextPolicy.Match(owner.Pack, textSection, text, out reason, speaker, textEndWait == null ? owner.Current : null);
        if (textEndWait is not { } wait) return here;
        if (here?.Id == wait.SourceId) here = null;
        Node? next = null;
        var expected = GameEndWaitPolicy.ExpectedNext(owner.Pack, wait);
        if (wait.NextSectionId is { } nextSection)
        {
            next = GameTextPolicy.Match(owner.Pack, nextSection, text, out _, speaker);
            if (next == null && expected != null && GameTextPolicy.Normalize(expected.Text) == GameTextPolicy.Normalize(text)) next = expected;
        }
        var pick = next != null && expected != null && next.Id == expected.Id ? next : here ?? next;
        if (pick == null) { reason = wait.Hint + " · 仍在等待游戏画面定位：" + reason; ShowGameEndWaitCard(wait, null); return null; }
        if (!GameEndWaitPolicy.CanResume(owner, wait, pick, out _, out var why))
        { reason = wait.Hint + " · 读到「" + pick.Text + "」，" + why + "；继续等待。"; ShowGameEndWaitCard(wait, "读到「" + pick.Text + "」，" + why + "。"); return null; }
        if (pick.SectionId != textSection)
        {
            var chapter = owner.Pack.Chapters.First(c => c.Sections.Any(s => s.Id == pick.SectionId));
            textSection = pick.SectionId;
            if (ChapterBox.SelectedItem != chapter) ChapterBox.SelectedItem = chapter;
            SectionBox.SelectedItem = chapter.Sections.First(s => s.Id == pick.SectionId);
        }
        ClearGameEndWait(pick.Id);
        return pick;
    }
}
