using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
namespace PgrVoice;
public partial class MainWindow
{
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    async Task RunBranchesUiTest()
    {
        var results=new List<string>();Window? scene=null;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        try
        {
            Check(engine!=null&&engine.Pack.SchemaVersion>=2,"v2 pack missing");
            scene=new Window{Title="分支菜单验收画面",Width=900,Height=520,Left=250,Top=100,Content=new TextBlock{Text="模拟游戏：鼠标在这里操作\n菜单不应抢焦点",FontSize=26,Margin=new Thickness(30)},Background=Brushes.DarkSlateGray};
            scene.Show();scene.Activate();var handle=new WindowInteropHelper(scene).Handle;
            game=new GameWindow(handle,scene.Title,"BranchFixture");Native.SetForegroundWindow(handle);await Task.Delay(250);
            if(Native.GetForegroundWindow()!=handle)
            {
                uint thread=GetCurrentThreadId(),front=GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
                bool attached=AttachThreadInput(thread,front,true);
                try{scene.Activate();Native.SetForegroundWindow(handle);}finally{if(attached)AttachThreadInput(thread,front,false);}
                await Task.Delay(150);
            }
            Check(Native.GetForegroundWindow()==handle,"测试画面未获得前台，无法验证焦点");
            string mid=engine!.Pack.Chapters[0].Sections[0].Id+"-menu-000";
            preferences.MenuSelections.Clear();engine.Restore(mid,new(),new(),new(),engine.Pack.SchemaVersion);
            int plays=playCalls;engine.Commit(mid);await Task.Delay(120);
            Check(branchMenu.IsVisible&&!expanded&&Native.GetForegroundWindow()==handle&&playCalls==plays,"自动菜单抢焦点、展开主面板或误播");
            Check(new[]{"戴里克","卡朋特","栗西"}.All(name=>engine.AvailableOptions.Any(o=>o.Label.Contains(name))),"人物菜单缺少对应入口");
            int hintIndex=engine.AvailableOptions.FindIndex(o=>o.Label.Contains("卡朋特") && engine.Pack.ById[o.TargetId].Text.Contains("先去找栗西"));
            Check(hintIndex>=1,"缺少可明确选择的初次营地提示");
            branchMenu.Options.SelectedIndex=hintIndex-1;
            results.Add("PASS: automatic compact menu preserves simulated game focus and stays silent");
            var bitmap=new RenderTargetBitmap((int)branchMenu.ActualWidth,(int)branchMenu.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(branchMenu);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(Log.DataDir,"branch-menu.png")))png.Save(stream);
            keyboard!.OnRawKey(40,true,handle);keyboard.OnRawKey(40,true,handle);keyboard.OnRawKey(40,false,handle);await Task.Delay(80);
            Check(branchMenu.Options.SelectedIndex==(keyboard.HookAvailable?hintIndex-1:hintIndex)&&playCalls==plays,"菜单输入来源未分离或误播");
            branchMenu.Options.SelectedIndex=hintIndex-1;
            Check(keyboard!.ProbeMenuKey(40,true),"down not captured");keyboard.OnRawKey(40,true,handle);await Task.Delay(80);Check(keyboard.ProbeMenuKey(40,true),"held down leaked");await Task.Delay(80);keyboard.ProbeMenuKey(40,false);
            keyboard.OnRawKey(40,false,handle);
            Check(branchMenu.Options.SelectedIndex==hintIndex&&playCalls==plays,"浏览误播或长按重复");
            Check(keyboard.ProbeMenuKey(27,true),"escape leaked");await Task.Delay(80);Check(keyboard.ProbeMenuKey(27,false),"escape release leaked");
            Check(!branchMenu.IsVisible&&engine.Mode==RunMode.Choice,"escape changed pending choice");
            OpenStory();Check(branchMenu.Options.SelectedIndex==hintIndex&&Native.GetForegroundWindow()==handle,"selection/focus lost on reopen");
            results.Add("PASS: hook consumes menu keys, repeat is ignored, Escape preserves selection");
            Check(keyboard.ProbeMenuKey(13,true),"enter leaked");await Task.Delay(120);
            Check(!branchMenu.IsVisible&&engine.Current!.Text.Contains("先去找栗西")&&playCalls==plays+1,"confirm failed to play initial-stage hint");
            Check(keyboard.ProbeMenuKey(13,true)&&keyboard.ProbeMenuKey(13,false),"confirm repeat/release leaked after dismissal");
            Check(playCalls==plays+1,"confirmation replayed");
            results.Add("PASS: confirm plays correct condition hint once; repeat and release remain consumed");
            engine.Next();Check(branchMenu.IsVisible&&engine.CurrentId==mid,"segment did not return to interaction menu");
            engine.EnterOriginal();Check(!branchMenu.IsVisible,"original mode displayed menu");engine.Next();engine.OpenMenu();Check(engine.Mode==RunMode.Original&&playCalls==plays+1,"original mode advanced");
            results.Add("PASS: advancing after segment returns menu; original mode suppresses menu and sound");
            engine.Commit(mid);branchMenu.Move(2);await Task.Delay(100);
            // HWND_NOACTIVATE 的鼠标激活策略，以及切离绑定游戏后的按键范围。
            Check(keyboard.CaptureMenu!(Key.Up,handle)>0&&keyboard.CaptureMenu(Key.Up,new IntPtr(12345))==0&&keyboard.CaptureMenu(Key.Space,handle)==0,"capture scope too broad");
            results.Add("PASS: only four menu keys from bound game/player are captured; advance and foreign app pass through");
            if(engine.Pack.Nodes.Any(n=>n.MenuNavigationEvidence.Count>0))
            {
                OpenInteractions();int count=playCalls;string? position=engine.CurrentId;
                HandleGlobal(Key.Space,handle);HandleGlobal(Key.PageDown,handle);
                Check(branchMenu.IsNavigation&&engine.CurrentId==position&&playCalls==count,"人物导航中推进键改变了剧情");
                string topic=engine.Pack.Chapters[0].Sections[0].Id+"-menu-006";
                branchMenu.Options.SelectedItem=branchMenu.Options.Items.OfType<ListBoxItem>().First(x=>(string?)x.Tag==topic);
                Check(keyboard.ProbeMenuKey(13,true),"人物导航确认键穿透");await Task.Delay(100);
                Check(engine.CurrentId==topic&&!branchMenu.IsNavigation&&playCalls==count&&Native.GetForegroundWindow()==handle,"人物导航确认误播或抢焦点");
                Check(keyboard.ProbeMenuKey(13,true)&&keyboard.ProbeMenuKey(13,false)&&playCalls==count,"确认后的长按或释放再次选择话题");
                results.Add("PASS: interaction navigation blocks advance, confirms silently and preserves focus; Enter repeat/release cannot choose a topic twice");
            }
            // 待核对选项确认后应保留菜单，并提供单句续接而不放开路线。
            var unknown=engine.Pack.Nodes.First(n=>!n.Archived&&n.Kind=="choice"&&n.PathId==""&&n.Options.Any(o=>engine.Pack.SchemaVersion==3?!o.BodyVerified:!o.Verified));
            engine.Commit(unknown.Id);engine.SelectBranch(engine.AvailableOptions.FindIndex(o=>engine.Pack.SchemaVersion==3?!o.BodyVerified:!o.Verified));
            Check(branchMenu.IsVisible&&engine.Mode==RunMode.Choice&&engine.ReviewRoute!=null,"unknown menu vanished");
            results.Add("PASS: unresolved route leaves menu and recovery controls visible");
            if(engine.Pack.SchemaVersion==3)
            {
                var partial=engine.Pack.Nodes.First(n=>!n.Archived&&n.Kind=="choice"&&n.PathId==""&&n.Options.Any(o=>o.BodyVerified&&!o.ExitVerified&&o.SegmentIds.All(id=>engine.Pack.ById[id].Kind=="line")&&o.Requires.Count==0));
                engine.Commit(partial.Id);int index=engine.AvailableOptions.FindIndex(o=>o.BodyVerified&&!o.ExitVerified&&o.SegmentIds.All(id=>engine.Pack.ById[id].Kind=="line"));
                engine.SelectBranch(index);Check(!branchMenu.IsVisible&&engine.Mode==RunMode.Following,"segment confirmation did not start playback");
                for(int i=0;i<500&&engine.Mode==RunMode.Following;i++)engine.Next();
                Check(engine.Mode==RunMode.Gap&&branchMenu.IsVisible,"segment boundary menu missing");
                var boundary=engine.CurrentId;int count=playCalls;engine.Next(true);Check(engine.CurrentId==boundary&&playCalls==count,"boundary advanced");
                HideBranchMenu();OpenStory();Check(branchMenu.IsVisible&&playCalls==count,"boundary reopen played");
                engine.EnterOriginal();Check(!branchMenu.IsVisible,"original mode boundary visible");
                results.Add("PASS: v3 segment confirmation, mandatory boundary, silent reopen and original mode");
            }
            File.WriteAllLines(Path.Combine(Log.DataDir,"branches-ui-test.txt"),results);
        }
        catch(Exception ex){File.WriteAllLines(Path.Combine(Log.DataDir,"branches-ui-test.txt"),results.Concat(new[]{"FAIL: "+ex}));}
        finally{game=null;scene?.Close();Close();}
    }
}
