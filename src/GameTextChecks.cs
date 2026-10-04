using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunGameTextUiTest()
    {
        var report = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); report.Add("PASS: " + name); }
        try
        {
            if (engine == null) throw new Exception("需要现有配音包作为只读夹具");
            var owner = engine;
            int initialPlays = playCalls;
            textProbes[1].Anchor.Text = "旧句遗漏了标点";
            BeginTextMonitoring();
            Check(textArmed && textProbes.All(p => p.AutoConnect) && textProbes.All(p => p.Reader == null) && playCalls == initialPlays,
                "尚无对白时也能一次开启两路监听，无需填写正文且不误播");
            Check(!textProbes[1].ManualPanel.IsExpanded, "手动填字入口默认收起，仅作为备用");
            var oldGame = game;
            game = new GameWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, "隔离测试窗口", "PgrVoice");
            try
            {
                foreach (string stopAction in new[] { "pause", "disconnect", "manual" })
                {
                    BeginTextMonitoring();
                    var completion = new TaskCompletionSource<GameTextReader>(TaskCreationOptions.RunContinuationsAsynchronously);
                    System.Threading.CancellationToken pendingToken = default;
                    textDiscoveryTest = (_, _, token) => { pendingToken = token; return completion.Task; };
                    var pending = RefreshTextConnection(textProbes[1]);
                    Check(textDiscoveryBusy && textProbes[1].Binding != null, "后台寻找与界面等待分离：" + stopAction);
                    if (stopAction == "pause") PauseTextPlayback("隔离测试暂停");
                    else if (stopAction == "disconnect") StopGameText("隔离测试断开");
                    else PrepareGamePlaybackAction();
                    var late = new GameTextReader(Environment.ProcessId); completion.SetResult(late); await pending;
                    Check(pendingToken.IsCancellationRequested && !textArmed && textProbes.All(p => !p.AutoConnect) &&
                        textProbes[1].Reader == null && !textDiscoveryBusy && playCalls == initialPlays,
                        "用户停止后迟到的扫描结果不能恢复连接或播放：" + stopAction);
                }
                BeginTextMonitoring();
                var continuing = textProbes[1]; continuing.Observed = continuing.Pending = "正在播放的同一句。";
                continuing.ObservedSpeaker = continuing.PendingSpeaker = "测试角色"; continuing.Active = true;
                textDiscoveryTest = (_, _, _) => Task.FromResult(new GameTextReader(Environment.ProcessId));
                await RefreshTextConnection(continuing);
                Check(continuing.Active && continuing.Observed == "正在播放的同一句。" && continuing.Pending == continuing.Observed &&
                    continuing.ObservedSpeaker == "测试角色" && continuing.Reader != null && textArmed && playCalls == initialPlays,
                    "后台重找完成保留当前句观察状态，不把同一句变成换句而截断配音");
            }
            finally { game = oldGame; textDiscoveryTest = null; StopGameText("隔离监听检查结束"); }
            int browseCalls = playCalls; string? browseNode = owner.CurrentId;
            gameTextSection.SelectedIndex = 1;
            Check(SectionBox.SelectedIndex == 1 && owner.CurrentId == browseNode && playCalls == browseCalls, "游戏配音页选小节与台词页同步，仅浏览不误播");
            SectionBox.SelectedIndex = 0;
            Check(gameTextSection.SelectedIndex == 0, "台词页改变小节会同步回游戏配音页，定位范围一致");
            var branch = owner.Pack.Nodes.First(n => n.Kind == "line" && !n.Archived && n.PathId.Length > 0 && n.Text.Length > 8 &&
                GameTextPolicy.Match(owner.Pack, n.SectionId, n.Text, out _)?.Id == n.Id && owner.Pack.ResolveAudio(n) != null && owner.Pack.AudioNotice(n).Length == 0);
            void Arm() { textOwner = owner; textSection = branch.SectionId; textArmed = true; lastTextPlayed = ""; }
            SectionBox.SelectedItem = SectionBox.Items.OfType<Section>().First(s => s.Id == branch.SectionId);
            Arm(); textAutoBox.IsChecked = false;
            var backgroundSample = new GameTextSample(true, branch.Text, true, Active: true, Speaker: branch.Speaker);
            gameTextSampleTest = p => p == textProbes[0] ? backgroundSample : new(true, "", false, Active: false);
            expanded = true;
            int backgroundBefore = playCalls;
            void SampleAndPlay()
            {
                TickGameText();
                textProbes[0].PendingSince -= Stopwatch.Frequency;
                TickGameText();
            }
            SampleAndPlay();
            Check(textArmed && owner.CurrentId == branch.Id && playCalls == backgroundBefore + 1, "游戏不在前台且面板展开时，实际采样流程仍提交对应配音");
            long backgroundTicket = audioRequest;
            HandleMouseGlobal(new("鼠标左键", IntPtr.Zero, 0, 0, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp()));
            HandleGlobal(Key.F8, IntPtr.Zero);
            Check(textArmed && audioRequest == backgroundTicket && owner.CurrentId == branch.Id, "其他窗口的鼠标和快捷键不停止当前配音或改动进度");
            var backgroundNext = owner.Pack.Nodes.First(n => n.Kind == "line" && n.SectionId == branch.SectionId && n.Id != branch.Id &&
                GameTextPolicy.Match(owner.Pack, n.SectionId, n.Text, out _, n.Speaker)?.Id == n.Id && owner.Pack.ResolveAudio(n) != null && owner.Pack.AudioNotice(n).Length == 0);
            backgroundSample = new(true, backgroundNext.Text, true, Active: true, Speaker: backgroundNext.Speaker);
            SampleAndPlay();
            Check(owner.CurrentId == backgroundNext.Id && playCalls == backgroundBefore + 2 && textArmed, "后台游戏换句后只播放新句，无需重新定位");
            foreach (int index in new[] { 1, 2, 3, 0 })
            {
                followModeTabs.SelectedIndex = index; UpdateLayout();
                Check(owner.CurrentId == backgroundNext.Id && playCalls == backgroundBefore + 2 && textArmed,
                    "查看跟随选项不切换运行方式或误播：" + ((TabItem)followModeTabs.Items[index]).Header);
            }
            PauseTextPlayback("验收手动暂停");
            backgroundSample = new(true, branch.Text, true, Active: true, Speaker: branch.Speaker); SampleAndPlay();
            Check(!textArmed && playCalls == backgroundBefore + 2, "后台跟随仍尊重用户主动暂停");
            Check(!ocr.Running, "后台文字跟随不依赖截图或 OCR");
            gameTextSampleTest = null;
            Arm(); int before = playCalls;
            preferences.DialogueGuardEnabled = true; previousDialogueMode = RunMode.Choice;
            CommitMemoryGameText(textProbes[0], owner, branch.Text);
            Check(owner.CurrentId == branch.Id && playCalls == before + 1 && !dialogueHeld, "普通文本确认分支正文，实际播放入口恰好调用一次");
            CommitMemoryGameText(textProbes[0], owner, branch.Text);
            CommitMemoryGameText(textProbes[1], owner, branch.Text);
            Check(playCalls == before + 1, "同句重复采样与另一路残留都不重复播放");
            RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Check(owner.CurrentId == branch.Id && playCalls == before + 1, "文本跟随开启时键盘推进不额外播放");
            Arm(); before = playCalls; CommitMemoryGameText(textProbes[1], owner, branch.Text);
            Check(playCalls == before + 1, "3D 路径复用相同的配音匹配入口");
            PrepareGamePlaybackAction(); before = playCalls;
            CommitMemoryGameText(textProbes[0], owner, branch.Text);
            Check(!textArmed && playCalls == before, "手动操作暂停文本配音，旧结果不能恢复播放");
            Arm(); textOwner = new PlaybackEngine(owner.Pack);
            CommitMemoryGameText(textProbes[0], owner, branch.Text);
            Check(playCalls == before, "过期配音包所有者不能提交结果");
            Arm(); CommitMemoryGameText(textProbes[0], owner, "这是一句配音包里不存在的测试台词。");
            Check(textArmed && playCalls == before, "未知正文不猜测且继续监听");
            var duplicate = new Node { Id = "game-text-duplicate-fixture", Kind = "line", SectionId = branch.SectionId, Text = branch.Text };
            owner.Pack.Nodes.Add(duplicate);
            try { Arm(); CommitMemoryGameText(textProbes[0], owner, branch.Text); Check(textArmed && playCalls == before, "多处分支同文仅跳过本句，不停整段追踪"); }
            finally { owner.Pack.Nodes.Remove(duplicate); }
            string? audioFile = branch.Audio;
            try { branch.Audio = null; Arm(); CommitMemoryGameText(textProbes[0], owner, branch.Text); Check(textArmed && playCalls == before, "缺少录音仍继续监听，不触发播放"); }
            finally { branch.Audio = audioFile; }
            CommitMemoryGameText(textProbes[0], owner, branch.Text);
            Check(textArmed && playCalls == before + 1, "缺音恢复后无需重新定位就能继续播放");
            Check(textProbes.Length == 2 && textProbes[0].Kind == GameTextKind.Ordinary && textProbes[1].Kind == GameTextKind.Scene3D, "普通与 3D 有独立的连接与状态");
            var probe = textProbes[0]; long stamp = Stopwatch.GetTimestamp();
            long At(int ms) => stamp + (long)(Stopwatch.Frequency * (ms / 1000.0));
            ObserveGameText(probe, new(true, "第一句很快被跳过的台词。", true, Active: true), At(0));
            Check(!TextReady(probe, At(30)), "刚出现的台词等待短暂稳定，不启动过期录音");
            ObserveGameText(probe, new(true, "第二句也很快被跳过。", true, Active: true), At(30));
            ObserveGameText(probe, new(true, branch.Text, true, Active: true), At(60));
            Check(!TextReady(probe, At(90)) && TextReady(probe, At(140)) && probe.Pending == branch.Text, "快速连续换句只保留最后一句，不累积播放队列");
            Arm(); before = playCalls; CommitMemoryGameText(probe, owner, probe.Pending); probe.Pending = "";
            Check(playCalls == before + 1, "停在最后一句后只提交最新配音");
            ObserveGameText(probe, new(true, branch.Text, false, Active: false), At(150));
            Check(!probe.Active && probe.Pending == "" && textSoundSource == null && !TextReady(probe, At(300)), "隐藏或关闭对白立即清除待播并停止原音频");
            ObserveGameText(probe, new(true, branch.Text, false, Active: true), At(350));
            before = playCalls; CommitMemoryGameText(probe, owner, probe.Pending); probe.Pending = "";
            Check(playCalls == before, "恢复同一句正文不会重播已经播放的配音");
            var shortLine = owner.Pack.Nodes.First(n => n.Kind == "line" && n.SectionId == branch.SectionId && n.Text == "正题？");
            Arm(); before = playCalls; CommitMemoryGameText(probe, owner, shortLine.Text);
            Check(owner.CurrentId == shortLine.Id && playCalls == before + 1 && textArmed, "完整且唯一的两字正文正常定位播放，不套用 OCR 长度限制");
            audio.Play("不存在且不应打开的过期音频.wav", shouldPlay: () => false);
            Check(!audio.Playing, "过期音频请求在访问文件和音频设备之前被取消");
            var args = Environment.GetCommandLineArgs(); int fixtureArgument = Array.IndexOf(args, "--test-speaker-pack");
            if (fixtureArgument >= 0 && fixtureArgument + 1 < args.Length)
            {
                LoadPack(args[fixtureArgument + 1]);
                owner = engine ?? throw new Exception("角色配音夹具未加载");
                var vote = owner.Pack.Nodes.First(n => n.Kind == "line" && n.Speaker == "监事会成员A" && n.Text == "我赞成更换首席执行官为莱昂纳多·诺曼。");
                textOwner = owner; textSection = vote.SectionId; textArmed = true; lastTextPlayed = "";
                var sequence = owner.Pack.Nodes.SkipWhile(n => n.Id != vote.Id).Take(25).ToArray();
                before = playCalls;
                for (int i = 0; i < 3; i++)
                {
                    var line = sequence[i];
                    ObserveGameText(probe, new(true, line.Text, true, Active: true, Speaker: line.Speaker), At(500 + i * 200));
                    Check(TextReady(probe, At(600 + i * 200)) && probe.PendingSpeaker == line.Speaker, "投票第 " + (i + 1) + " 句可检测，包括同文换角色");
                    CommitMemoryGameText(probe, owner, probe.Pending, probe.PendingSpeaker); probe.Pending = "";
                    Check(owner.CurrentId == line.Id && playCalls == before + i + 1 && textArmed, "投票第 " + (i + 1) + " 句独立定位并播放对应录音");
                }
                before = playCalls; string prior = owner.CurrentId!;
                CommitMemoryGameText(probe, owner, vote.Text);
                Check(textArmed && playCalls == before && owner.CurrentId == prior, "丢失角色的同文句保留进度且不关闭监听");
                var afterVote = sequence[3]; CommitMemoryGameText(probe, owner, afterVote.Text, afterVote.Speaker);
                Check(textArmed && owner.CurrentId == afterVote.Id && playCalls == before + 1, "同文歧义之后的唯一正文自动接续，无需重新定位");
                foreach (var line in sequence.Skip(4))
                {
                    before = playCalls; CommitMemoryGameText(probe, owner, line.Text, line.Speaker);
                    bool punctuation = GameTextPolicy.Normalize(line.Text).Length == 0;
                    Check(textArmed && (punctuation ? playCalls == before : owner.CurrentId == line.Id && playCalls == before + 1),
                        "连续推进保持监听：" + line.Speaker + " · " + line.Text);
                }
                PauseTextPlayback("测试手动暂停"); before = playCalls;
                CommitMemoryGameText(probe, owner, vote.Text, vote.Speaker);
                Check(!textArmed && playCalls == before, "手动暂停后新台词仍不能自行恢复");
            }
            textProbes[0].Anchor.Text = branch.Text;
            gameTextLive.Text = "普通剧情 · 测试读取\n" + branch.Text;
            Expand(gameTextTab); await Task.Delay(120); Screenshot("game-text-tracking-ui.png");
            foreach (int index in new[] { 1, 2, 3 })
            {
                followModeTabs.SelectedIndex = index; await Task.Delay(80); Screenshot("follow-mode-" + index + ".png");
            }
            report.Add("INFO: 使用真实配音包读取匹配；testUi 不播放声音，不连接或操作游戏。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            gameTextSampleTest = null;
            Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "game-text-ui-test.txt"), report);
            Close();
        }
    }
}
