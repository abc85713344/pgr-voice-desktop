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

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunBranchAnchorUiChecks(Action<bool,string> check,string root)
    {
        if(!testUi)throw new InvalidOperationException("首句卡片测试只允许隔离入口。");
        var pack=new Pack{SchemaVersion=3,Id="anchor-cards-ui",Title="首句卡片布局",Root=root,
            Chapters=new(){new(){Id="book",Sections=new(){new(){Id="section",StartId="menu"}}}},
            Nodes=new(){new(){Id="menu",SectionId="section",Kind="choice",Text="手动备用分支"}}};
        var menu=pack.Nodes[0];
        for(int i=0;i<5;i++)
        {
            string id="option-"+i,line="first-"+i,gap="gap-"+i;
            menu.Options.Add(new(){Id=id,Label="路线"+i,PathId=id,TargetId=line,BodyVerified=true,BoundaryId=gap,
                LineIds=new(){line},SegmentIds=new(){line},BodyEvidence=new(){"isolated-anchor-fixture"}});
            pack.Nodes.Add(new(){Id=line,SectionId="section",PathId=id,Speaker="角色"+i,Text="这是第"+i+"条路线真实开头的完整正文。"+new string('长',i==4?180:0),Audio="voice.wav"});
            pack.Nodes.Add(new(){Id=gap,SectionId="section",PathId=id,Kind="gap"});
        }
        pack.Validate();string file=Path.Combine(root,"anchor-cards.json");Json.Save(file,pack);LoadPack(file);
        StopGameText("手动卡片夹具");HideBranchMenu();engine!.OpenGameMenu("menu","section");
        branchMenu.Present(engine,50,250,0,anchors:true);await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);branchMenu.UpdateLayout();
        check(branchMenu.IsAnchorView && branchMenu.Options.Items.Count==4 && branchMenu.AnchorPage==0,
            "手动备用首句入口每页最多四张卡片，完整五支保留分页");
        var items=branchMenu.Options.Items.OfType<ListBoxItem>().ToArray();
        Point[] points=items.Select(x=>x.TransformToAncestor(branchMenu).Transform(new Point())).ToArray();
        check(points[0].Y==points[1].Y && points[2].Y==points[3].Y && points[2].Y>points[0].Y && points[1].X>points[0].X,
            "首句卡片真实WPF布局为顶部两排两列，横向分布不堆在左侧");
        check(branchMenu.Top<=SystemParameters.WorkArea.Top+20 && branchMenu.ActualHeight<=440,
            "手动首句窗位于屏幕顶部且高度受限，给游戏正文留出空间");
        string nav=System.Text.Json.JsonSerializer.Serialize(engine.ExportNavigation());int plays=playCalls;var stale=branchMenu.AnchorOffer!;string staleCard=branchMenu.SelectedAnchor!.Id;
        branchMenu.ChangeAnchorPage(1);branchMenu.UpdateLayout();
        check(branchMenu.AnchorPage==1 && branchMenu.Options.Items.Count==1 && branchMenu.SelectedAnchor?.NodeId=="first-4"
            && System.Text.Json.JsonSerializer.Serialize(engine.ExportNavigation())==nav && playCalls==plays,
            "分页显示第五支且不自动播放或改进度");
        Button FindButton(DependencyObject rootElement,string label)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(rootElement);i++)
            {
                var child=VisualTreeHelper.GetChild(rootElement,i);
                if(child is Button b && b.Content?.ToString()==label)return b;
                try{return FindButton(child,label);}catch(InvalidOperationException){}
            }
            throw new InvalidOperationException("未找到按钮："+label);
        }
        async Task Invoke(string label)
        {
            var button=FindButton(branchMenu,label);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        await Invoke("展开 / 收起完整句");branchMenu.UpdateLayout();
        check(branchMenu.ActualHeight<=440 && playCalls==plays,"长首句可展开且限高滚动，展开不发声");
        ScreenshotBranchAnchor("手动首句卡片-分页长句.png");
        await Invoke("游戏正显示这句 · 确认");
        check(engine.CurrentId=="first-4" && playCalls==plays+1 && engine.Choices.GetValueOrDefault("menu")=="option-4",
            "卡片UIA确认走原SelectBranch，先播放选中首句且保存原路线选择");
        check(!BranchAnchorPolicy.TryResolve(engine,stale,staleCard,out _,out _) && playCalls==plays+1,
            "确认后旧首句offer失效，不能再跳另一支");
        engine.Next(true);check(engine.Mode==RunMode.Gap,"首句卡片不放开原未知出口");
        // 第二句只在玩家明确点中后定位，不能默认替代与选项同文的首句。
        pack.Nodes.Single(n=>n.Id=="first-0").NextId="second-0";
        pack.Nodes.Add(new(){Id="second-0",SectionId="section",PathId="option-0",Speaker="旁白",Text="游戏已经显示的下一条真实正文。",Audio="voice.wav"});
        menu.Options[0].SegmentIds.Add("second-0");menu.Options[0].LineIds.Add("second-0");
        pack.Validate();string twoLines=Path.Combine(root,"anchor-two-lines.json");Json.Save(twoLines,pack);LoadPack(twoLines);
        engine!.OpenGameMenu("menu","section");branchMenu.Present(engine,50,250,0,anchors:true);
        check(branchMenu.AnchorOffer!.Cards.Count(c=>c.OptionId=="option-0")==2 && branchMenu.SelectedAnchor?.NodeId=="first-0",
            "同一支已核开头两句同时保留，默认仍展示第一句而非悄悄跳过");
        branchMenu.Options.SelectedItem=branchMenu.Options.Items.OfType<ListBoxItem>().Single(i=>(i.Tag as BranchAnchorCard)?.NodeId=="second-0");
        plays=playCalls;await Invoke("游戏正显示这句 · 确认");
        check(engine.CurrentId=="second-0" && playCalls==plays+1 && engine.Choices.GetValueOrDefault("menu")=="option-0",
            "玩家明确点第二句才按游戏位置播放第二句，不补播第一句或串支");
        engine.OpenGameMenu("menu","section");branchMenu.Present(engine,50,250,0,anchors:true);plays=playCalls;await Invoke("游戏正显示这句 · 确认");
        check(engine.CurrentId=="first-0" && playCalls==plays+1,"明确点首句仍先播首句，不被第二句卡片挤掉");
        engine.Next(true);check(engine.CurrentId=="second-0" && playCalls==plays+2,"首句后新的推进顺序播放第二句，保持原分支导航");
        // 两条同角色同正文仍保留两张卡；不删一张制造唯一性。
        pack.Nodes.Single(n=>n.Id=="first-1").Text=pack.Nodes.Single(n=>n.Id=="first-0").Text;
        pack.Nodes.Single(n=>n.Id=="first-1").Speaker=pack.Nodes.Single(n=>n.Id=="first-0").Speaker;
        pack.Validate();string ambiguous=Path.Combine(root,"anchor-ambiguous.json");Json.Save(ambiguous,pack);LoadPack(ambiguous);
        engine!.OpenGameMenu("menu","section");branchMenu.Present(engine,50,250,0,anchors:true);branchMenu.UpdateLayout();
        check(branchMenu.AnchorOffer!.Cards.Count(c=>c.IsAmbiguous)==2 && !branchMenu.CanConfirm,
            "同角色同文首句两支均保留并标明待核，不把删掉一支当唯一锚定");
        plays=playCalls;ConfirmSmallBranch();check(playCalls==plays && engine.Mode==RunMode.Choice,
            "同文首句确认被拒且仍可回原选项，不错误播放");
        ScreenshotBranchAnchor("手动首句卡片-顶部两排.png");
        HideBranchMenu();
    }

    void ScreenshotBranchAnchor(string name)
    {
        branchMenu.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(branchMenu.ActualWidth),(int)Math.Ceiling(branchMenu.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(branchMenu);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(Log.DataDir,name));png.Save(file);
    }
}
