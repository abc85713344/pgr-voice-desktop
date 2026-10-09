using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    // Isolated UI entry only. The real chapter manifest is copied byte for byte; audio paths
    // are read from its original root and testUi prevents opening an audio device.
    async Task RunStoryTailUiTest()
    {
        var report = new List<string>();
        string? source = null, sourceHash = null;
        string realTail = ""; int realLineCount = 0;
        bool passed = false;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            report.Add("PASS: " + label);
        }
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        string Navigation() => JsonSerializer.Serialize(engine!.ExportNavigation());
        async Task Drain() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
        async Task Invoke(Button button)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Drain();
        }
        bool HasEndingNotice() => StoryContinuationNotice.Visibility == Visibility.Visible &&
            (StoryContinuationNotice.Text.Contains("没有下一句") || StoryContinuationNotice.Text.Contains("已结束"));
        void CheckNotEnded(string label) => Check(!HasEndingNotice() && NextLineButton.IsEnabled &&
            NextLineButton.Content?.ToString() == "配音下一句", label);
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            int stateAt = Array.IndexOf(args, "--test-state"), packAt = Array.IndexOf(args, "--story-tail-pack");
            Check(testUi && stateAt >= 0 && stateAt + 1 < args.Length &&
                string.Equals(Path.GetFullPath(args[stateAt + 1]), Path.GetFullPath(Log.DataDir), StringComparison.OrdinalIgnoreCase),
                "本轮只在显式隔离测试目录运行，不使用玩家真实设置与进度");
            Check(packAt >= 0 && packAt + 1 < args.Length && File.Exists(args[packAt + 1]), "已传入本次32章真实包只读来源");
            source = Path.GetFullPath(args[packAt + 1]); sourceHash = Hash(source);
            string root = Path.Combine(Log.DataDir, "fixtures", "story-tail"); Directory.CreateDirectory(root);
            string copy = Path.Combine(root, "real-ch32.json"); File.Copy(source, copy, false);
            Check(Hash(copy) == sourceHash, "真实32章清单副本逐字节一致，未改原文或路线以迎合界面");
            preferences.DialogueGuardEnabled = false;
            StopGameText("末句界面隔离检查"); StopListeningForGame(); LoadPack(copy);
            Check(engine?.Pack.Id == "pgr-ch32", "载入真实第32章副本");
            engine!.Pack.Root = Path.GetDirectoryName(source)!;
            const string section = "ch32-71fcf055b68c2cfb1350";
            var tail = engine.Pack.Nodes.Single(n => !n.Archived && n.Kind == "line" && n.SectionId == section &&
                n.Text.StartsWith("抖动的兜帽边缘模糊了嘴角的笑意", StringComparison.Ordinal));
            realTail = tail.Id;
            var expected = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section).Select(n => n.Id).OrderBy(x => x).ToArray();
            realLineCount = expected.Length;
            Check(tail.NextId != null && engine.Pack.ById[tail.NextId].Kind == "end" && StoryLinePresentation.IsExplicitEnding(engine.Pack, tail),
                "截图所指真实末句有明确同节结束连接，不把未知连接推测为结尾");
            Check(engine.ConfirmGameLine(tail.Id), "真实截图末句按现有明确定位进入，不改共享Core");
            Expand(StoryTab); SearchBox.Clear(); SearchChapterBox.IsChecked = false; showAllSectionLines.IsChecked = false; BrowseCurrent(); await Drain();
            Check(rows.Count > 0 && rows.All(r => r.Node.Kind == "line"), "默认正文目录只含台词，不再把选择点或共同线排成末句后的台词");
            Check(HasEndingNotice() && !NextLineButton.IsEnabled && NextLineButton.Content?.ToString() == "已到末句",
                "真实末句当前卡明确没有下一句，底部下一句按钮禁用并显示已到末句");
            Check(rows.Single(r => r.Node.Id == tail.Id).EndingText.Contains("没有下一句"), "真实末句行显示路线结束提示");
            string before = Navigation(); int plays = playCalls;
            showAllSectionLines.IsChecked = true; FillLines(); await Drain();
            Check(rows.Select(r => r.Node.Id).OrderBy(x => x).SequenceEqual(expected) && rows.All(r => r.Node.Kind == "line"),
                "查看全部台词保留真实本节每个正文ID，仍不混入choice/merge/gap/return/end结构");
            Check(Navigation() == before && playCalls == plays, "切换全部台词只浏览，不写当前位置或请求播放");
            SearchBox.Text = "选择路线"; SearchChapterBox.IsChecked = true; FillLines(); await Drain();
            Check(rows.All(r => r.Node.Kind == "line") && Navigation() == before && playCalls == plays,
                "全章搜索也不把历史选择点伪装成后续正文，搜索不改变导航或发声");
            SearchBox.Clear(); SearchChapterBox.IsChecked = false; showAllSectionLines.IsChecked = false; BrowseCurrent();
            var early = rows.First(r => r.Node.Id != tail.Id && r.Node.PathId.Length == 0 && r.Node.NextId != null &&
                engine.Pack.ById[r.Node.NextId].Kind == "line");
            LinesList.SelectedItem = early; await Drain();
            Check(Navigation() == before && playCalls == plays && HasEndingNotice() && !NextLineButton.IsEnabled,
                "只选看早句保留当前末句及禁用状态，不把浏览冒充当前位置");
            LinesList.SelectedItem = rows.Single(r => r.Node.Id == tail.Id); LinesList.ScrollIntoView(LinesList.SelectedItem);
            SetExpandedPanelSize(940, 800); await Drain(); Screenshot("真实32-6-末句明确结束.png");
            LinesList.SelectedItem = early; await Invoke(ConfirmPlayButton);
            Check(engine.CurrentId == early.Node.Id && playCalls == plays + 1, "明确点击确认播放早句才提交新位置并只请求一次播放");
            CheckNotEnded("确认早句后恢复下一句按钮，清除当前路线末句提示");
            string expectedNext = early.Node.NextId!; Expand(StoryTab); await Invoke(NextLineButton);
            Check(engine.CurrentId == expectedNext && playCalls == plays + 2, "普通下一句按钮仍实际推进到原Next台词");

            // Safe/partial/unverified branches are local fixtures. A literal end edge alone
            // must not override a scope boundary, pending review, or a single-line audition.
            var fixture = new Pack
            {
                Id = "story-tail-ui", Title = "末句与等待界面验收", SchemaVersion = 3, Root = root,
                Chapters = new() { new() { Id = "chapter", Title = "验收章节", Sections = new()
                    { new() { Id = "section", Title = "已核结尾与未知出口", StartId = "start" } } } },
                Nodes = new()
                {
                    new() { Id="start", SectionId="section", Speaker="旁白", Text="这一句后面确实还有台词。", NextId="menu" },
                    new() { Id="menu", SectionId="section", Kind="choice", Text="选择路线", Options=new()
                    {
                        new() { Id="a", PathId="a", Label="已核路线", TargetId="a1", BodyVerified=true, ExitVerified=true,
                            SegmentIds=new(){"a1","a2","a-end"}, LineIds=new(){"a1","a2"}, ReturnId="a-end", BodyEvidence=new(){"isolated-fixture"}, ExitEvidence=new(){"isolated-fixture"} },
                        new() { Id="b", PathId="b", Label="出口未核路线", TargetId="b1", BodyVerified=true, ExitVerified=false,
                            SegmentIds=new(){"b1"}, LineIds=new(){"b1"}, BoundaryId="b-gap", BodyEvidence=new(){"isolated-fixture"} },
                        new() { Id="c", PathId="c", Label="正文未核路线", TargetId="c1", LineIds=new(){"c1"}, Reason="等待核对游戏画面" }
                    } },
                    new() { Id="a1", SectionId="section", PathId="a", Speaker="角色甲", Text="已核路线第一句。", NextId="a2" },
                    new() { Id="a2", SectionId="section", PathId="a", Speaker="角色甲", Text="已核路线最后一句。", NextId="a-end" },
                    new() { Id="a-end", SectionId="section", PathId="a", Kind="end" },
                    new() { Id="b1", SectionId="section", PathId="b", Speaker="角色乙", Text="这个已核段落后面的连接还未核实。", NextId="end" },
                    new() { Id="b-gap", SectionId="section", PathId="b", Kind="gap", Text="待手动续接", ResumeMenuIds=new(){"menu"} },
                    new() { Id="c1", SectionId="section", PathId="c", Speaker="角色丙", Text="这里只允许确认单句。", NextId="end" },
                    new() { Id="join", SectionId="section", Kind="merge", Text="返回共同线", NextId="loose" },
                    new() { Id="loose", SectionId="section", Speaker="旁白", Text="缺少连接不能擅自说这是末句。" },
                    new() { Id="end", SectionId="section", Kind="end" }
                }
            };
            fixture.Validate(); string local = Path.Combine(root, "fixture.json"); Json.Save(local, fixture); LoadPack(local);
            engine!.Commit("start"); engine.Next(true); Expand(StoryTab); await Drain();
            Check(engine.Mode == RunMode.Choice && BranchBox.Visibility == Visibility.Visible && BranchList.Items.Count == 3,
                "结构节点退出正文后，当前分支仍在专用分支框显示全部三个选项");
            CheckNotEnded("等待选择分支不能误报路线结束");
            BranchList.SelectedIndex = 0; ConfirmBranch(); engine.Next(true); Expand(StoryTab); BrowseCurrent(); await Drain();
            Check(engine.CurrentId == "a2" && HasEndingNotice() && !NextLineButton.IsEnabled,
                "已核分支正文及出口的显式末句同样显示结束");
            plays = playCalls; Reselect(); await Drain();
            Check(engine.Mode == RunMode.Choice && engine.CurrentId == "menu" && branchMenu.IsVisible && playCalls == plays,
                "末句仍能重选最近分支，打开菜单保持静音");
            CheckNotEnded("重选回待选菜单后不保留旧末句提示");
            engine.SelectBranch(1); Expand(StoryTab); BrowseCurrent(); await Drain();
            Check(engine.CurrentId == "b1" && rows.Single(r => r.Node.Id == "b1").EndingText.Length == 0,
                "未核出口分支即使原Next写end，正文行也不标记已结束");
            CheckNotEnded("未核出口分支当前卡与下一句保持等待语义");
            await Invoke(NextLineButton); Expand(StoryTab); showAllSectionLines.IsChecked = true; BrowseCurrent(); await Drain();
            Check(engine.Mode == RunMode.Gap && engine.CurrentId == "b-gap" && rows.All(r => r.Node.Kind == "line"),
                "下一句触发原范围保护停在Gap，未知断点未被正文过滤丢失");
            CheckNotEnded("真实Gap等待不显示没有下一句或已结束，也不禁用手动续接按钮");
            Screenshot("未知出口-保持等待不报结束.png");
            plays = playCalls; OpenInteractions(); await Drain();
            Check(branchMenu.IsVisible && branchMenu.IsNavigation && branchMenu.Options.Items.Count > 0 && playCalls == plays,
                "待续接仍能由手动分支入口打开菜单目录，静音浏览不会丢失选择入口");
            HideBranchMenu(); engine.OpenGameMenu("menu", "section"); engine.SelectBranch(2);
            Check(engine.ConfirmGameLine("c1") && engine.ExportNavigation().Current.SingleLine, "正文未核路线仍允许玩家明确确认单句");
            Expand(StoryTab); await Drain(); CheckNotEnded("未核正文单句Next=end也不显示路线结束");
            engine.OpenGameMenu("menu", "section"); engine.SelectBranch(0); engine.CommitSingle("a2"); Expand(StoryTab); await Drain();
            Check(engine.ExportNavigation().Current.SingleLine, "已核末句也建立独立单句播放状态");
            CheckNotEnded("已核末句单句播放不阻止下一次返回菜单，不误报整段结束");
            await Invoke(NextLineButton); Check(engine.Mode == RunMode.Choice && engine.CurrentId == "menu", "单句播放的下一句实际返回所属菜单");
            engine.SelectBranch(0); engine.Next(true); engine.EnterOriginal(); Expand(StoryTab); await Drain();
            Check(engine.CurrentId == "a2" && engine.Mode == RunMode.Original && StateText.Text.Contains("游戏原声"),
                "当前节点为显式末句时原声状态仍保持原声提示");
            CheckNotEnded("原声状态不被末句提示或禁用按钮覆盖");
            engine.Restore("loose"); engine.Commit("loose"); Expand(StoryTab); BrowseCurrent(); await Drain();
            Check(rows.Single(r => r.Node.Id == "loose").EndingText.Length == 0, "没有Next的资料不足台词不显示静态末句标签");
            CheckNotEnded("缺失Next不被显示层自行认定为已核结尾");
            engine.Next(true); Expand(StoryTab); await Drain();
            Check(engine.Mode == RunMode.End && engine.CurrentId == "loose" && !HasEndingNotice() &&
                StoryContinuationNotice.Visibility == Visibility.Visible && StoryContinuationNotice.Text.Contains("尚未确认") &&
                NextLineButton.Content?.ToString() == "后续待确认" && !NextLineButton.IsEnabled && !StateText.Text.Contains("结束"),
                "旧Core缺失Next产生End但节点仍是正文时，显示层明确连接待确认，不冒称当前路线已结束");
            engine.OpenGameMenu("menu", "section"); engine.SelectBranch(0); engine.Next(true); engine.Next(true);
            Expand(StoryTab); await Drain();
            Check(engine.Mode == RunMode.End && engine.CurrentId == "a-end" && HasEndingNotice() &&
                !NextLineButton.IsEnabled && NextLineButton.Content?.ToString() == "已到末句",
                "实际进入明确end节点后显示路线结束，区分资料缺口造成的旧End状态");
            engine.OpenGameMenu("menu", "section"); engine.SelectBranch(0); engine.Next(true);
            Check(engine.CurrentId == "a2" && HasEndingNotice() && !NextLineButton.IsEnabled,
                "换章负例先建立当前末句与禁用下一句状态");
            fixture.Id = "story-tail-ui-new-chapter"; fixture.Title = "尚未选择起点的新章";
            string newChapter = Path.Combine(root, "new-chapter.json"); Json.Save(newChapter, fixture);
            plays = playCalls; LoadPack(newChapter); Expand(StoryTab); await Drain();
            Check(engine!.Pack.Id == fixture.Id && engine.Current == null && playCalls == plays &&
                CurrentText.Text == "请选择起始台词", "切换没有旧进度的新章清除上一章当前台词且保持未确认、静音");
            CheckNotEnded("新章尚无当前位置时清除旧末句提示，恢复下一句按钮初始状态");
            Check(Hash(source) == sourceHash && Hash(copy) == sourceHash, "所有UI检查后真实章包与隔离原样副本均保持原SHA");
            passed = true;
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            HideBranchMenu();
            if (source != null && sourceHash != null && File.Exists(source) && Hash(source) != sourceHash)
            { passed = false; report.Add("FAIL: 本轮源pack发生变化，请独立核查并保留证据。"); }
            File.WriteAllLines(Path.Combine(Log.DataDir, "story-tail-ui-test.txt"), report);
            Json.Save(Path.Combine(Log.DataDir, "story-tail-ui-result.json"), new
            {
                passed, checks = report.Count(x => x.StartsWith("PASS:", StringComparison.Ordinal)), realPack = source,
                sourceSha256 = sourceHash, realTailNode = realTail, realSectionLineCount = realLineCount,
                scope = "隔离WPF真实事件及播放请求；未操作真实游戏、未打开音频设备、未写真实玩家进度", results = report
            });
            Close();
        }
    }
}
