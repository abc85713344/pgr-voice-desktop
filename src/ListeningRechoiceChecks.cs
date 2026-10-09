using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    // 仅消费报告中冻结的反馈副本；所有保存、书签与播放票据均在独立测试目录内。
    async Task RunListeningRechoiceChecks(Action<bool, string> check)
    {
        var args = Environment.GetCommandLineArgs();
        string? Argument(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string? packFile = Argument("--listening-rechoice-pack");
        string? snapshotFile = Argument("--listening-rechoice-snapshot");
        if (packFile == null && snapshotFile == null) return;
        if (!testUi || !listeningTestAudio || packFile == null || snapshotFile == null)
            throw new InvalidOperationException("听书重选检查需要隔离测试入口、假音频和两个夹具路径。");
        string realState = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgrStoryVoice"));
        string testState = Path.GetFullPath(Log.DataDir);
        if (testState.Equals(realState, StringComparison.OrdinalIgnoreCase)
            || testState.StartsWith(realState + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("听书重选检查不得使用真实用户状态目录。");
        if (Path.GetFullPath(snapshotFile).StartsWith(realState + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请传报告中的冻结反馈副本，不能直接读取实时听书存档。");

        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        async Task Invoke(Button button)
        {
            button.BringIntoView(); UpdateLayout();
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("听书重选按钮没有 UIA Invoke。");
            provider.Invoke(); await Drain();
        }
        IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in Elements<T>(child)) yield return descendant;
            }
        }
        async Task Click(string label)
        {
            listeningOptions.IsExpanded = true; UpdateLayout();
            await Invoke(Elements<Button>((DependencyObject)listeningTab.Content).Single(b => b.Content?.ToString() == label));
        }

        const string section = "ch32-71fcf055b68c2cfb1350";
        const string menu = section + "-menu-004";
        const string choiceKey = menu + ":0";
        const string losa = section + "-58bab10fa4c29e2a9766";
        const string link = section + "-4968d11ac2fcf2b118ce";
        const string wait = section + "-ef2e53bb35a8c396c964";
        const string firstOption = section + "-route-004-00";
        const string secondOption = section + "-route-004-01";
        string originalFile = listeningFile, originalChapter = listeningSession!.ChapterId;
        double originalHeight = Height;
        string gameBefore = engine == null ? "" : JsonSerializer.Serialize(engine.ExportNavigation());
        var document = Json.Read<ListeningProgressDocument>(snapshotFile);
        var reported = document.ForChapter("ch32").Resume
            ?? throw new InvalidDataException("反馈副本缺少第32章听书断点。");
        var unaffected = reported.Choices.Where(p => !p.Key.StartsWith(section + "-menu-004:", StringComparison.Ordinal)
            && !p.Key.StartsWith(section + "-menu-005:", StringComparison.Ordinal)
            && !p.Key.StartsWith(section + "-menu-006:", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        bool PreservesOtherChoices()
        {
            var choices = listeningSession!.Capture().Choices;
            return unaffected.All(p => choices.TryGetValue(p.Key, out var value) && value == p.Value);
        }
        async Task OpenReportedChoice()
        {
            listeningSections.SelectedItem = listeningSections.Items.OfType<ListeningEntry>().Single(s => s.Id == section);
            var row = listeningLines.Items.OfType<ListeningEntry>().Single(e => e.RechoiceKey == choiceKey);
            listeningLines.SelectedItem = row; await Drain();
            check(row.IsChoice && row.Id == "choice:" + choiceKey
                && listeningLocate.Content?.ToString() == "重新选择这个分支",
                "真实反馈的已选分支保留稳定目录项，并提供明确重选按钮");
            await Invoke(listeningLocate);
        }
        async Task OpenReportedChoiceFromTop()
        {
            await Invoke(listeningRechoose);
            var menuItems = listeningRechoose.ContextMenu
                ?? throw new InvalidOperationException("顶部重选按钮未打开菜单。");
            var entry = menuItems.Items.OfType<MenuItem>().Single(i => i.Tag?.ToString() == choiceKey);
            var provider = new MenuItemAutomationPeer(entry).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("顶部重选菜单项没有 UIA Invoke。");
            provider.Invoke(); await Drain(); menuItems.IsOpen = false; await Drain();
            check(listeningSession!.HasPendingChoice && listeningSession.Current?.ChoiceKey == choiceKey && !listeningRunning,
                "顶部重选分支菜单的实际点击路由可静音重开反馈菜单");
        }
        async Task CaptureLayout(string stage)
        {
            double priorHeight = Height;
            try
            {
                listeningOptions.IsExpanded = false;
                foreach (double height in new[] { 620d, 760d })
                {
                    Height = height; ApplyCompactLayout(); UpdateLayout(); await Drain(); UpdateLayout();
                    var playTop = listeningPlay.TransformToAncestor(this).Transform(new Point(0, 0));
                    var listTop = listeningLines.TransformToAncestor(this).Transform(new Point(0, 0));
                    var rechooseTop = listeningRechoose.TransformToAncestor(this).Transform(new Point(0, 0));
                    check(listeningPlay.IsVisible && listeningPlay.ActualHeight > 0
                        && playTop.Y + listeningPlay.ActualHeight <= listTop.Y + 1,
                        $"{stage}在{height:0}高度的播放控制固定在台词列表上方");
                    check(listeningRechoose.IsVisible && listeningRechoose.ActualWidth > 0 && listeningRechoose.ActualHeight > 0
                        && rechooseTop.X >= 0 && rechooseTop.X + listeningRechoose.ActualWidth <= ActualWidth + 1
                        && rechooseTop.Y >= 0 && rechooseTop.Y + listeningRechoose.ActualHeight <= ActualHeight + 1,
                        $"{stage}在{height:0}高度的顶部重选按钮完整可见");
                    Screenshot($"听书布局-{stage}-{height:0}.png");
                }
            }
            finally { Height = priorHeight; ApplyCompactLayout(); listeningOptions.IsExpanded = false; UpdateLayout(); await Drain(); }
        }
        Button Option(string label) => listeningChoices.Children.OfType<Button>().Single(b => b.Content?.ToString() == label);

        try
        {
            check(document.PackId == "pgr-ch32" && reported.Policy == ListeningBranchPolicy.Manual
                && reported.NodeId == losa && reported.Choices.GetValueOrDefault(choiceKey) == secondOption,
                "冻结现场是手动听书，回到洛莎询问前句但已保存再等等分支");
            OpenListeningPack(packFile, "ch32"); PauseListening();
            check(listeningSession?.Pack.Id == "pgr-ch32" && listeningSession.TryRestore(reported, out _),
                "真实第32章包可在隔离会话恢复反馈断点");
            listeningDocument = document; listeningOffset = listeningSession!.ResumePositionMs;
            listeningPreserveResume = false; listeningBrowsingLocation = false; MarkExplicitListeningPosition();
            check(SaveListeningProgress(), "反馈副本只保存到隔离听书存档");
            RefreshListening(); Expand(listeningTab); await Drain();
            var point = listeningSession.ChoicePoints.Single(p => p.Item.NodeId == menu);
            check(point.IsResolved && point.SelectedOptionId == secondOption
                && point.Item.Options?.Any(o => o.Id == firstOption) == true && !listeningSession.HasPendingChoice,
                "已选菜单仍可定位，进行链接选项保留，恢复旧存档不自行要求重选");
            var reportedRows = listeningLines.Items.OfType<ListeningEntry>().ToList();
            int losaRow = reportedRows.FindIndex(e => e.Id == losa);
            int menuRow = reportedRows.FindIndex(e => e.RechoiceKey == choiceKey);
            check(losaRow >= 0 && menuRow == losaRow + 1 && menuRow + 1 < reportedRows.Count
                && reportedRows[menuRow + 1].Id == link,
                "真实32-6已选菜单紧接洛莎询问句并位于进行链接正文前，不落到配音包尾部");
            check(Path.GetFullPath(listeningStore!.GetPath("pgr-ch32")).StartsWith(testState + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "重选验收的自动断点写入独立测试目录");
            listeningBookmarkName.Text = "重选前的洛莎"; await Click("记下本句");
            string bookmarksBefore = JsonSerializer.Serialize(ListeningProgress!.Bookmarks);
            string savedBookmark = ListeningProgress.Bookmarks.Last().Id;
            check(ListeningProgress.Bookmarks.Last().Snapshot.NodeId == losa
                && ListeningProgress.Bookmarks.Last().Snapshot.Choices.GetValueOrDefault(choiceKey) == secondOption,
                "重选前书签保留洛莎位置与原再等等路线");
            listeningOffset = 2345;
            check(!listeningRunning && SaveListeningProgress(), "非零句内断点夹具在暂停状态保存为2345毫秒");
            var pausedChoices = listeningSession.Capture().Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).ToArray();
            await Invoke(listeningRechoose);
            var cancelMenu = listeningRechoose.ContextMenu
                ?? throw new InvalidOperationException("非零断点用例未打开重选菜单。");
            check(cancelMenu.IsOpen && !listeningRunning && listeningOffset == 2345
                && listeningSession.Current?.NodeId == losa,
                "暂停时打开重选菜单保留2345毫秒句内断点");
            cancelMenu.IsOpen = false; await Drain();
            var cancelledProgress = listeningStore!.Load("pgr-ch32").ForChapter("ch32");
            check(!cancelMenu.IsOpen && !listeningRunning && listeningOffset == 2345
                && cancelledProgress.Resume?.NodeId == losa && cancelledProgress.Resume.PositionMs == 2345
                && cancelledProgress.Resume.Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).SequenceEqual(pausedChoices)
                && JsonSerializer.Serialize(cancelledProgress.Bookmarks) == bookmarksBefore
                && JsonSerializer.Serialize(ListeningProgress.Bookmarks) == bookmarksBefore,
                "取消重选后内存与落盘位置保留2345毫秒、原路线和完整书签");
            listeningOffset = 0; MarkExplicitListeningPosition();
            check(SaveListeningProgress(), "非零取消用例结束后只将隔离夹具恢复为原零毫秒断点");
            await CaptureLayout("洛莎原位置");

            await Invoke(listeningPlay); long oldLineTicket = listeningTicket;
            check(listeningRunning && listeningSession.Current?.NodeId == losa, "真实反馈句可先绑定隔离播放票据");
            string[] choicesBeforeBrowse = listeningSession.Capture().Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).ToArray();
            check(listeningRechoose.IsEnabled, "反馈句顶部重选分支入口处于可用状态");
            await Invoke(listeningRechoose);
            var openedMenu = listeningRechoose.ContextMenu;
            check(openedMenu?.IsOpen == true && openedMenu.Items.OfType<MenuItem>().Any(i => i.Tag?.ToString() == choiceKey)
                && !listeningRunning && listeningSession.Current?.NodeId == losa
                && listeningSession.Capture().Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).SequenceEqual(choicesBeforeBrowse),
                "顶部入口暂停收听并列出反馈菜单，单独展开菜单不更改位置和选择");
            openedMenu!.IsOpen = false; await Drain();
            check(!openedMenu.IsOpen && !listeningRunning && listeningSession.Current?.NodeId == losa
                && listeningSession.Capture().Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).SequenceEqual(choicesBeforeBrowse),
                "关闭顶部重选菜单保持原位置与全部选择，不自动播放");
            await Invoke(listeningPlay); oldLineTicket = listeningTicket;
            listeningSections.SelectedItem = listeningSections.Items.OfType<ListeningEntry>().Single(s => s.Id == section);
            listeningLines.SelectedItem = listeningLines.Items.OfType<ListeningEntry>().Single(e => e.RechoiceKey == choiceKey);
            await Drain();
            check(listeningSession.Current?.NodeId == losa && listeningRunning && listeningTicket == oldLineTicket
                && listeningSession.Capture().Choices.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).SequenceEqual(choicesBeforeBrowse),
                "浏览已选分支目录项不改收听位置、播放票据或既有选择");
            await OpenReportedChoice(); OnListeningCompleted(oldLineTicket); await Drain();
            check(listeningSession.HasPendingChoice && listeningSession.Current?.ChoiceKey == choiceKey
                && !listeningRunning && listeningOffset == 0,
                "重选明确暂停在原菜单，旧洛莎音频完成事件不会推进");
            check(PreservesOtherChoices() && !listeningSession.Capture().Choices.ContainsKey(choiceKey)
                && !listeningSession.Capture().Choices.ContainsKey(section + "-menu-005:0"),
                "只撤销本处及后续嵌套选择，前序菜单和其它小节选择全部保留");
            check(Option("进行链接。").IsVisible && Option("再等等。").IsVisible,
                "重选时两条真实选项均实际显示可见");
            await CaptureLayout("分支待选择");
            double beforeLongOptionsHeight = Height;
            string beforeLongOptionsState = JsonSerializer.Serialize(listeningSession.Capture().Choices);
            string beforeLongOptionsBookmarks = JsonSerializer.Serialize(ListeningProgress!.Bookmarks);
            try
            {
                Height = 620; ApplyCompactLayout(); listeningOptions.IsExpanded = false;
                var menuButtons = listeningChoices.Children.OfType<Button>().ToArray();
                string longLabel = string.Concat(Enumerable.Repeat("这是仅用于检查小窗口自动换行与滚动的很长选项，完整正文应当能够在选项区域中阅读。", 7));
                foreach (var button in menuButtons.Take(menuButtons.Length - 1)) button.Content = longLabel;
                UpdateLayout(); await Drain(); UpdateLayout();
                var wrapped = Elements<TextBlock>(menuButtons[0]).Single(t => t.Text == longLabel);
                check(wrapped.TextWrapping == TextWrapping.Wrap && wrapped.ActualHeight > wrapped.FontSize * 2
                    && wrapped.ActualWidth <= menuButtons[0].ActualWidth && listeningCurrentView.ScrollableHeight > 0,
                    "620高度的长选项实际按按钮宽度换行，超出内容使用选项区内部滚动");
                listeningCurrentView.ScrollToEnd(); UpdateLayout(); await Drain(); UpdateLayout();
                var finalButton = menuButtons.Last();
                var finalTop = finalButton.TransformToAncestor(listeningCurrentView).Transform(new Point(0, 0));
                var playTop = listeningPlay.TransformToAncestor(this).Transform(new Point(0, 0));
                var listTop = listeningLines.TransformToAncestor(this).Transform(new Point(0, 0));
                check(finalButton.IsVisible && finalButton.ActualHeight > 0 && finalTop.Y >= -1
                    && finalTop.Y + finalButton.ActualHeight <= listeningCurrentView.ActualHeight + 1,
                    "620高度的长选项可滚到底并完整看到最后的跳过按钮");
                check(listeningPlay.IsEnabled && listeningPlay.IsVisible && playTop.Y >= 0
                    && playTop.Y + listeningPlay.ActualHeight <= listTop.Y + 1,
                    "长选项滚动不挤走或禁用页面播放控件");
                check(listeningSession.HasPendingChoice && listeningSession.Current?.ChoiceKey == choiceKey && !listeningRunning
                    && JsonSerializer.Serialize(listeningSession.Capture().Choices) == beforeLongOptionsState
                    && JsonSerializer.Serialize(ListeningProgress.Bookmarks) == beforeLongOptionsBookmarks
                    && listeningSession.Pack.ById[menu].Options.All(o => o.Label != longLabel),
                    "长文字仅替换测试控件内容，不确认路线、不改配音包或书签");
                Screenshot("听书布局-长选项滚到底-620.png");
            }
            finally
            {
                Height = beforeLongOptionsHeight; ApplyCompactLayout(); listeningOptions.IsExpanded = false;
                RefreshListening(); UpdateLayout(); await Drain();
            }
            listeningOptions.IsExpanded = false; UpdateLayout(); Screenshot("听书重选-洛莎选项.png");
            var oldFirstButton = Option("进行链接。");
            var oldSecondButton = Option("再等等。");
            await Invoke(oldFirstButton); long firstTicket = listeningTicket;
            check(listeningSession.Current?.NodeId == link && listeningRunning && firstTicket > 0
                && listeningSession.Capture().Choices.GetValueOrDefault(choiceKey) == firstOption,
                "改选进行链接后准确播放该选项首句，写入新的听书选择");
            oldSecondButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            oldFirstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); OnListeningCompleted(oldLineTicket); await Drain();
            check(listeningSession.Current?.NodeId == link && listeningRunning && listeningTicket == firstTicket,
                "旧菜单按钮重复点击和旧音频完成事件都不覆盖已确认的新路线");

            await OpenReportedChoiceFromTop();
            oldFirstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            oldSecondButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); OnListeningCompleted(firstTicket); await Drain();
            check(listeningSession.HasPendingChoice && listeningSession.Current?.ChoiceKey == choiceKey && !listeningRunning,
                "同一会话再次重开相同菜单时，上一轮按钮及音频票据仍彻底失效");
            await Invoke(Option("进行链接。"));
            check(PauseListening() && SaveListeningProgress(), "新进行链接路线与暂停位置可以保存");
            OpenListeningPack(packFile, "ch32"); await Drain();
            check(listeningSession!.Current?.NodeId == link && !listeningRunning
                && listeningSession.Capture().Choices.GetValueOrDefault(choiceKey) == firstOption,
                "同章重新打开精确恢复进行链接的新选择，并保持暂停");
            check(PreservesOtherChoices() && JsonSerializer.Serialize(ListeningProgress!.Bookmarks) == bookmarksBefore,
                "保存重开后前序与其它节选择不变，已有书签完整保留");
            var bookmarkProbe = new ListeningSession(listeningSession.Pack, "ch32", ListeningBranchPolicy.Manual);
            check(bookmarkProbe.TryRestore(ListeningProgress.Bookmarks.Single(b => b.Id == savedBookmark).Snapshot, out _)
                && bookmarkProbe.Current?.NodeId == losa && bookmarkProbe.Capture().Choices.GetValueOrDefault(choiceKey) == secondOption
                && listeningSession.Current?.NodeId == link,
                "旧书签仍能独立恢复原路线，检查书签不会回退当前新选择");

            await OpenReportedChoice();
            await Invoke(Option("再等等。"));
            check(listeningSession.Current?.NodeId == wait && listeningRunning
                && listeningSession.Capture().Choices.GetValueOrDefault(choiceKey) == secondOption,
                "保存重开后还可再次改回再等等，并准确播放其首句");
            check(!listeningSession.Capture().Choices.ContainsKey(section + "-menu-005:0") && PreservesOtherChoices(),
                "改回旧路线不会复活已撤销的嵌套选项，也不清空其它选择");
            check(JsonSerializer.Serialize(ListeningProgress!.Bookmarks) == bookmarksBefore
                && (engine == null || JsonSerializer.Serialize(engine.ExportNavigation()) == gameBefore),
                "真实反馈的浏览、重选、保存重开全程保留书签与游戏导航");
            PauseListening(); listeningOptions.IsExpanded = false; UpdateLayout(); Screenshot("听书重选-重新收听.png");
        }
        finally
        {
            if (listeningRechoose.ContextMenu != null) listeningRechoose.ContextMenu.IsOpen = false;
            Height = originalHeight; listeningOptions.IsExpanded = false;
            PauseListening(); OpenListeningPack(originalFile, originalChapter);
        }
    }
}
