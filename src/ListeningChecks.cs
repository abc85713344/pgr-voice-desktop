using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    // Only used by the isolated --test-listening-ui entry point; fake audio never opens a device.
    async Task RunListeningUiTest()
    {
        var report = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        async Task DrainListeningUi() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            if (!testUi) throw new InvalidOperationException("听书检查只允许在隔离测试入口运行。");
            listeningTestAudio = true;
            string root = Path.Combine(Log.DataDir, "fixtures", "desktop-listening"); Directory.CreateDirectory(root);
            using (var wave = new BinaryWriter(File.Create(Path.Combine(root, "audio.wav"))))
            {
                wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(3236);
                wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16);
                wave.Write((short)1); wave.Write((short)1); wave.Write(8000); wave.Write(16000);
                wave.Write((short)2); wave.Write((short)16); wave.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                wave.Write(3200); wave.Write(new byte[3200]);
            }
            var pack = new Pack
            {
                Id = "desktop-listening-check", Title = "听书验收", Root = root,
                Chapters = new() { new() { Id = "book", Title = "验收大章", Sections = new()
                { new() { Id = "s1", Title = "第一小节", StartId = "a" }, new() { Id = "s2", Title = "第二小节", StartId = "c" } } },
                    new() { Id = "other-book", Title = "另一大章", Sections = new() { new() { Id = "s3", Title = "第三小节", StartId = "e" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="s1", Text="第一句", Audio="audio.wav", NextId="missing" },
                    new() { Id="missing", SectionId="s1", Text="未补配音", NextId="menu" },
                    new() { Id="menu", SectionId="s1", Kind="choice", Options=new()
                    {
                        new() { Id="one", Label="路线一", TargetId="b", PathId="one", MergeId="join", Verified=true },
                        new() { Id="two", Label="路线二", TargetId="d", PathId="two", MergeId="join", Verified=true }
                    } },
                    new() { Id="b", SectionId="s1", Text="路线一台词", PathId="one", Audio="audio.wav", NextId="join" },
                    new() { Id="d", SectionId="s1", Text="路线二台词", PathId="two", Audio="audio.wav", NextId="join" },
                    new() { Id="join", SectionId="s1", Kind="merge", NextId="z" },
                    new() { Id="z", SectionId="s1", Text="共同线", Audio="audio.wav" },
                    new() { Id="c", SectionId="s2", Text="跨小节句", Audio="audio.wav" },
                    new() { Id="e", SectionId="s3", Text="另一大章的台词", Audio="audio.wav" }
                }
            };
            pack.Validate(); string file = Path.Combine(root, "pack.json"); Json.Save(file, pack); LoadPack(file);
            engine!.Commit("menu"); engine.SelectBranch(1);
            string? gameNode = engine?.CurrentId;
            int gameHistory = engine?.History.Count ?? 0;
            string gameChoices = System.Text.Json.JsonSerializer.Serialize(engine!.Choices);
            Check(gameNode == "d" && gameHistory > 0, "游戏夹具预先选第二条路线，位置与履历非空");
            OpenListeningPack(file, "book");
            Check(listeningSession!.Policy == ListeningBranchPolicy.Manual && !listeningRunning, "新章默认手动分支，打开后保持暂停");
            Check(ListeningActive && (engine?.CurrentId == gameNode) && (engine?.History.Count ?? 0) == gameHistory, "进入听书不改变游戏台词与履历");
            StartListening(); long oldTicket = listeningTicket;
            Check(listeningRunning && listeningSession.Current?.NodeId == "a", "首句绑定独立播放票据");
            PauseListening(); OnListeningCompleted(oldTicket);
            Check(listeningSession.Current?.NodeId == "a" && !listeningRunning, "暂停后的旧自然完成回调不推进");
            listeningOffset = 1234; SaveListeningProgress();
            OpenListeningPack(file, "book");
            Check(listeningOffset == 1234 && !listeningRunning, "同章重开恢复句内断点并保持静音");
            listeningBookmarkName.Text = "验收书签"; AddListeningBookmark();
            Check(ListeningProgress!.Bookmarks.Count == 1 && ListeningProgress.Bookmarks[0].Snapshot.PositionMs == 1234, "命名书签保留本句与句内位置");
            StartListening(); long first = listeningTicket; OnListeningCompleted(first); await DrainListeningUi();
            Check(listeningSession.HasPendingChoice && !listeningRunning && listeningSkipped == 1, $"自然结束跳过缺音频，遇手动分支停止（{listeningSession.Current?.NodeId}；跳过 {listeningSkipped}；播放 {listeningRunning}）");
            Check(listeningSession.Choose("one"), "可选择有效分支"); StartListening();
            long branchTicket = listeningTicket;
            OnListeningCompleted(first); Check(listeningSession.Current?.NodeId == "b", "上一句重复完成事件不能推进新一句");
            OnListeningCompleted(branchTicket); await DrainListeningUi();
            Check(listeningSession.Current?.NodeId == "z", "分支结束进入共同线");
            OnListeningCompleted(listeningTicket); await DrainListeningUi();
            Check(listeningSession.Current?.NodeId == "c", "同大章跨小节自动连续");
            OnListeningCompleted(listeningTicket); await DrainListeningUi();
            Check(listeningSession.Completed && !listeningRunning && ListeningProgress.Resume!.Completed, "整章结束停止并保存完结位置");
            RestoreListeningBookmark();
            Check(listeningSession.Current?.NodeId == "a" && listeningOffset == 1234 && !listeningRunning, "书签恢复句内位置且不自动发声");
            listeningPolicy.SelectedIndex = 2;
            Check(listeningSession.Policy == ListeningBranchPolicy.All && listeningSession.Items.Count(i => i.Kind == ListeningItemKind.Line) == 6, "全部听取包含两条互斥路线和后续小节");
            listeningPolicy.SelectedIndex = 1;
            Check(listeningSession.Policy == ListeningBranchPolicy.First && !listeningSession.Items.Any(i => i.NodeId == "d"), "默认第一项只展开首条路线");
            StartListening(); long stoppedTicket = listeningTicket; StopListeningForGame(); OnListeningCompleted(stoppedTicket);
            Check(!ListeningActive && !listeningRunning && listeningSession.Current?.NodeId == "a", "退出听书使未到达的音频回调失效");
            long inactiveRequest = listeningRequest; var inactiveSavedAt = ListeningProgress.Resume!.UpdatedUtc;
            StopListeningForGame();
            Check(listeningRequest == inactiveRequest && ListeningProgress.Resume.UpdatedUtc == inactiveSavedAt, "听书未活动时游戏操作不重复保存或刷新听书状态");
            SetListeningVolume(.12f); SetListeningVolume(.76f);
            Check(Math.Abs(listeningAudio.Volume - .76f) < .0001f, "连续调音量以最新值为准");
            var saved = listeningStore!.Load(pack.Id);
            Check(saved.ForChapter("book").Bookmarks.Single().Label == "验收书签" && Path.GetDirectoryName(listeningStore.GetPath(pack.Id)) == Path.Combine(Log.DataDir, "listening"), "书签及自动断点写入独立听书目录");
            listeningTestAudio = false; listeningOffset = 1234; StartListening();
            var realAudioDeadline = DateTime.UtcNow.AddSeconds(15);
            while (listeningRunning && DateTime.UtcNow < realAudioDeadline) await Task.Delay(80);
            Check(listeningSession.Completed && !listeningRunning, "真实200毫秒静音音频从越界断点恢复，自然完成自动跨小节直到整章结束");
            Check(engine?.CurrentId == gameNode && (engine?.History.Count ?? 0) == gameHistory, "听书播放与恢复全程不污染游戏进度");
            Check(System.Text.Json.JsonSerializer.Serialize(engine!.Choices) == gameChoices, "听书选择另一条路线不会覆盖游戏已有的第二路线选择");
            RestoreListeningBookmark();
            listeningTestAudio = true;
            await RunListeningControlChecks(Check);
            await RunListeningInteractionChecks(Check, root);
            await RunListeningChapterSelectionChecks(Check, root);
            await RunListeningBrowseChecks(Check, root);
            listeningOptions.IsExpanded = false;
            Expand(listeningTab); await Task.Delay(80); Screenshot("听书验收.png");
            listeningOptions.IsExpanded = true;
            await DrainListeningUi(); Screenshot("听书书签验收.png");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { listeningTestAudio = false; StopListeningForGame(); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "listening-ui-test.txt"), report);
        Close();
    }

    // 使用本进程真实 WPF 控件的 UIA Invoke 和 Selector 事件，不调用相应按钮的业务方法，
    // 不移动系统鼠标、不向游戏发送按键。它验证控件接线，不替代实体输入命中测试。
    async Task RunListeningControlChecks(Action<bool, string> check)
    {
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
            var button = Elements<Button>((DependencyObject)listeningTab.Content).Single(b => b.Content?.ToString() == label);
            button.BringIntoView(); UpdateLayout();
            var invoke = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("听书按钮没有 UIA Invoke：" + label);
            invoke.Invoke(); await Drain();
        }
        void Select(Selector box, string id) => box.SelectedItem = box.Items.OfType<ListeningEntry>().Single(x => x.Id == id);
        async Task LocateLine(string section, string node)
        { Select(listeningSections, section); Select(listeningLines, node); await Click("定位到选中台词"); }
        Expand(listeningTab); await Drain();
        check(ListeningActive && !listeningRunning, "UIA检查从独立听书暂停页开始");
        await Click("恢复选中书签");
        check(listeningSession!.Current?.NodeId == "a" && listeningOffset == 1234, "UIA恢复书签按钮恢复本句及毫秒断点");
        listeningPolicy.SelectedIndex = 1;

        await Click("播放 / 续听"); long stale = listeningTicket;
        check(listeningRunning && listeningPlay.Content?.ToString() == "暂停", "UIA播放按钮启动并变成暂停按钮");
        Select(listeningSections, "s2"); Select(listeningLines, "c");
        await Click("暂停"); OnListeningCompleted(stale);
        check(!listeningRunning && listeningSession.Current?.NodeId == "a", "UIA暂停按钮使旧完成回调失效");
        check((listeningSections.SelectedItem as ListeningEntry)?.Id == "s2" && (listeningLines.SelectedItem as ListeningEntry)?.Id == "c",
            "暂停不会把已选定位小节/台词重置到当前播放句");
        await Click("定位到小节开头");
        check(listeningSession.Current?.NodeId == "c" && !listeningRunning, "UIA小节定位按钮采用用户选中小节并暂停");
        await Click("上一句"); check(listeningSession.Current?.NodeId == "z", "UIA上一句按钮能跨小节回到共同线");
        await Click("下一句"); check(listeningSession.Current?.NodeId == "c", "UIA下一句按钮能跨小节前进");
        await Click("下一句"); check(listeningSession.Completed && !listeningRunning, "UIA末句下一句保存整章完结");
        await Click("上一句"); check(listeningSession.Current?.NodeId == "c", "UIA完结后上一句能回到末句");
        await LocateLine("s1", "b");
        check(listeningSession.Current?.NodeId == "b" && !listeningRunning, "UIA台词定位按钮采用用户选中句");
        listeningOffset = 987;
        await Click("从本句开头"); check(listeningOffset == 0 && !listeningRunning, "UIA从本句开头清零断点且保持暂停");
        int originalBookmarks = ListeningProgress!.Bookmarks.Count;
        listeningBookmarkName.Text = "控件验收书签"; await Click("记下本句");
        check(ListeningProgress.Bookmarks.Count == originalBookmarks + 1 && ListeningProgress.Bookmarks.Last().Snapshot.NodeId == "b", "UIA记下本句保存命名书签");
        string addedBookmark = ListeningProgress.Bookmarks.Last().Id;
        await LocateLine("s2", "c"); Select(listeningBookmarks, addedBookmark); await Click("恢复选中书签");
        check(listeningSession.Current?.NodeId == "b" && !listeningRunning, "UIA恢复明确选中的书签，不受台词选择影响");
        Select(listeningBookmarks, addedBookmark); await Click("删除选中书签");
        check(ListeningProgress.Bookmarks.Count == originalBookmarks && !listeningStore!.Load(listeningSession.Pack.Id).ForChapter("book").Bookmarks.Any(b => b.Id == addedBookmark), "UIA删除选中书签同步更新内存与独立存档");

        listeningPolicy.SelectedIndex = 2;
        check(listeningSession.Policy == ListeningBranchPolicy.All && listeningSession.Items.Any(i => i.NodeId == "d"), "策略控件切到全部听取可定位另一互斥路线");
        await LocateLine("s1", "d"); check(listeningSession.Current?.NodeId == "d", "UIA可定位全部听取中的第二路线");
        listeningPolicy.SelectedIndex = 0;
        await LocateLine("s1", "a"); await Click("播放 / 续听"); OnListeningCompleted(listeningTicket); await Drain();
        check(listeningSession.HasPendingChoice, "手动策略显示真实分支按钮");
        await Click("路线二");
        check(listeningSession.Current?.NodeId == "d" && listeningRunning, "UIA第二路线按钮确实选择第二项并开始收听");
        Select(listeningSections, "s2"); Select(listeningLines, "c");
        OnListeningCompleted(listeningTicket); await Drain();
        check(listeningSession.Current?.NodeId == "z" && (listeningSections.SelectedItem as ListeningEntry)?.Id == "s2" && (listeningLines.SelectedItem as ListeningEntry)?.Id == "c",
            "自然换句刷新不会覆盖用户正在浏览的定位目标");
        await Click("暂停"); listeningPolicy.SelectedIndex = 1; listeningPolicy.SelectedIndex = 0;
        await LocateLine("s1", "a"); await Click("播放 / 续听"); OnListeningCompleted(listeningTicket); await Drain();
        await Click("跳过本处选择");
        check(listeningSession.Current?.NodeId == "z" && listeningRunning, "UIA跳过分支按钮进入共同线并继续收听");
        await Click("暂停");

        Select(listeningChapters, "other-book"); await Drain();
        check(listeningSession.ChapterId == "other-book" && listeningSession.Current?.NodeId == "e" && !listeningRunning && listeningLines.Items.OfType<ListeningEntry>().Single().Id == "e", "切换包内大章立即同步台词列表，无需打开按钮且保持静音");
        await Click("播放 / 续听"); long exiting = listeningTicket;
        await Click("退出听书"); OnListeningCompleted(exiting);
        check(Tabs.SelectedItem == StoryTab && !ListeningActive && !listeningRunning && listeningSession.Current?.NodeId == "e", "UIA退出听书切回剧情页并使完成回调失效");
        Tabs.SelectedItem = listeningTab; await Drain(); await Click("恢复上回章节");
        check(listeningSession.ChapterId == "other-book" && !listeningRunning, "UIA恢复上回章节恢复独立章节选择且保持暂停");
        await Click("从当前游戏章开始听");
        check(listeningSession.ChapterId == "book" && !listeningRunning, "UIA从当前游戏章开始听按游戏所在大章打开");

        listeningPolicy.SelectedIndex = 1; await LocateLine("s1", "a");
        listeningOffset = 4321; SaveListeningProgress(); listeningOffset = 5678;
        string progressPath = listeningStore!.GetPath(listeningSession.Pack.Id);
        using (var blocked = new FileStream(progressPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Select(listeningChapters, "other-book"); await Drain();
            check(listeningSession.ChapterId == "book" && listeningSession.Current?.NodeId == "a" && listeningOffset == 5678 && listeningSaveWarning.Length > 0 && (listeningChapters.SelectedItem as ListeningEntry)?.Id == "book",
                "存档被占用时UIA切章保留原会话及未写入断点，不读取旧盘档覆盖内存");
            var quit = Elements<Button>(this).Single(b => b.Content?.ToString() == "×");
            ((IInvokeProvider)new ButtonAutomationPeer(quit).GetPattern(PatternInterface.Invoke)).Invoke(); await Drain();
            check(IsLoaded && !closing && listeningSaveWarning.Length > 0, "窗口UIA关闭在存档失败时取消退出，原位置仍可恢复");
        }
        Select(listeningChapters, "other-book"); await Drain(); await Click("从当前游戏章开始听");
        check(listeningSession.ChapterId == "book" && listeningOffset == 5678 && listeningSaveWarning.Length == 0,
            "解除文件占用后切章重试能保存原断点并恢复");

        using (var blocked = new FileStream(ListeningSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Select(listeningChapters, "other-book"); await Drain();
            check(listeningSaveWarning.Length > 0, "上回章节文件写入失败显示警告");
            await Click("播放 / 续听"); await Click("暂停");
            check(listeningSaveWarning.Length > 0, "普通断点保存成功不能清掉尚未保存的上回章节警告");
        }
        SaveListeningProgress();
        check(listeningSaveWarning.Length == 0 && Json.Read<ListeningLastSelection>(ListeningSettingsFile).ChapterId == "other-book",
            "解除占用后重试同时补存上回章节并清除警告");
        listeningBookmarkName.Text = "保留旧断点时的书签"; await Click("记下本句");
        await Click("从当前游戏章开始听");
        var unresolved = listeningDocument!.ForChapter("other-book").Resume!;
        unresolved.ItemId = "removed-item"; unresolved.NodeId = "removed-node";
        SaveListeningProgress();
        Select(listeningChapters, "other-book"); await Drain();
        check(listeningPreserveResume && ListeningProgress!.Bookmarks.Count == 1, "内容变化导致旧断点无法恢复时保留旧断点及有效书签");
        using (var blocked = new FileStream(progressPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Click("删除选中书签");
            check(listeningSaveWarning.Length > 0 && ListeningProgress!.Bookmarks.Count == 0, "保留旧断点时删除书签失败仍保留可重试状态");
        }
        SaveListeningProgress();
        var preserved = listeningStore.Load(listeningSession.Pack.Id).ForChapter("other-book");
        check(listeningSaveWarning.Length == 0 && preserved.Bookmarks.Count == 0 && preserved.Resume?.NodeId == "removed-node",
            "保存重试能写入书签变化，同时不覆盖无法恢复的原断点");
        await Click("从当前游戏章开始听");
    }
    async Task RunListeningInteractionChecks(Action<bool, string> check, string root)
    {
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        async Task Invoke(Button button)
        {
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("互动选项按钮未提供 UIA Invoke。");
            provider.Invoke(); await Drain();
        }
        string originalFile = listeningFile, originalChapter = listeningSession!.ChapterId;
        string gameBefore = System.Text.Json.JsonSerializer.Serialize(engine!.ExportNavigation());
        PauseListening(); listeningOptions.IsExpanded = false;
        foreach (var policy in new[] { ListeningBranchPolicy.Manual, ListeningBranchPolicy.First, ListeningBranchPolicy.All })
        {
            var fixture = new Pack
            {
                SchemaVersion = 3, Id = "desktop-interaction-" + policy, Title = "等待互动续听验收", Root = root,
                Chapters = new() { new() { Id = "interaction-book", Title = "互动大章", Sections = new()
                    { new() { Id = "interaction-section", Title = "互动验收小节", StartId = "before" } } } },
                Nodes = new()
                {
                    new() { Id = "before", SectionId = "interaction-section", Speaker = "露西亚", Text = "必须阻止它们！", Audio = "audio.wav", NextId = "wait" },
                    new() { Id = "wait", SectionId = "interaction-section", Kind = "gap", Text = "等待游戏交互后继续", NextId = "interaction", ResumeMenuIds = new() { "interaction" } },
                    new() { Id = "interaction", SectionId = "interaction-section", Kind = "choice", MenuType = "interaction", NextId = "interaction-join", Options = new()
                    {
                        new() { Id = "ask", Label = "询问幸存者", TargetId = "ask-one", PathId = "ask", BodyVerified = true, BodyEvidence = new() { "隔离测试：显式两句正文连接" }, BoundaryId = "ask-boundary", SegmentIds = new() { "ask-one", "ask-two", "ask-boundary" } },
                        new() { Id = "inspect", Label = "与地上的构造体互动", TargetId = "inspect-one", PathId = "inspect", BodyVerified = true, BodyEvidence = new() { "隔离测试：显式两句正文连接" }, BoundaryId = "inspect-boundary", SegmentIds = new() { "inspect-one", "inspect-two", "inspect-boundary" } }
                    } },
                    new() { Id = "ask-one", SectionId = "interaction-section", PathId = "ask", Speaker = "里", Text = "请说明情况。", Audio = "audio.wav", NextId = "ask-two" },
                    new() { Id = "ask-two", SectionId = "interaction-section", PathId = "ask", Speaker = "构造体士兵", Text = "前面还有感染体。", Audio = "audio.wav", NextId = "ask-boundary" },
                    new() { Id = "ask-boundary", SectionId = "interaction-section", PathId = "ask", Kind = "gap", Text = "已审分支段落结束，请按游戏画面手动续接", NextId = "interaction-join" },
                    new() { Id = "inspect-one", SectionId = "interaction-section", PathId = "inspect", Speaker = "里", Text = "很遗憾，他……刚刚失去了意识活动……", Audio = "audio.wav", NextId = "inspect-two" },
                    new() { Id = "inspect-two", SectionId = "interaction-section", PathId = "inspect", Speaker = "露西亚", Text = "前方还有幸存者！", Audio = "audio.wav", NextId = "inspect-boundary" },
                    new() { Id = "inspect-boundary", SectionId = "interaction-section", PathId = "inspect", Kind = "gap", Text = "已审分支段落结束，请按游戏画面手动续接", NextId = "interaction-join" },
                    new() { Id = "interaction-join", SectionId = "interaction-section", Kind = "merge", NextId = "after" },
                    new() { Id = "after", SectionId = "interaction-section", Speaker = "露西亚", Text = "最后两只了！", Audio = "audio.wav" }
                }
            };
            fixture.Validate();
            string file = Path.Combine(root, "interaction-" + policy + ".json"); Json.Save(file, fixture);
            OpenListeningPack(file, "interaction-book");
            listeningPolicy.SelectedIndex = policy switch { ListeningBranchPolicy.First => 1, ListeningBranchPolicy.All => 2, _ => 0 };
            Expand(listeningTab); await Drain(); StartListening();
            long beforeTicket = listeningTicket;
            OnListeningCompleted(beforeTicket); await Drain(); UpdateLayout();
            if (policy == ListeningBranchPolicy.Manual)
            {
                check(listeningSession!.HasPendingChoice && !listeningRunning && listeningTicket == 0,
                    "手动策略遇等待互动停止，未跳过本节余下内容");
                check(listeningText.Text.StartsWith("等待互动", StringComparison.Ordinal) && listeningStatus.Text.Contains("确认后继续本节"),
                    "手动策略明确显示等待互动和选后续听提示");
                var inspectButton = listeningChoices.Children.OfType<Button>().Single(b => b.Content?.ToString() == "与地上的构造体互动");
                check(inspectButton.IsVisible && listeningCurrentView.MaxHeight == 175 && listeningCurrentView.ViewportHeight > 105,
                    "手动策略互动选项实际可见，选择区增加高度");
                string pending = listeningSession.Current!.Id;
                listeningLines.SelectedIndex = 0; inspectButton.Focus(); await Drain(); OnListeningCompleted(beforeTicket); await Drain();
                check(listeningSession.Current?.Id == pending && !listeningRunning && listeningTicket == 0,
                    "手动策略浏览台词、聚焦选项和旧音频完成事件均不播放或跳过互动");
                await Invoke(inspectButton);
                check(listeningSession.Current?.NodeId == "inspect-one" && listeningRunning && listeningTicket != 0,
                    "手动策略真实互动按钮确认后从所选段首句续听");
                long inspectTicket = listeningTicket;
                inspectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain(); OnListeningCompleted(beforeTicket); await Drain();
                check(listeningSession.Current?.NodeId == "inspect-one" && listeningRunning && listeningTicket == inspectTicket,
                    "手动策略重复旧按钮事件和旧完成事件不会重复选路或中断新音频");
                OnListeningCompleted(inspectTicket); await Drain();
                check(listeningSession.Current?.NodeId == "inspect-two" && listeningRunning,
                    "手动策略互动段按原连接续听第二句");
                OnListeningCompleted(listeningTicket); await Drain();
            }
            else
            {
                check(!listeningSession!.Items.Any(i => i.Kind == ListeningItemKind.Choice) && !listeningSession.HasPendingChoice,
                    $"{policy}策略等待互动自动展开，没有强制确认菜单");
                check(listeningSession.Current?.NodeId == "ask-one" && listeningRunning && listeningTicket != 0,
                    $"{policy}策略前句自然结束后立即播放互动首项首句");
                check(listeningChoices.Children.Count == 0 && listeningCurrentView.MaxHeight == 105,
                    $"{policy}策略不显示多余互动确认按钮，保留正常阅读高度");
                string[] expected = policy == ListeningBranchPolicy.All
                    ? new[] { "ask-one", "ask-two", "inspect-one", "inspect-two" }
                    : new[] { "ask-one", "ask-two" };
                foreach (string id in expected)
                {
                    check(listeningSession.Current?.NodeId == id && listeningRunning, $"{policy}策略按顺序自动收听：{id}");
                    long activeTicket = listeningTicket;
                    OnListeningCompleted(beforeTicket); await Drain();
                    check(listeningSession.Current?.NodeId == id && listeningTicket == activeTicket && listeningRunning,
                        $"{policy}策略旧完成回调不会重复推进：{id}");
                    OnListeningCompleted(activeTicket); await Drain();
                }
                check(policy != ListeningBranchPolicy.First || !listeningSession.Items.Any(i => i.NodeId == "inspect-one"),
                    $"{policy}策略自动展开遵循首项/全听范围");
            }
            check(listeningSession.Current?.NodeId == "after" && listeningRunning && listeningText.Text.Contains("最后两只了！"),
                $"{policy}策略互动完成后实际播放本节共同线后文");
            check(listeningCurrentView.MaxHeight == 105 && listeningLines.Items.OfType<ListeningEntry>().Any(i => i.Id == "after"),
                $"{policy}策略恢复正常台词区高度，并将共同线后文显示到列表");
            check(System.Text.Json.JsonSerializer.Serialize(engine!.ExportNavigation()) == gameBefore,
                $"{policy}策略互动听书完全保留游戏导航与分支状态");
            if (policy == ListeningBranchPolicy.Manual)
            {
                PauseListening();
                listeningSession.SetPolicy(ListeningBranchPolicy.First); listeningSession.SetPolicy(ListeningBranchPolicy.Manual);
                listeningSession.SeekSection("interaction-section"); listeningOffset = 0; StartListening();
                OnListeningCompleted(listeningTicket); await Drain(); UpdateLayout();
                Screenshot("听书互动选项.png");

                // 旧版将 All 也停在互动菜单；保留其稳定菜单编号模拟旧断点，升级后不回退到小节开头。
                var oldSnapshot = listeningSession.Capture();
                listeningPolicy.SelectedIndex = 1; await Drain();
                check(listeningSession.Policy == ListeningBranchPolicy.First && listeningSession.Current?.NodeId == "ask-one" && !listeningRunning,
                    "当前互动待选处切为默认第一项，静音定位该互动首句，不回退到小节开头");
                listeningPolicy.SelectedIndex = 2; await Drain();
                check(listeningSession.Policy == ListeningBranchPolicy.All && listeningSession.Current?.NodeId == "ask-one" && !listeningRunning,
                    "继续切为全部听取保留当前句并保持静音");
                oldSnapshot.Policy = ListeningBranchPolicy.All; oldSnapshot.Fingerprint = "listening-plan-2-old-interaction";
                PauseListening(); ListeningProgress!.Resume = oldSnapshot; listeningStore!.Save(listeningDocument!);
                listeningPreserveResume = true; // 模拟重新启动，不让本测试中的旧会话覆盖刚写入的升级前存档。
                OpenListeningPack(file, "interaction-book"); await Drain();
                check(listeningSession.Policy == ListeningBranchPolicy.All && listeningSession.Current?.NodeId == "ask-one"
                    && !listeningSession.HasPendingChoice && !listeningRunning && listeningTicket == 0 && listeningOffset == 0,
                    "旧版全听互动待选断点恢复到展开后的第一句，保持静音且不回到小节开头");
                check(!listeningText.Text.StartsWith("等待互动", StringComparison.Ordinal) && listeningText.Text.Contains("请说明情况"),
                    "旧版互动断点恢复后的界面显示实际台词，移除过时等待提示");
            }
            PauseListening();
        }
        OpenListeningPack(originalFile, originalChapter);
    }

    async Task RunListeningChapterSelectionChecks(Action<bool, string> check, string root)
    {
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var originalFile = listeningFile;
        var originalPack = listeningSession!.Pack;
        var originalNode = listeningSession.Current?.NodeId;
        listeningOffset = 2468; SaveListeningProgress();
        var second = new Pack { Id = "listening-second-pack", Title = "第18章切换验收", Chapters = new()
            { new() { Id = "book", Title = "第18章", Sections = new() { new() { Id = "other-section", Title = "18-1 目标小节", StartId = "other-line" } } } },
            Nodes = new() { new() { Id = "other-line", SectionId = "other-section", Speaker = "旁白", Text = "这是切换章节后应当立即显示的正文。", Audio = "audio.wav" } } };
        var secondFile = Path.Combine(root, "second-pack.json"); Json.Save(secondFile, second);
        var firstChoice = new PackChoice(originalFile, originalPack.Title);
        var secondChoice = new PackChoice(secondFile, second.Title);
        listeningRefreshing = true;
        listeningPacks.ItemsSource = new[] { firstChoice, secondChoice };
        listeningPacks.SelectedItem = firstChoice; listeningRefreshing = false;
        StartListening(); long oldTicket = listeningTicket;
        listeningPacks.SelectedItem = secondChoice; await Drain(); OnListeningCompleted(oldTicket);
        check(listeningSession.Pack.Id == second.Id && listeningSession.ChapterId == "book" && !listeningRunning,
            "跨配音包选章立即静音打开，相同章编号不导致旧包残留，旧回调不能推进");
        check((listeningSections.SelectedItem as ListeningEntry)?.Id == "other-section" &&
            listeningLines.Items.OfType<ListeningEntry>().Single().Id == "other-line" && listeningText.Text.Contains("切换章节后"),
            "跨包后目录、可见台词列表和当前正文全部属于新章");
        check(listeningChapters.Visibility == Visibility.Collapsed, "单章配音包隐藏重复的第二个大章框");
        listeningRefreshing = true;
        listeningPacks.ItemsSource = new[] { firstChoice, secondChoice };
        listeningPacks.SelectedItem = secondChoice; listeningRefreshing = false;
        listeningPacks.SelectedItem = firstChoice; await Drain();
        check(listeningSession.Pack.Id == originalPack.Id && listeningSession.Current?.NodeId == originalNode && listeningOffset == 2468 && !listeningRunning,
            "选回上一包恢复独立句内位置且保持静音");
        check(listeningChapters.Visibility == Visibility.Visible, "多章配音包保留包内章节选择");
        var before = listeningSession.Capture();
        listeningSections.SelectedIndex = 1; listeningLines.SelectedIndex = 0; await Drain();
        check(listeningSession.Current?.NodeId == before.NodeId && listeningOffset == 2468 && !listeningRunning,
            "浏览其他小节和台词只更新列表，不定位或播放");
        listeningOptions.IsExpanded = false; Expand(listeningTab); await Drain(); UpdateLayout();
        double originalHeight = Height;
        foreach (double height in new[] { 760d, 620d })
        {
            Height = height; ApplyCompactLayout(); UpdateLayout(); await Drain();
            var listPoint = listeningLines.TranslatePoint(new Point(), (UIElement)listeningTab.Content);
            var playPoint = listeningPlay.TranslatePoint(new Point(), (UIElement)listeningTab.Content);
            var content = (FrameworkElement)listeningTab.Content;
            Screenshot($"听书布局-{height}.png");
            check(listeningLines.ActualHeight >= 80 && listPoint.Y >= 0 &&
                playPoint.Y + listeningPlay.ActualHeight <= content.ActualHeight + 1,
                $"{height}逻辑像素窗口内台词列表直接可见、播放按钮位于可用区域（列表高{listeningLines.ActualHeight:0}）");
        }
        Height = originalHeight; ApplyCompactLayout();
        var args = Environment.GetCommandLineArgs();
        int sample = Array.IndexOf(args, "--listening-sample");
        if (sample >= 0 && sample + 1 < args.Length)
        {
            OpenListeningPack(args[sample + 1], null); Expand(listeningTab); await Drain(); Screenshot("听书实际章节.png");
            const string section = "bw-a226100500df8ada";
            if (listeningSession?.Chapter.Sections.Any(s => s.Id == section) == true)
            {
                PauseListening(); listeningSession.SetPolicy(ListeningBranchPolicy.Manual);
                listeningSession.SeekSection(section);
                for (int steps = 0; steps < 500 && listeningSession.Current?.NodeId != section + "-l0db17f8544ea05cc0cdb"; steps++)
                {
                    if (listeningSession.HasPendingChoice) listeningSession.Choose(listeningSession.Current!.Options!.First().Id);
                    else if (!listeningSession.MoveNext()) break;
                }
                check(listeningSession.SeekNode(section + "-l0db17f8544ea05cc0cdb"), "真实第一章可定位必须阻止它们");
                listeningOffset = 0; StartListening(); OnListeningCompleted(listeningTicket); await Drain();
                check(listeningSession.HasPendingChoice && listeningSession.Current!.NodeId == section + "-m002-continue" && !listeningRunning,
                    "真实第一章第一节0056后停在构造体互动，不跳节");
                Screenshot("第一章第一节-互动等待.png");
                var option = listeningChoices.Children.OfType<Button>().Single(b => b.Content?.ToString() == "（与地上的构造体互动）");
                ((IInvokeProvider)new ButtonAutomationPeer(option).GetPattern(PatternInterface.Invoke)).Invoke(); await Drain();
                foreach (string suffix in new[] { "l26856bf51b6aa7a0562e", "le3abce06639fe1f8aff7", "lb6caa0f76c95b6944422" })
                {
                    check(listeningRunning && listeningSession.Current?.NodeId == section + "-" + suffix,
                        "真实互动确认后按顺序续播：" + listeningSession.Current?.Node?.Text);
                    string? audioFile = listeningSession.Pack.ResolveAudio(listeningSession.Current!.Node!);
                    using var reader = new NAudio.Wave.AudioFileReader(audioFile ?? throw new InvalidOperationException("真实互动缺录音"));
                    check(reader.TotalTime.TotalMilliseconds > 0 && reader.Read(new byte[4096], 0, 4096) > 0,
                        "真实节点录音可以解码且非空：" + suffix);
                    if (suffix != "lb6caa0f76c95b6944422") { OnListeningCompleted(listeningTicket); await Drain(); }
                }
                check(!listeningSession.Current!.BranchLabel.Contains("补充片段"), "最后两只了属于正常共同后文，不被降为散落补充");
                check(listeningSkipped == 0, "动作提示不朗读且不误报为缺少配音");
                Screenshot("第一章第一节-继续后文.png"); PauseListening();
                await RunListeningAutomaticRealInteractionChecks(check);
            }
        }
    }

    async Task RunListeningAutomaticRealInteractionChecks(Action<bool, string> check)
    {
        const string section = "bw-ce856565aff4ef6d";
        if (listeningSession?.Chapter.Sections.Any(s => s.Id == section) != true) return;
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var pack = listeningSession.Pack; string chapter = listeningSession.ChapterId;
        foreach (var policy in new[] { ListeningBranchPolicy.First, ListeningBranchPolicy.All })
        {
            PauseListening();
            listeningSession = new ListeningSession(pack, chapter, policy); listeningOffset = 0;
            listeningBrowsingLocation = false; listeningPreserveResume = false;
            check(listeningSession.SeekSection(section) && !listeningSession.Items.Any(i => i.SectionId == section && i.Kind == ListeningItemKind.Choice),
                $"真实第一章第三节{policy}新会话展开所有互动入口，不留下强制待选点");
            RefreshListening(); StartListening();
            var visited = new List<string>();
            bool staleSafe = true;
            long stale = 0;
            for (int steps = 0; steps < 100 && listeningSession.Current?.SectionId == section && listeningRunning; steps++)
            {
                string id = listeningSession.Current.NodeId;
                visited.Add(id);
                long active = listeningTicket;
                OnListeningCompleted(stale); await Drain();
                staleSafe &= listeningSession.Current?.NodeId == id && listeningTicket == active && listeningRunning;
                if (id == section + "-l202e84a58d27fda770d3")
                { UpdateLayout(); Screenshot($"第一章第三节-{policy}-自动互动.png"); }
                OnListeningCompleted(active); await Drain(); stale = active;
            }
            string[] expected = new[] { "l202e84a58d27fda770d3", "le8cba1a75455d65a9a4b", "l7c8561423a0e8b0fb22a",
                "l35ff1c5ee2ed7e7a79c9", "l5fd6e9fb7e6dec03c529", "lf01590886b6b8e2018ed", "l8fa14bf0134b0a2b4e38" };
            check(expected.All(suffix => visited.Contains(section + "-" + suffix)),
                $"真实第一章第三节{policy}自然续播覆盖两次与丽芙互动、前往目标点及全部七句正文");
            check(visited.IndexOf(section + "-l202e84a58d27fda770d3") < visited.IndexOf(section + "-l35ff1c5ee2ed7e7a79c9")
                && visited.IndexOf(section + "-l35ff1c5ee2ed7e7a79c9") < visited.IndexOf(section + "-l5fd6e9fb7e6dec03c529"),
                $"真实第一章第三节{policy}同名丽芙互动保持第一次、目标点、第二次的原有顺序");
            check(staleSafe && visited.Count == visited.Distinct().Count(),
                $"真实第一章第三节{policy}旧完成事件不重复推进，共同台词不重复播放");
            check(!listeningSession.HasPendingChoice && (listeningSession.Completed || listeningSession.Current?.SectionId != section),
                $"真实第一章第三节{policy}不中途等待点击，完整继续到下一节或章尾");
            PauseListening();
        }
    }

}
