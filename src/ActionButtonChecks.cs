using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunActionButtonUiTest()
    {
        var report = new List<string>();
        void Check(bool valid, string label)
        { if (!valid) throw new InvalidOperationException(label + $" [node={engine?.CurrentId}, mode={engine?.Mode}, plays={playCalls}]"); report.Add("PASS: " + label); }
        IEnumerable<Button> Buttons(DependencyObject root)
        {
            if (root is Button button) yield return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Invoke(Button button)
        {
            UpdateLayout();
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("按钮未提供调用接口：" + button.Content);
            invoke.Invoke(); await Drain();
        }
        async Task Click(string label, DependencyObject? scope = null)
        {
            UpdateLayout();
            var matches = Buttons(scope ?? this).Where(b => b.Content?.ToString() == label).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"按钮匹配数应为1：{label} / {matches.Length}");
            await Invoke(matches[0]);
        }
        async Task Scenario(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { report.Add("FAIL: " + name + " — " + ex); }
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("按钮验收只允许在隔离测试入口运行。");
            timer.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose(); game = null;
            preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            string folder = Path.Combine(Log.DataDir, "fixtures", "action-buttons"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "fixture.wav"), new byte[] { 0 });
            var pack = new Pack
            {
                Id = "action-buttons-fixture", Title = "按钮隔离验收", Root = folder,
                Chapters = new() { new() { Id = "chapter", Title = "按钮章节", Sections = new()
                    { new() { Id = "section", Title = "第一小节", StartId = "a" }, new() { Id = "other", Title = "第二小节", StartId = "c" } } } },
                Nodes = new()
                {
                    new() { Id="a", SectionId="section", Text="第一句", Audio="fixture.wav", NextId="b" },
                    new() { Id="b", SectionId="section", Text="第二句", Audio="fixture.wav", NextId="menu" },
                    new() { Id="menu", SectionId="section", Kind="choice", Text="路线菜单", Options=new()
                    { new() { Id="one", Label="路线一", TargetId="r1", PathId="one", MergeId="merge", Verified=true },
                      new() { Id="two", Label="路线二", TargetId="r2", PathId="two", MergeId="merge", Verified=true } } },
                    new() { Id="r1", SectionId="section", Text="路线一", Audio="fixture.wav", PathId="one", NextId="merge" },
                    new() { Id="r2", SectionId="section", Text="路线二", Audio="fixture.wav", PathId="two", NextId="merge" },
                    new() { Id="merge", SectionId="section", Kind="merge", NextId="tail" },
                    new() { Id="tail", SectionId="section", Text="共同线末句", Audio="fixture.wav" },
                    new() { Id="c", SectionId="other", Text="另一小节", Audio="fixture.wav" }
                }
            };
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); LoadPack(file);
            void Prepare(string node = "a") { StopListeningForGame(); engine!.Commit(node); HideBranchMenu(); Expand(StoryTab); BrowseCurrent(); }

            await Scenario("基本控制", async () =>
            {
                Expand(StoryTab); LinesList.SelectedItem = rows.Single(r => r.Node.Id == "a"); int plays = playCalls;
                await Invoke(ConfirmPlayButton);
                Check(engine!.CurrentId == "a" && engine.Mode == RunMode.Following && playCalls == plays + 1 && !expanded, "确认播放按钮提交所选台词并收起");
                Expand(StoryTab); await Click("配音下一句", PlaybackBar); Check(engine.CurrentId == "b", "下一句按钮推进一条台词");
                await Click("配音上一句", PlaybackBar); Check(engine.CurrentId == "a", "上一句按钮恢复实际访问台词");
                plays = playCalls; await Click("重播", PlaybackBar); Check(engine.CurrentId == "a" && playCalls == plays + 1, "重播按钮只重播本句");
                plays = playCalls; await Click("暂停/跟随", PlaybackBar); Check(engine.Mode == RunMode.Paused && playCalls == plays, "暂停按钮停止跟随且不额外播放");
                await Click("暂停/跟随", PlaybackBar); Check(engine.Mode == RunMode.Following && playCalls == plays, "恢复跟随按钮恢复状态且不重复发声");
                string? current = engine.CurrentId; SectionBox.SelectedIndex = 1; SearchBox.Text = "另一";
                Check(engine.CurrentId == current && playCalls == plays, "切小节与搜索只浏览，不改变游戏位置");
                await Click("回到当前句", NavigationToolbar);
                Check(SectionBox.SelectedIndex == 0 && SearchBox.Text.Length == 0 && (LinesList.SelectedItem as LineRow)?.Node.Id == current, "回到当前句按钮清搜索并返回所在小节");
            });
            await Scenario("游戏原声", async () =>
            {
                Prepare(); int plays = playCalls; await Click("游戏原声", PlaybackBar);
                Check(engine!.Mode == RunMode.Original, "游戏原声按钮进入原声模式");
                await Click("配音下一句", PlaybackBar); await Click("配音上一句", PlaybackBar); await Click("重播", PlaybackBar);
                Check(engine.CurrentId == "a" && engine.Mode == RunMode.Original && plays == playCalls, "原声模式下普通推进与重播不误播");
                await Click("游戏原声", PlaybackBar); Check(engine.Mode == RunMode.Original && expanded && resumeOriginalRequested, "再次点原声只打开续接选择，仍保持静音");
                LinesList.SelectedItem = rows.Single(r => r.Node.Id == "b"); await Invoke(ConfirmPlayButton);
                Check(engine.CurrentId == "b" && engine.Mode == RunMode.Following && playCalls == plays + 1, "明确确认续接台词后退出原声并播放");
            });
            await Scenario("历史试听与恢复", async () =>
            {
                Prepare(); engine!.Next(true); Expand(StoryTab); int plays = playCalls;
                await Invoke(HistoryButton); Check(Tabs.SelectedItem == historyTab && engine.Mode == RunMode.Paused && playCalls == plays, "历史按钮展开记录并暂停游戏配音");
                historyFilter.SelectedIndex = 0;
                historyList.SelectedItem = historyList.Items.OfType<HistoryItem>().First(i => engine.History[i.Index].NodeId == "a");
                await Click("试听本句", (DependencyObject)historyTab.Content);
                Check(historyStatus.Text.Contains("试听") && engine.CurrentId == "b" && playCalls == plays, "历史试听按钮不改变游戏位置");
                await Click("停止试听", (DependencyObject)historyTab.Content); Check(!previewingHistory && engine.CurrentId == "b", "停止试听按钮终止试听且保留位置");
                await Click("从这里继续", (DependencyObject)historyTab.Content);
                Check(engine.CurrentId == "a" && engine.Mode == RunMode.Paused && playCalls == plays, "从历史继续按钮先静音定位");
                await Invoke(UndoCorrectionButton); Check(engine.CurrentId == "b" && playCalls == plays, "撤销纠偏按钮恢复定位前状态且不发声");
            });
            await Scenario("游戏书签", async () =>
            {
                Prepare(); bookmarkNote.Text = "按钮验收书签"; int before = progressStore!.Bookmarks(pack.Id).Count;
                await Click("记住这里", NavigationToolbar); Check(progressStore.Bookmarks(pack.Id).Count == before + 1, "记住这里按钮保存命名书签");
                await Invoke(HistoryButton); historyFilter.SelectedIndex = 2;
                historyList.SelectedItem = historyList.Items.OfType<HistoryItem>().First(i => i.Bookmark?.Label == "按钮验收书签");
                await Click("删除书签", (DependencyObject)historyTab.Content); Check(progressStore.Bookmarks(pack.Id).Count == before, "删除书签按钮仅删除选中书签");
            });
            await Scenario("定位候选确认与取消", async () =>
            {
                Prepare(); Expand(LocateTab); CandidatesList.ItemsSource = new[] { new MatchCandidate(engine!.Pack.ById["b"], 1, "隔离候选", null) }; CandidatesList.SelectedIndex = 0;
                automaticPendingOwner = engine; automaticPendingDeadline = DateTime.UtcNow.AddMinutes(1);
                await Click("取消定位", (DependencyObject)LocateTab.Content);
                Check(CandidatesList.Items.Count == 0 && automaticPendingOwner == null && !ocr.Running && engine.CurrentId == "a", "取消定位按钮清候选与自动准备，不改变位置");
                CandidatesList.ItemsSource = new[] { new MatchCandidate(engine.Pack.ById["b"], 1, "隔离候选", null) }; CandidatesList.SelectedIndex = 0; await Drain();
                int plays = playCalls; await Click("确认定位并播放", (DependencyObject)LocateTab.Content);
                Check(engine.CurrentId == "b" && playCalls == plays + 1 && !expanded, "定位候选按钮确认后播放且收起");
                Expand(LocateTab); CandidatesList.ItemsSource = new[] { new MatchCandidate(engine.Pack.ById["menu"], 1, "菜单候选", null) }; CandidatesList.SelectedIndex = 0; await Drain();
                plays = playCalls; await Click("打开此分支菜单（不播放）", (DependencyObject)LocateTab.Content);
                Check(engine.CurrentId == "menu" && engine.Mode == RunMode.Choice && playCalls == plays, "菜单候选确认按钮只打开分支，不误播台词");
            });
            await Scenario("确认分支与重选", async () =>
            {
                engine!.OpenGameMenu("menu", "section"); Expand(StoryTab); BranchList.SelectedIndex = 1; int plays = playCalls;
                await Click("确认分支  ↵", BranchBox);
                Check(engine.CurrentId == "r2" && engine.Mode == RunMode.Following && playCalls == plays + 1, "确认分支按钮选择高亮路线并播放");
                Expand(StoryTab); plays = playCalls; await Invoke(ReselectButton);
                Check(engine.CurrentId == "menu" && engine.Mode == RunMode.Choice && playCalls == plays, "重选按钮恢复最近菜单并保持静音");
            });
            await Scenario("游戏按钮结束听书", async () =>
            {
                OpenListeningPack(file, "chapter"); listeningTestAudio = true; StartListening(); Expand(StoryTab);
                Check(ListeningActive && listeningRunning, "准备独立假音频听书状态");
                await Click("游戏原声", PlaybackBar);
                Check(!ListeningActive && !listeningRunning && engine!.Mode == RunMode.Original, "普通游戏原声按钮结束听书并进入原声");
            });
            Expand(StoryTab); await Drain(); Screenshot("action-buttons-ui.png");
            report.Add("INFO: 所有按钮通过 WPF UIA Invoke 接口调用；没有系统键盘/鼠标输入，没有 OCR 或实际音频。");
        }
        catch (Exception ex) { report.Add("FAIL: 初始化 — " + ex); }
        finally { listeningTestAudio = false; StopListeningForGame(); StopAutomatic("按钮隔离验收结束", false); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "action-buttons-ui-test.txt"), report); Close();
    }
}
