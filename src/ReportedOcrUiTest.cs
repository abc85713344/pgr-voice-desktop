using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunReportedOcrUiTest(string[] args)
    {
        var checks=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add("PASS: "+text);}
        try
        {
            if(engine==null)throw new Exception("需要第29章实际配音包");
            var section=engine.Pack.Chapters.SelectMany(c=>c.Sections).First(s=>s.Title.StartsWith("29-1 "));
            engine.Restore(section.StartId);SectionBox.SelectedItem=section;
            int calls=playCalls;var facts=engine.Facts.ToArray();
            OpenInteractions();await Task.Delay(80);
            string menuId=section.Id+"-menu-020";
            branchMenu.Options.SelectedItem=branchMenu.Options.Items.OfType<ListBoxItem>().Single(x=>(string?)x.Tag==menuId);
            branchMenu.Options.ScrollIntoView(branchMenu.Options.SelectedItem);
            await Task.Delay(80);InteractionScreenshot("手动分支目录.png");
            Check(playCalls==calls && engine.CurrentId==section.StartId,"浏览手动分支目录不改变剧情、不播放");
            ConfirmSmallBranch();await Task.Delay(80);
            Check(engine.CurrentId==menuId && engine.Mode==RunMode.Choice && engine.Facts.SetEquals(facts) && playCalls==calls,"前置未同步也能静音打开截图中的确认方向菜单，不伪造条件");
            Check(branchMenu.Options.Items.Count==3 && engine.AvailableOptions[2].Label.Contains("森林"),"三个方向选项完整显示");
            InteractionScreenshot("方向分支选择.png");
            branchMenu.Move(1);int selected=branchMenu.Options.SelectedIndex;DismissSmallMenu();OpenStory();
            Check(branchMenu.IsVisible && branchMenu.Options.SelectedIndex==selected && playCalls==calls,"收起再打开保持选择，仍不播放");
            branchMenu.Options.SelectedIndex=2;ConfirmSmallBranch();
            Check(playCalls==calls+1 && engine.Current?.Kind=="line","明确确认方向仅播放该路线首句");
            for(int i=0;i<40 && engine.Mode==RunMode.Following;i++)engine.Next(true);
            Check(engine.MenuWaiting || engine.Mode==RunMode.Gap,"可靠正文结束后等待选择，不越过未知出口");
            calls=playCalls;ReselectClick(this,new());
            Check(engine.CurrentId==menuId && engine.Mode==RunMode.Choice && playCalls==calls,"读完段落仍可重选方向，回到菜单不播放");

            int inputIndex=Array.IndexOf(args,"--test-ocr-results");
            if(inputIndex<0)throw new Exception("需要三张实际截图的OCR结果");
            using var document=JsonDocument.Parse(File.ReadAllText(args[inputIndex+1]));
            var blockSets=document.RootElement.EnumerateArray().Select(item=>JsonSerializer.Deserialize<List<OcrBlock>>(item.GetProperty("blocks").GetRawText(),Json.Options)!).ToList();
            foreach(var blocks in blockSets.Take(2))
            {
                var matches=Matcher.Find(engine,section.Id,blocks);
                Check(matches.Any(c=>c.Node.Id==menuId),"实际选项页OCR能提出正确菜单");
                Expand(LocateTab);CandidatesList.ItemsSource=matches;CandidatesList.SelectedItem=matches.First(c=>c.Node.Id==menuId);
                ConfirmCandidate();
                Check(engine.CurrentId==menuId && engine.Mode==RunMode.Choice && playCalls==calls,"确认OCR菜单只打开选项、不播放");
            }
            var battle=Matcher.Find(engine,section.Id,blockSets[2]);
            var line=battle.First(c=>c.Node.Text.Contains("安葬他之后"));
            Expand(LocateTab);CandidatesList.ItemsSource=battle;CandidatesList.SelectedItem=line;
            OcrScope.Text="查找范围："+section.Title;
            OcrStatus.Text="已找到战斗台词，候选包含不同路线的位置。请选择与游戏一致的上下文，确认后才播放。";
            await Task.Delay(100);Screenshot("战斗台词定位.png");
            Check(playCalls==calls,"查看未选路线的战斗台词候选不播放");
            ConfirmCandidate();
            Check(engine.CurrentId==line.Node.Id && playCalls==calls+1,"确认战斗字幕后进入对应台词并仅播放一次");
            int silent=0;var restored=new PlaybackEngine(engine.Pack);restored.PlayRequested+=_=>silent++;
            Check(restored.ImportNavigation(engine.ExportNavigation()) && restored.CurrentId==line.Node.Id && silent==0,"手动路线存档可恢复，重启保持静音");
            calls=playCalls;engine.EnterOriginal();
            Expand(LocateTab);CandidatesList.ItemsSource=battle;CandidatesList.SelectedItem=line;ConfirmCandidate();
            Check(engine.Mode==RunMode.Original && playCalls==calls,"原声期间OCR确认不播放、不改路线");
            Original();Expand(LocateTab);CandidatesList.ItemsSource=battle;CandidatesList.SelectedItem=line;ConfirmCandidate();
            Check(engine.CurrentId==line.Node.Id && playCalls==calls+1,"明确结束原声后可用OCR确认续接");
            Expand(StoryTab);BrowseCurrent();await Task.Delay(80);Screenshot("手动分支主面板.png");
        }
        catch(Exception ex){checks.Add("FAIL: "+ex);}
        File.WriteAllLines(Path.Combine(Log.DataDir,"reported-ocr-ui-test.txt"),checks);Close();
    }
}
