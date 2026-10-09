using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunGameBranchRecoveryUiTest()
    {
        var report = new List<string>(); Window? scene = null;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception(label + $" [node={engine?.CurrentId}, armed={textArmed}, recovery={inputBranchRecovery != null}, notice={gameTextNotice.Text}]");
            report.Add("PASS: " + label);
        }
        try
        {
            timer.Stop(); gameTextTimer.Stop(); gamepadTimer?.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose(); StopGamepadInput();
            preferences.DialogueGuardEnabled = false; preferences.OcrEnabled = false;
            string folder = Path.Combine(Log.DataDir, "branch-fixture"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "voice.wav"), new byte[] { 0 });
            var pack = new Pack { Id = "branch-resume-fixture", Title = "游戏选支线续接检查", Root = folder,
                Chapters = new() { new() { Id = "chapter", Sections = new() { new() { Id = "s", Title = "分支检查", StartId = "before" }, new() { Id = "other", Title = "其他小节", StartId = "elsewhere" } } } },
                Nodes = new() {
                    new() { Id="before", SectionId="s", Text="选择前最后一句。", Speaker="甲", Audio="voice.wav", NextId="choice" },
                    new() { Id="choice", SectionId="s", Kind="choice", Text="在游戏里选择", Options=new() {
                        new() { Id="option-a", PathId="a", Label="甲路线", TargetId="a1", MergeId="merge", Verified=true },
                        new() { Id="option-b", PathId="b", Label="乙路线", TargetId="b1", MergeId="merge", Verified=true } } },
                    new() { Id="a1", SectionId="s", PathId="a", Text="这是甲路线第一句。", Speaker="甲", Audio="voice.wav", NextId="a2" },
                    new() { Id="a2", SectionId="s", PathId="a", Text="这是甲路线第二句。", Speaker="甲", Audio="voice.wav", NextId="gap" },
                    new() { Id="gap", SectionId="s", PathId="a", Kind="gap" },
                    new() { Id="b1", SectionId="s", PathId="b", Text="这是乙路线第一句。", Speaker="乙", Audio="voice.wav", NextId="merge" },
                    new() { Id="merge", SectionId="s", Kind="merge", NextId="tail" },
                    new() { Id="tail", SectionId="s", Text="游戏已经返回共同剧情。", Speaker="甲", Audio="voice.wav" },
                    new() { Id="elsewhere", SectionId="other", Text="另一小节的正文不能自动接入。", Audio="voice.wav" } } };
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); LoadPack(file);
            scene = new Window { Title="分支续接隔离场景", Width=850, Height=560, Left=120, Top=100,
                Background=Brushes.DarkSlateGray, ShowInTaskbar=false };
            scene.Show(); var handle = new WindowInteropHelper(scene).Handle; game = new(handle, scene.Title, "BranchFixture");
            branchRecoveryEnvironmentTest = () => true;
            GameTextSample ordinary = new(true, "", false), sceneText = new(true, "", false);
            gameTextSampleTest = p => p.Kind == GameTextKind.Ordinary ? ordinary : sceneText;
            async Task Focus()
            {
                Collapse(false); scene.Show(); scene.Activate(); Native.SetForegroundWindow(handle);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (Native.GetForegroundWindow() != handle)
                {
                    uint own = GetCurrentThreadId(), front = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                    bool attached = front != own && AttachThreadInput(own, front, true);
                    try { scene.Activate(); Native.SetForegroundWindow(handle); }
                    finally { if (attached) AttachThreadInput(own, front, false); }
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check(Native.GetForegroundWindow() == handle, "隔离游戏窗口取得前台");
            }
            async Task Start(FollowInputSource source = FollowInputSource.Keyboard)
            {
                StopGameText("重置隔离检查"); HideBranchMenu();
                ordinary = sceneText = new(true, "", false);
                SectionBox.SelectedIndex = 0; preferences.MouseFollowEnabled = source == FollowInputSource.Mouse;
                engine!.ConfirmGameLine("before", resumeOriginal: true); DialoguePositionConfirmed();
                await Focus();
                preferences.ClickZoneEnabled = true; UpdateClickZone();
                clickZone.PlaceAt(scene.Left + 140, scene.Top + 160); clickZone.SetDraggable(false); clickZone.UpdateLayout();
                DialoguePositionConfirmed();
                if (source == FollowInputSource.Mouse)
                {
                    var center = clickZone.PointToScreen(new Point(32, 32)); long stamp = Stopwatch.GetTimestamp();
                    await ObserveMouseTapForTest(new("鼠标左键", handle, (int)center.X, (int)center.Y, stamp, stamp, new IntPtr(91)));
                }
                else RequestFollowNext(source, Stopwatch.GetTimestamp());
                Check(engine.CurrentId == "choice" && inputBranchRecovery != null && textArmed && !branchMenu.IsVisible,
                    source + " 实际推进到分支后开启临时监听，软件菜单不拦游戏选择");
            }
            void Read(string id, bool useScene = false)
            {
                var node = engine!.Pack.ById[id];
                var value = new GameTextSample(true, node.Text, true, Active:true, Speaker:node.Speaker);
                if (useScene) { ordinary = new(true, "", false); sceneText = value; } else { ordinary = value; sceneText = new(true, "", false); }
                Sample();
            }
            void Sample()
            {
                TickGameText();
                foreach (var probe in textProbes) probe.PendingSince -= Stopwatch.Frequency;
                TickGameText();
            }

            foreach (var source in new[] { FollowInputSource.Keyboard, FollowInputSource.Mouse, FollowInputSource.Gamepad })
            {
                await Start(source); int plays = playCalls;
                Read("before"); Check(playCalls == plays && engine!.CurrentId == "choice", source + " 选择前残留正文不倒退或重播");
                textAutoBox.IsChecked = true; long selectionInput = Stopwatch.GetTimestamp();
                Read(source == FollowInputSource.Gamepad ? "b1" : "a1", source == FollowInputSource.Gamepad);
                string expected = source == FollowInputSource.Gamepad ? "b1" : "a1";
                Check(engine!.CurrentId == expected && playCalls == plays + 1 && !textArmed && inputBranchRecovery == null &&
                    textProbes.All(p => !p.AutoConnect && p.Binding == null) && textAutoLine == null &&
                    preferences.MouseFollowEnabled == (source == FollowInputSource.Mouse) && preferences.TextAutoAdvanceEnabled,
                    source + " 按游戏新对白只播第一句，原输入方式和自动点击偏好保留，临时监听退出");
                UpdateExperienceStatus();
                Check(StripText.Text.StartsWith(expected == "a1" ? "分支一：" : "分支二：") &&
                    StripText.Text.EndsWith(engine.Current!.Text), source + " 悬浮台词按实际选入路线标编号并保留完整原文");
                long ticket = audioRequest; Sample();
                RequestFollowNext(source, selectionInput);
                Check(engine.CurrentId == expected && playCalls == plays + 1 && audioRequest == ticket,
                    source + " 重复采样、迟到的选项按压不重播、不跳过第一句、不切断新录音");
                RequestFollowNext(source, Stopwatch.GetTimestamp());
                Check(engine.CurrentId == (expected == "a1" ? "a2" : "tail") && playCalls == plays + 2,
                    source + " 下一次新的游戏推进继续原路线");
                var restored = new PlaybackEngine(engine.Pack);
                Check(restored.ImportNavigation(engine.ExportNavigation()) && restored.CurrentId == engine.CurrentId &&
                    restored.Choices.GetValueOrDefault("choice") == engine.Choices.GetValueOrDefault("choice"),
                    source + " 自动续接后的进度和所选路线可保存恢复");
            }

            await Start(); Read("a1"); RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            // 同来源快速检查仍是新的时间戳，不通过重放原输入跨越边界。
            await Task.Delay(5); RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Check(engine!.CurrentId is "a2" or "gap" && inputBranchRecovery != null, "已核对支线段尾的未知连接也等待真实游戏正文");
            int before = playCalls; Read("tail");
            Check(engine.CurrentId == "tail" && playCalls == before + 1 && !textArmed && engine.Pack.ById["gap"].NextId == null,
                "返回共同剧情后凭正文续接，不编造或改写原路线连接");

            StopGameText("准备游戏自行显示选项场景"); HideBranchMenu(); engine.ConfirmGameLine("before");
            await Focus(); BeginInputBranchRecovery("before"); before = playCalls;
            Check(inputBranchRecovery is { BeforeBranch:true } && engine.CurrentId == "before", "分支前一句提前监听，游戏自行显示选项时也能接上");
            Read("before"); Check(playCalls == before && engine.CurrentId == "before", "提前监听不重播仍显示的原句");
            Read("b1"); Check(engine.CurrentId == "b1" && playCalls == before + 1 && !textArmed,
                "游戏直接点选项后首句自动播放，不必额外推进软件到选择点");

            engine.ConfirmGameLine("before"); DialoguePositionConfirmed(); await Focus(); BeginInputBranchRecovery("before");
            long originalAudio = audioRequest; PauseTextPlayback("用户暂停分支前的配音");
            Check(audioRequest > originalAudio && !textArmed && inputBranchRecovery == null,
                "提前监听期间用户暂停也停止原来的配音，不留下背景声音");

            engine.ConfirmGameLine("a2"); DialoguePositionConfirmed(); await Focus();
            RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Check(engine.CurrentId == "gap" && inputBranchRecovery is {BeforeBranch:false}, "已到达待续接节点时仍能启动正文续接");
            Read("tail"); Check(engine.CurrentId == "tail" && !textArmed, "未知出口只凭游戏显示的共同线正文续接");

            await Start(); before = playCalls;
            ordinary = new(true, "尚未匹配的选项文字", true, Active:true); Sample(); Read("elsewhere");
            Check(engine.CurrentId == "choice" && playCalls == before && textArmed, "选项文本和其他小节的正文均不猜测");
            var duplicate = new Node {Id="duplicate", SectionId="s", Speaker="甲", Text=engine.Pack.ById["a1"].Text};
            engine.Pack.Nodes.Add(duplicate); Read("a1"); engine.Pack.Nodes.Remove(duplicate);
            Check(engine.CurrentId == "choice" && playCalls == before && textArmed, "同文同角色无法唯一定位时保持等待");
            UpdateExperienceStatus();
            Check(StripState.Text.Contains("多个位置") && compactFollowActions["continuation"].IsEnabled,
                "同文歧义在悬浮条说明多个位置，并提供手动续接");
            ordinary = new(true, "", false); Sample();
            UpdateExperienceStatus();
            Check(!StripState.Text.Contains("多个位置"), "正文清空后不沿用上一帧同文歧义原因");
            var target = engine.Pack.ById["a1"]; string? audioFile = target.Audio; target.Audio = null;
            Read("a1"); target.Audio = audioFile;
            Check(engine.CurrentId == "choice" && playCalls == before && gameTextNotice.Text.Contains("暂无可用配音"), "缺音不误称已经接上");
            UpdateExperienceStatus();
            Check(StripState.Text.Contains("暂无可用配音"), "悬浮条明确指出当前句缺配音");
            Read("b1"); Check(engine.CurrentId == "b1" && playCalls == before + 1, "歧义或缺音后出现下一条明确正文可自行接上");
            UpdateExperienceStatus();
            Check(!StripState.Text.Contains("暂无可用配音") && StripText.Text.StartsWith("分支二："),
                "成功续接后清除上一句缺音原因并显示实际第二支");

            await Start(); before = playCalls;
            ordinary = new(true, target.Text, true, Active:true, Speaker:target.Speaker);
            sceneText = new(true, engine.Pack.ById["b1"].Text, true, Active:true, Speaker:"乙"); Sample();
            Check(engine.CurrentId == "choice" && playCalls == before && textArmed, "两路同时出现不同对白时不以遍历次序抢播");
            UpdateExperienceStatus();
            Check(StripState.Text.Contains("两路"), "两路正文冲突在悬浮条显示真实等待原因");
            sceneText = new(true, "", false); Read("a1");
            Check(engine.CurrentId == "a1" && playCalls == before + 1, "冲突消失后无需点连接就能接上");

            await Start(); PauseTextPlayback("用户暂停分支续接"); before=playCalls;
            ordinary=sceneText=new(true,"",false);Sample();UpdateExperienceStatus();
            Check(StripState.Text.Contains("用户暂停") && !textArmed && playCalls==before,
                "暂停后的迟到空帧保留暂停原因且不恢复配音");
            ordinary=new(true,engine.Pack.ById["a1"].Text,true,Active:true,Speaker:"甲");
            sceneText=new(true,engine.Pack.ById["b1"].Text,true,Active:true,Speaker:"乙");Sample();UpdateExperienceStatus();
            Check(StripState.Text.Contains("用户暂停") && !textArmed && playCalls==before,
                "暂停后的迟到双路冲突不能覆盖暂停说明或启动播放");

            foreach (string action in new[] { "pause", "disconnect", "manual", "browse", "original", "section", "window", "listening", "pack" })
            {
                await Start();
                switch (action)
                {
                    case "pause": PauseTextPlayback("用户暂停"); break;
                    case "disconnect": StopGameText("用户断开"); break;
                    case "manual": PrepareGamePlaybackAction(); engine.Next(true); break;
                    case "browse": engine.PauseForBrowse(); break;
                    case "original": engine.EnterOriginal(); break;
                    case "section": SectionBox.SelectedIndex = 1; break;
                    case "window": game = null; break;
                    case "listening": SuspendGameForListening(); break;
                    case "pack": LoadPack(file); break;
                }
                before = playCalls; Read("a1");
                Check(inputBranchRecovery == null && !textArmed && playCalls == before, "用户改变状态后旧正文不能恢复播放：" + action);
                game = new(handle, scene.Title, "BranchFixture");
            }

            await Start(); var completion = new TaskCompletionSource<GameTextReader>(); CancellationToken token = default;
            textDiscoveryTest = (_, _, cancellation) => { token = cancellation; return completion.Task; };
            var pending = RefreshTextConnection(textProbes[0]);
            PauseTextPlayback("用户暂停扫描"); completion.SetResult(new GameTextReader(Environment.ProcessId)); await pending;
            Check(token.IsCancellationRequested && textProbes[0].Reader == null && !textArmed && !textDiscoveryBusy,
                "临时分支扫描的迟到结果不能覆盖暂停");
            textDiscoveryTest = null;

            // 文字跟随保持原方式：分支消失时加快重查，接上后继续监听及原自动下一句。
            engine.ConfirmGameLine("before"); SectionBox.SelectedIndex = 0; BeginTextMonitoring();
            Read("before"); before = playCalls;
            var deadline = DateTime.UtcNow;
            ordinary = new(true, "", false); Sample();
            var scheduled = textProbes[0].NextConnectAttempt;
            Check(textArmed && scheduled >= deadline && scheduled < deadline.AddSeconds(2), "文字跟随遇到对白框消失后缩短自动重找等待");
            Sample(); Check(textProbes[0].NextConnectAttempt == scheduled, "连续空帧不反复扫描或推迟重找");
            Read("b1");
            Check(textArmed && inputBranchRecovery == null && engine.CurrentId == "b1" && playCalls == before + 1,
                "文字跟随选完支线后自动播放第一句并保留持续监听");
            PauseTextPlayback("用户明确暂停"); before = playCalls; Read("a1");
            Check(playCalls == before && !textArmed, "普通文字跟随暂停后不会因支线新正文自行恢复");

            await Start(); Expand(gameTextTab); await Task.Delay(100); Screenshot("分支自动续接-等待.png");
            StopGameText("截图完成"); HideBranchMenu();

            branchRecoveryEnvironmentTest = () => false;
            engine.ConfirmGameLine("before"); DialoguePositionConfirmed(); await Focus();
            RequestFollowNext(FollowInputSource.Keyboard, Stopwatch.GetTimestamp());
            Check(engine.CurrentId == "choice" && inputBranchRecovery == null && !textArmed && branchMenu.IsVisible,
                "不支持只读文字连接的客户端保留原手动分支入口");
            var args=Environment.GetCommandLineArgs();int realIndex=Array.IndexOf(args,"--branch-real-pack");
            if(realIndex>=0 && realIndex+1<args.Length)
            {
                StopGameText("准备真实包首句锚定检查");HideBranchMenu();LoadPack(args[realIndex+1]);
                branchRecoveryEnvironmentTest=()=>true;
                var pair=engine!.Pack.Nodes.Where(n=>!n.Archived && n.Kind=="choice").SelectMany(m=>m.Options.Select(o=>(Menu:m,Option:o)))
                    .First(x=>x.Option.BodyVerified && x.Option.ExitVerified && engine.Pack.ById[x.Option.TargetId] is {Kind:"line"} n
                        && n.Text.Length>=8 && n.Text!=x.Option.Label && engine.Pack.AudioNotice(n).Length==0
                        && File.Exists(engine.Pack.ResolveAudio(n)) && GameTextPolicy.Match(engine.Pack,n.SectionId,n.Text,out _,n.Speaker)?.Id==n.Id);
                var firstLine=engine.Pack.ById[pair.Option.TargetId];
                foreach(var source in new[]{FollowInputSource.Keyboard,FollowInputSource.Mouse,FollowInputSource.Gamepad})
                {
                    StopGameText("重置真实首句检查");ordinary=sceneText=new(true,"",false);
                    preferences.MouseFollowEnabled=source==FollowInputSource.Mouse;
                    engine.OpenGameMenu(pair.Menu.Id,pair.Menu.SectionId);BrowseCurrent();await Focus();BeginInputBranchRecovery("");
                    Check(textArmed && inputBranchRecovery!=null && gameTextLive.Text.Contains("等待选后第一句"),source+" 真实分支菜单等待选后第一句完整正文");
                    int count=playCalls;Read(firstLine.Id);
                    Check(engine.CurrentId==firstLine.Id && playCalls==count+1 && !textArmed && inputBranchRecovery==null
                        && engine.Choices.GetValueOrDefault(pair.Menu.Id)==pair.Option.PathId
                        && preferences.MouseFollowEnabled==(source==FollowInputSource.Mouse),source+" 真实分支首句锚定后先播该句并保持原跟随方式："+firstLine.Id);
                    Check(gameTextNotice.Text.Contains("已对齐：") && gameTextNotice.Text.Contains(firstLine.Text),source+" 对齐提示展示真实角色与完整首句");
                    Read(firstLine.Id);Check(playCalls==count+1 && engine.CurrentId==firstLine.Id,source+" 真实首句重复读取不重播、不跳句");
                }
            }
            await RunBranchAnchorUiChecks(Check,folder);
            await RunDesktopBranchFeedbackUiChecks(Check,folder);
            report.Add("INFO: 运行实际桌面入口与播放请求；对白及输入来自隔离夹具，无真实游戏输入、音频解码或人耳听审。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            gameTextSampleTest = null; textDiscoveryTest = null; branchRecoveryEnvironmentTest = null;
            StopGameText("分支续接检查结束"); scene?.Close();
            Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "game-branch-recovery-ui-test.txt"), report);
            Close();
        }
    }
}
