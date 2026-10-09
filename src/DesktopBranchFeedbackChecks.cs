using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunDesktopBranchFeedbackUiChecks(Action<bool, string> check, string root)
    {
        if (!testUi) throw new InvalidOperationException("分支反馈验收仅允许独立测试入口。");
        StopGameText("开始分支显示与手动入口验收"); HideBranchMenu(); StopListeningForGame();
        var pack = new Pack { Id="desktop-branch-feedback", Title="分支标识与续接验收", SchemaVersion=3, Root=root,
            Chapters=new(){new(){Id="chapter", Sections=new(){new(){Id="s",Title="普通与嵌套分支",StartId="before"}}}},
            Nodes=new() {
                new(){Id="before",SectionId="s",Speaker="旁白",Text="选择前的共同台词。",Audio="voice.wav",NextId="menu"},
                new(){Id="menu",SectionId="s",Kind="choice",Text="三个选择"},
                new(){Id="a1",SectionId="s",PathId="a",Speaker="甲",Text="第一条路线正文。",Audio="voice.wav",NextId="join"},
                new(){Id="b1",SectionId="s",PathId="b",Speaker="乙",Text="第二条路线正文。",Audio="voice.wav",NextId="inner"},
                new(){Id="inner",SectionId="s",PathId="b",Kind="choice",Text="内层两个选择"},
                new(){Id="x1",SectionId="s",PathId="x",Speaker="甲",Text="内层第一条路线正文。",Audio="voice.wav",NextId="inner-join"},
                new(){Id="y1",SectionId="s",PathId="y",Speaker="乙",Text="内层第二条路线正文。",Audio="voice.wav",NextId="inner-join"},
                new(){Id="inner-join",SectionId="s",PathId="b",Kind="merge",NextId="b2"},
                new(){Id="b2",SectionId="s",PathId="b",Speaker="乙",Text="返回外层第二条路线。",Audio="voice.wav",NextId="join"},
                new(){Id="c1",SectionId="s",PathId="c",Speaker="丙",Text="第三条路线正文。",Audio="voice.wav",NextId="join"},
                new(){Id="join",SectionId="s",Kind="merge",NextId="common"},
                new(){Id="common",SectionId="s",Speaker="旁白",Text="三条路线之后的共同正文。",Audio="voice.wav",NextId="end"},
                new(){Id="gap",SectionId="s",Kind="gap",Text="后续连接资料仍待确认",ResumeMenuIds=new(){"menu"}},
                new(){Id="end",SectionId="s",Kind="end"}
            }};
        var menu=pack.Nodes.Single(n=>n.Id=="menu"); var inner=pack.Nodes.Single(n=>n.Id=="inner");
        ChoiceOption Route(string id,string path,string first,string join,params string[] segment) => new() {
            Id=id,PathId=path,Label=id,TargetId=first,ReturnId=join,MergeId=join,BodyVerified=true,ExitVerified=true,
            SegmentIds=segment.ToList(),LineIds=segment.Where(x=>pack.Nodes.Single(n=>n.Id==x).Kind=="line").ToList(),
            BodyEvidence=new(){"isolated-feedback-test"},ExitEvidence=new(){"isolated-feedback-test"}};
        menu.Options.Add(Route("第一项","a","a1","join","a1"));
        menu.Options.Add(Route("第二项","b","b1","join","b1","inner","inner-join","b2"));
        menu.Options.Add(Route("第三项","c","c1","join","c1"));
        inner.Options.Add(Route("内层第一项","x","x1","inner-join","x1"));
        inner.Options.Add(Route("内层第二项","y","y1","inner-join","y1"));
        pack.Validate(); string file=Path.Combine(root,"desktop-branch-feedback.json"); Json.Save(file,pack); LoadPack(file);
        preferences.CompactMode="strip";preferences.CompactControlsEnabled=true;
        async Task Drain(){UpdateExperienceStatus();UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
        async Task Choose(string menuId,int index){HideBranchMenu();engine!.OpenGameMenu(menuId,"s");engine.SelectBranch(index);HideBranchMenu();Collapse(false);await Drain();}
        async Task Invoke(Button button){((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();await Drain();}
        await Choose("menu",0);
        check(StripText.Text=="分支一：第一条路线正文。" && engine!.Current!.Text=="第一条路线正文。",
            "第一支编号只在显示层添加，不改正文与当前节点");
        string nav=JsonSerializer.Serialize(engine!.ExportNavigation()); int plays=playCalls;
        UpdateState();await Drain();
        check(nav==JsonSerializer.Serialize(engine.ExportNavigation()) && plays==playCalls,"刷新分支编号不写路线或重复播放");
        await Choose("menu",1);
        check(StripText.Text=="分支二：第二条路线正文。","按实际第二个选项显示分支二");
        var missing=engine.Pack.ById["b1"];string? savedAudio=missing.Audio;missing.Audio=null;engine.Replay();await Drain();
        check(CurrentBranchWaitReason().Length>0 && compactFollowActions["continuation"].IsEnabled &&
            !StripState.Text.Contains("跟随中"),"手动进入的分支句缺音也明确等待原因并提供续接");
        missing.Audio=savedAudio;engine.Replay();await Drain();
        check(CurrentBranchWaitReason().Length==0 && !compactFollowActions["continuation"].IsEnabled,
            "同一句恢复有效音频并重播后清掉旧播放失败反馈");
        await Choose("inner",0);
        check(StripText.Text=="分支一：内层第一条路线正文。","外层第二支进入内层第一支后显示内层实际编号");
        engine.ConfirmGameLine("b2");await Drain();
        check(StripText.Text=="分支二：返回外层第二条路线。","离开内层后恢复外层第二支，不使用最近一次选择编号");
        engine.ConfirmGameLine("common");await Drain();
        check(StripText.Text=="三条路线之后的共同正文。" && GameBranchLabel().Length==0,"返回共同线清除分支编号");
        await Choose("menu",2);
        check(StripText.Text=="分支三：第三条路线正文。","第三项不误标为分支二");
        var duplicate=new Node{Id="duplicate-owner",SectionId="s",Kind="choice",Options=new(){menu.Options[2]}};
        engine.Pack.Nodes.Add(duplicate);await Drain();
        check(StripText.Text==engine.Current!.Text,"同一路径归属多个菜单时不猜分支编号");
        engine.Pack.Nodes.Remove(duplicate);await Drain();
        previewingHistory=true;await Drain();check(GameBranchLabel().Length==0,"历史预览不标成当前分支跟随");previewingHistory=false;
        engine.CommitSingle("c1");await Drain();check(GameBranchLabel().Length==0 && StripText.Text==engine.Current!.Text,"单句试听不冒充已接入分支");
        await Choose("menu",1);engine.EnterOriginal();await Drain();check(GameBranchLabel().Length==0,"原声时段不显示游戏配音分支编号");

        engine.ConfirmGameLine("common",resumeOriginal:true);
        engine.OpenGameMenu("menu","s");HideBranchMenu();Collapse(false);await Drain();
        check(compactFollowActions["continuation"].IsVisible && compactFollowActions["continuation"].IsEnabled &&
            StripState.Text.Contains("游戏"),"小台词条等待状态直接提供手动续接与游戏选择说明");
        plays=playCalls;string beforeId=engine.CurrentId!;int history=engine.History.Count;
        string choices=JsonSerializer.Serialize(engine.Choices);
        await Invoke(compactFollowActions["continuation"]);
        check(expanded && Tabs.SelectedItem==StoryTab && manualContinuationBrowsing && showAllSectionLines.IsChecked==true &&
            rows.All(r=>r.Node.Kind=="line"),"点击手动续接直达可滚动的全部正文目录");
        check(engine.CurrentId==beforeId && engine.History.Count==history && JsonSerializer.Serialize(engine.Choices)==choices && playCalls==plays,
            "手动续接入口只浏览，不定位、不改选支或请求播放");
        check(!textArmed && inputBranchRecovery==null,"打开手动续接后旧自动正文监听已撤销");
        LinesList.SelectedItem=rows.Single(r=>r.Node.Id=="a1");await Drain();
        check(playCalls==plays && engine.CurrentId==beforeId,"单击预览另一支正文不会播放或改变实际位置");
        await Invoke(ConfirmPlayButton);
        check(engine.CurrentId=="a1" && playCalls==plays+1,"明确确认游戏当前句才请求播放一次");

        engine.OpenGameMenu("menu","s");HideBranchMenu();preferences.CompactControlsEnabled=false;Collapse(false);await Drain();
        check(!compactFollowButtons.IsVisible && stripContinuationButton.IsVisible && stripContinuationButton.IsEnabled,
            "关闭一般快捷按钮仍保留等待时的直接续接入口");
        var bounds=stripContinuationButton.TransformToAncestor(this).TransformBounds(new Rect(stripContinuationButton.RenderSize));
        check(bounds.Width>0 && bounds.Height>0 && bounds.Bottom<=ActualHeight+1 && bounds.Right<=ActualWidth+1,
            "无快捷按钮的小台词条仍完整显示手动续接入口");
        check(stripContinuationButton.Focusable && stripContinuationButton.IsTabStop,"独立续接按钮保留键盘焦点能力");
        Screenshot("分支等待-关闭快捷按钮仍可续接.png");
        plays=playCalls;await Invoke(stripContinuationButton);
        check(manualContinuationBrowsing && playCalls==plays,"独立续接按钮也只打开目录而不播放");
        preferences.CompactControlsEnabled=true;
        engine.ConfirmGameLine("common");Collapse(false);await Drain();
        check(!stripContinuationButton.IsVisible && !compactFollowActions["continuation"].IsEnabled &&
            CurrentBranchWaitReason().Length==0,"确认共同线后移除旧等待原因与续接按钮状态");
        engine.Restore("gap");engine.OpenMenu();HideBranchMenu();Collapse(false);await Drain();
        check(StripState.Text.Contains("尚未确认") && compactFollowActions["continuation"].IsEnabled &&
            !StripState.Text.Contains("已结束"),"未知连接明确等待并提供续接，不冒称路线结束");
        Screenshot("分支等待-未知连接与续接按钮.png");
        await Choose("menu",1);Screenshot("分支二-实际路线编号.png");
        StopGameText("分支反馈检查完成");HideBranchMenu();
    }
}
