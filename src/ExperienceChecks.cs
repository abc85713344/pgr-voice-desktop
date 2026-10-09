using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PgrVoice;
public partial class MainWindow
{
    async Task RunUpgradeUiTest()
    {
        var results=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new Exception(text);results.Add("PASS: "+text);}
        try
        {
            var baseline=Json.Read<Preferences>(Path.Combine(Log.DataDir,"before.json"));
            Check(engine?.CurrentId==baseline.NodeId && engine?.Mode==RunMode.Ready,"真实旧进度迁移后位置保留、保持静音");
            Check(engine!.History.Count==baseline.Visits.Count && engine.HistoryPosition==baseline.VisitPosition,"旧实际访问历史完整保留");
            Check(baseline.Keys.All(x=>preferences.Keys.GetValueOrDefault(x.Key)==x.Value) && Math.Abs(preferences.Volume-baseline.Volume)<.00001,"自定义按键与音量保留");
            int calls=playCalls;string original=preferences.PackFile;
            var other=LibraryBox.Items.OfType<PackChoice>().First(x=>x.File!=original);
            LoadPack(other.File);LoadPack(original);
            Check(engine.CurrentId==baseline.NodeId && engine.History.Count==baseline.Visits.Count && playCalls==calls,"实际章节切换后旧进度恢复且不发声");
            Check(progressStore!.HasProgress(engine.Pack.Id),"旧唯一进度已进入独立章节存档");
            Expand(StoryTab);SearchBox.Clear();BrowseCurrent();await Task.Delay(100);
            var selected=LinesList.SelectedItem;var container=LinesList.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement;
            Check(container!=null && container.TranslatePoint(new Point(0,0),LinesList).Y>=-1 && container.TranslatePoint(new Point(0,0),LinesList).Y<LinesList.ActualHeight,"回到当前句会将真实进度滚入可见列表");
            Screenshot("升级后主面板.png");
        }
        catch(Exception ex){results.Add("FAIL: "+ex);}
        File.WriteAllLines(Path.Combine(Log.DataDir,"upgrade-ui-test.txt"),results);Close();
    }
    async Task RunExperienceUiTest()
    {
        var results=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new Exception(text);results.Add("PASS: "+text);}
        string Fixture(string id)
        {
            string folder=Path.Combine(Log.DataDir,"fixtures",id);Directory.CreateDirectory(folder);
            var pack=new Pack{Id=id,Title="导航验收 · "+id,Root=folder,Chapters=new(){new(){Id="chapter",Title="测试章节",Sections=new(){new(){Id="section",Title="营地 → 人物 → 话题",StartId="start"}}}},Nodes=new()
            {
                new(){Id="start",SectionId="section",Speaker="旁白",Text="大家在营地集合。",NextId="people"},
                new(){Id="people",SectionId="section",Kind="choice",Text="营地 → 选择人物",Options=new(){new(){Id="choose-a",Label="栗西",PathId="a",TargetId="topics",MergeId="merge",Verified=true,Preview="进入栗西的话题菜单"},new(){Id="choose-b",Label="戴里克",PathId="b",TargetId="b1",MergeId="merge",Verified=true,Preview="先去找栗西了解情况吧……"}}},
                new(){Id="topics",SectionId="section",Kind="choice",PathId="a",Text="营地 → 栗西 → 选择话题",Options=new(){new(){Id="choose-t1",Label="关于现状",PathId="t1",TargetId="t1a",MergeId="merge",Verified=true,Preview="我们先谈谈现状。"},new(){Id="choose-t2",Label="关于营地",PathId="t2",TargetId="t2a",MergeId="merge",Verified=true,Preview="营地就在这里。"}}},
                new(){Id="t1a",SectionId="section",PathId="t1",Speaker="栗西",Text="我们先谈谈现状。",NextId="t1b"},
                new(){Id="t1b",SectionId="section",PathId="t1",Speaker="栗西",Text="大家都需要一点时间。",NextId="merge"},
                new(){Id="t2a",SectionId="section",PathId="t2",Speaker="栗西",Text="营地就在这里。",NextId="merge"},
                new(){Id="b1",SectionId="section",PathId="b",Speaker="戴里克",Text="先去找栗西了解情况吧……",NextId="merge"},
                new(){Id="merge",SectionId="section",Kind="merge",Text="返回共同线",NextId="common"},
                new(){Id="common",SectionId="section",Speaker="旁白",Text="大家重新汇合。",NextId="end"},
                new(){Id="end",SectionId="section",Kind="end"}
            }};
            pack.Validate();string path=Path.Combine(folder,"pack.json");Json.Save(path,pack);return path;
        }
        try
        {
            string a=Fixture("experience-a"),b=Fixture("experience-b");
            LoadPack(a);engine!.Commit("start");engine.Next(true);
            int calls=playCalls;engine.SelectBranch(0);
            Check(engine.CurrentId=="topics" && playCalls==calls,"人物直接进入话题菜单保持静音");
            engine.SelectBranch(0);engine.Next(true);string original=engine.CurrentId!;calls=playCalls;
            Reselect();await Task.Delay(80);
            Check(engine.CurrentId=="topics" && engine.Mode==RunMode.Choice && playCalls==calls && branchMenu.IsVisible,"一键重选恢复最近菜单且不误播");
            branchMenu.Move(1);HideBranchMenu();OpenStory();
            Check(branchMenu.Options.SelectedIndex==1,"重选菜单浏览后收起再开保留选项");
            ConfirmSmallBranch();Check(engine.CurrentId=="t2a" && playCalls==calls+1,"确认重选只播放新路线首句");
            UndoCorrectionClick(this,new());Check(engine.CurrentId==original && playCalls==calls+1 && engine.Mode==RunMode.Paused,"撤销纠偏静音恢复原路线与位置");
            OpenHistory();calls=playCalls;string current=engine.CurrentId!;
            historyList.SelectedIndex=Math.Min(1,historyList.Items.Count-1);
            Check(playCalls==calls && engine.CurrentId==current,"浏览履历不播放不改变位置");
            // Fixture has no audio; preview must report the missing binding without changing the story.
            PreviewHistory();Check(engine.CurrentId==current && playCalls==calls,"履历缺音提示不推进剧情");
            SaveBookmark();Check(progressStore!.Bookmarks(engine.Pack.Id).Count>0,"完整路线书签可保存");
            Save();LoadPack(b);engine!.Commit("start");LoadPack(a);
            Check(engine!.CurrentId==current && engine.Mode==RunMode.Ready,"切换章节后独立进度静音恢复");
            calls=playCalls;FocusCatalog();await Task.Delay(120);Check(chapterPickerButtons[LibraryBox].Chapter.IsKeyboardFocusWithin,"F4聚焦可见章节目录");
            SearchBox.Text="栗西 现状";SearchBox.Focus();
            OnPreviewKey(SearchBox,new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(this),Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=SearchBox});
            Check(playCalls==calls && LinesList.IsKeyboardFocusWithin && rows.Any(r=>r.Node.Id=="t1a"),"角色加正文搜索，回车进入结果且不误播");
            keyTestBox.IsChecked=true;string before=engine.CurrentId!;HandleTestKey(Key.PageDown);
            Check(engine.CurrentId==before && playCalls==calls && keyTestStatus.Text.Contains("没有执行"),"按键测试显示绑定而不推进");keyTestBox.IsChecked=false;
            engine.EnterOriginal();OpenHistory();PreviewHistory();Reselect();
            Check(engine.Mode==RunMode.Original && engine.CurrentId==before && playCalls==calls,"原声模式允许查看历史但不试听或改选");
            engine.Restore("start");engine.Commit("start");engine.Next(true);engine.SelectBranch(0);engine.SelectBranch(0);
            OpenHistory();Screenshot("台词履历.png");historyFilter.SelectedIndex=1;Screenshot("最近选择.png");historyFilter.SelectedIndex=2;Screenshot("书签.png");
            Expand(StoryTab);BrowseCurrent();Screenshot("新版主面板.png");
            preferences.CompactMode="strip";Collapse(false);await Task.Delay(60);Screenshot("小台词条.png");
            Check(StripView.IsVisible && !BallView.IsVisible && Width>64,"小台词条与悬浮球正确切换");
            preferences.CompactMode="ball";Collapse(false);
            preferences.ReadingScale=2;ApplyReadingScale();Expand(StoryTab);await Task.Delay(40);
            Check(CurrentText.FontSize==30,"200%阅读字号生效");Screenshot("大字阅读.png");preferences.ReadingScale=1;ApplyReadingScale();
            Reselect();await Task.Delay(50);
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)branchMenu.ActualWidth,(int)branchMenu.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(branchMenu);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var f=File.Create(Path.Combine(Log.DataDir,"重选分支.png")))encoder.Save(f);
            Expand(SettingsTab);Screenshot("新增设置.png");
        }
        catch(Exception ex){results.Add("FAIL: "+ex);}
        File.WriteAllLines(Path.Combine(Log.DataDir,"experience-ui-test.txt"),results);
        Close();
    }
}
