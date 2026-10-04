using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    // 仅测试进程合成观察事件，不调用系统鼠标输入。
    async Task ObserveMouseTapForTest(ObservedMouseInput down, bool accepted = true, string reason = "")
    {
        if (!testUi) throw new InvalidOperationException("仅隔离测试允许合成轻点。");
        HandleMouseGlobal(down);
        long now = Stopwatch.GetTimestamp();
        HandleMouseGesture(new(down, down with { Timestamp = now, ReceivedTimestamp = now }, accepted, reason));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    async Task RunMouseFollowUiTest()
    {
        var report = new List<string>(); Window? scene = null;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message + $" [node={engine?.CurrentId},mode={engine?.Mode},pending={pendingMouseFollow != null},notice={mouseFollowNotice}]");
            report.Add("PASS: " + message);
        }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            timer.Stop(); gamepadTimer?.Stop(); rawKeyboard?.Dispose(); keyboard?.Dispose(); StopGamepadInput();
            preferences.DialogueGuardEnabled = false; preferences.MouseFollowEnabled = true; preferences.GamepadFollowEnabled = false;
            preferences.ClickZoneEnabled = true; preferences.OcrEnabled = false;
            var pack = new Pack { Id="mouse-follow-fixture", Title="轻点隔离检查", Root=Log.DataDir,
                Chapters=new() { new() { Id="chapter",Title="检查",Sections=new() { new() {Id="section",Title="共同线",StartId="a"} } } },
                Nodes=new() { new() {Id="a",SectionId="section",Text="第一句",NextId="b"}, new() {Id="b",SectionId="section",Text="第二句",NextId="choice"},
                    new() {Id="choice",SectionId="section",Kind="choice",Text="选择路线",Options=new() {new() {Id="one",Label="路线一",TargetId="r",PathId="one",MergeId="merge",Verified=true}}},
                    new() {Id="r",SectionId="section",PathId="one",Text="分支正文",NextId="merge"},new(){Id="merge",SectionId="section",Kind="merge",NextId="tail"},new(){Id="tail",SectionId="section",Text="共同线结束"} } };
            pack.Validate(); string file = Path.Combine(Log.DataDir,"mouse-follow-pack.json"); Json.Save(file,pack); LoadPack(file);
            scene = new Window {Title="轻点跟随隔离场景",Left=150,Top=120,Width=900,Height=600,Background=Brushes.DarkSlateGray,Content=new TextBlock {Text="仅测试进程，不会点击真实游戏",Margin=new Thickness(30)},ShowInTaskbar=false};
            scene.Show(); var handle = new WindowInteropHelper(scene).Handle; game = new(handle,scene.Title,"MouseFixture");
            async Task FocusScene()
            {
                Collapse(false); scene.Show(); scene.Activate(); Native.SetForegroundWindow(handle); await Drain();
                if (Native.GetForegroundWindow()!=handle)
                {
                    uint own=GetCurrentThreadId(),front=GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
                    bool attached=front!=own && AttachThreadInput(own,front,true);
                    try {scene.Activate();Native.SetForegroundWindow(handle);} finally {if(attached)AttachThreadInput(own,front,false);}
                    await Drain();
                }
                if (Native.GetForegroundWindow()!=handle) throw new InvalidOperationException("隔离窗口未取得前台");
            }
            async Task Reset()
            {
                CancelPendingMouseFollow(); StopAutomatic("准备轻点测试",false); HideBranchMenu(); keyTestBox.IsChecked=false;
                preferences.MouseFollowEnabled=true; preferences.ClickZoneEnabled=true; engine!.Commit("a");
                FillLines(); LinesList.SelectedItem = rows.First(r => r.Node.Id == "a");
                UpdateClickZone(); clickZone.PlaceAt(scene.Left+140,scene.Top+160); clickZone.SetDraggable(false); clickZone.UpdateLayout();
                await FocusScene(); DialoguePositionConfirmed(); await Drain();
            }
            ObservedMouseInput Down()
            {
                var center=clickZone.PointToScreen(new Point(32,32)); long now=Stopwatch.GetTimestamp();
                return new("鼠标左键",handle,(int)center.X,(int)center.Y,now,now,new IntPtr(71));
            }
            void End(ObservedMouseInput down,bool accepted=true,string reason="")
            {long now=Stopwatch.GetTimestamp();HandleMouseGesture(new(down,down with {Timestamp=now,ReceivedTimestamp=now},accepted,reason));}

            await Reset(); var input=Down(); int plays=playCalls; HandleMouseGlobal(input); await Drain();
            Check(engine!.CurrentId=="a" && playCalls==plays && pendingMouseFollow!=null,"按下仅准备观察，不提前推进配音");
            End(input); await Drain(); Check(engine.CurrentId=="b" && playCalls==plays+1,"有效松开只跟随一句");
            End(input); await Drain(); Check(engine.CurrentId=="b" && playCalls==plays+1,"重复释放不再次推进");

            foreach(string reason in new[]{"拖动不是轻点","按住时间过长","连续点击过快","点击期间切换了窗口","多个鼠标同时操作","输入到达过晚"})
            {
                await Reset(); input=Down(); plays=playCalls; HandleMouseGlobal(input); End(input,false,reason); await Drain();
                Check(engine.CurrentId=="a" && engine.Mode==RunMode.Paused && playCalls==plays && mouseFollowNotice.Contains(reason),"拒绝并提示："+reason);
            }
            await Reset(); input=Down(); HandleMouseGlobal(input); engine.Commit("b"); int afterManual=playCalls; End(input); await Drain();
            Check(engine.CurrentId=="b" && engine.Mode==RunMode.Following && playCalls==afterManual,"手动改变位置后旧松开不推进也不暂停新位置");
            await Reset(); input=Down(); HandleMouseGlobal(input); scene.Left+=25; await Drain(); End(input); await Drain();
            Check(engine.CurrentId=="a" && engine.Mode==RunMode.Paused,"点击中窗口移动会停住，保留当前位置");
            await Reset(); input=Down(); scene.Width+=20; await Drain(); HandleMouseGlobal(input); End(input); await Drain();
            Check(engine.CurrentId=="a" && engine.Mode==RunMode.Paused,"窗口尺寸变化后需要重新核对热区");
            await Reset(); input=Down(); keyTestBox.IsChecked=true; engine.Commit("a"); plays=playCalls;
            await ObserveMouseTapForTest(input); Check(engine.CurrentId=="a" && playCalls==plays && keyTestStatus.Text.Contains("测试"),"按键诊断只显示轻点输入");
            await Reset(); preferences.MouseFollowEnabled=false; await ObserveMouseTapForTest(Down());
            Check(engine.CurrentId=="a","关闭点按跟随后热区轻点不推进");
            preferences.MouseFollowEnabled=true; await Reset(); engine.PauseForBrowse(); plays=playCalls;
            ConfirmDialogueAnchor(); Check(engine.Mode==RunMode.Following && playCalls==plays,"确认仍是当前句静音恢复，不重播、不改句");
            await ObserveMouseTapForTest(Down()); Check(engine.CurrentId=="b","确认后可再次点按跟随");
            await ObserveMouseTapForTest(Down()); Check(engine.Mode==RunMode.Choice && branchMenu.IsVisible,"遇到分支显示选择菜单，不代选路线");
            string choice=engine.CurrentId!; await ObserveMouseTapForTest(Down()); Check(engine.CurrentId==choice && engine.Mode==RunMode.Choice,"分支菜单中连续点热区不穿过选择");

            branchMenu.Options.SelectedIndex=0; ConfirmSmallBranch(); await FocusScene(); DialoguePositionConfirmed(); plays=playCalls;
            await ObserveMouseTapForTest(Down());
            Check(engine.CurrentId=="tail" && playCalls==plays+1,"关闭对白检查时，一次轻点也跨过虚拟汇合并播放共同线，不少一行");

            await Reset(); input=Down(); HandleMouseGlobal(input); int locateRevision=ocrGeneration;
            await Locate(); int afterLocateRevision=ocrGeneration;
            Check(pendingMouseFollow==null && Tabs.SelectedItem==LocateTab,"主动定位先取消旧轻点观察");
            End(input); TickMouseFollow(); await Drain();
            Check(engine.CurrentId=="a" && engine.Mode==RunMode.Following && ocrGeneration==afterLocateRevision,
                "旧鼠标松开不会暂停引擎或取消新定位流程");

            await Reset(); Expand(StoryTab); input=Down(); await ObserveMouseTapForTest(input);
            Check(engine.CurrentId=="a" && pendingMouseFollow==null,"展开面板时热区不再推进");
            await Reset(); textArmed = true; textOwner = engine; textSection = "section";
            plays = playCalls; StartKeyFollowing();
            Check(!textArmed && !preferences.MouseFollowEnabled && engine.Mode == RunMode.Following && playCalls == plays + 1,
                "开始按键跟随会关闭文字及点按跟随，只播放一次已确认的当前句");
            await Reset(); textArmed = true; textOwner = engine; textSection = "section";
            ToggleMouseFollow();
            Check(!textArmed && preferences.MouseFollowEnabled && engine.Mode == RunMode.Following,
                "从文字切到点按时启动点按，不把保存的旧开关误当成停止操作");
            plays = playCalls; ToggleMouseFollow();
            Check(preferences.MouseFollowEnabled && engine.Mode == RunMode.Following && playCalls == plays + 1,
                "重复点击开始会确认并播放当前句，不因保存的开关反转成停止");
            await Reset(); LinesList.SelectedItem = rows.First(r => r.Node.Id == "b"); plays = playCalls;
            StartKeyFollowing();
            Check(engine.CurrentId == "b" && playCalls == plays + 1, "键盘跟随从台词页新选中的句子开始，不误用旧播放位置");
            await FocusScene();
            HandleGlobal(System.Windows.Input.Key.Space, handle, Stopwatch.GetTimestamp());
            Check(engine.Mode == RunMode.Choice, "启动后真实快捷键路由可继续推进到分支");
            await Reset(); LinesList.SelectedItem = rows.First(r => r.Node.Id == "b"); plays = playCalls;
            ToggleMouseFollow();
            Check(engine.CurrentId == "b" && playCalls == plays + 1, "点按开始会播放选中的当前句，并保持后续输入跟随");
            await Reset();
            using (var pendingBinding = new System.Threading.CancellationTokenSource())
            {
                textProbes[0].Binding = pendingBinding; int revision = textProbes[0].Revision;
                StartKeyFollowing();
                Check(pendingBinding.IsCancellationRequested && textProbes[0].Binding == null && textProbes[0].Revision > revision && !TextFollowing,
                    "切到输入跟随取消未完成的文字连接，旧连接不能回来抢占模式");
            }
            await Reset();
            using (var packets = new RawKeyboardListener(new WindowInteropHelper(this).Handle, (RawKeyInput _) => { }))
            {
                Check(packets.MouseGestureAvailable, "真实 Raw Input 接收器和前台监听已就绪");
                packets.MouseObserved += HandleMouseGlobal;
                packets.MouseGestureObserved += HandleMouseGesture;
                input = Down() with { Device = IntPtr.Zero }; plays = playCalls;
                packets.ObserveMousePacket(input, 1, false, 0);
                Check(pendingMouseFollow != null && engine.CurrentId == "a" && playCalls == plays, "无设备编号的鼠标包进入完整轻点链路，按下不提前播放");
                long release = Stopwatch.GetTimestamp();
                var up = input with { Timestamp = release, ReceivedTimestamp = release };
                packets.ObserveMousePacket(up, 2, false, 0); await Drain();
                Check(engine.CurrentId == "b" && playCalls == plays + 1 && engine.Mode == RunMode.Following,
                    "无设备编号松开后恰好跟随一句，不再报无法确认轻点");
                packets.ObserveMousePacket(up, 2, false, 0); await Drain();
                Check(playCalls == plays + 1, "原始包重复松开不会重复播放");
                await Reset(); plays = playCalls; input = Down() with { Device = IntPtr.Zero };
                packets.ObserveMousePacket(input, 1, false, DesktopAdvanceInput.InputMarker);
                packets.ObserveMousePacket(input, 2, false, DesktopAdvanceInput.InputMarker); await Drain();
                Check(pendingMouseFollow == null && engine.CurrentId == "a" && playCalls == plays,
                    "自身自动点击仍在原始包入口排除，不能当成用户操作重复推进");
            }
            Expand(gameTextTab); followModeTabs.SelectedIndex = 2; await Task.Delay(80); Screenshot("mouse-follow-repaired-ui.png");
            report.Add("SUMMARY: " + report.Count + " passed, 0 failed");
        }
        catch(Exception ex) {report.Add("FAIL: "+ex);}
        finally
        {
            CancelPendingMouseFollow(); File.WriteAllLines(Path.Combine(Log.DataDir,"mouse-follow-ui-test.txt"),report);
            game=null;scene?.Close();Close();
        }
    }
}
