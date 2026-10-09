using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PgrVoice;

public partial class MainWindow
{
    string? dismissedInteractionSection;
    string? interactionNavigationSection;
    bool allInteractionMenus;
    bool resumeOriginalRequested;
    void InteractionsClick(object sender, RoutedEventArgs e) => OpenInteractions();

    void DismissSmallMenu()
    {
        if (branchMenu.IsVisible && branchMenu.IsNavigation) dismissedInteractionSection = interactionNavigationSection;
        HideBranchMenu();
    }

    void OpenInteractions() => ShowInteractionNavigation(true);
    void ToggleNavigationScope() => ShowInteractionNavigation(!branchMenu.IsAllMenus,interactionNavigationSection);
    void ShowInteractionNavigation(bool? allMenus = null,string? sectionId=null)
    {
        CancelInputBranchRecovery("已打开手动分支菜单，自动续接已取消。");
        if (engine == null) { Tell("请先打开章节配音包。"); return; }
        if (engine.Mode == RunMode.Original) { Tell("游戏原声时段不能切换配音路线，请先结束原声时段。"); return; }
        string? section=sectionId ?? (expanded?(SectionBox.SelectedItem as Section)?.Id:null) ?? engine.Current?.SectionId ?? (SectionBox.SelectedItem as Section)?.Id;
        if (section==null || engine.GetStoryMenus(section).Count == 0) { Tell("当前小节没有选择点，可使用章节目录或定位当前台词。"); return; }
        bool hasInteractions=section==engine.Current?.SectionId && engine.InteractionMenus.Count>0;
        if(interactionNavigationSection!=section)
        {
            interactionNavigationSection=section;
            allInteractionMenus=!hasInteractions;
        }
        if(allMenus.HasValue)allInteractionMenus=allMenus.Value;
        if(!hasInteractions)allInteractionMenus=true;
        dismissedInteractionSection = null;
        StopPreview(); CancelOcr(); engine.PauseForBrowse();
        if (expanded) Collapse(false);
        string key=(allInteractionMenus?"story-navigation:":"navigation:")+section;
        int selected = preferences.MenuSelections.GetValueOrDefault(key, 0);
        branchMenu.PresentNavigation(engine, Left + 70, Top, selected, allInteractionMenus,section);
        PublishMenuCapture(); UpdateDialogueMonitor();
        Tell(allInteractionMenus?"按游戏当前选项定位；打开选择点保持静音，未知连接仍需确认。":"按游戏当前遇到的人物选择；打开话题菜单不会播放录音。");
    }

    void ConfirmInteractionNavigation()
    {
        if (engine == null || !branchMenu.IsNavigation || branchMenu.SelectedNavigationId is not string id) return;
        StopPreview(); CancelOcr();
        bool opened=engine.OpenGameMenu(id,interactionNavigationSection);
        if (!opened) { branchMenu.ShowNavigationNotice(engine.NavigationError); Tell(engine.NavigationError); return; }
        singleResume = false; FillLines();
        // Changed 已显示目标菜单；确认只是定位，仍需玩家选择具体话题才播放。
        Tell(engine.Notice);
    }

    void ReturnToTopic()
    {
        if (engine == null) return;
        StopPreview(); CancelOcr();
        bool returned=engine.ParentStoryMenu is { } parent?engine.OpenGameMenu(parent.MenuId):engine.ReturnInteractionTarget!=null?engine.ReturnToInteractionMenu():engine.ReturnToStoryMenu();
        if (!returned) { Tell(engine.NavigationError); return; }
        singleResume = false; FillLines();
    }

    void InteractionScreenshot(string name)
    {
        branchMenu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(branchMenu.ActualWidth), (int)Math.Ceiling(branchMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(branchMenu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
    }

    async Task RunInteractionsUiTest()
    {
        var checks = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add("PASS: " + text); }
        try
        {
            if (engine == null) throw new Exception("请提供第29章修复候选包");
            string prefix = engine.Pack.Chapters.First().Sections.First().Id;
            string Menu(string suffix) => prefix + "-menu-" + suffix;
            void OpenTarget(string suffix)
            {
                OpenInteractions();
                var item = branchMenu.Options.Items.OfType<ListBoxItem>().First(x => (string?)x.Tag == Menu(suffix));
                branchMenu.Options.SelectedItem = item; ConfirmSmallBranch();
            }
            void FinishTopic()
            {
                for (int i = 0; i < 30 && engine.Mode == RunMode.Following; i++) engine.Next(true);
            }
            engine.Restore(engine.Pack.Chapters.First().Sections.First().StartId);
            int calls = playCalls; string? original = engine.CurrentId;
            OpenInteractions(); await Task.Delay(60);
            Check(branchMenu.IsNavigation && branchMenu.IsVisible && engine.CurrentId == original && playCalls == calls, "打开人物导航不跳台词、不播放");
            Check(branchMenu.Options.Items.OfType<ListBoxItem>().Any(x => (string?)x.Tag == Menu("006")) && branchMenu.Options.Items.OfType<ListBoxItem>().Any(x => (string?)x.Tag == Menu("012")), "不要求先听录音即可明确选择戴里克或卡朋特的话题");
            InteractionScreenshot("人物与话题导航.png");
            branchMenu.Move(1); int selected = branchMenu.Options.SelectedIndex; DismissSmallMenu(); OpenStory();
            Check(branchMenu.IsNavigation && branchMenu.Options.SelectedIndex == selected && playCalls == calls, "Esc收起后悬浮球或展开键恢复人物导航和浏览位置，保持静音");
            OpenTarget("006");
            Check(engine.CurrentId == Menu("006") && engine.Mode == RunMode.Choice && !branchMenu.IsNavigation && playCalls == calls, "直接打开戴里克话题菜单，无需重播开场");
            Check(engine.AvailableOptions.Select(x => x.Label).SequenceEqual(new[] { "你的伤势……", "关于那些雾……", "多保重！" }), "戴里克三个话题与游戏截图一致");
            InteractionScreenshot("戴里克话题.png");
            engine.SelectBranch(1); FinishTopic();
            Check(engine.CurrentId == Menu("006") && engine.Mode == RunMode.Choice, "询问雾的台词逐句结束后返回戴里克话题");
            Check(engine.Facts.Count == 0, "听完话题不擅自补算游戏任务条件");
            calls = playCalls; OpenTarget("012");
            Check(engine.CurrentId == Menu("012") && playCalls == calls, "戴里克之后可直接换卡朋特，打开菜单保持静音");
            Check(engine.AvailableOptions.Count == 3 && engine.AvailableOptions[0].Label.Contains("突围") && engine.AvailableOptions[1].Label.Contains("备用零件"), "卡朋特的突围、零件及结束话题完整可选");
            InteractionScreenshot("卡朋特话题.png");
            engine.SelectBranch(0); FinishTopic();
            Check(engine.CurrentId == Menu("012") && engine.Mode == RunMode.Choice, "突围话题结束后返回卡朋特本人话题");
            engine.SelectBranch(2); FinishTopic();
            Check(engine.CurrentId == Menu("000") && engine.Mode == RunMode.Choice, "结束交谈后返回人物菜单，不串到其他人的台词");
            OpenTarget("011"); engine.SelectBranch(1); FinishTopic();
            Check(engine.CurrentId == Menu("011"), "保留另一种卡朋特零件正文及对应返回位置");
            OpenTarget("006"); engine.SelectBranch(0); string? before = engine.CurrentId; calls = playCalls;
            var facts = engine.Facts.ToArray(); var heard = engine.Heard.ToArray();
            OpenTarget("012"); UndoCorrectionClick(this, new());
            Check(engine.CurrentId == before && playCalls == calls && engine.Facts.SetEquals(facts) && engine.Heard.SetEquals(heard), "换人后可撤销并恢复原句和实际经历，保持静音");
            ReturnToTopic();
            Check(engine.CurrentId == Menu("006") && playCalls == calls, "对话中途可直接回本人的话题菜单");
            OpenTarget("012"); OpenTarget("006"); OpenTarget("012");
            Check(engine.CurrentId == Menu("012") && playCalls == calls, "卡朋特与戴里克可反复自由切换，浏览不误播");
            engine.EnterOriginal(); HideBranchMenu(); OpenInteractions();
            Check(engine.Mode == RunMode.Original && !branchMenu.IsVisible && playCalls == calls, "原声时段拒绝换人和播放");
        }
        catch (Exception ex) { checks.Add("FAIL: " + ex); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "interaction-ui-test.txt"), checks); Close();
    }

    async Task RunStoryMenusUiTest(string[] args)
    {
        var checks=new List<string>();
        void Check(bool ok,string text) {if(!ok)throw new Exception(text);checks.Add("PASS: "+text);}
        try
        {
            if(engine==null)throw new Exception("请提供已有章节配音包。");
            int arg=Array.IndexOf(args,"--test-menu-section");
            string? sectionPrefix=arg>=0 && arg+1<args.Length?args[arg+1]:null;
            var sections=engine.Pack.Chapters.SelectMany(c=>c.Sections);
            var section=sectionPrefix!=null?sections.First(s=>s.Title.StartsWith(sectionPrefix,StringComparison.Ordinal)):sections.OrderByDescending(s=>engine.Pack.Nodes.Count(n=>!n.Archived && n.Kind=="choice" && n.SectionId==s.Id)).First();
            engine.Restore(null);SectionBox.SelectedItem=section;int initialCalls=playCalls;
            ShowInteractionNavigation(true);await Task.Delay(40);
            Check(branchMenu.IsNavigation && branchMenu.IsAllMenus && branchMenu.Options.Items.Count==engine.GetStoryMenus(section.Id).Count && engine.CurrentId==null && playCalls==initialCalls,"新章节尚未播放时也可打开所选小节的目录，保持静音");
            engine.Restore(section.StartId);int calls=playCalls;
            string fingerprint=PlaybackEngine.NavigationFingerprint(engine.Pack);
            ShowInteractionNavigation(true);await Task.Delay(80);
            Check(branchMenu.IsNavigation && branchMenu.IsAllMenus && branchMenu.IsVisible,"其它章节可打开本节全部选择点");
            Check(branchMenu.Options.Items.Count==engine.Pack.Nodes.Count(n=>!n.Archived && n.Kind=="choice" && n.SectionId==section.Id),"目录覆盖当前小节全部有效选择点，未混入其它小节");
            Check(engine.CurrentId==section.StartId && playCalls==calls,"查看目录不改变位置、不播放");
            InteractionScreenshot("本节选择点.png");
            branchMenu.Move(1);int selected=branchMenu.Options.SelectedIndex;
            DismissSmallMenu();OpenStory();
            Check(branchMenu.IsAllMenus && branchMenu.Options.SelectedIndex==selected && playCalls==calls,"收起后重新展开保留目录范围和选择位置");
            var target=engine.StoryMenus.First(t=>t.CanOpen);
            branchMenu.Options.SelectedItem=branchMenu.Options.Items.OfType<ListBoxItem>().First(x=>(string?)x.Tag==target.MenuId);
            ConfirmSmallBranch();
            Check(engine.CurrentId==target.MenuId && engine.Mode==RunMode.Choice && !branchMenu.IsNavigation && playCalls==calls,"明确确认只打开选择点，仍需选择具体选项才播放");
            Check(engine.Facts.Count==0 && engine.Heard.Count==0,"定位没有补写任务完成或已听状态");
            InteractionScreenshot("定位后的选择菜单.png");
            UndoCorrectionClick(this,new());
            Check(engine.CurrentId==section.StartId && playCalls==calls,"撤销菜单定位恢复原位置，保持静音");
            ShowInteractionNavigation(true);
            var blocked=engine.StoryMenus.FirstOrDefault(t=>!t.CanOpen && t.FallbackMenuId!=null);
            if(blocked!=null)
            {
                branchMenu.Options.SelectedItem=branchMenu.Options.Items.OfType<ListBoxItem>().First(x=>(string?)x.Tag==blocked.MenuId);
                ConfirmSmallBranch();
                Check(engine.CurrentId==blocked.MenuId && engine.Mode==RunMode.Choice && playCalls==calls && engine.Facts.Count==0,"玩家明确定位深层菜单，不要求重读前置、不伪造条件、不播放");
            }
            engine.EnterOriginal();HideBranchMenu();OpenInteractions();
            Check(engine.Mode==RunMode.Original && !branchMenu.IsVisible && playCalls==calls,"原声模式拒绝定位和播放");
            Check(PlaybackEngine.NavigationFingerprint(engine.Pack)==fingerprint,"目录导航未改动配音包路线定义");
        }
        catch(Exception ex){checks.Add("FAIL: "+ex);}
        File.WriteAllLines(Path.Combine(Log.DataDir,"story-menu-ui-test.txt"),checks);Close();
    }
}
