using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunRouteCompatibilityUiTest()
    {
        var report = new List<string>();
        var originals = new Dictionary<string,string>();
        void Check(bool ok,string text) { if(!ok) throw new InvalidOperationException(text); report.Add("PASS: "+text); }
        string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        string Content(Pack p)=>JsonSerializer.Serialize(p.Nodes.Where(n=>n.Kind=="line").Select(n=>new{n.Id,n.Text,n.Speaker,n.Audio}),Json.Options);
        string Graph(Pack p)=>PlaybackEngine.NavigationFingerprint(p);
        try
        {
            Check(testUi,"使用独立测试状态且禁用实际音频设备");
            var args=Environment.GetCommandLineArgs();int pos=Array.IndexOf(args,"--route-compat-catalog");
            Check(pos>=0 && pos+1<args.Length,"已提供冻结来源清单");
            using var catalog=JsonDocument.Parse(File.ReadAllText(args[pos+1]));
            foreach(var entry in catalog.RootElement.GetProperty("packs").EnumerateArray())
            {
                string file=entry.GetProperty("current").GetString()!;originals[file]=Hash(file);
                Check(originals[file].Equals(entry.GetProperty("expectedCurrentSha256").GetString(),StringComparison.OrdinalIgnoreCase),"当前来源身份 "+entry.GetProperty("packId").GetString());
                var raw=Pack.Load(file);var current=DesktopPackLoader.Load(file);
                Check(Graph(raw)==Graph(current) && Content(raw)==Content(current),"新包路线和正文音频保持 "+raw.Id);
                foreach(var oldItem in entry.GetProperty("before").EnumerateArray())
                {
                    string oldFile=oldItem.GetString()!;originals[oldFile]=Hash(oldFile);
                    var old=Pack.Load(oldFile);var compatible=DesktopPackLoader.Load(oldFile);
                    Check(Graph(compatible)==Graph(current),"旧包内存路线达到现行已核图 "+old.Id+" "+oldFile);
                    Check(Content(old)==Content(compatible) && old.Root==compatible.Root,"旧包正文音频及根目录保留 "+old.Id);
                    Check(compatible.CompatibleNavigationFingerprints.Contains(Graph(old)),"旧包导航标记兼容 "+old.Id);
                }
            }
            var ch32=catalog.RootElement.GetProperty("packs").EnumerateArray().Single(e=>e.GetProperty("packId").GetString()=="pgr-ch32");
            string newest=ch32.GetProperty("current").GetString()!, oldest=ch32.GetProperty("before")[0].GetString()!;
            const string section="ch32-71fcf055b68c2cfb1350";
            var old32=Pack.Load(oldest);var oldEngine=new PlaybackEngine(old32);
            oldEngine.Commit(old32.Nodes.Single(n=>n.NextId==section+"-menu-004").Id);
            for(int i=0;i<50 && oldEngine.Mode!=RunMode.Gap;i++) { if(oldEngine.Mode==RunMode.Choice)oldEngine.SelectBranch(1);else oldEngine.Next(); }
            Check(oldEngine.Mode==RunMode.Gap && oldEngine.History.Last().NodeId==section+"-4c93d48d53bbbd670f35","真实旧32图复现我并不是之后误停");
            var saved=oldEngine.ExportNavigation();var heard=saved.Current.Heard.Order().ToArray();
            foreach(string file in new[]{oldest,newest})
            {
                int plays=playCalls;LoadPack(file);await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(engine!=null && Graph(engine.Pack)==Graph(DesktopPackLoader.Load(newest)),"游戏主窗口加载旧/新包实际经过兼容入口 "+file);
                Check(engine!.ImportNavigation(saved,allowBoundaryResume:true) && engine.Current?.Text=="嗤。","游戏旧断点静音恢复到嗤 "+file);
                Check(playCalls==plays && engine.Heard.Order().SequenceEqual(heard),"恢复不发声也不虚增已听事实 "+file);
                Check(engine.Mode==RunMode.Ready,"恢复保持待确认，不擅自开始播放 "+file);
                engine.Commit(engine.CurrentId!);
                Check(engine.Mode==RunMode.Following && engine.Current?.Text=="嗤。" && playCalls==plays+1,"明确确认后从恢复的嗤句开始播放 "+file);
                engine.Next(true);Check(engine.Current?.Text=="去吧，早去早回。","开始后手动下一句继续后文 "+file);
                OpenListeningPack(file,null);
                Check(listeningSession!=null && Graph(listeningSession.Pack)==Graph(engine.Pack) && !listeningRunning,"听书实际入口加载相同修正并保持暂停 "+file);
                var snapshot=listeningSession!.Capture();
                OpenListeningPack(file,null);
                Check(listeningSession!=null && Graph(listeningSession.Pack)==Graph(engine.Pack) && listeningSession.Capture().ItemId==snapshot.ItemId,"听书保存重开仍走兼容入口并保留位置 "+file);
            }
            Check(DesktopPackLoader.Load(newest).CompatibleNavigationFingerprints.Contains("825990ED44822605DB6AFBD71FBEB5193D8084504815FE64C98C0F69C06A08CC"),"内置同源Android旧图标记，未改变全局指纹格式");
            Check(originals.All(x=>Hash(x.Key)==x.Value),"全部实际旧/新章文件原SHA不变");
        }
        catch(Exception ex) { report.Add("FAIL: "+ex); }
        finally
        {
            if(originals.Any(x=>Hash(x.Key)!=x.Value))report.Add("FAIL: 来源章文件发生变化");
            File.WriteAllLines(Path.Combine(Log.DataDir,"route-compat-ui-test.txt"),report);
            Json.Save(Path.Combine(Log.DataDir,"route-compat-ui-result.json"),new{passed=!report.Any(x=>x.StartsWith("FAIL:")),checks=report.Count(x=>x.StartsWith("PASS:")),sources=originals,scope="最终WPF程序游戏和听书加载、旧图恢复；隔离状态，无真实音频/游戏操作"});
            Close();
        }
    }
}
