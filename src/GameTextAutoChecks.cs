using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunTextAutoUiTest()
    {
        var report = new List<string>(); int clicks = 0; long clock = 10000;
        bool environment = true, foreground = true, clickSucceeds = true;
        GameTextSample sample = new(true, "第一句。", true, Active: true, Speaker: "甲");
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); report.Add("PASS: " + label); }
        try
        {
            timer.Stop(); gameTextTimer.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose();
            textAutoClockTest = () => clock; textAutoEnvironmentTest = () => environment;
            textAutoForegroundTest = () => foreground;
            textAutoSampleTest = () => sample; textAutoClickTest = () => { clicks++; return clickSucceeds; };
            string folder = Path.Combine(Log.DataDir, "text-auto-fixture"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "voice.wav"), new byte[] { 0 });
            var pack = new Pack { Id = "text-auto-fixture", Title = "文本自动点击验收", Root = folder,
                Chapters = new() { new() { Id = "chapter", Sections = new() { new() { Id = "s", StartId = "a" } } } },
                Nodes = new() {
                    new() { Id = "a", SectionId = "s", Speaker = "甲", Text = "第一句。", Audio = "voice.wav", NextId = "b" },
                    new() { Id = "b", SectionId = "s", Speaker = "乙", Text = "第二句。", Audio = "voice.wav", NextId = "choice" },
                    new() { Id = "choice", SectionId = "s", Kind = "choice", Text = "请选择", Options = new() {
                        new() { Id = "opt", PathId = "route", Label = "选项", TargetId = "r1", MergeId = "merge", Verified = true } } },
                    new() { Id = "r1", SectionId = "s", PathId = "route", Speaker = "甲", Text = "路线第一句。", Audio = "voice.wav", NextId = "r2" },
                    new() { Id = "r2", SectionId = "s", PathId = "route", Speaker = "乙", Text = "路线第二句。", Audio = "voice.wav", NextId = "merge" },
                    new() { Id = "merge", SectionId = "s", Kind = "merge", NextId = "tail" },
                    new() { Id = "tail", SectionId = "s", Speaker = "甲", Text = "最后一句。", Audio = "voice.wav", NextId = "end" },
                    new() { Id = "end", SectionId = "s", Kind = "end" } } };
            pack.Validate();
            long Start(string id = "a", GameTextKind kind = GameTextKind.Ordinary)
            {
                CancelTextAutoAdvance(); environment = foreground = true; clickSucceeds = true; textActiveConflict = false;
                textAutoBox.IsChecked = true; textAutoDelay.SelectedIndex = 1;
                var owner = new PlaybackEngine(pack); engine = textOwner = owner; textSection = "s"; textArmed = true; lastTextPlayed = "";
                owner.PlayRequested += _ => { playCalls++; audioRequest++; };
                owner.StopRequested += StopAudio;
                foreach (var p in textProbes) { p.Active = false; p.Observed = p.Pending = p.ObservedSpeaker = p.PendingSpeaker = ""; }
                var node = pack.ById[id]; sample = new(true, node.Text, true, Active: true, Speaker: node.Speaker);
                var probe = textProbes[kind == GameTextKind.Ordinary ? 0 : 1];
                ObserveGameText(probe, sample, Stopwatch.GetTimestamp());
                CommitMemoryGameText(probe, owner, node.Text, node.Speaker); probe.Pending = "";
                if (textAutoLine == null) throw new Exception("未捕获音频对应的内存台词");
                return audioRequest;
            }
            Check(new Preferences().TextAutoDelaySeconds == 1.5 && !new Preferences().TextAutoAdvanceEnabled, "通用版默认等待 1.5 秒，自动点击需主动开启");
            long ticket = Start(); int before = clicks; int plays = playCalls;
            clock += 60000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine != null, "只等时长不点击，必须收到自然播完回调");
            OnTextAudioCompleted(ticket - 1); TickTextAutoAdvance();
            Check(textAutoLine?.CompletedAt == null && clicks == before, "旧音频完成回调不能授权点击");
            OnTextAudioCompleted(ticket); clock += 1499; TickTextAutoAdvance();
            Check(clicks == before, "配音结束后不足 1.5 秒不点击");
            clock++; TickTextAutoAdvance();
            Check(clicks == before + 1 && engine!.CurrentId == "a" && playCalls == plays, "到时只点击一次，不凭顺序播放下一句");
            OnTextAudioCompleted(ticket); clock += 10000; TickTextAutoAdvance();
            Check(clicks == before + 1, "重复完成回调或游戏未换句均不补点");
            var next = pack.ById["b"]; sample = new(true, next.Text, true, Active: true, Speaker: next.Speaker);
            ObserveGameText(textProbes[0], sample, Stopwatch.GetTimestamp()); CommitMemoryGameText(textProbes[0], engine!, next.Text, next.Speaker);
            Check(engine!.CurrentId == "b" && playCalls == plays + 1, "探针读到真实下一句后才请求对应录音");
            OnTextAudioCompleted(audioRequest); clock += 3000; TickTextAutoAdvance();
            Check(clicks == before + 1 && textAutoLine == null && textArmed, "分支前停住，不替用户选分支，文本监听继续");
            foreach (int delayIndex in new[] { 0, 2 })
            {
                ticket = Start(); textAutoDelay.SelectedIndex = delayIndex; before = clicks;
                OnTextAudioCompleted(ticket); clock += TextAutoDelayMs - 1; TickTextAutoAdvance(); Check(clicks == before, "自定义等待未到期：" + TextAutoDelayMs);
                clock++; TickTextAutoAdvance(); Check(clicks == before + 1, "支持用户选择等待毫秒：" + TextAutoDelayMs);
            }
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            sample = sample with { Text = "玩家已快进到另一句。" }; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "点击前再次读取内存，快进后的旧句不点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            sample = sample with { Speaker = "乙" }; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "正文相同而角色已换时取消旧点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            sample = sample with { Active = false }; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "对白关闭或隐藏后不点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            environment = false; TickTextAutoAdvance(); environment = true; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "移动游戏或点击位置后取消旧点击");
            ticket = Start(); before = clicks; int backgroundPlays = playCalls;
            foreground = false; TickTextAutoAdvance(); OnTextAudioCompleted(ticket); clock += 10000; TickTextAutoAdvance();
            Check(clicks == before && textArmed && textAutoLine?.CompletedAt != null && textAutoLine.FocusHeld && playCalls == backgroundPlays,
                "后台保留当前配音完成状态，等待期间不点击也不重播");
            HandleMouseGlobal(new("鼠标左键", IntPtr.Zero, 0, 0, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp()));
            HandleGlobal(System.Windows.Input.Key.F8, IntPtr.Zero);
            Check(textAutoLine != null && textArmed && audioRequest == ticket, "其他窗口连续点击和按键不清除文本跟随及待恢复状态");
            foreground = true; TickTextAutoAdvance(); clock += 1499; TickTextAutoAdvance();
            Check(clicks == before, "回到游戏重新等待完整 1.5 秒，不立即补点");
            clock++; TickTextAutoAdvance(); TickTextAutoAdvance();
            Check(clicks == before + 1 && engine!.CurrentId == "a" && playCalls == backgroundPlays, "返回后核对同句才点击一次，仍由游戏新文字驱动配音");
            ticket = Start(); before = clicks; OnTextAudioCompleted(ticket); foreground = false; TickTextAutoAdvance();
            sample = sample with { Text = "后台已换到了另一句。" }; foreground = true; clock += 5000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "后台已换句时回前台不能点击旧句");
            ticket = Start(); before = clicks; foreground = false; TickTextAutoAdvance();
            sample = sample with { Active = false }; OnTextAudioCompleted(ticket); foreground = true; clock += 5000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "后台对白关闭时自然完成回调也不能留下自动点击");
            ticket = Start(); before = clicks; foreground = false; TickTextAutoAdvance(); foreground = true; TickTextAutoAdvance();
            clock += 5000; TickTextAutoAdvance(); Check(clicks == before, "配音尚未播完时返回游戏，不能仅凭焦点恢复点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            textAutoBox.IsChecked = false; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && textArmed, "关闭开关取消已排定点击，仍可跟随台词");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            StopAudio(); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "主动停止音频不能当成自然播完");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            PauseTextPlayback("测试手动暂停"); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && !textArmed, "暂停文本配音同时取消自动点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            ObserveTextAutoInput(true, "验收游戏内手动输入"); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "游戏内主动操作仍取消当前点击任务");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            engine = new PlaybackEngine(pack); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "更换配音包后旧完成事件不能点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks;
            textActiveConflict = true; clock += 2000; TickTextAutoAdvance();
            Check(clicks == before, "两路对白冲突时不点击");
            ticket = Start(); OnTextAudioCompleted(ticket); before = clicks; clickSucceeds = false;
            clock += 2000; TickTextAutoAdvance(); TickTextAutoAdvance(); OnTextAudioCompleted(ticket);
            Check(clicks == before + 1 && engine!.CurrentId == "a" && textAutoLine == null, "点击失败不推进配音、不重复尝试");
            ticket = Start("tail"); before = clicks; OnTextAudioCompleted(ticket); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "小节末尾不自动跨越");
            ticket = Start(); before = clicks; pack.ById["b"].Audio = null;
            OnTextAudioCompleted(ticket); clock += 2000; TickTextAutoAdvance(); pack.ById["b"].Audio = "voice.wav";
            Check(clicks == before && textArmed, "下一句缺配音不继续自动点击，等待手动处理");
            ticket = Start("r1"); before = clicks; OnTextAudioCompleted(ticket); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before + 1 && engine!.CurrentId == "r1", "已读取并确认的支线正文也可自动点一次");
            ticket = Start("a", GameTextKind.Scene3D); before = clicks; OnTextAudioCompleted(ticket); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before + 1, "3D 探针使用同一完成及等待规则");
            pack.ById["a"].Text = "。。。。。。"; pack.ById["a"].Audio = null;
            ticket = Start(); before = clicks;
            Check(textAutoLine?.SilentPause == true && textAutoLine.CompletedAt != null, "纯标点没有音频时独立计时，不等待不存在的播完事件");
            OnTextAudioCompleted(ticket); textAutoDelay.SelectedIndex = 2;
            clock += 1199; TickTextAutoAdvance(); Check(clicks == before, "旧音频回调和等待设置不缩短标点停顿");
            clock++; TickTextAutoAdvance(); TickTextAutoAdvance();
            Check(clicks == before + 1 && engine!.CurrentId == "a", "标点停顿1200毫秒后只点一次，仍等待游戏实际换句");
            next = pack.ById["b"]; sample = new(true, next.Text, true, Active: true, Speaker: next.Speaker);
            ObserveGameText(textProbes[0], sample, Stopwatch.GetTimestamp());
            CommitMemoryGameText(textProbes[0], engine!, next.Text, next.Speaker);
            Check(engine!.CurrentId == "b" && textAutoLine?.SilentPause == false && textAutoLine.CompletedAt == null, "读到标点后的正常正文才接上配音");

            ticket = Start(); before = clicks; foreground = false; TickTextAutoAdvance(); clock += 5000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine?.FocusHeld == true, "标点停顿期间切后台不点击");
            foreground = true; TickTextAutoAdvance(); clock += 1199; TickTextAutoAdvance();
            Check(clicks == before, "标点回到前台重新等待1200毫秒");
            clock++; TickTextAutoAdvance(); Check(clicks == before + 1, "前台核对后标点只点击一次");
            foreach (string change in new[] { "stop", "toggle", "text", "speaker", "hidden", "environment", "input", "owner", "conflict" })
            {
                ticket = Start(); before = clicks;
                switch (change)
                {
                    case "stop": PauseTextPlayback("标点停顿停止"); break;
                    case "toggle": textAutoBox.IsChecked = false; break;
                    case "text": sample = sample with { Text = "已换句。" }; break;
                    case "speaker": sample = sample with { Speaker = "其他人" }; break;
                    case "hidden": sample = sample with { Active = false }; break;
                    case "environment": environment = false; break;
                    case "input": ObserveTextAutoInput(true, "手动操作"); break;
                    case "owner": engine = new PlaybackEngine(pack); break;
                    case "conflict": textActiveConflict = true; break;
                }
                OnTextAudioCompleted(ticket); clock += 1200; TickTextAutoAdvance();
                Check(clicks == before && textAutoLine == null, "标点待点击仍遵守原有取消条件：" + change);
            }
            pack.ById["a"].Audio = "voice.wav";
            ticket = Start(); before = clicks; clock += 5000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine?.SilentPause == false && textAutoLine.CompletedAt == null, "标点已有配音时继续等待真实播完");
            OnTextAudioCompleted(ticket); clock += 1500; TickTextAutoAdvance(); Check(clicks == before + 1, "标点已有配音使用普通播完等待");
            pack.ById["a"].Text = "第一句。";
            pack.ById["b"].Text = "……"; pack.ById["b"].Audio = null;
            ticket = Start(); before = clicks; OnTextAudioCompleted(ticket); clock += 1500; TickTextAutoAdvance();
            Check(clicks == before + 1, "下一句是无配音标点时不误当缺音阻断");
            next = pack.ById["b"]; sample = new(true, next.Text, true, Active: true, Speaker: next.Speaker);
            ObserveGameText(textProbes[0], sample, Stopwatch.GetTimestamp()); CommitMemoryGameText(textProbes[0], engine!, next.Text, next.Speaker);
            Check(engine!.CurrentId == "b" && textAutoLine?.SilentPause == true, "读到真实标点后开始独立停顿");
            clock += 1200; TickTextAutoAdvance(); Check(clicks == before + 1 && textAutoLine == null, "标点之后遇分支仍不点击");
            pack.ById["b"].Audio = "missing.wav";
            ticket = Start(); before = clicks; OnTextAudioCompleted(ticket); clock += 2000; TickTextAutoAdvance();
            Check(clicks == before && textAutoLine == null, "标点引用的录音丢失不能作为静默停顿跳过");
            Check(!ocr.Running, "整条自动点击链路不启动 OCR");
            CancelTextAutoAdvance(); textArmed = false; Expand(gameTextTab); await Task.Delay(120); Screenshot("text-auto-ui.png");
            report.Add("INFO: 全部点击使用隔离计数器；无系统输入、无真实游戏操作、无音频输出。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            CancelTextAutoAdvance(); textAutoEnvironmentTest = null; textAutoForegroundTest = null; textAutoSampleTest = null; textAutoClickTest = null; textAutoClockTest = null;
            Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "text-auto-ui-test.txt"), report); Close();
        }
    }
}
