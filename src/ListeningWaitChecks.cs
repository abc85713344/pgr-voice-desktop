using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunListeningWaitChecks(Action<bool, string> check, string root)
    {
        if (!testUi || !listeningTestAudio) throw new InvalidOperationException("等待续接验收仅限隔离假音频。");
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        async Task Invoke(Button button)
        {
            button.BringIntoView(); UpdateLayout();
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); await Drain();
        }
        string State()
        {
            var snapshot = listeningSession!.Capture(listeningOffset); snapshot.UpdatedUtc = default;
            return JsonSerializer.Serialize(snapshot);
        }
        string oldFile = listeningFile, oldChapter = listeningSession!.ChapterId, oldGame = preferences.PackFile;
        var fixture = new Pack
        {
            SchemaVersion = 3, Id = "desktop-waiting-line-preview", Title = "等待续接与单句试听", Root = root,
            Chapters = new() { new() { Id = "wait-book", Title = "等待续接", Sections = new()
            { new() { Id = "wait-section", Title = "本节未核实连接", StartId = "wait-menu" }, new() { Id = "later-section", Title = "后面小节", StartId = "later-line" } } } },
            Nodes = new()
            {
                new() { Id="wait-menu", SectionId="wait-section", Kind="choice", Options=new()
                { new() { Id="wait-option", Label="选择这条路线", PathId="wait-option", TargetId="known-line", BodyVerified=true,
                    ExitVerified=false, BoundaryId="wait-gap", SegmentIds=new(){"known-line"}, LineIds=new(){"known-line","unknown-line","missing-line"}, BodyEvidence=new(){"isolated-ui-fixture"} },
                  new() { Id="other-option", Label="另一条待核路线", PathId="other-option", TargetId="other-line", LineIds=new(){"other-line"} } } },
                new() { Id="known-line", SectionId="wait-section", PathId="wait-option", Speaker="旁白", Text="这里是最后一条已核正文。", Audio="audio.wav", NextId="unknown-line" },
                new() { Id="unknown-line", SectionId="wait-section", PathId="wait-option", Speaker="露西亚", Text="已收录而尚未接入的后一句。", Audio="audio.wav", NextId="missing-line" },
                new() { Id="missing-line", SectionId="wait-section", PathId="wait-option", Text="这句尚无音频。" },
                new() { Id="other-line", SectionId="wait-section", PathId="other-option", Speaker="里", Text="这是另一条待核路线的正文。", Audio="audio.wav" },
                new() { Id="wait-gap", SectionId="wait-section", PathId="wait-option", Kind="gap", Text="等待确认后续位置。" },
                new() { Id="later-line", SectionId="later-section", Text="不能自动越过等待到这里。", Audio="audio.wav" }
            }
        };
        fixture.Validate(); string file = Path.Combine(root,"waiting-preview.json"); Json.Save(file,fixture);
        try
        {
            LoadPack(file); engine!.Commit("wait-menu"); engine.SelectBranch(0); engine.Next();
            Expand(StoryTab); SearchBox.Clear(); showAllSectionLines.IsChecked=false; BrowseCurrent();
            check(engine.Mode == RunMode.Gap && rows.All(r=>r.Node.Id!="other-line"), "游戏范围末端保留Gap，另一条未确认正文原本被资格过滤");
            string gameBefore=JsonSerializer.Serialize(engine.ExportNavigation());
            var toggle=(IToggleProvider)new ToggleButtonAutomationPeer(showAllSectionLines).GetPattern(PatternInterface.Toggle);
            toggle.Toggle(); await Drain();
            check(rows.Any(r=>r.Node.Id=="other-line") && rows.Any(r=>r.Node.Id=="missing-line") && SearchBox.Text.Length==0,
                "实际全台词切换无需猜搜索词即可看见未接入后句及缺音正文");
            check((LinesList.SelectedItem as LineRow)?.Node.Id=="known-line","游戏等待时全台词目录定位在最后实际播过句附近");
            LinesList.SelectedItem=rows.Single(r=>r.Node.Id=="other-line"); await Drain();
            check(!engine.CanLocate(engine.Pack.ById["other-line"]) && JsonSerializer.Serialize(engine.ExportNavigation())==gameBefore,
                "全台词仅阅读，不自动赋予定位资格或改写游戏路线");
            int gamePlays=playCalls; await Invoke(ConfirmPlayButton);
            check(engine.CurrentId=="other-line" && playCalls==gamePlays+1 && engine.ExportNavigation().Current.SingleLine,
                "玩家显式确认游戏当前句后实际申请单句音频，未核段不开放连续播放");
            engine.Next(true);
            check(engine.Mode==RunMode.Choice && engine.CurrentId=="wait-menu" && playCalls==gamePlays+1,
                "未知正文单句推进返回所属选择点，不跨未知出口");
            OpenListeningPack(file,"wait-book"); Expand(listeningTab);
            check(listeningSession!.Choose("wait-option"),"等待续接夹具显式选中路线");
            listeningOffset=2345; RefreshListening(); SaveListeningProgress();
            listeningBookmarkName.Text="等待之前的位置"; AddListeningBookmark();
            StartListening(); long oldTicket=listeningTicket; OnListeningCompleted(oldTicket); await Drain();
            check(listeningSession.HasBlockingNotice && !listeningSession.Completed && !listeningRunning && listeningTicket==0,
                "已核末句自然结束后停在明确阻断，不跨到下一小节或标记章末");
            check(listeningPosition.Text.Contains("等待续接") && listeningStatus.Text.Contains("等待续接")
                && !listeningPosition.Text.Contains("已听完") && ListeningProgress!.Resume?.Completed==false,
                "等待位置、提示与存档均不误称整章已听完");
            check((listeningLines.SelectedItem as ListeningEntry)?.Id=="known-line", "听书阻断时目录停留最后已播正文，不跳回目录顶部");
            string blocked=State(); MoveListening(()=>listeningSession.MoveNext()); StartListening(); OnListeningCompleted(oldTicket); await Drain();
            check(State()==blocked && listeningSession.HasBlockingNotice && !listeningRunning,
                "等待时下一句、续听及旧音频完成均不能越过阻断");
            listeningSections.SelectedItem=listeningSections.Items.OfType<ListeningEntry>().Single(x=>x.Id=="wait-section");
            listeningLines.SelectedItem=listeningLines.Items.OfType<ListeningEntry>().Single(x=>x.Id=="unknown-line");
            string bookmarks=JsonSerializer.Serialize(ListeningProgress!.Bookmarks); string choices=JsonSerializer.Serialize(listeningSession.Capture().Choices);
            check(listeningLocate.Content?.ToString()=="仅试听选中这一句", "未接入后文提供明确单句试听按钮");
            await Invoke(listeningLocate); long preview=listeningPreviewTicket;
            check(preview>0 && listeningPreviewNode?.Id=="unknown-line" && !listeningRunning && State()==blocked
                && listeningPlay.Content?.ToString()=="停止试听", "实际试听按钮仅申请这句音频，保留阻断位置并提供停止入口");
            OnListeningCompleted(oldTicket); await Drain();
            check(listeningPreviewTicket==preview && State()==blocked,"原连续播放的旧回调不能取消或推进新单句试听");
            OnListeningCompleted(preview); await Drain();
            check(listeningPreviewTicket==0 && !listeningRunning && State()==blocked && listeningSession.HasBlockingNotice,
                "单句试听自然结束保持暂停与等待，不自动下一句");
            check(JsonSerializer.Serialize(listeningSession.Capture().Choices)==choices && JsonSerializer.Serialize(ListeningProgress.Bookmarks)==bookmarks,
                "单句试听不改原路线选择或既有书签");
            await Invoke(listeningLocate); long cancelled=listeningPreviewTicket; await Invoke(listeningPlay); OnListeningCompleted(cancelled); await Drain();
            check(listeningPreviewTicket==0 && State()==blocked && !listeningRunning,"停止试听后旧完成回调失效且位置保持");
            listeningLines.SelectedItem=listeningLines.Items.OfType<ListeningEntry>().Single(x=>x.Id=="missing-line"); await Invoke(listeningLocate);
            check(listeningPreviewTicket==0 && listeningStatus.Text.Contains("无法试听") && State()==blocked,
                "缺音正文明确提示无法试听，不假装播过或改变断点");
            listeningLines.SelectedItem=listeningLines.Items.OfType<ListeningEntry>().Single(x=>x.Id=="unknown-line");
            listeningTestAudio=false; await Invoke(listeningLocate);
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(listeningPreviewTicket!=0 && DateTime.UtcNow<deadline) await Task.Delay(60);
            listeningTestAudio=true;
            check(listeningPreviewTicket==0 && !listeningRunning && State()==blocked,
                "真实200毫秒静音单句音频完成后仍等待，不改变路线和断点");
            OpenListeningPack(file,"wait-book");
            check(listeningSession.HasBlockingNotice && !listeningSession.Completed && !listeningRunning,
                "关闭重开恢复明确等待位置，不重播或变成已完成");
            double oldHeight=Height; Height=620; ApplyCompactLayout(); UpdateLayout(); await Drain(); Screenshot("等待续接-后文单句试听-620.png"); Height=oldHeight;
        }
        finally
        {
            listeningTestAudio=true; PauseListening(); showAllSectionLines.IsChecked=false;
            if(File.Exists(oldGame))LoadPack(oldGame);
            OpenListeningPack(oldFile,oldChapter);
        }
    }
}
