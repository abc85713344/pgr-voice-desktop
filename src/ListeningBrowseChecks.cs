using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    // 只由隔离验收入口调用；试听使用假音频票据，不向游戏发送输入。
    async Task RunListeningBrowseChecks(Action<bool, string> check, string root)
    {
        if (!testUi || !listeningTestAudio) throw new InvalidOperationException("听书目录验收只能使用隔离假音频入口。");
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        async Task Invoke(Button button)
        {
            button.BringIntoView(); UpdateLayout();
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("听书目录按钮没有 UIA Invoke。");
            provider.Invoke(); await Drain();
        }
        void Browse(string section, string? line = null)
        {
            listeningSections.SelectedItem = listeningSections.Items.OfType<ListeningEntry>().Single(s => s.Id == section);
            if (line != null) listeningLines.SelectedItem = listeningLines.Items.OfType<ListeningEntry>().Single(s => s.Id == line);
        }
        string originalFile = listeningFile, originalChapter = listeningSession!.ChapterId;
        string gameBefore = engine == null ? "" : System.Text.Json.JsonSerializer.Serialize(engine.ExportNavigation());
        var fixture = new Pack
        {
            Id = "desktop-listening-full-directory", Title = "完整正文目录验收", Root = root,
            Chapters = new() { new() { Id = "browse-book", Title = "目录验收大章", Sections = new()
            {
                new() { Id = "browse-one", Title = "第一节互动", StartId = "browse-before" },
                new() { Id = "browse-two", Title = "第二节收听", StartId = "browse-current" },
                new() { Id = "browse-three", Title = "首项分支", StartId = "browse-first-menu" },
                new() { Id = "browse-four", Title = "未知正文边界", StartId = "browse-known" }
            } } },
            Nodes = new()
            {
                new() { Id = "browse-before", SectionId = "browse-one", Speaker = "露西亚", Text = "必须阻止它们！", Audio = "audio.wav", NextId = "browse-wait" },
                new() { Id = "browse-wait", SectionId = "browse-one", Kind = "gap", Text = "等待游戏交互后继续", NextId = "browse-menu", ResumeMenuIds = new() { "browse-menu" } },
                new() { Id = "browse-menu", SectionId = "browse-one", Kind = "choice", MenuType = "interaction", NextId = "browse-join", Options = new()
                {
                    new() { Id = "browse-inspect", Label = "与地上的构造体互动", TargetId = "browse-inspect-one", PathId = "browse-inspect", MergeId = "browse-join", Verified = true },
                    new() { Id = "browse-ask", Label = "询问附近的人", TargetId = "browse-ask-line", PathId = "browse-ask", MergeId = "browse-join", Verified = true }
                } },
                new() { Id = "browse-inspect-one", SectionId = "browse-one", Speaker = "里", Text = "很遗憾，他刚刚失去了意识活动。", PathId = "browse-inspect", Audio = "audio.wav", NextId = "browse-inspect-two" },
                new() { Id = "browse-inspect-two", SectionId = "browse-one", Speaker = "露西亚", Text = "前方还有幸存者！", PathId = "browse-inspect", Audio = "audio.wav", NextId = "browse-join" },
                new() { Id = "browse-ask-line", SectionId = "browse-one", Speaker = "里", Text = "另一条尚未选择的路线。", PathId = "browse-ask", Audio = "audio.wav", NextId = "browse-join" },
                new() { Id = "browse-join", SectionId = "browse-one", Kind = "merge", NextId = "browse-after" },
                new() { Id = "browse-after", SectionId = "browse-one", Speaker = "露西亚", Text = "最后两只了！", Audio = "audio.wav", NextId = "browse-tail" },
                new() { Id = "browse-tail", SectionId = "browse-one", Speaker = "构造体士兵", Text = "在那之前，还有一件事……", Audio = "audio.wav" },
                new() { Id = "browse-archived", SectionId = "browse-one", Archived = true, Text = "已归档旧正文，不应进入目录。" },
                new() { Id = "browse-current", SectionId = "browse-two", Speaker = "里", Text = "第二节正在收听的句子。", Audio = "audio.wav" },
                new() { Id = "browse-first-menu", SectionId = "browse-three", Kind = "choice", Options = new()
                { new() { Id = "browse-first-option", Label = "首项菜单选项", TargetId = "browse-first-line", PathId = "browse-first-option", MergeId = "browse-first-merge", Verified = true } } },
                new() { Id = "browse-first-line", SectionId = "browse-three", PathId = "browse-first-option", Text = "首项菜单后的正文。", Audio = "audio.wav", NextId = "browse-first-merge" },
                new() { Id = "browse-first-merge", SectionId = "browse-three", Kind = "merge" },
                new() { Id = "browse-known", SectionId = "browse-four", Text = "未知边界之前的可靠正文。", Audio = "audio.wav", NextId = "browse-unknown" },
                new() { Id = "browse-unknown", SectionId = "browse-four", Kind = "gap", Text = "正文连接未核实", NextId = "browse-unlinked" },
                new() { Id = "browse-unlinked", SectionId = "browse-four", Text = "已有录音，但没有可靠连接的正文。", Audio = "audio.wav" }
            }
        };
        fixture.Validate();
        string file = Path.Combine(root, "full-listening-directory.json"); Json.Save(file, fixture);
        try
        {
            OpenListeningPack(file, "browse-book"); Expand(listeningTab); listeningOptions.IsExpanded = false;
            check(listeningSession!.SeekNode("browse-current"), "目录验收预先定位第二节，模拟截图中的收听位置");
            listeningOffset = 3210; RefreshListening(); SaveListeningProgress();
            Browse("browse-one", "browse-after"); await Drain();
            var entries = listeningLines.Items.OfType<ListeningEntry>().ToArray();
            check(entries.Count(e => !e.IsChoice) == 6 && entries.All(e => e.Id != "browse-archived")
                && entries.Any(e => e.Id == "browse-after" && e.IsPreview) && entries.Any(e => e.Id == "browse-tail" && e.IsPreview),
                "完整目录显示互动之后的全部六句正文、标出未展开预览，并排除已归档台词");
            var pending = listeningSession.Items.Single(i => i.SectionId == "browse-one" && i.Kind == ListeningItemKind.Choice);
            check(entries.Any(e => e.IsChoice && e.Id == pending.Id && e.ItemId == pending.Id)
                && listeningLineCount.Text.Contains("6") && listeningLineCount.Text.Contains("待"),
                "目录保留有稳定项目编号的待选互动，计数包含后文并说明等待选择");
            check(listeningSession.Current?.NodeId == "browse-current" && listeningOffset == 3210 && !listeningRunning
                && listeningBrowseHint.Text.Contains("第一节互动") && listeningBrowseHint.Text.Contains("第二节收听")
                && listeningPendingChoice.Visibility == Visibility.Visible,
                "浏览第一节不会改动第二节收听断点，并明确显示两处位置及待选互动按钮");
            RefreshListening(); await Drain();
            check((listeningSections.SelectedItem as ListeningEntry)?.Id == "browse-one"
                && (listeningLines.SelectedItem as ListeningEntry)?.Id == "browse-after",
                "普通刷新不会把正在浏览的小节与后文选择重置为收听位置");

            StartListening(); long previousTicket = listeningTicket;
            Browse("browse-one", "browse-after"); SaveListeningProgress();
            string progressPath = listeningStore!.GetPath(fixture.Id);
            using (var locked = new FileStream(progressPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Invoke(listeningPendingChoice);
                check(listeningSession.Current?.NodeId == "browse-current" && listeningOffset == 3210 && !listeningRunning
                    && listeningSaveWarning.Length > 0,
                    "断点保存失败时打开待选互动保留原位置与句内断点，不读旧存档覆盖内存");
            }
            await Invoke(listeningPendingChoice); OnListeningCompleted(previousTicket); await Drain();
            check(listeningSession.Current?.Id == pending.Id && listeningSession.HasPendingChoice && !listeningRunning
                && listeningChoices.Children.OfType<Button>().Any(b => b.Content?.ToString() == "与地上的构造体互动"),
                "UIA待选互动按钮静音打开第一节真实选项，第二节的旧完成回调不推进");

            listeningSession.SeekNode("browse-current"); listeningOffset = 0; listeningBrowsingLocation = false; RefreshListening();
            Browse("browse-one", pending.Id); await Drain();
            check(listeningLocate.Content?.ToString() == "打开选中互动 / 分支" && !listeningRunning,
                "选中目录里的互动项目只更新定位按钮，不直接进入或播放");
            await Invoke(listeningLocate);
            check(listeningSession.Current?.Id == pending.Id && !listeningRunning,
                "UIA目录互动定位按钮按项目编号静音打开相同待选菜单");
            listeningSession.SeekNode("browse-current"); listeningBrowsingLocation = false; RefreshListening();
            Browse("browse-one", "browse-tail"); await Invoke(listeningLocate);
            check(listeningSession.Current?.NodeId == "browse-current" && !listeningRunning && listeningPreviewNode?.Id == "browse-tail"
                && !listeningSession.Items.Any(i => i.NodeId == "browse-tail"),
                "未接入的后文可显式单句试听，原收听位置和未选菜单不改变");
            await Invoke(listeningPendingChoice);
            var optionButton = listeningChoices.Children.OfType<Button>().Single(b => b.Content?.ToString() == "与地上的构造体互动");
            await Invoke(optionButton); long selectedTicket = listeningTicket;
            optionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); OnListeningCompleted(previousTicket); await Drain();
            check(listeningSession.Current?.NodeId == "browse-inspect-one" && listeningRunning && listeningTicket == selectedTicket
                && listeningLines.Items.OfType<ListeningEntry>().Any(e => e.Id == "browse-after" && !e.IsPreview),
                "确认互动才播放首句并展开共同后文，重复旧按钮与旧完成回调不重复选择");
            OnListeningCompleted(selectedTicket); await Drain(); OnListeningCompleted(listeningTicket); await Drain();
            check(listeningSession.Current?.NodeId == "browse-after" && listeningRunning,
                "通过完整目录进入互动后自然续播到最后两只了，不跳到第二节");
            PauseListening(); Browse("browse-two"); await Drain();
            check(listeningPendingChoice.Visibility != Visibility.Visible,
                "浏览没有待选菜单的小节时隐藏待选互动入口");
            Browse("browse-three"); await Drain();
            var firstChoice = listeningLines.Items.OfType<ListeningEntry>().First();
            check(firstChoice.IsChoice && listeningLines.Items.OfType<ListeningEntry>().Any(e => e.Id == "browse-first-line" && e.IsPreview),
                "首项就是分支的小节也显示菜单及后文预览，不呈现空目录");
            listeningLines.SelectedItem = firstChoice; await Invoke(listeningLocate);
            check(listeningSession.Current?.NodeId == "browse-first-menu" && !listeningRunning
                && listeningChoices.Children.OfType<Button>().Any(b => b.Content?.ToString() == "首项菜单选项"),
                "普通分支与等待互动共用静音定位入口，首项菜单可以正常打开");
            string beforeUnknown = listeningSession.Current!.Id;
            Browse("browse-four", "browse-unlinked"); await Invoke(listeningLocate);
            check(listeningSession.Current?.Id == beforeUnknown && !listeningRunning && listeningPreviewNode?.Id == "browse-unlinked"
                && listeningStatus.Text.Contains("仅试听"),
                "没有待选菜单的未接入正文也可单句试听，保留原路线位置且不连续推进");
            check(engine == null || System.Text.Json.JsonSerializer.Serialize(engine.ExportNavigation()) == gameBefore,
                "完整目录浏览、互动确认及后文续听都不改变游戏路线进度");

            var args = Environment.GetCommandLineArgs(); int sampleIndex = Array.IndexOf(args, "--listening-sample");
            if (sampleIndex >= 0 && sampleIndex + 1 < args.Length)
            {
                OpenListeningPack(args[sampleIndex + 1], null);
                const string section = "bw-a226100500df8ada";
                if (listeningSession?.Chapter.Sections.Any(s => s.Id == section) == true)
                {
                    PauseListening();
                    listeningSession = new ListeningSession(listeningSession.Pack, listeningSession.ChapterId, ListeningBranchPolicy.Manual);
                    listeningSession.SeekSection(section);
                    for (int steps = 0; steps < 500 && listeningSession.Current?.NodeId != section + "-m002-continue"; steps++)
                    {
                        if (listeningSession.HasPendingChoice) listeningSession.Choose(listeningSession.Current!.Options!.First().Id);
                        else if (!listeningSession.MoveNext()) break;
                    }
                    check(listeningSession.HasPendingChoice && listeningSession.Current?.NodeId == section + "-m002-continue",
                        "真实目录手动用例已确认前序分支，只在尚未确认的构造体互动处待选");
                    string secondSection = listeningSession.Chapter.Sections.Skip(1).First().Id;
                    check(listeningSession.SeekSection(secondSection), "真实第一章可以从第二节收听位置复现目录问题");
                    listeningBrowsingLocation = false; RefreshListening(); Browse(section); await Drain();
                    var actualRows = listeningLines.Items.OfType<ListeningEntry>().ToArray();
                    int expected = listeningSession.Pack.Nodes.Count(n => n.SectionId == section && n.Kind == "line" && !n.Archived);
                    check(actualRows.Count(e => !e.IsChoice) == expected
                        && actualRows.Any(e => e.Label.Contains("最后两只了"))
                        && actualRows.Any(e => e.Label.Contains("在那之前，还有一件事")),
                        $"真实第一节目录显示全部 {expected} 句，包含最后两只了及在那之前还有一件事");
                    Browse(section, section + "-lb6caa0f76c95b6944422");
                    listeningLines.ScrollIntoView(listeningLines.SelectedItem); UpdateLayout(); await Drain();
                    Screenshot("第一章第一节-完整后文目录.png");
                    Browse(section, section + "-l069d916ba7651e9ad83b");
                    listeningLines.ScrollIntoView(listeningLines.SelectedItem); UpdateLayout(); await Drain();
                    check(listeningLines.ItemContainerGenerator.ContainerFromItem(listeningLines.SelectedItem) is ListBoxItem { IsVisible: true },
                        "第一节末句可实际滚动到视口，完整后文不只是后台数据");
                    Screenshot("第一章第一节-后文末尾.png");
                    await Invoke(listeningPendingChoice);
                    check(listeningSession.Current?.NodeId == section + "-m002-continue" && !listeningRunning
                        && listeningChoices.Children.OfType<Button>().Any(b => b.Content?.ToString() == "（与地上的构造体互动）"),
                        "真实第一章从第二节浏览第一节后可静音打开构造体互动，不必倒放此前句子");
                    Screenshot("第一章第一节-目录打开互动.png");
                }
            }
        }
        finally
        {
            PauseListening(); OpenListeningPack(originalFile, originalChapter);
        }
    }
}
