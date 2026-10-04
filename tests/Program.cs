using PgrVoice;
using System.Text.Json;

if(args.Length==2 && args[0]=="--navigation-fingerprint")
{
    Console.WriteLine(PlaybackEngine.NavigationFingerprint(Pack.Load(args[1]))); return;
}
if(args.Length==2 && args[0]=="--android-pack")
{
    AndroidCoreTests.RunPack(Pack.Load(args[1])); return;
}
if(args.Length is 2 or 3 && args[0]=="--audit-explicit-returns")
{
    var pack=Pack.Load(args[1]);string sectionId="ch30-ceed979fd98e932b93e5",menuId=sectionId+"-menu-002",routeId=sectionId+"-route-002-01";
    var menu=pack.ById[menuId];var option=menu.Options.Single(o=>o.Id==routeId);
    if(!option.BodyVerified || !option.ExitVerified || option.ReturnId!=menuId)throw new Exception("明确返回选项没有核实到所属菜单");
    var engine=new PlaybackEngine(pack);engine.Restore(menuId);engine.OpenMenu();int plays=0;engine.PlayRequested+=_=>plays++;
    for(int repetition=0;repetition<2;repetition++)
    {
        int before=plays;engine.SelectBranch(engine.AvailableOptions.FindIndex(o=>o.Id==routeId));
        int steps=0;
        while(engine.Mode==RunMode.Following && engine.Current?.NextId is string next && pack.ById[next].Kind=="line" && steps++<100)engine.Next();
        if(steps>=100 || engine.Current?.Kind!="line" || engine.Mode!=RunMode.Following || plays-before!=option.LineIds.Count)throw new Exception("返回路线未逐句读完可靠正文");
        string? lastLine=engine.CurrentId;int lastPlays=plays;engine.SuspendSound();
        if(engine.CurrentId!=lastLine || engine.Mode!=RunMode.Following || plays!=lastPlays)throw new Exception("音频结束前后擅自推进");
        engine.Next();
        if(engine.CurrentId!=menuId || engine.Mode!=RunMode.Choice || plays!=lastPlays || !engine.Heard.Contains(option.PathId))throw new Exception("末句后的下一次推进未静音返回所属菜单");
        engine.Next(true);if(engine.CurrentId!=menuId || plays!=lastPlays)throw new Exception("菜单未经确认自行进入路线");
    }
    if(engine.RecentChoices.Count!=2 || engine.RecentChoices[0].Sequence==engine.RecentChoices[1].Sequence)throw new Exception("重听覆盖实际选择记录");
    Console.WriteLine("PASS EXPLICIT RETURN: 30-8 还是等会吧，两次逐句播放，末句下一次推进静音返回 menu002，重复选择记录独立");
    var continuing=menu.Options.Single(o=>o.Id!=routeId);int entering=plays;engine.SelectBranch(engine.AvailableOptions.FindIndex(o=>o.Id==continuing.Id));
    if(engine.CurrentId!=continuing.TargetId || plays!=entering+1)throw new Exception("继续前进未进入原有台词");
    engine.Next();if(engine.Mode!=RunMode.Merge || engine.CurrentId!=continuing.MergeId)throw new Exception("继续前进被错误改成菜单循环");
    engine.Next();if(engine.CurrentId==menuId || engine.Current?.Kind!="line")throw new Exception("继续前进未走共同线");
    Console.WriteLine("PASS EXPLICIT RETURN: 继续前进保留共同线，不受返回选项影响");
    engine.EnterOriginal();string? original=engine.CurrentId;int silent=plays;engine.Next();engine.Next(true);engine.Previous();engine.SelectBranch(0);
    if(engine.OpenStoryMenu(menuId) || engine.ReturnToStoryMenu() || engine.Mode!=RunMode.Original || engine.CurrentId!=original || plays!=silent)throw new Exception("原声期间被菜单返回推进");
    Console.WriteLine("PASS EXPLICIT RETURN: 原声期间推进、回退、选择及菜单定位均不播放或移动");
    string oldFile=args.Length==3?args[2]:Path.Combine(@"packs", "第30章_镜像星尘", "pack.json");
    var oldPack=Pack.Load(oldFile);string oldFingerprint=PlaybackEngine.NavigationFingerprint(oldPack);
    if(oldFingerprint==PlaybackEngine.NavigationFingerprint(pack) || !pack.CompatibleNavigationFingerprints.Contains(oldFingerprint))throw new Exception("候选没有精确保留此次旧包指纹");
    var prior=new PlaybackEngine(oldPack);prior.Restore(menuId);prior.OpenMenu();var snapshots=new List<NavigationSnapshot>{prior.ExportNavigation()};prior.SelectBranch(prior.AvailableOptions.FindIndex(o=>o.Id==routeId));
    int oldSteps=0;while(prior.Mode==RunMode.Following && oldSteps++<100){snapshots.Add(prior.ExportNavigation());prior.Next();}snapshots.Add(prior.ExportNavigation());
    foreach(var saved in snapshots)
    {
        var restored=new PlaybackEngine(pack);restored.PlayRequested+=_=>throw new Exception("旧包导航迁移时播放");
        if(!restored.ImportNavigation(saved) || restored.CurrentId!=saved.Current.NodeId || restored.History.Count!=saved.Visits.Count || restored.Mode!=RunMode.Ready || restored.ExportNavigation().PackFingerprint!=PlaybackEngine.NavigationFingerprint(pack))throw new Exception("旧包导航未能静音完整迁移："+restored.NavigationError);
    }
    Console.WriteLine($"PASS EXPLICIT RETURN: {snapshots.Count} 个旧包菜单、段内、段尾与汇合快照静音迁移，保留访问历史并导出新指纹");return;
}
if(args.Length==2 && args[0]=="--audit-camp-navigation")
{
    var pack=Pack.Load(args[1]);var section=pack.Chapters[0].Sections[0];
    foreach(string suffix in new[]{"006","011","012"})
    {
        var engine=new PlaybackEngine(pack);engine.Commit(section.StartId);int plays=0;engine.PlayRequested+=_=>plays++;
        string topic=section.Id+"-menu-"+suffix;
        if(!engine.InteractionMenus.Any(m=>m.MenuId==topic) || !engine.OpenInteractionMenu(topic) || engine.CurrentId!=topic || plays!=0)
            throw new Exception("人物话题直达失败："+suffix+" "+engine.NavigationError);
        int first=engine.AvailableOptions.FindIndex(o=>o.BodyVerified);
        engine.SelectBranch(first);
        if(engine.Current?.Kind!="line" || engine.ReturnInteractionTarget?.MenuId!=topic)throw new Exception("人物话题返回目标错误："+suffix);
        int before=plays;
        if(!engine.ReturnToInteractionMenu() || engine.CurrentId!=topic || plays!=before || engine.Facts.Count>0 || engine.Heard.Count>0)
            throw new Exception("提前返回话题改变了游戏条件或自动播放："+suffix);
        foreach(string other in new[]{"006","011","012"})if(!engine.OpenInteractionMenu(section.Id+"-menu-"+other))throw new Exception("换人失败："+other);
        if(!engine.ValidateNavigation(engine.ExportNavigation()))throw new Exception(engine.NavigationError);
        foreach(var option in pack.ById[topic].Options)
        {
            var route=new PlaybackEngine(pack);route.Commit(section.StartId);route.OpenInteractionMenu(topic);
            route.SelectBranch(route.AvailableOptions.FindIndex(o=>o.Id==option.Id));int steps=0;
            while(route.Mode==RunMode.Following && steps++<200)route.Next();
            if(steps>=200 || route.Mode!=RunMode.Choice || route.CurrentId!=option.ReturnId || route.Facts.Count>0)
                throw new Exception("话题出口或事件状态错误："+option.Label+" → "+route.CurrentId);
        }
        Console.WriteLine("PASS CAMP navigation: "+suffix+" direct entry, topic return, switching, silent snapshots");
    }
    return;
}
if(args.Length==3 && args[0]=="--audit-navigation-progress")
{
    var pack=Pack.Load(args[1]);var document=Json.Read<ChapterProgress>(args[2]);var saved=document.Navigation??throw new Exception("存档没有导航状态");
    var engine=new PlaybackEngine(pack);int plays=0;engine.PlayRequested+=_=>plays++;
    if(!engine.ImportNavigation(saved) || engine.CurrentId!=saved.Current.NodeId || plays!=0 || engine.History.Count!=saved.Visits.Count)
        throw new Exception("旧进度未能保留："+engine.NavigationError);
    foreach(var bookmark in document.Bookmarks)if(!engine.ValidateNavigation(bookmark.Snapshot))throw new Exception("旧书签失效："+engine.NavigationError);
    Console.WriteLine($"PASS REAL PROGRESS: {engine.CurrentId}, {engine.History.Count} visits, {document.Bookmarks.Count} bookmarks, silent compatible import");return;
}
if(args.Length==2 && args[0]=="--audit-menu-library")
{
    int chapters=0,menus=0,opened=0,fallbacks=0,blocked=0,boundaries=0;
    foreach(string file in Directory.EnumerateDirectories(args[1]).Select(d=>Path.Combine(d,"pack.json")).Where(File.Exists).OrderBy(x=>x))
    {
        var pack=Pack.Load(file);string fingerprint=PlaybackEngine.NavigationFingerprint(pack);int count=0;
        foreach(var section in pack.Chapters.SelectMany(c=>c.Sections))
        {
            var catalog=new PlaybackEngine(pack);catalog.Restore(section.StartId);
            foreach(var item in catalog.StoryMenus)
            {
                var engine=new PlaybackEngine(pack);engine.Restore(section.StartId);int plays=0;engine.PlayRequested+=_=>plays++;
                bool ok=engine.OpenStoryMenu(item.MenuId);
                if(!ok)
                {
                    if(item.CanOpen || item.FallbackMenuId!=null || engine.CurrentId!=section.StartId || plays>0 || engine.Choices.Count>0)throw new Exception("选择点阻断不一致："+item.MenuId);
                    blocked++;menus++;count++;continue;
                }
                if(plays>0 || engine.Mode!=RunMode.Choice || engine.Facts.Count>0 || engine.Heard.Count>0)throw new Exception("菜单定位播放或推测条件："+item.MenuId);
                if(engine.CurrentId==item.MenuId)opened++;else {if(item.FallbackMenuId!=engine.CurrentId)throw new Exception("错误的所属菜单："+item.MenuId);fallbacks++;}
                var restored=new PlaybackEngine(pack);restored.PlayRequested+=_=>throw new Exception("导入菜单快照时播放");
                if(!restored.ImportNavigation(engine.ExportNavigation()) || restored.CurrentId!=engine.CurrentId)throw new Exception("菜单存档失效："+item.MenuId+" "+restored.NavigationError);
                int option=engine.AvailableOptions.FindIndex(o=>o.BodyVerified);
                if(option>=0)
                {
                    engine.SelectBranch(option);int steps=0;while(engine.Mode==RunMode.Following && steps++<3000)engine.Next();
                    if(steps>=3000)throw new Exception("正文推进未到边界："+item.MenuId);
                    if(engine.Mode==RunMode.Gap){boundaries++;string? gap=engine.CurrentId;int calls=plays;engine.Next(true);if(engine.CurrentId!=gap || plays!=calls)throw new Exception("越过未知边界："+item.MenuId);}
                }
                menus++;count++;
            }
        }
        if(fingerprint!=PlaybackEngine.NavigationFingerprint(pack))throw new Exception("菜单定位改变了配音包");
        chapters++;Console.WriteLine($"PASS MENU LIBRARY {pack.Id}: {count} menu entries");
    }
    Console.WriteLine($"MENU LIBRARY RESULT: {chapters} chapters, {menus} menus, {opened} direct positions, {fallbacks} safe parent positions, {blocked} blocked, {boundaries} unknown boundaries held");return;
}

if (args.Length > 0 && args[0] == "--match")
{
    var pack = Pack.Load(args[1]); var engine = new PlaybackEngine(pack);
    var cases = Json.Read<List<EvaluationCase>>(args[2]);
    var results = cases.Select(c =>
    {
        engine.Restore(c.CurrentId);
        var candidates = Matcher.Find(engine, c.SectionId, c.Blocks);
        return new { c.Id, candidates = candidates.Select(x => new { x.Node.Id, x.Node.Text, x.Score }), hit = candidates.Any(x => x.Node.Id == c.ExpectedId) };
    }).ToList();
    Console.WriteLine(JsonSerializer.Serialize(results, Json.Options)); return;
}

if(args.Length>1 && args[0]=="--audit-library")
{
    int routes=0,limited=0,unavailable=0;var reports=new List<object>();
    foreach(var file in Directory.GetFiles(args[1],"pack.json",SearchOption.AllDirectories))
    {
        var p=Pack.Load(file);int count=0;
        foreach(var n in p.Nodes)if(n.Audio!=null&&!File.Exists(p.ResolveAudio(n)))throw new Exception("missing audio "+n.Id);
        foreach(var menu in p.Nodes.Where(n=>!n.Archived&&n.Kind=="choice")) foreach(var o in menu.Options)
        {
            var choices=new Dictionary<string,string>();var seen=new HashSet<string>();
            void Parents(string path){if(path==""||!seen.Add(path))return;var owner=p.Nodes.First(n=>!n.Archived&&n.Options.Any(x=>x.PathId==path));choices[owner.Id]=path;Parents(owner.PathId);}
            Parents(menu.PathId);var e=new PlaybackEngine(p);e.Restore(menu.Id,choices,new(o.Requires),new(),3);
            if(e.CurrentId!=menu.Id || !e.Allowed(menu)){unavailable++;continue;}
            e.OpenMenu();var idx=e.AvailableOptions.FindIndex(x=>x.Id==o.Id);if(idx<0)throw new Exception("missing option "+o.Id);
            int plays=0;e.PlayRequested+=_=>plays++;e.SelectBranch(idx);
            if(!o.BodyVerified){if(e.Mode!=RunMode.Choice||plays>0)throw new Exception("unverified route played "+o.Id);continue;}
            int steps=0;while(e.Mode==RunMode.Following&&steps++<1500)e.Next();
            if(steps>=1500 || e.Mode is not (RunMode.Choice or RunMode.Gap or RunMode.Merge or RunMode.End))throw new Exception("stuck route "+o.Id);
            if(!o.ExitVerified && e.Mode is RunMode.Merge or RunMode.End)throw new Exception("unknown exit escaped "+o.Id);
            if(e.Mode==RunMode.Gap){limited++;var before=e.CurrentId;e.Next(true);if(e.CurrentId!=before)throw new Exception("gap advanced");}
            routes++;count++;
        }
        reports.Add(new{pack=p.Id,routes=count});Console.WriteLine($"AUDIT {p.Id}: {count} routes, all audio paths exist");
    }
    Console.WriteLine($"LIBRARY PASS: {routes} playable routes, {limited} boundaries, {unavailable} menus protected by unresolved parent scopes");return;
}

int passed = 0;
DialogueFrameTests.Run();
SubtitleStabilityTests.Run();
AndroidCoreTests.Run();
StateSaveQueueTests.Run();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
Pack Fixture()
{
    var pack = new Pack { Id = "test", Root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Chapters = new() { new() { Id="c", Sections=new() { new() { Id="s", Title="测试", StartId="start" } } } } };
    pack.Nodes = new()
    {
        new() { Id="start", SectionId="s", Text="新生文明在枯败的生命中绽放", Speaker="旁白", NextId="choice" },
        new() { Id="choice", SectionId="s", Kind="choice", Options=new() { new() { Id="a", Label="向左", PathId="a", TargetId="a1", MergeId="merge", Verified=true }, new() { Id="b", Label="向右", PathId="b", TargetId="b1", MergeId="merge", Verified=true } } },
        new() { Id="a1", SectionId="s", Text="左边已经没有人了", PathId="a", NextId="merge" },
        new() { Id="b1", SectionId="s", Text="右边已经没有人了", PathId="b", NextId="merge" },
        new() { Id="merge", SectionId="s", Kind="merge", NextId="common" },
        new() { Id="common", SectionId="s", Text="大家重新汇合，继续向前走", NextId="end" },
        new() { Id="end", SectionId="s", Kind="end" }
    }; pack.Validate(); return pack;
}
Test("key holds fire once, rapid separate taps all fire", () => { var gate=new KeyPressGate();int count=0;for(int i=0;i<50;i++)if(gate.Update(32,true))count++;Check(count==1,"repeat keydowns leaked");for(int i=0;i<20;i++){gate.Update(32,false);if(gate.Update(32,true))count++;}Check(count==21,"rapid taps lost");Check(!gate.Update(99,true,true),"injected key accepted"); });
Test("load and restore are silent", () => { var e = new PlaybackEngine(Fixture()); int count=0; e.PlayRequested += _=>count++; e.Restore("start"); e.Next(); Check(count==0 && e.Mode==RunMode.Ready,"startup played"); });
Test("choice never auto plays an option", () => { var e=new PlaybackEngine(Fixture()); int count=0; e.PlayRequested+=_=>count++; e.Commit("start"); e.Next(); e.Next(); Check(count==1 && e.Mode==RunMode.Choice,"choice auto played"); });
Test("unselected route cannot be committed", () => { var e=new PlaybackEngine(Fixture()); try { e.Commit("b1"); throw new Exception("accepted branch"); } catch(InvalidOperationException) { } });
Test("malformed automatic link cannot cross an unselected route", () => {var p=Fixture();p.ById["a1"].NextId="b1";var e=new PlaybackEngine(p);e.Commit("choice");e.SelectBranch(0);e.Next();Check(e.Mode==RunMode.Gap&&e.CurrentId=="a1","unsafe path followed");});
Test("nested choice requires its selected parent path", () => {var p=Fixture();p.Nodes.Add(new(){Id="nested",SectionId="s",Kind="choice",PathId="a",Options=new(){new(){Id="child",PathId="child",TargetId="childline",MergeId="merge",Verified=true}}});p.Nodes.Add(new(){Id="childline",SectionId="s",PathId="child",Text="内层台词",NextId="merge"});p.Validate();var e=new PlaybackEngine(p);e.Restore("start",new(){{"choice","b"},{"nested","child"}});Check(!e.Allowed(p.ById["childline"]),"stale nested path allowed");});
Test("selected route and merge wait", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.Next(); e.SelectBranch(0); Check(e.CurrentId=="a1","wrong branch"); e.Next(); Check(e.CurrentId=="merge" && e.Mode==RunMode.Merge,"missing merge"); e.Next(); Check(e.CurrentId=="common","failed common"); });
Test("back follows visited route across merge", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.Next(); e.SelectBranch(1); e.Next(); e.Next(); e.Previous(); Check(e.CurrentId=="b1","back crossed route"); e.Previous(); Check(e.CurrentId=="start" && e.Choices.Count==0,"route history not restored"); });
Test("back from choice restores last displayed line", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.Next(); e.Previous(); Check(e.CurrentId=="start","wrong back"); });
Test("unknown branches block without guessing", () => { var p=Fixture(); p.ById["choice"].Options[0].Verified=false; var e=new PlaybackEngine(p); e.Commit("choice"); e.SelectBranch(0); e.Next(true); Check(e.Mode==RunMode.Gap && e.CurrentId=="choice","guessed branch"); });
Test("original mode blocks all playback/navigation", () => { var e=new PlaybackEngine(Fixture()); int count=0; e.PlayRequested+=_=>count++; e.Commit("start"); e.EnterOriginal(); e.Next(); e.Next(true); e.Previous(); e.Replay(); e.TogglePause(); Check(count==1 && e.CurrentId=="start" && e.Mode==RunMode.Original,"original mode leaked"); });
Test("explicit resume leaves original mode", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.EnterOriginal(); e.Commit("common"); Check(e.CurrentId=="common" && e.Mode==RunMode.Following,"cannot resume"); });
Test("pause does not catch up keypresses", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.TogglePause(); for(int i=0;i<50;i++)e.Next(); e.TogglePause(); Check(e.CurrentId=="start","queued keypresses"); });
Test("end does not auto enter next chapter", () => { var e=new PlaybackEngine(Fixture()); e.Commit("common"); e.Next(); e.Next(); Check(e.Mode==RunMode.End,"end failure"); });
Test("new route replaces abandoned history", () => { var e=new PlaybackEngine(Fixture()); e.Commit("start"); e.Next(); e.SelectBranch(0); e.Previous(); e.Next(); e.SelectBranch(1); e.Next(); e.Next(); e.Previous(); Check(e.CurrentId=="b1","stale route history"); });
Test("OCR finds unselected branch without choosing or playing", () => { var e=new PlaybackEngine(Fixture()); e.Restore("start");int calls=0;e.PlayRequested+=_=>calls++; var r=Matcher.Find(e,"s",new() { new() { Text="右边已经没有人了", Score=.99 } }); Check(r.Any(x=>x.Node.Id=="b1") && e.CurrentId=="start" && e.Choices.Count==0 && calls==0,"OCR failed to propose branch or changed playback"); });
Test("OCR does not change position or play", () => { var e=new PlaybackEngine(Fixture()); e.Restore("start"); int count=0;e.PlayRequested+=_=>count++; var r=Matcher.Find(e,"s",new() { new() { Text="大家重新汇合，继续向前走", Score=.99 } }); Check(r[0].Node.Id=="common" && count==0 && e.CurrentId=="start","OCR mutated state"); });
Test("OCR handles wrap and punctuation", () => { var e=new PlaybackEngine(Fixture()); var r=Matcher.Find(e,"s",new() { new() { Text="新生文明在枯败的", Score=.99 }, new() { Text="生命中绽放。", Score=.99 } }); Check(r[0].Node.Id=="start","wrapped text not found"); });
Test("OCR ignores empty and low-confidence noise", () => { var e=new PlaybackEngine(Fixture()); Check(Matcher.Find(e,"s",new() { new() { Text="菜单退出自动播放", Score=.2 } }).Count==0,"noise matched"); });
Test("OCR stays in selected section", () => { var e=new PlaybackEngine(Fixture()); Check(Matcher.Find(e,"elsewhere",new() { new() { Text="新生文明在枯败的生命中绽放", Score=1 } }).Count==0,"cross section"); });
Test("repeated short lines show different context without playing", () => { var p=Fixture();p.ById["start"].Text="嗯";p.ById["common"].Text="嗯";var e=new PlaybackEngine(p);e.Restore("start");int count=0;e.PlayRequested+=_=>count++;var r=Matcher.Find(e,"s",new(){new(){Text="嗯",Score=1}});Check(r.Count==2 && r[0].Context!=r[1].Context && count==0,"ambiguous short line lacks context"); });
Test("ellipsis is searchable and redaction symbols remain distinct", () => { var p=Fixture();p.ById["start"].Text="……";var e=new PlaybackEngine(p);var r=Matcher.Find(e,"s",new(){new(){Text="...",Score=.95}});Check(r.Count==1 && r[0].Node.Id=="start","ellipsis disappeared");Check(Matcher.Normalize("■的名字")!=Matcher.Normalize("名字"),"redaction stripped"); });
Test("path traversal rejected", () => { var p=Fixture(); p.Nodes[0].Audio="../secret.wav"; try { p.Validate(); throw new Exception("traversal accepted"); } catch(InvalidDataException) { } });
Test("verified route needs merge", () => { var p=Fixture();p.ById["choice"].Options[0].MergeId=null;try { p.Validate();throw new Exception("missing merge accepted"); } catch(InvalidDataException) { } });
Test("dangling node link rejected", () => { var p=Fixture();p.Nodes[0].NextId="missing";try {p.Validate();throw new Exception("bad graph accepted");} catch(InvalidDataException) {} });
Test("audio notices distinguish review, not found and missing file", () => { var p=Fixture();var n=p.Nodes[0];Check(p.AudioNotice(n)=="配音待核对","legacy fallback");n.AudioStatus="not-found";Check(p.AudioNotice(n)=="未找到对应配音","missing record");n.Audio="absent-test-93b5.wav";Check(p.AudioNotice(n)=="音频文件缺失","missing file");Check(p.AudioNotice(p.ById["choice"])=="","choice should not need audio"); });
Test("completion notices distinguish visual symbols and failed verification",()=>{var p=Fixture();var n=p.Nodes[0];n.AudioStatus="not-spoken";Check(p.AudioNotice(n)=="无可朗读正文","visual symbol mislabeled");n.AudioStatus="generation-failed";Check(p.AudioNotice(n)=="补配未通过核验","failure mislabeled");});
Pack SegmentFixture()
{
    var p=Fixture();p.SchemaVersion=3;
    foreach(var o in p.ById["choice"].Options)
    {
        o.BodyVerified=true;o.ExitVerified=false;o.Verified=false;o.BodyEvidence.Add("render-fixture");o.SegmentIds.Add(o.TargetId);o.BoundaryId=o.PathId+"-gap";
        p.Nodes.Add(new(){Id=o.BoundaryId,Kind="gap",SectionId="s",PathId=o.PathId,Text="待续接",ResumeMenuIds=new(){"choice"}});
    }
    p.Validate();return p;
}
Test("v3 verified segment stops before unverified common line",()=>{
    var p=SegmentFixture();var e=new PlaybackEngine(p);var played=new List<string>();e.PlayRequested+=n=>played.Add(n!.Id);
    e.Commit("choice");e.SelectBranch(0);Check(e.CurrentId=="a1"&&played.Count==1,"segment not playable");
    e.Next();Check(e.CurrentId=="a-gap"&&e.Mode==RunMode.Gap&&played.Count==1,"unknown exit escaped");
    e.Next(true);Check(e.CurrentId=="a-gap","boundary advanced");e.SelectContinuation(0);Check(e.CurrentId=="choice"&&played.Count==1,"return menu played");
    e.SelectBranch(1);e.Previous();Check(e.CurrentId=="a1"&&!e.Allowed(p.ById["b1"]),"rollback crossed routes");
});
Test("v3 direct commit cannot bypass segment bounds",()=>{
    var p=SegmentFixture();p.Nodes.Add(new(){Id="a2",SectionId="s",PathId="a",Text="unreviewed"});p.ById["a1"].NextId="a2";p.Validate();
    var e=new PlaybackEngine(p);e.Commit("choice");e.SelectBranch(0);Check(!e.Allowed(p.ById["a2"]),"whole path opened");e.Next();Check(e.CurrentId=="a-gap","bypassed boundary");
});
Test("v3 restores silently and preserves actual visit history",()=>{
    var p=SegmentFixture();var e=new PlaybackEngine(p);e.Commit("choice");e.SelectBranch(0);e.Next();
    var r=new PlaybackEngine(p);int played=0;r.PlayRequested+=_=>played++;r.Restore(e.CurrentId,e.Choices,e.Facts,e.Heard,3);r.RestoreHistory(e.History,e.HistoryPosition);
    Check(played==0&&r.Mode==RunMode.Ready,"restore played");r.OpenMenu();Check(r.Mode==RunMode.Gap&&played==0,"boundary not restored");r.Previous();Check(r.CurrentId=="a1"&&played==1,"history lost");
});
Test("v3 malformed cross-route segment rejected",()=>{
    var p=SegmentFixture();p.ById["choice"].Options[0].SegmentIds.Add("b1");try{p.Validate();throw new Exception("foreign line accepted");}catch(InvalidDataException){}
});
Test("v3 original mode suppresses continuation and reopen",()=>{
    var e=new PlaybackEngine(SegmentFixture());e.Commit("choice");e.SelectBranch(0);e.Next();e.EnterOriginal();e.SelectContinuation(0);e.OpenMenu();e.Next();Check(e.Mode==RunMode.Original,"original escaped");
});
Pack NavigationFixture()
{
    var p = Fixture();
    p.ById["choice"].Options[0].TargetId = "topics";
    p.Nodes.AddRange(new Node[]
    {
        new() { Id="topics", SectionId="s", Kind="choice", PathId="a", Text="选择话题", Options=new()
        {
            new() { Id="topic-one", Label="第一个话题", PathId="topic-one", TargetId="topic-line", Verified=true, MergeId="merge" },
            new() { Id="topic-two", Label="第二个话题", PathId="topic-two", TargetId="other-line", Verified=true, MergeId="merge" }
        } },
        new() { Id="topic-line", SectionId="s", PathId="topic-one", Text="话题正文", NextId="topic-return" },
        new() { Id="topic-return", SectionId="s", Kind="return", PathId="topic-one", NextId="topics", CompleteRoute="topic-one", SetFacts=new(){"talked"} },
        new() { Id="other-line", SectionId="s", PathId="topic-two", Text="另外一个话题", NextId="merge" }
    });
    p.Validate(); return p;
}
Test("nested choices without dialogue have independent rollback checkpoints",()=>{
    var e=new PlaybackEngine(NavigationFixture());int plays=0;e.PlayRequested+=_=>plays++;
    e.Commit("start");e.Next();e.SelectBranch(0);
    Check(e.CurrentId=="topics" && plays==1 && e.RecentChoices.Count==1,"menu-to-menu choice was not recorded silently");
    e.SelectBranch(0);e.Next();e.SelectBranch(1);
    var root=e.RecentChoices.Last();int before=plays;
    Check(e.ReselectChoice(root.Sequence),"cannot reselect root");
    Check(e.CurrentId=="choice" && e.Mode==RunMode.Choice && e.Facts.Count==0 && e.Heard.Count==0 && e.Choices.Count==0 && plays==before,"root before-state was incomplete or played");
    Check(e.CurrentReselectOptionId=="a","old option highlight missing");
    e.SelectBranch(1);
    Check(e.CurrentId=="b1" && e.History.All(v=>v.NodeId!="topic-line" && v.NodeId!="other-line") && e.RecentChoices.Count==1,"abandoned child state leaked");
});
Test("repeated visits to the same menu retain their own before-state",()=>{
    var e=new PlaybackEngine(NavigationFixture());e.Commit("choice");e.SelectBranch(0);e.SelectBranch(0);e.Next();e.SelectBranch(1);
    var repeated=e.RecentChoices.Where(c=>c.MenuId=="topics").ToList();
    Check(repeated.Count==2 && repeated[0].Sequence!=repeated[1].Sequence,"repeat menu instances collapsed");
    Check(e.ReselectChoice(repeated[0].Sequence) && e.Facts.Contains("talked") && e.Heard.Contains("topic-one"),"late checkpoint lost completed topic");
    Check(e.UndoCorrection() && e.CurrentId=="other-line","undo reselect failed");
    Check(e.ReselectChoice(repeated[1].Sequence) && !e.Facts.Contains("talked") && !e.Heard.Contains("topic-one"),"early checkpoint inherited future conditions");
});
Test("confirming a replacement that enters another menu immediately truncates future",()=>{
    var e=new PlaybackEngine(NavigationFixture());e.Commit("start");e.Next();e.SelectBranch(1);e.Next();e.Next();
    Check(e.ReselectLastChoice(),"reselect missing");int count=e.History.Count;
    Check(count==3,"opening reselect prematurely deleted undoable future");e.SelectBranch(0);
    Check(e.CurrentId=="topics" && e.History.Count==1 && e.RecentChoices.Count==1,"menu-only replacement retained abandoned future");
    Check(e.UndoCorrection() && e.CurrentId=="common" && e.History.Count==3 && e.Choices["choice"]=="b","undo did not restore complete old route");
});
Test("manual correction is one-step undoable and normal advancing expires undo",()=>{
    var e=new PlaybackEngine(Fixture());int plays=0;e.PlayRequested+=_=>plays++;e.Commit("start");e.Commit("common");
    Check(e.CanUndoCorrection,"manual correction has no undo");int before=plays;Check(e.UndoCorrection() && e.CurrentId=="start" && plays==before,"undo played or wrong location");
    Check(!e.CanUndoCorrection && !e.UndoCorrection(),"undo repeated indefinitely");e.Commit("common");e.Next();Check(!e.CanUndoCorrection,"next left undo alive");
    e.Commit("start");e.Previous();Check(!e.CanUndoCorrection,"previous left undo alive");
});
Test("history restoration restores route snapshot and stays silent until explicit play",()=>{
    var e=new PlaybackEngine(Fixture());int plays=0;e.PlayRequested+=_=>plays++;e.Commit("start");e.Next();e.SelectBranch(1);e.Next();e.Next();
    int before=plays;Check(e.RestoreVisit(0) && e.CurrentId=="start" && e.Choices.Count==0 && e.RecentChoices.Count==0 && plays==before,"history restore leaked future choice or audio");
    Check(e.UndoCorrection() && e.CurrentId=="common" && e.RecentChoices.Count==1,"history undo lost route");
    Check(e.RestoreVisit(1,true) && e.CurrentId=="b1" && plays==before+1,"confirmed history play failed");
});
Test("navigation snapshots are independent and import silently",()=>{
    var p=NavigationFixture();var e=new PlaybackEngine(p);e.Commit("start");e.Next();e.SelectBranch(0);e.SelectBranch(0);e.Next();
    var snapshot=e.ExportNavigation();e.SelectBranch(1);Check(snapshot.Choices.Count==2,"export aliases live history");
    var r=new PlaybackEngine(p);int plays=0;r.PlayRequested+=_=>plays++;Check(r.ImportNavigation(snapshot),r.NavigationError);
    Check(r.Mode==RunMode.Ready && r.CurrentId=="topics" && r.RecentChoices.Count==2 && plays==0,"cold import auto-followed or played");
    r.OpenMenu();Check(r.Mode==RunMode.Choice && r.Facts.Contains("talked"),"menu restoration lost conditions");
    r.SelectBranch(1);Check(r.RecentChoices.First().Sequence>snapshot.Choices.Last().Sequence,"choice sequence reused");
});
Test("old history migration never invents selection checkpoints",()=>{
    var p=Fixture();var old=new PlaybackEngine(p);old.Commit("start");old.Next();old.SelectBranch(0);
    var e=new PlaybackEngine(p);e.Restore(old.CurrentId,old.Choices,old.Facts,old.Heard,1);e.RestoreHistory(old.History,old.HistoryPosition);
    Check(e.RecentChoices.Count==0 && !e.ReselectLastChoice() && e.History.All(v=>v.ChoiceCursor==0),"legacy history fabricated choice events");
    Check(e.ValidateNavigation(e.ExportNavigation()),e.NavigationError);
});
Test("changed graph refuses snapshots but audio and display repairs preserve compatibility",()=>{
    var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");var snapshot=e.ExportNavigation();
    p.ById["start"].Audio="replacement.wav";p.ById["start"].Text="修订显示文字";
    var r=new PlaybackEngine(p);Check(r.ImportNavigation(snapshot),"audio-only update broke navigation");
    p.ById["start"].NextId="merge";var current=r.CurrentId;
    Check(!r.ImportNavigation(snapshot) && r.CurrentId==current && r.NavigationError.Contains("路线"),"changed graph silently restored");
});
Test("corrupt visit and selection cursors are rejected without partial restoration",()=>{
    var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");e.Next();e.SelectBranch(0);var snap=e.ExportNavigation();
    snap.Current.ChoiceCursor=0;var r=new PlaybackEngine(p);Check(!r.ImportNavigation(snap) && r.CurrentId==null,"inconsistent cursor accepted");
    snap=e.ExportNavigation();snap.Choices[0].Before.Facts=null!;Check(!r.ImportNavigation(snap),"null checkpoint accepted");
    snap=e.ExportNavigation();snap.Visits[1]=snap.Visits[1] with { NodeId="b1" };Check(!r.ImportNavigation(snap),"foreign branch history accepted");
});
Test("bookmarks restore complete navigation silently and can be undone",()=>{
    var e=new PlaybackEngine(NavigationFixture());e.Commit("choice");e.SelectBranch(0);e.SelectBranch(0);var bookmark=e.CreateBookmark("营地话题");
    e.Next();e.SelectBranch(1);int plays=0;e.PlayRequested+=_=>plays++;
    Check(e.RestoreBookmark(bookmark) && e.CurrentId=="topic-line" && !e.Facts.Contains("talked") && plays==0,"bookmark restored only node or played");
    Check(e.UndoCorrection() && e.CurrentId=="other-line" && e.Facts.Contains("talked") && plays==0,"bookmark undo lost state");
});
Test("original mode blocks new history and branch navigation",()=>{
    var e=new PlaybackEngine(Fixture());e.Commit("start");e.Next();e.SelectBranch(0);var bookmark=e.CreateBookmark("branch");e.EnterOriginal();int plays=0;e.PlayRequested+=_=>plays++;
    Check(!e.ReselectLastChoice() && !e.RestoreVisit(0,true) && !e.RestoreBookmark(bookmark) && !e.UndoCorrection() && !e.CanUndoCorrection,"original accepted navigation");e.PauseForBrowse();Check(e.Mode==RunMode.Original && plays==0,"browse escaped original");
    var r=new PlaybackEngine(e.Pack);Check(r.ImportNavigation(e.ExportNavigation()) && r.Mode==RunMode.Original,"original state not preserved silently");
});
Test("unreviewed option and verified segment boundary remain protected after navigation",()=>{
    var p=SegmentFixture();p.ById["choice"].Options[1].BodyVerified=false;p.ById["choice"].Options[1].LineIds.Add("b1");var e=new PlaybackEngine(p);e.Commit("choice");e.SelectBranch(1);
    Check(e.Mode==RunMode.Choice && e.RecentChoices.Count==1,"unreviewed choice lost menu");e.CommitSingle("b1");var snap=e.ExportNavigation();var r=new PlaybackEngine(p);Check(r.ImportNavigation(snap),r.NavigationError);
    r.RestoreVisit(0,true);r.Next();Check(r.CurrentId=="choice" && !r.Choices.ContainsKey("choice"),"single-line restore unlocked whole route");
    r.SelectBranch(0);r.Next();var b=r.CreateBookmark("边界");r.SelectContinuation(0);Check(r.RestoreBookmark(b) && r.Mode==RunMode.Gap,"boundary bookmark escaped gap");r.Next(true);Check(r.Mode==RunMode.Gap,"restored unknown boundary advanced");
});
Test("chapter progress is independent and a corrupt file recovers previous valid backup",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));
    var store=new ProgressStore(dir);store.Load();var a=Fixture();a.Id="chapter-a";var b=Fixture();b.Id="chapter-b";
    var ea=new PlaybackEngine(a);ea.Commit("start");store.Save(a,ea.ExportNavigation());ea.Commit("common");store.Save(a,ea.ExportNavigation());
    var eb=new PlaybackEngine(b);eb.Commit("choice");eb.SelectBranch(1);store.Save(b,eb.ExportNavigation());
    var aFile=Directory.GetFiles(dir,"*.progress.json").Single(f=>Json.Read<ChapterProgress>(f).PackId==a.Id);
    File.WriteAllText(aFile,"broken");var recovered=new ProgressStore(dir);recovered.Load();
    var ra=new PlaybackEngine(a);var rb=new PlaybackEngine(b);Check(recovered.RecoveredFromBackup && recovered.TryRestore(ra) && ra.CurrentId=="start","chapter backup not recovered");
    Check(recovered.TryRestore(rb) && rb.CurrentId=="b1" && rb.RecentChoices.Count==1,"other chapter progress lost");
    ra.Commit("common");recovered.Save(a,ra.ExportNavigation());Check(Json.Read<ChapterProgress>(aFile+".bak").Navigation!.Current.NodeId=="start","bad original replaced valid backup");
    Check(recovered.GetSummary(a).Length>0 && recovered.GetSummary(b.Id).Length>0,"chapter summary missing");
});
Test("legacy chapter migration runs once and bookmarks persist separately",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));var store=new ProgressStore(dir);var p=Fixture();var e=new PlaybackEngine(p);e.Commit("choice");e.SelectBranch(0);
    Check(store.MigrateLegacy(p,e.CurrentId,e.Choices,e.Facts,e.Heard,1,e.History,e.HistoryPosition),store.LastError);
    Check(!store.MigrateLegacy(p,"start",new(),new(),new(),1,null,-1),"existing progress overwritten by migration");
    var restored=new PlaybackEngine(p);Check(store.TryRestore(restored) && restored.CurrentId=="a1" && restored.RecentChoices.Count==0,"migration lost line or invented choices");
    var bookmark=e.CreateBookmark("第一条路线");store.SaveBookmark(p.Id,bookmark);var reloaded=new ProgressStore(dir);reloaded.Load();
    Check(reloaded.Bookmarks(p.Id).Count==1 && reloaded.Bookmarks(p.Id)[0].Snapshot.Choices.Count==1,"bookmark lacked route snapshot");
    reloaded.RemoveBookmark(p.Id,bookmark.Id);Check(reloaded.Bookmarks(p.Id).Count==0 && reloaded.TryRestore(restored),"deleting bookmark deleted chapter progress");
});
Test("settings backup read preserves the last valid file",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));string path=Path.Combine(dir,"settings.json");
    Json.Save(path,new Dictionary<string,int>{{"volume",30}});Json.Save(path,new Dictionary<string,int>{{"volume",60}});File.WriteAllText(path,"broken");
    var old=Json.ReadWithBackup<Dictionary<string,int>>(path,out bool recovered);Check(recovered && old["volume"]==30,"valid settings backup missing");
});
Test("one-step correction undo survives restart without recursive snapshots or audio",()=>{
    var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");e.Commit("common");var saved=e.ExportNavigation();
    Check(saved.Undo?.Current.NodeId=="start" && saved.Undo.Undo==null,"undo checkpoint not flat");
    var restored=new PlaybackEngine(p);int plays=0;restored.PlayRequested+=_=>plays++;
    Check(restored.ImportNavigation(saved) && restored.Mode==RunMode.Ready && restored.CanUndoCorrection,"restart lost single undo");
    Check(restored.UndoCorrection() && restored.CurrentId=="start" && plays==0,"restored undo played or wrong state");
    e.Commit("start");saved=e.ExportNavigation();Check(saved.Undo?.Undo==null,"second correction stored recursive old undo");
    saved.Undo!.Undo=e.ExportNavigation();Check(!restored.ImportNavigation(saved),"nested undo accepted");
});
Test("history preview pause does not reopen a waiting menu",()=>{
    var e=new PlaybackEngine(Fixture());e.Commit("choice");int changes=0;e.Changed+=()=>changes++;e.PauseForBrowse();
    Check(e.Mode==RunMode.Choice && changes==0,"browsing retriggered choice popup");
    e.SelectBranch(0);changes=0;e.PauseForBrowse();Check(e.Mode==RunMode.Paused && changes==1,"normal pause not reflected");
});
Test("unreadable chapter progress blocks automatic legacy replacement and preserves evidence",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");
    var store=new ProgressStore(dir);store.Save(p,e.ExportNavigation());string file=Directory.GetFiles(dir,"*.progress.json").Single();File.WriteAllText(file,"broken original");
    var reopened=new ProgressStore(dir);reopened.Load();Check(reopened.HasProgress(p.Id),"damaged file treated as absent");
    Check(!reopened.MigrateLegacy(p,"common",new(),new(),new(),1,null,-1),"legacy migration overwrote damaged evidence");
    var blank=new PlaybackEngine(p);Check(!reopened.TryRestore(blank) && reopened.LastError.Length>0 && File.ReadAllText(file)=="broken original","restore erased failure reason or source");
    try { reopened.Save(p,blank.ExportNavigation());throw new Exception("blank startup overwrote old progress"); } catch(InvalidDataException) { }
    e.Commit("common");reopened.Save(p,e.ExportNavigation());
    Check(Directory.GetFiles(Path.Combine(dir,"recovery")).Any(f=>File.ReadAllText(f)=="broken original"),"explicit new position did not preserve damaged original");
});
Test("structurally corrupt progress recovers valid backup and retains it on next save",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");var store=new ProgressStore(dir);store.Save(p,e.ExportNavigation());e.Commit("common");store.Save(p,e.ExportNavigation());
    string file=Directory.GetFiles(dir,"*.progress.json").Single();var corrupt=Json.Read<ChapterProgress>(file);corrupt.Navigation!.Current.ChoiceCursor=999;File.WriteAllText(file,JsonSerializer.Serialize(corrupt,Json.Options));
    var reopened=new ProgressStore(dir);reopened.Load();var r=new PlaybackEngine(p);Check(reopened.TryRestore(r) && r.CurrentId=="start" && reopened.RecoveredFromBackup,"semantic corruption bypassed backup");
    r.Commit("common");reopened.Save(p,r.ExportNavigation());Check(Json.Read<ChapterProgress>(file+".bak").Navigation!.Current.NodeId=="start","corrupt JSON replaced usable backup");
});
Test("incompatible old graph is retained when user confirms a new starting point",()=>{
    string dir=Path.Combine(Path.GetTempPath(),"PgrVoiceNavigationTests-"+Guid.NewGuid().ToString("N"));var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");var store=new ProgressStore(dir);store.Save(p,e.ExportNavigation());
    p.ById["start"].NextId="merge";var r=new PlaybackEngine(p);Check(!store.TryRestore(r) && r.CurrentId==null,"changed graph auto-restored");
    r.Commit("common");store.Save(p,r.ExportNavigation());Check(Directory.GetFiles(Path.Combine(dir,"recovery")).Length>0,"incompatible progress was silently replaced");
});
Pack InteractionFixture()
{
    var p=NavigationFixture();p.SchemaVersion=3;
    p.ById["choice"].MenuType="interaction";p.ById["choice"].Text="营地 → 选择人物";p.ById["choice"].MenuNavigationEvidence.Add("fixture: verified camp menu");
    p.ById["topics"].MenuType="topics";p.ById["topics"].MenuNavigationEvidence.Add("fixture: verified person A topics");
    p.Nodes.AddRange(new Node[]
    {
        new(){Id="other-topics",Kind="choice",SectionId="s",PathId="b",MenuType="topics",Text="人物 B → 选择话题",MenuNavigationEvidence=new(){"fixture: verified person B topics"},Options=new(){new(){Id="b-topic",Label="关于现状",PathId="b-topic",TargetId="b-topic-line",BoundaryId="b-topic-gap"}}},
        new(){Id="b-topic-line",SectionId="s",PathId="b-topic",Text="人物 B 的话题正文",NextId="merge"},
        new(){Id="b-topic-gap",Kind="gap",SectionId="s",PathId="b-topic",Text="未核实出口",ResumeMenuIds=new(){"other-topics","choice"}}
    });
    p.ById["b1"].NextId="other-topics";
    foreach(var node in p.Nodes.Where(n=>n.Kind=="choice"))foreach(var option in node.Options)
    {
        option.BodyVerified=true;option.BodyEvidence.Add("fixture: continuous body");
        option.SegmentIds=p.Nodes.Where(n=>n.PathId==option.PathId && n.Kind!="gap").Select(n=>n.Id).ToList();
        option.ExitVerified=option.PathId!="b-topic";option.ExitEvidence.Add("fixture: explicit return");
        option.ReturnId=option.PathId=="topic-one"?"topics":"merge";
    }
    p.Validate();return p;
}
Test("trusted interaction root and proven topic chains open silently in either person order",()=>{
    foreach(bool bFirst in new[]{true,false})
    {
        var p=InteractionFixture();var e=new PlaybackEngine(p);e.Commit("start");int plays=0;e.PlayRequested+=_=>plays++;
        string first=bFirst?"other-topics":"topics",second=bFirst?"topics":"other-topics";
        Check(e.InteractionMenus.Any(t=>t.MenuId==first) && e.OpenInteractionMenu(first),e.NavigationError);
        Check(e.Mode==RunMode.Choice && e.CurrentId==first && e.Facts.Count==0 && e.Heard.Count==0 && plays==0,"menu navigation inferred task completion or played");
        Check(e.OpenInteractionMenu(second) && e.CurrentId==second && plays==0 && e.RecentChoices.Count==2,"free person switching failed");
        Check(e.UndoCorrection() && e.CurrentId==first && plays==0,"person switch undo lost old menu");
        Check(e.ValidateNavigation(e.ExportNavigation()),e.NavigationError);
    }
});
Test("return-to-topic leaves heard and game facts unchanged and remains undoable",()=>{
    var e=new PlaybackEngine(InteractionFixture());e.Commit("start");e.OpenInteractionMenu("other-topics");e.SelectBranch(0);int plays=0;e.PlayRequested+=_=>plays++;
    Check(e.ReturnInteractionTarget?.MenuId=="other-topics","return skipped current person's topics");
    Check(e.ReturnToInteractionMenu() && e.CurrentId=="other-topics" && e.Heard.Count==0 && e.Facts.Count==0 && plays==0,"explicit return falsely completed topic");
    Check(e.UndoCorrection() && e.CurrentId=="b-topic-line" && plays==0,"return undo not complete");
    Check(e.OpenInteractionMenu("choice") && e.CurrentId=="choice","cannot return to people menu");
});
Test("interaction navigation never unlocks unproved entry, blocked condition or noncontinuous body",()=>{
    var p=InteractionFixture();p.ById["topics"].MenuNavigationEvidence.Clear();var e=new PlaybackEngine(p);e.Commit("start");
    Check(!e.OpenInteractionMenu("topics") && e.CurrentId=="start","unproved topic opened");
    p=InteractionFixture();p.ById["choice"].Options[1].Requires.Add("not-confirmed");e=new PlaybackEngine(p);e.Commit("start");
    Check(!e.OpenInteractionMenu("other-topics") && e.Choices.Count==0,"navigation inferred prerequisite");
    p=InteractionFixture();p.ById["b1"].NextId="merge";e=new PlaybackEngine(p);e.Commit("start");
    Check(!e.OpenInteractionMenu("other-topics") && !e.InteractionMenus.Any(t=>t.MenuId=="other-topics"),"segment membership bypassed disconnected opening");
    p=InteractionFixture();p.ById["choice"].Options[0].BodyVerified=false;p.ById["choice"].Options[0].ExitVerified=false;p.Validate();e=new PlaybackEngine(p);e.Commit("start");
    Check(!e.OpenInteractionMenu("topics"),"unverified branch opened by topic navigation");
});
Test("topic navigation cannot skip an intervening exclusive choice",()=>{
    var p=InteractionFixture();p.Nodes.Add(new(){Id="intervening-choice",SectionId="s",PathId="b",Kind="choice",MenuType="exclusive",NextId="other-topics"});
    p.ById["choice"].Options[1].SegmentIds.Add("intervening-choice");p.ById["b1"].NextId="intervening-choice";p.Validate();
    var e=new PlaybackEngine(p);e.Commit("start");Check(!e.OpenInteractionMenu("other-topics") && e.RecentChoices.Count==0,"direct topic bypassed exclusive menu");
});
Test("interaction menu jump trims abandoned future but undo restores it",()=>{
    var e=new PlaybackEngine(InteractionFixture());e.Commit("start");e.OpenInteractionMenu("topics");e.SelectBranch(0);e.Next();e.SelectBranch(1);
    int visits=e.History.Count;Check(e.RestoreVisit(0),"cannot prepare earlier history");
    Check(e.OpenInteractionMenu("other-topics") && e.History.Count==1 && e.RecentChoices.Count==1,"menu jump retained old person's future");
    Check(e.UndoCorrection() && e.History.Count==visits && e.CurrentId=="start","menu jump undo lost original visits");
});
Test("interaction original-mode guard and unknown topic boundary remain intact",()=>{
    var e=new PlaybackEngine(InteractionFixture());e.Commit("start");e.OpenInteractionMenu("other-topics");e.SelectBranch(0);e.Next();
    Check(e.Mode==RunMode.Gap,"direct menu entry unlocked unknown topic exit");int plays=0;e.PlayRequested+=_=>plays++;e.EnterOriginal();
    Check(!e.OpenInteractionMenu("choice") && !e.ReturnToInteractionMenu() && e.Mode==RunMode.Original && plays==0,"original mode escaped via interaction menu");
});
Test("approved old navigation fingerprints still validate every restored point",()=>{
    var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");e.Next();e.SelectBranch(0);var saved=e.ExportNavigation();
    p.ById["common"].Text="文本修正";p.ById["start"].NextId="merge";p.CompatibleNavigationFingerprints.Add(saved.PackFingerprint);p.Validate();
    var r=new PlaybackEngine(p);int plays=0;r.PlayRequested+=_=>plays++;Check(r.ImportNavigation(saved) && r.CurrentId=="a1" && plays==0,"explicit compatible patch lost valid position");
    Check(r.ExportNavigation().PackFingerprint==PlaybackEngine.NavigationFingerprint(p) && r.ExportNavigation().PackFingerprint!=saved.PackFingerprint,"compatible import did not migrate fingerprint");
    var forged=JsonSerializer.Deserialize<NavigationSnapshot>(JsonSerializer.Serialize(saved,Json.Options),Json.Options)!;forged.Current.NodeId="b1";
    Check(!r.ImportNavigation(forged),"compatibility whitelist bypassed selected-route checks");
    p.CompatibleNavigationFingerprints.Clear();Check(!r.ImportNavigation(saved),"unlisted graph update accepted");
});
Test("menu entry evidence alone does not invalidate existing graph progress",()=>{
    var p=Fixture();var e=new PlaybackEngine(p);e.Commit("start");var saved=e.ExportNavigation();
    p.ById["choice"].MenuType="interaction";var changed=PlaybackEngine.NavigationFingerprint(p);
    p.ById["choice"].MenuNavigationEvidence.Add("verified UI entry");p.Validate();
    Check(PlaybackEngine.NavigationFingerprint(p)==changed,"evidence-only metadata changed graph identity");
});
Pack GeneralMenusFixture()
{
    var p=InteractionFixture();int source=0;
    foreach(var n in p.Nodes.Where(n=>n.Kind=="choice")) { n.MenuType="exclusive";n.MenuNavigationEvidence.Clear();n.Source="fixture.html#menu-"+(source++); }
    p.Validate();return p;
}
Test("all-section menu directory works without claiming free interaction",()=>{
    var p=GeneralMenusFixture();var e=new PlaybackEngine(p);e.Restore("start");int plays=0;e.PlayRequested+=_=>plays++;
    Check(e.InteractionMenus.Count==0 && e.StoryMenus.Count==3,"generic menus were promoted to free interaction");
    var topic=e.StoryMenus.Single(m=>m.MenuId=="other-topics");Check(topic.CanOpen && topic.Status=="仅定位选择点" && topic.Preview.Contains("关于现状"),"menu preview/status missing");
    Check(e.OpenStoryMenu("other-topics") && e.CurrentId=="other-topics" && e.Mode==RunMode.Choice && e.Facts.Count==0 && e.Heard.Count==0 && plays==0,"explicit nested menu location inferred facts or played");
    Check(e.Choices["choice"]=="b" && e.RecentChoices.Count==1,"explicit parent selection not recorded");
    Check(e.UndoCorrection() && e.CurrentId=="start" && e.Choices.Count==0 && plays==0,"generic menu jump undo failed");
});
Test("generic menu location stops at the nearest provable parent",()=>{
    var p=GeneralMenusFixture();p.ById["b1"].NextId="merge";var e=new PlaybackEngine(p);e.Restore("start");
    var target=e.StoryMenus.Single(m=>m.MenuId=="other-topics");Check(!target.CanOpen && target.FallbackMenuId=="choice" && target.Reason.Length>0,"blocked directory entry did not describe parent");
    int plays=0;e.PlayRequested+=_=>plays++;
    Check(e.OpenStoryMenu("other-topics") && e.CurrentId=="choice" && e.Choices.Count==0 && plays==0 && e.Notice.Contains("连续正文"),"unknown opening crossed or no safe fallback shown");
    Check(e.UndoCorrection() && e.CurrentId=="start","fallback was not undoable");
});
Test("generic menu location cannot pass intermediate choice, return or unmet prerequisite",()=>{
    foreach(string kind in new[]{"choice","return"})
    {
        var p=GeneralMenusFixture();p.Nodes.Add(new(){Id="blocking",SectionId="s",PathId="b",Kind=kind,NextId="other-topics",Source="fixture.html#menu-9"});
        p.ById["b1"].NextId="blocking";p.ById["choice"].Options[1].SegmentIds.Add("blocking");p.Validate();var e=new PlaybackEngine(p);e.Restore("start");
        Check(e.OpenStoryMenu("other-topics") && e.CurrentId=="choice" && !e.Choices.ContainsKey("choice"),"intermediate node skipped: "+kind);
    }
    var blocked=GeneralMenusFixture();blocked.ById["choice"].Options[1].Requires.Add("unknown-event");var engine=new PlaybackEngine(blocked);engine.Restore("start");
    Check(engine.OpenStoryMenu("other-topics") && engine.CurrentId=="choice" && engine.Facts.Count==0,"condition was synthesized for deep menu");
});
Test("return to an actually visited choice restores its own conditions",()=>{
    var e=new PlaybackEngine(GeneralMenusFixture());e.Commit("start");e.Next();e.SelectBranch(0);e.SelectBranch(0);e.Next();
    Check(e.Facts.Contains("talked"),"fixture did not complete verified event");
    Check(e.StoryMenus.Single(m=>m.MenuId=="choice").IsVisited && e.OpenStoryMenu("choice") && !e.Facts.Contains("talked") && e.Heard.Count==0,"old choice inherited future conditions");
    Check(e.UndoCorrection() && e.Facts.Contains("talked") && e.CurrentId=="topics","visited menu return undo lost conditions");
});
Test("generic unknown branch ends at its original boundary instead of inventing a loop",()=>{
    var e=new PlaybackEngine(GeneralMenusFixture());e.Restore("start");e.OpenStoryMenu("other-topics");e.SelectBranch(0);e.Next();
    Check(e.Mode==RunMode.Gap && e.CurrentId=="b-topic-gap","unknown exit became a menu loop");string? gap=e.CurrentId;e.Next(true);Check(e.CurrentId==gap,"unknown boundary advanced");
    Check(e.ParentStoryMenu?.MenuId=="other-topics" && e.ReturnToStoryMenu() && e.CurrentId=="other-topics","generic parent selection not available");
});
Test("generic menu snapshots restart silently and original mode refuses all locations",()=>{
    var p=GeneralMenusFixture();var e=new PlaybackEngine(p);e.Restore("start");e.OpenStoryMenu("other-topics");var snapshot=e.ExportNavigation();
    var restored=new PlaybackEngine(p);int plays=0;restored.PlayRequested+=_=>plays++;Check(restored.ImportNavigation(snapshot) && restored.Mode==RunMode.Ready && plays==0,"generic menu startup played");
    restored.EnterOriginal();var before=restored.ExportNavigation();Check(!restored.OpenStoryMenu("choice") && !restored.ReturnToStoryMenu() && restored.CurrentId==before.Current.NodeId && restored.Mode==RunMode.Original,"original mode escaped via generic menu");
});
Test("unproven menu failures are inert and same-name menus remain distinct",()=>{
    var p=GeneralMenusFixture();p.ById["choice"].Source=null;p.ById["topics"].Source=null;var e=new PlaybackEngine(p);e.Restore("start");int plays=0;e.PlayRequested+=_=>plays++;
    Check(!e.OpenStoryMenu("topics") && e.CurrentId=="start" && e.Choices.Count==0 && !e.CanUndoCorrection && plays==0,"failed location mutated state");
    p=GeneralMenusFixture();p.ById["topics"].Text=p.ById["other-topics"].Text="选择人物";
    p.ById["topics"].Options[0].Preview="战斗之前";p.ById["other-topics"].Options[0].Preview="战斗之后";e=new PlaybackEngine(p);e.Restore("start");
    var duplicates=e.StoryMenus.Where(m=>m.MenuId is "topics" or "other-topics").ToList();Check(duplicates.Count==2 && duplicates[0].MenuId!=duplicates[1].MenuId && duplicates[0].Preview!=duplicates[1].Preview && duplicates[0].Label!=duplicates[1].Label,"same-name menu contexts collapsed");
});
Test("generic browsing scopes are explicit and initial directory needs no playback",()=>{
    var p=GeneralMenusFixture();p.Chapters[0].Sections.Add(new(){Id="elsewhere",Title="另一小节",StartId="else-choice"});p.Nodes.Add(new(){Id="else-choice",SectionId="elsewhere",Kind="choice",Source="else.html#menu-0"});p.Validate();
    var e=new PlaybackEngine(p);Check(e.StoryMenus.Count==3 && e.CurrentId==null,"initial directory changed position");
    e.Restore("start");Check(e.GetStoryMenus("elsewhere").Single().MenuId=="else-choice" && !e.OpenStoryMenu("else-choice"),"cross-section implicit jump accepted");
    Check(e.OpenStoryMenu("else-choice","elsewhere") && e.Mode==RunMode.Choice,"explicit section selection failed");
});
Test("manual menu positioning bypasses stale prerequisites without inventing facts",()=>{
    var p=GeneralMenusFixture();p.ById["choice"].Options[1].Requires.Add("untracked");
    p.ById["other-topics"].Options[0].Requires.Add("untracked-topic");
    var e=new PlaybackEngine(p);e.Restore("start");int calls=0;e.PlayRequested+=_=>calls++;
    Check(e.OpenGameMenu("other-topics") && e.CurrentId=="other-topics" && calls==0 && e.Facts.Count==0,"manual menu blocked or invented facts");
    Check(e.AvailableOptions.Count==p.ById["other-topics"].Options.Count,"hidden options remain");
    e.SelectBranch(0);Check(e.CurrentId=="b-topic-line" && calls==1 && e.Facts.Count==0,"explicit option confirmation failed");
    var save=e.ExportNavigation();var r=new PlaybackEngine(p);r.PlayRequested+=_=>throw new Exception("restored with sound");
    Check(r.ImportNavigation(save) && r.CurrentId=="b-topic-line" && r.Mode==RunMode.Ready,"manual state not persisted: "+r.NavigationError);
    Check(e.ReselectLastChoice() && e.CurrentId=="other-topics" && calls==1,"manual reselect failed");
    e.SelectBranch(0);e.Next();Check(e.Mode==RunMode.Gap,"manual confirmation unlocked unknown exit");
    Check(e.OpenGameMenu("topics") && e.UndoCorrection() && e.Mode==RunMode.Gap && e.CurrentId=="b-topic-gap","manual jump undo failed");
});
Test("OCR line confirmation selects only verified segment and undo restores old route",()=>{
    var p=GeneralMenusFixture();var e=new PlaybackEngine(p);e.Restore("start");int calls=0;e.PlayRequested+=_=>calls++;
    Check(e.ConfirmGameLine("b-topic-line") && e.CurrentId=="b-topic-line" && calls==1 && e.Facts.Count==0,"unselected line could not be synchronized");
    Check(e.UndoCorrection() && e.CurrentId=="start" && calls==1 && e.ObservedMenus.Count==0,"OCR undo lost previous scope");
    e.ConfirmGameLine("b-topic-line");e.Next();Check(e.Mode==RunMode.Gap,"OCR crossed unknown exit");
    e.EnterOriginal();string? before=e.CurrentId;int prior=calls;
    Check(!e.ConfirmGameLine("b-topic-line") && !e.OpenGameMenu("topics") && e.CurrentId==before && calls==prior,"original mode played or changed route");
});
Test("unverified OCR line is single only with persistent history",()=>{
    var p=GeneralMenusFixture();var option=p.ById["other-topics"].Options[0];option.BodyVerified=false;option.ExitVerified=false;
    option.LineIds=new(){"b-topic-line"};p.Validate();var e=new PlaybackEngine(p);e.Restore("start");int calls=0;e.PlayRequested+=_=>calls++;
    Check(e.ConfirmGameLine("b-topic-line") && calls==1,"single confirmation rejected");
    var r=new PlaybackEngine(p);Check(r.ImportNavigation(e.ExportNavigation()),r.NavigationError);
    e.Next();Check(e.CurrentId=="other-topics" && e.Mode==RunMode.Choice && calls==1,"unverified single line continued into route");
    e.Previous();Check(e.CurrentId=="b-topic-line" && calls==2,"single history lost its scope");
});
Test("OCR recognizes choice labels before prerequisites and remains silent",()=>{
    var p=GeneralMenusFixture();p.ById["other-topics"].Options[0].Label="12点方向（废弃营地方向）";
    var e=new PlaybackEngine(p);e.Restore("start");int calls=0;e.PlayRequested+=_=>calls++;
    var results=Matcher.Find(e,"s",new(){new(){Text="12点方向(废弃营地方向)",Score=.99}});
    Check(results.Any(c=>c.Node.Id=="other-topics") && e.CurrentId=="start" && calls==0,"option page not found or played");
    Check(e.OpenGameMenu(results.First(c=>c.Node.Id=="other-topics").Node.Id) && calls==0,"menu confirmation played");
});
Test("battle subtitle fragments keep left-to-right order despite sloped boxes",()=>{
    var p=Fixture();p.ById["start"].Text="是栗西提到的备用零件……安葬他之后，把零件带回去吧。";
    var e=new PlaybackEngine(p);var result=Matcher.Find(e,"s",new(){
        new(){Text="·安葬他之后，把零件带回去吧。",Score=.98,Box=new[]{new double[]{1129,1423},new double[]{1686,1427},new double[]{1686,1480},new double[]{1129,1476}}},
        new(){Text="是栗西提到的备用零件",Score=.99,Box=new[]{new double[]{728,1430},new double[]{1146,1428},new double[]{1146,1474},new double[]{728,1476}}}
    });Check(result[0].Node.Id=="start" && result[0].Score>.9,"battle subtitle was reversed");
});
Test("draft missing lines remain visible and navigation continues",()=>{
    var p=Fixture();p.Delivery=new(){Status="draft",Revision="old"};p.ById["start"].AudioStatus="draft-missing";p.ById["start"].AudioReason="角色待审";
    var e=new PlaybackEngine(p);e.Commit("start");Check(p.AudioNotice(e.Current!).Contains("角色待审") && e.Current!.Text.Length>0,"draft missing line hidden");
    e.Next();Check(e.CurrentId=="choice" && e.Mode==RunMode.Choice,"draft gap blocked next line");
    Check(p.DraftSummary.Contains("未逐句核验") && p.DraftSummary.Contains("待补 4"),"draft quality or counts missing");
});
Test("draft audio update preserves all navigation and does not play",()=>{
    string folder=Path.Combine(Path.GetTempPath(),"pgr-draft-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
    try
    {
        var p=Fixture();p.Root=folder;p.Delivery=new(){Status="draft",Revision="old"};
        var e=new PlaybackEngine(p);e.Commit("start");e.Next();e.SelectBranch(1);
        string before=JsonSerializer.Serialize(e.ExportNavigation(),Json.Options);int plays=0,stops=0;e.PlayRequested+=_=>plays++;e.StopRequested+=()=>stops++;
        var updated=JsonSerializer.Deserialize<Pack>(JsonSerializer.Serialize(p,Json.Options),Json.Options)!;updated.Root=folder;updated.Delivery!.Revision="new";
        File.WriteAllBytes(Path.Combine(folder,"new.wav"),new byte[]{1,2,3});updated.Nodes.Single(n=>n.Id=="b1").Audio="new.wav";updated.Validate();
        Check(p.TryRefreshDraftAudio(updated,out var reason),reason);
        Check(JsonSerializer.Serialize(e.ExportNavigation(),Json.Options)==before && plays==0 && stops==0,"audio refresh mutated live navigation");
        Check(e.Current!.Audio=="new.wav" && p.Delivery!.Revision=="new","new audio not applied to live node");
        e.EnterOriginal();string original=JsonSerializer.Serialize(e.ExportNavigation(),Json.Options);stops=0;
        updated.ById["b1"].Audio=null;updated.ById["b1"].AudioStatus="draft-missing";updated.ById["b1"].AudioReason="候选复核失败";
        Check(p.TryRefreshDraftAudio(updated,out reason) && JsonSerializer.Serialize(e.ExportNavigation(),Json.Options)==original && plays==0 && stops==0,"removing bad audio changed original mode");
    }
    finally { Directory.Delete(folder,true); }
});
Test("draft refresh rejects incomplete audio and changed story without altering old audio",()=>{
    var p=Fixture();p.Delivery=new(){Status="draft",Revision="old"};
    var updated=JsonSerializer.Deserialize<Pack>(JsonSerializer.Serialize(p,Json.Options),Json.Options)!;updated.Root=p.Root;updated.Delivery!.Revision="new";updated.Validate();
    updated.ById["start"].Audio="pgr-absent-"+Guid.NewGuid().ToString("N")+".wav";
    Check(!p.TryRefreshDraftAudio(updated,out _) && p.Delivery!.Revision=="old" && p.ById["start"].Audio==null,"partial publication became visible");
    updated.ById["start"].Audio=null;updated.ById["start"].Text="发生正文变更";
    Check(!p.TryRefreshDraftAudio(updated,out _) && p.ById["start"].Text!="发生正文变更","story correction auto-applied");
    updated.ById["start"].Text=p.ById["start"].Text;updated.ById["start"].NextId="common";
    Check(!p.TryRefreshDraftAudio(updated,out _),"changed navigation auto-applied");
    updated=Fixture();updated.Delivery=null;Check(!p.TryRefreshDraftAudio(updated,out _),"formal pack changed by draft refresh");
});
if(args.Length > 1 && args[1]=="--branches") BranchTests.Run(Pack.Load(args[0]));
if(args.Length > 0) Test("real pack loads and every bound audio exists", () => { var p=Pack.Load(args[0]);Check(p.Nodes.All(n=>n.Audio==null || File.Exists(p.ResolveAudio(n))),"missing shipped audio"); Console.WriteLine($"REAL PACK: {p.Nodes.Count} nodes"); });
Console.WriteLine($"RESULT: {passed} tests passed");
public sealed class EvaluationCase
{
    public string Id { get; set; }="";
    public string SectionId { get; set; }="";
    public string? CurrentId { get; set; }
    public string ExpectedId { get; set; }="";
    public List<OcrBlock> Blocks { get; set; }=new();
}
