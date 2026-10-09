using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PgrVoice;

public partial class MainWindow
{
    bool manualContinuationBrowsing;
    readonly WrapPanel manualContinuationTools=new(){Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,4)};
    void InitializeManualContinuationView()
    {
        AddButton(manualContinuationTools,"展开台词窗口",()=>{preferences.PanelSizeCustomized=true;SetExpandedPanelSize(Math.Max(960,Width),Math.Max(860,Height));ApplyCompactLayout();Save();});
        AddButton(manualContinuationTools,"返回分支选项",()=>
        {
            manualContinuationBrowsing=false;ApplyCompactLayout();
            if(engine?.MenuWaiting==true)ShowBranchMenu();
            else Tell("当前位置不在待选菜单，原位置保持不变。");
        });
        AddButton(manualContinuationTools,"恢复普通台词页",()=>{manualContinuationBrowsing=false;ApplyCompactLayout();});
        manualContinuationTools.Children.Add(new TextBlock{Text="本节全部正文 · 仅浏览不改变路线",FontSize=11,VerticalAlignment=VerticalAlignment.Center,Foreground=Theme.Brush("MutedText")});
        if(NavigationToolbar.Parent is StackPanel host)host.Children.Insert(0,manualContinuationTools);
        SizeChanged+=(_,_)=>{if(manualContinuationBrowsing)ApplyCompactLayout();};
        Tabs.SelectionChanged+=(_,e)=>
        {
            if(ReferenceEquals(e.Source,Tabs) && Tabs.SelectedItem!=StoryTab && manualContinuationBrowsing)
            {manualContinuationBrowsing=false;ApplyCompactLayout();}
        };
    }
    void OpenManualContinuation()
    {
        if(engine==null)return;
        singleResume=engine.ReviewRoute!=null;HideBranchMenu();
        manualContinuationBrowsing=true;SearchBox.Clear();SearchChapterBox.IsChecked=false;showAllSectionLines.IsChecked=true;
        Expand(StoryTab);manualContinuationBrowsing=true;ApplyCompactLayout();BrowseCurrent();
        if(!preferences.PanelSizeCustomized){SetExpandedPanelSize(960,860);ApplyCompactLayout();}
        // 当前可能是目录尾部的虚拟菜单；优先返回最后实际经历的正文附近。
        string? section=engine.Current?.SectionId;
        var previous=engine.History.LastOrDefault(v=>engine.Pack.ById.TryGetValue(v.NodeId,out var n) && n.SectionId==section);
        var nearby=rows.FirstOrDefault(r=>r.Node.Id==previous?.NodeId) ?? rows.FirstOrDefault(r=>r.Node.SectionId==section && r.Node.NextId==engine.CurrentId)
            ?? rows.FirstOrDefault(r=>r.Node.Kind=="line" && engine.Current?.Options.Any(o=>o.LineIds.Contains(r.Node.Id))==true)
            ?? rows.FirstOrDefault(r=>r.Node.Kind=="line");
        if(nearby!=null){LinesList.SelectedItem=nearby;LinesList.UpdateLayout();LinesList.ScrollIntoView(nearby);}
        LinesList.Focus();
        Tell("已显示本节全部台词，可滚动查找。按游戏画面确认当前句；目录顺序不代表已核实的游戏连接。");
    }
    void ApplyManualContinuationLayout()
    {
        if(CurrentCard==null || NavigationToolbar==null)return;
        bool active=manualContinuationBrowsing && Tabs.SelectedItem==StoryTab;
        manualContinuationTools.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        NavigationToolbar.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        CurrentCard.Visibility=Tabs.SelectedItem==StoryTab && !active?Visibility.Visible:Visibility.Collapsed;
        ManualPlaybackButtons.Visibility=Tabs.SelectedItem==StoryTab && !active?Visibility.Visible:Visibility.Collapsed;
        BranchBox.Visibility=Tabs.SelectedItem==StoryTab && !active && engine?.Mode==RunMode.Choice?Visibility.Visible:Visibility.Collapsed;
        if(!active){LinesList.MinHeight=0;return;}
        Hero.Height=Height>=760?70:44;HeroEyebrow.Visibility=Visibility.Collapsed;HeroTitleStack.Margin=new Thickness(18,7,0,0);
        HeroHeading.Margin=new Thickness(0);HeroTitle.FontSize=21;HeaderChapter.Visibility=Height>=760?Visibility.Visible:Visibility.Collapsed;
        PlaybackBar.Padding=new Thickness(12,6,12,6);ConfirmPlayButton.Padding=new Thickness(10,6,10,6);ConfirmPlayButton.Margin=new Thickness(0,0,0,4);
        LinesList.MinHeight=180;
        ScrollViewer.SetVerticalScrollBarVisibility(LinesList,ScrollBarVisibility.Visible);
    }
    void SetExpandedPanelSize(double width,double height)
    {
        var area=SystemParameters.WorkArea;
        double maxWidth=Math.Max(200,area.Width-20),maxHeight=Math.Max(240,area.Height-20);
        Width=Math.Clamp(double.IsFinite(width)?width:580,Math.Min(420,maxWidth),maxWidth);
        Height=Math.Clamp(double.IsFinite(height)?height:760,Math.Min(480,maxHeight),maxHeight);
        Left=Math.Clamp(double.IsFinite(Left)?Left:area.Left+10,area.Left,Math.Max(area.Left,area.Right-Width));
        Top=Math.Clamp(double.IsFinite(Top)?Top:area.Top+10,area.Top,Math.Max(area.Top,area.Bottom-Height));
    }
    void PanelResizeDrag(object sender,DragDeltaEventArgs e)
    {
        if(!expanded)return;
        double dx=ReferenceEquals(sender,PanelResizeBottom)?0:e.HorizontalChange;
        double dy=ReferenceEquals(sender,PanelResizeRight)?0:e.VerticalChange;
        preferences.PanelSizeCustomized=true;SetExpandedPanelSize(Width+dx,Height+dy);ApplyCompactLayout();e.Handled=true;
    }
    void PanelResizeDone(object sender,DragCompletedEventArgs e)
    {if(expanded){preferences.PanelWidth=Width;preferences.PanelHeight=Height;Save();}e.Handled=true;}
}
