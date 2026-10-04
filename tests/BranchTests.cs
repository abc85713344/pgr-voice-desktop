using PgrVoice;
public static class BranchTests
{
    public static void Run(Pack p)
    {
        if(p.SchemaVersion==3){RunV3(p);return;}
        void Check(bool value,string reason){if(!value)throw new Exception(reason);}
        var section=p.Chapters[0].Sections[0].Id;
        string Menu(int i)=>$"{section}-menu-{i:000}";
        PlaybackEngine Start(){var e=new PlaybackEngine(p);e.Commit(Menu(0));return e;}
        void Choose(PlaybackEngine e,string text){int i=e.AvailableOptions.FindIndex(o=>o.Label.Contains(text));Check(i>=0,"missing choice "+text+" at "+e.CurrentId);e.SelectBranch(i);}
        void ToMenu(PlaybackEngine e){for(int i=0;i<150 && e.Mode is RunMode.Following or RunMode.Merge;i++)e.Next();Check(e.Mode==RunMode.Choice,"route stopped at "+e.CurrentId+" "+e.Mode);}
        void Lixi(PlaybackEngine e){Choose(e,"栗西 · 了解");ToMenu(e);Choose(e,"关于现状");ToMenu(e);Choose(e,"小队");ToMenu(e);Check(e.CurrentId==Menu(3),"topic didn't return");Choose(e,"小队");ToMenu(e);Check(e.Heard.Count>0,"heard missing");Choose(e,"说回刚刚");ToMenu(e);Choose(e,"有撤离");ToMenu(e);Check(e.CurrentId==Menu(0)&&e.Facts.Contains("camp.started"),"camp phase missing");}
        var e=Start();Check(e.AvailableOptions.Count==3,"initial camp should have three people");Choose(e,"戴里克");Check(e.Current!.Text.Contains("先去找栗西"),"premature Derek");ToMenu(e);Check(e.CurrentId==Menu(0),"hint failed return");Choose(e,"卡朋特");Check(e.Current!.Text.Contains("先去找栗西"),"premature carpenter");ToMenu(e);Lixi(e);
        foreach(bool carpenterFirst in new[]{true,false})
        {
            e=Start();Lixi(e);
            foreach(string person in carpenterFirst?new[]{"卡朋特","戴里克"}:new[]{"戴里克","卡朋特"})
            {Choose(e,person);ToMenu(e);Choose(e,person=="戴里克"?"关于那些雾":"备用零件");ToMenu(e);Choose(e,person=="戴里克"?"多保重":"先这样");ToMenu(e);Check(e.CurrentId==Menu(0),"person exit crossed line");}
            Check(e.Facts.Contains("camp.fog")&&e.Facts.Contains("camp.parts"),"facts missing");
            Choose(e,"讨论撤离方案");ToMenu(e);Choose(e,"和栗西");ToMenu(e);Choose(e,"3点");ToMenu(e);Check(e.CurrentId==Menu(20),"wrong direction left menu");Choose(e,"12点");ToMenu(e);Choose(e,"灰鸦小队");
            while(e.Mode==RunMode.Following && !e.Current!.Text.Contains("让我们一起努力"))e.Next();
            Check(e.Current!.Text.Contains("让我们一起努力"),"failed camp common line");
            e.Previous();Check(e.Current!.Text.Contains("提前谢谢"),"back crossed route");
        }
        e=Start();var saved=e.CurrentId;int plays=0;e.PlayRequested+=_=>plays++;e.EnterOriginal();e.SelectBranch(0);e.Next();e.OpenMenu();Check(plays==0&&e.Mode==RunMode.Original&&e.CurrentId==saved,"original mode menu leaked");
        var restored=new PlaybackEngine(p);restored.PlayRequested+=_=>throw new Exception("restore played");var legacy=p.Migrations.Keys.First();restored.Restore(legacy);Check(restored.Mode==RunMode.Ready&&restored.Current!=null,"migration failure");
        foreach(var id in p.Migrations.Keys){restored.Restore(id);Check(restored.Mode==RunMode.Ready&&restored.Current!=null,"unrestorable migration "+id);}
        var first=p.ById[Menu(0)].Options[0];bool verified=first.Verified,body=first.BodyVerified;
        try
        {
            first.Verified=false;first.BodyVerified=false;e=Start();e.SelectBranch(0);Check(e.Mode==RunMode.Choice,"unknown option hid menu");
            var line=p.ById[first.TargetId];Check(e.CanLocate(line)&&!e.Allowed(line),"unknown route lookup scope missing");
            e.CommitSingle(line.Id);Check(e.CurrentId==line.Id&&e.Mode==RunMode.Following,"single line failed");e.Next();Check(e.CurrentId==Menu(0)&&e.Mode==RunMode.Choice&&!e.Choices.ContainsKey(Menu(0)),"single line unlocked route");
        }
        finally{first.Verified=verified;first.BodyVerified=body;}
        var gate=new MenuKeyGate();Check(gate.Update(13,true,true)==(true,true),"capture failed");Check(gate.Update(13,true,false)==(true,false),"repeat leaked after hide");Check(gate.Update(13,false,false)==(true,false),"keyup leaked after hide");Check(gate.Update(13,true,false)==(false,false),"normal key swallowed");gate.Update(13,false,false);Check(gate.Update(40,true,false)==(false,false),"foreign app key swallowed");Check(gate.Update(40,true,true)==(false,false),"held key captured midway");
        // 逐个真实路线建立合法的父路径，走到下一个菜单、共同线或终点。
        int routes=0;
        foreach(var menu in p.Nodes.Where(n=>n.Kind=="choice"&&!n.Archived))
        foreach(var option in menu.Options)
        {
            var choices=new Dictionary<string,string>();var facts=new HashSet<string>(option.Requires);
            void Parents(string path){if(path=="")return;var owner=p.Nodes.First(n=>!n.Archived&&n.Options.Any(o=>o.PathId==path));choices[owner.Id]=path;Parents(owner.PathId);}
            Parents(menu.PathId);var run=new PlaybackEngine(p);run.Restore(menu.Id,choices,facts,new(),p.SchemaVersion);if(!run.Allowed(menu))continue;run.OpenMenu();int index=run.AvailableOptions.FindIndex(o=>o.Id==option.Id);Check(index>=0,"conditional unavailable");run.SelectBranch(index);
            if(!(p.SchemaVersion==3?option.BodyVerified:option.Verified)){Check(run.Mode==RunMode.Choice&&run.CurrentId==menu.Id,"unknown menu disappeared");continue;}
            for(int i=0;i<500&&run.Mode==RunMode.Following;i++)run.Next();
            Check(run.Mode is RunMode.Choice or RunMode.Merge or RunMode.End or RunMode.Gap,"broken route "+option.Id+" "+run.Mode);routes++;
        }
        Console.WriteLine($"PASS BRANCH V2: camp ordering, topics, replay, conditions, common line, history, original, migration, keyboard; {routes} verified routes checked");
    }
    static void RunV3(Pack p)
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        string root=p.Chapters[0].Sections[0].Id+"-menu-000";
        var e=new PlaybackEngine(p);int plays=0;e.PlayRequested+=_=>plays++;
        e.Commit(root);Check(e.AvailableOptions.Count==3&&plays==0,"initial camp menu");
        foreach(string person in new[]{"戴里克","卡朋特"})
        {
            e.SelectBranch(e.AvailableOptions.FindIndex(o=>o.Label.Contains(person)));
            Check(e.Current!.Text.Contains("先去找栗西"),"premature person route");e.Next();Check(e.CurrentId==root&&e.Mode==RunMode.Choice,"hint return");
        }
        e.SelectBranch(e.AvailableOptions.FindIndex(o=>o.Label.Contains("栗西")));
        for(int i=0;i<100&&e.Mode==RunMode.Following;i++)e.Next();Check(e.Mode==RunMode.Choice,"Lixi topics missing");
        e.SelectBranch(e.AvailableOptions.FindIndex(o=>o.Label.Contains("关于现状")));
        for(int i=0;i<100&&e.Mode==RunMode.Following;i++)e.Next();Check(e.Mode==RunMode.Choice,"nested topic missing");
        e.SelectBranch(e.AvailableOptions.FindIndex(o=>o.Label.Contains("小队")));
        for(int i=0;i<100&&e.Mode==RunMode.Following;i++)e.Next();
        Check(e.Mode==RunMode.Gap,"unverified topic exit was guessed");Check(!e.Facts.Contains("camp.started"),"listening inferred game phase");
        Check(e.ResumeMenus.Count>0,"reviewed manual return missing");var target=e.ResumeMenus[0].Id;int before=plays;e.SelectContinuation(0);Check(e.CurrentId==target&&e.Mode==RunMode.Choice&&plays==before,"manual return autoplay");
        e.Previous();Check(e.Current?.Kind=="line","actual history lost");
        Console.WriteLine("PASS BRANCH V3: three camp entrances, conditional hints, nested topics, uncertain exit stop, silent manual return and actual history");
    }
}
