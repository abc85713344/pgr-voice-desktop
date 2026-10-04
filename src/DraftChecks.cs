using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace PgrVoice;
public partial class MainWindow
{
    async Task RunDraftUiTest()
    {
        var results = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); results.Add("PASS: " + label); }
        try
        {
            if (engine?.Pack.IsDraft == true)
            {
                Check(DraftNotice.IsVisible && DraftNotice.Text.Contains("未逐句核验"), "真实草稿持续显示质量标记");
                Check(LibraryBox.Items.OfType<PackChoice>().First().Title == "1-1灰鸦过境", "真实草稿按章节小节顺序列出可读标题");
            }
            string folder = Path.Combine(Log.DataDir, "draft-fixture"); Directory.CreateDirectory(folder);
            var pack = new Pack { Id="draft-ui-fixture", Title="草稿刷新验收", Root=folder,
                Delivery=new(){Status="draft",Revision="before"},
                Chapters=new(){new(){Id="chapter",Sections=new(){new(){Id="section",Title="测试小节",StartId="start"}}}},
                Nodes=new(){
                    new(){Id="start",SectionId="section",Text="保留正文",NextId="choice",AudioStatus="draft-missing",AudioReason="生成待补"},
                    new(){Id="choice",SectionId="section",Kind="choice",Options=new(){new(){Id="a",Label="左",PathId="a",TargetId="a-line",MergeId="merge",Verified=true},new(){Id="b",Label="右",PathId="b",TargetId="b-line",MergeId="merge",Verified=true}}},
                    new(){Id="a-line",SectionId="section",PathId="a",Text="左侧台词",NextId="merge"},
                    new(){Id="b-line",SectionId="section",PathId="b",Text="右侧台词",NextId="merge",AudioStatus="draft-missing",AudioReason="角色待审"},
                    new(){Id="merge",SectionId="section",Kind="merge",NextId="end"},
                    new(){Id="end",SectionId="section",Kind="end"}} };
            string path=Path.Combine(folder,"pack.json");Json.Save(path,pack);LoadPack(path);Expand(StoryTab);
            engine!.Commit("start");Check(CurrentText.Text=="保留正文" && diagnosticStatus.Text.Contains("生成待补"), "缺句显示全文及具体原因");
            engine.Next(true);engine.SelectBranch(1);HideBranchMenu();
            string before=JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options);int calls=playCalls;
            File.WriteAllBytes(Path.Combine(folder,"new.wav"),new byte[]{1,2,3});
            pack.Nodes.Single(n=>n.Id=="b-line").Audio="new.wav";pack.Nodes.Single(n=>n.Id=="b-line").AudioStatus="draft-audio";pack.Delivery!.Revision="after";
            Json.Save(path,pack);lastDraftCheck=DateTime.MinValue;audioStarting=true;RefreshDraftIfUpdated();
            Check(engine.Pack.Delivery!.Revision=="before", "音频启动期间推迟草稿更新");
            audioStarting=false;lastDraftCheck=DateTime.MinValue;RefreshDraftIfUpdated();
            Check(engine.Pack.Delivery!.Revision=="after" && engine.Current!.Audio=="new.wav", "音频空闲时自动接入补齐音频");
            Check(JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options)==before && playCalls==calls, "自动更新不移动游标、不改分支、不重播");
            Check(DraftNotice.Text.Contains("可听 1") && DraftNotice.Text.Contains("未逐句核验"), "草稿补齐数量即时更新且不冒充正式");
            pack.Nodes.Single(n=>n.Id=="b-line").Audio="absent.wav";pack.Delivery!.Revision="incomplete";
            Json.Save(path,pack);lastDraftCheck=DateTime.MinValue;RefreshDraftIfUpdated();
            Check(engine.Pack.Delivery!.Revision=="after" && engine.Current!.Audio=="new.wav", "未完整发布时保留上一可听版本");
            Expand(StoryTab);await Task.Delay(100);Screenshot("草稿播放器验收.png");
        }
        catch(Exception ex) { results.Add("FAIL: " + ex); }
        finally { audioStarting=false; }
        Directory.CreateDirectory(Log.DataDir);File.WriteAllLines(Path.Combine(Log.DataDir,"draft-ui-test.txt"),results);Close();
    }
}
