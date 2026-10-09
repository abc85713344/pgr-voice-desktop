using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunGameWaitingRecoveryChecks(Action<bool,string> check)
    {
        string[] args=Environment.GetCommandLineArgs();
        int argument=Array.IndexOf(args,"--waiting-recovery-catalog");
        if(argument<0)return;
        if(!testUi || argument+1>=args.Length)throw new InvalidOperationException("真实待续接检查必须使用隔离测试入口与冻结目录。");
        using var catalog=JsonDocument.Parse(File.ReadAllText(args[argument+1]));
        string oldGame=preferences.PackFile;
        try
        {
            // 只读正式包。用户进度由 --test-state 完全隔离，测试不打开真实录音设备。
            var sections=catalog.RootElement.GetProperty("sections").EnumerateArray().ToList();
            (string File,string Menu,string Option,string Node)? selected=null;
            foreach(var item in catalog.RootElement.GetProperty("cases").EnumerateArray())
            {
                string packId=item.GetProperty("packId").GetString()!;
                string menuId=item.GetProperty("menuId").GetString()!;
                string optionId=item.GetProperty("optionId").GetString()!;
                var source=sections.First(s=>s.GetProperty("packId").GetString()==packId);
                string file=source.GetProperty("packFile").GetString()!;
                string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                if(!hash.Equals(source.GetProperty("packSha256").GetString(),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("真实目录冻结后包身份发生变化："+packId);
                var pack=Pack.Load(file);var option=pack.ById[menuId].Options.Single(o=>o.Id==optionId);
                if(!option.BodyVerified || option.ExitVerified)continue;
                var line=option.LineIds.Select(id=>pack.ById[id]).FirstOrDefault(n=>!n.Archived && n.Kind=="line"
                    && n.PathId==option.PathId && !option.SegmentIds.Contains(n.Id) && n.Text.Length>=10
                    && pack.AudioNotice(n).Length==0 && File.Exists(pack.ResolveAudio(n))
                    && GameTextPolicy.Match(pack,n.SectionId,n.Text,out _,n.Speaker)?.Id==n.Id);
                if(line==null)continue;
                // 本用例验证的是范围末端 Gap 的手动正文/OCR 入口；先遇嵌套选择的路线
                // 仍由全量目录与阻断检查覆盖，不能把尚未作出的嵌套选择当作已走到 Gap。
                var probe=new PlaybackEngine(pack);
                if(!probe.OpenGameMenu(menuId,line.SectionId))continue;
                probe.SelectBranch(probe.AvailableOptions.FindIndex(o=>o.Id==optionId));
                for(int i=0;i<500 && probe.Mode==RunMode.Following;i++)probe.Next(true);
                if(probe.Mode!=RunMode.Gap || probe.Allowed(line))continue;
                selected=(file,menuId,optionId,line.Id);break;
            }
            check(selected!=null,"本轮真实待续接目录包含已录音但范围外的明确正文，验收未用虚构正文代替");
            var target=selected!.Value;LoadPack(target.File);
            var owner=engine!;var lineNode=owner.Pack.ById[target.Node];
            var requests=new List<string>();owner.PlayRequested+=n=>{if(n!=null)requests.Add(n.Id);};
            void Boundary()
            {
                owner.OpenGameMenu(target.Menu,lineNode.SectionId);
                owner.SelectBranch(owner.AvailableOptions.FindIndex(o=>o.Id==target.Option));
                for(int i=0;i<500 && owner.Mode==RunMode.Following;i++)owner.Next(true);
            }
            Boundary();
            check(owner.Mode==RunMode.Gap && !owner.Allowed(lineNode),"真实已核正文后保留Gap，范围外后句未被自动开放："+target.Node);
            Expand(StoryTab);SearchBox.Clear();showAllSectionLines.IsChecked=true;BrowseCurrent();
            string before=JsonSerializer.Serialize(owner.ExportNavigation());
            LinesList.SelectedItem=rows.Single(r=>r.Node.Id==lineNode.Id);
            check(JsonSerializer.Serialize(owner.ExportNavigation())==before && ConfirmPlayButton.Content!.ToString()!.Contains("游戏正显示这句"),
                "真实全台词目录无搜索即可选中后文，浏览不改变断点且明确要求游戏当前句确认");
            int plays=playCalls, requested=requests.Count;
            ((IInvokeProvider)new ButtonAutomationPeer(ConfirmPlayButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(owner.CurrentId==lineNode.Id && playCalls==plays+1 && requests.Skip(requested).Contains(lineNode.Id)
                && owner.ExportNavigation().Current.SingleLine,"真实范围外正文经桌面确认到达PlayRequested，按现有音频绑定单句播放");
            owner.Next(true);
            check(owner.Mode==RunMode.Choice && owner.CurrentId==target.Menu && playCalls==plays+1,
                "真实未核后文单句结束后返回所属选择点，不顺序跨过未知连接");
            Boundary();before=JsonSerializer.Serialize(owner.ExportNavigation());
            var candidates=Matcher.FindAll(owner,lineNode.SectionId,new()
            {new(){Text=lineNode.Text,Score=1},new(){Text=lineNode.Speaker,Score=1}});
            var match=candidates.Single(c=>c.Node.Id==lineNode.Id);
            check(match.Score>.85 && JsonSerializer.Serialize(owner.ExportNavigation())==before,
                "F9候选匹配范围覆盖真实范围外正文，提出候选阶段不修改进度");
            CandidatesList.ItemsSource=new[]{match};CandidatesList.SelectedIndex=0;
            plays=playCalls;ConfirmCandidate();
            check(owner.CurrentId==lineNode.Id && playCalls==plays+1 && owner.ExportNavigation().Current.SingleLine,
                "F9候选确认实际发出范围外单句播放，不仅显示候选");
            owner.Next(true);check(owner.Mode==RunMode.Choice && playCalls==plays+1,
                "OCR定位的未知正文仍单句回待选，不因识别成功放开未知出口");
            before=JsonSerializer.Serialize(owner.ExportNavigation());
            check(Matcher.FindAll(owner,lineNode.SectionId,new()).Count==0 && JsonSerializer.Serialize(owner.ExportNavigation())==before
                && owner.Mode!=RunMode.End,"OCR零识别不会推进、跳节或解释成故事结束");
            check(GameTextPolicy.Match(owner.Pack,lineNode.SectionId,lineNode.Text,out _,lineNode.Speaker)?.Id==lineNode.Id,
                "原正文监听策略可唯一匹配真实范围外台词，不受CanLocate过滤");
        }
        finally { showAllSectionLines.IsChecked=false;if(File.Exists(oldGame))LoadPack(oldGame); }
    }
}
