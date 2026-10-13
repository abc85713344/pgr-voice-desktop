using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    // 隔离 UI 回归：只操作本进程 WPF 和 test-state 内的合成图片/存档。
    async Task RunUiReviewTest()
    {
        if (!testUi) throw new InvalidOperationException("只允许独立测试状态。");
        timer.Stop(); listeningTestAudio = true;
        var results = new List<string>();
        var measurements = new List<object>();
        void Check(bool ok, string message) => results.Add((ok ? "PASS: " : "FAIL: ") + message);
        async Task Drain() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(80); UpdateLayout(); }
        void Capture(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(Path.Combine(Log.DataDir, name + ".png")); png.Save(stream);
        }
        T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T value) return value;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
            return null;
        }
        string Fixture(string id, string title)
        {
            string folder = Path.Combine(Log.DataDir, id); Directory.CreateDirectory(folder);
            var pack = new Pack { Id = id, Title = title, Chapters = new() { new() { Id = id, Title = title, Sections = new() { new() { Id = id + "-s", Title = "1-1 检查小节", StartId = id + "-0" } } } } };
            for (int i = 0; i < 15; i++) pack.Nodes.Add(new() { Id = id + "-" + i, SectionId = id + "-s", Speaker = "丽芙", Text = i == 0 ? string.Concat(Enumerable.Repeat("这是一句用于检查长台词显示的文字，窗口里需要同时看清当前句与后续台词。", 5)) : "后续台词 " + i, NextId = i < 14 ? id + "-" + (i + 1) : null });
            pack.Validate(); string file = Path.Combine(folder, "pack.json"); Json.Save(file, pack); return file;
        }
        try
        {
            string gameFile = Fixture("ui-review-game", "01 游戏章节"), bookFile = Fixture("ui-review-book", "02 收听章节");
            LoadPack(gameFile); engine!.Commit("ui-review-game-0"); Expand(StoryTab); BrowseCurrent();
            foreach (var size in new[] { (580d,760d,1d), (580d,620d,1d), (420d,620d,1d), (580d,760d,2d), (580d,488d,1d), (420d,480d,2d), (420d,760d,1d) })
            {
                Width=size.Item1; Height=size.Item2; preferences.ReadingScale=size.Item3; ApplyReadingScale(); ApplyCompactLayout(); UpdateState(); await Drain();
                var bottom=PlaybackBar.TranslatePoint(new Point(0,PlaybackBar.ActualHeight),this).Y;
                measurements.Add(new { scenario="layout", width=Width,height=Height,scale=size.Item3,listHeight=LinesList.ActualHeight,currentHeight=CurrentCard.ActualHeight,scroll=CurrentLineScroll.ScrollableHeight,footerBottom=bottom,toolbarHeight=ToolbarHost.ActualHeight,volumeWidth=VolumeSlider.ActualWidth });
                Check(LinesList.ActualHeight>=80 && bottom<=Height+1 && VolumeSlider.ActualWidth>=40,$"{Width}×{Height} 字号 {size.Item3*100}%：台词列表≥80、底栏可见、音量可操作");
                Check(CurrentLineScroll.ScrollableHeight>0,"长台词可独立滚动 "+size);
                Capture(this,$"layout-{Width}-{Height}-{size.Item3}");
            }
            Width=580;Height=760;preferences.ReadingScale=1;ApplyReadingScale();ApplyCompactLayout();
            string? before=engine.CurrentId;
            OpenListeningPack(bookFile,"ui-review-book");Expand(listeningTab);UpdateState();await Drain();UpdateState();
            measurements.Add(new {scenario="listening-header",title=HeroTitle.Text,number=HeroNumberChipText.Text,section=HeaderChapter.Text,actual=listeningSession?.Pack.Title});
            Check(HeroTitle.Text.Contains("收听章节") && HeroNumberChipText.Text=="CH.02","听书页封面标题与实际收听章节一致");
            Check(engine.CurrentId==before,"听书页刷新不改变游戏位置");Capture(this,"listening-header");
            StopListeningForGame();Expand(StoryTab);UpdateState();await Drain();
            Check(HeroTitle.Text.Contains("游戏章节") && HeroNumberChipText.Text=="CH.01","返回游戏页恢复对应章节标题");

            var choices=Enumerable.Range(1,43).Select(i=>new PackChoice(Path.Combine(Log.DataDir,"chapter-"+i,"pack.json"),$"主线第{i:00}章 滚动检查") { PackId=$"chapter-{i:00}" }).ToList();
            var picker=new ChapterPickerWindow(choices,choices[0],false) {Owner=this};
            var coverFixture=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255},4);coverFixture.Freeze();
            picker.CoverLoader=(_,apply)=>apply(coverFixture);
            picker.Show();await Drain();
            picker.Categories.SelectedItem="本我回廊";await Drain();picker.Categories.SelectedItem=choices[0].Category;await Drain();picker.UpdateLayout();
            var scroll=Find<ScrollViewer>(picker.Chapters)!;scroll.ScrollToTop();await Drain();
            double offset=scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.MouseWheelEvent});await Drain();
            measurements.Add(new {scenario="chapter-wheel",before=offset,after=scroll.VerticalOffset,viewport=scroll.ViewportHeight,extent=scroll.ExtentHeight,wheelLines=SystemParameters.WheelScrollLines});
            Check(scroll.VerticalOffset>offset && scroll.VerticalOffset-offset<scroll.ViewportHeight,"章节滚轮一次只滚动一段");Capture(picker,"chapter-wheel");
            picker.Chapters.ScrollIntoView(choices[^1]);await Drain();
            Check(picker.CoverOf(choices[^1])!=null,"超过40章时，滚动到末尾也能加载章节缩略图");
            picker.Chapters.SelectedItem=choices[^1];await Drain();
            Check(picker.CoverOf(choices[^1])!=null,"超过40章时，末尾章节也能加载封面预览");Capture(picker,"chapter-last-cover");picker.Close();

            string art=Path.Combine(Log.DataDir,"cover-reload.png");
            void WriteImage(Color c)
            {
                var pixel=new byte[]{c.B,c.G,c.R,255}; var bmp=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,pixel,4);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var stream=File.Create(art);png.Save(stream);
            }
            async Task<BitmapSource?> ReadImage()
            {
                var completion=new TaskCompletionSource<BitmapSource?>();LoadCharacterImage(art,CoverDecodeHeight,image=>completion.TrySetResult(image));
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            byte[] Pixel(BitmapSource? source)
            {
                if(source==null)return Array.Empty<byte>();var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
                var data=new byte[4];converted.CopyPixels(new Int32Rect(0,0,1,1),data,4,0);return data;
            }
            WriteImage(Colors.Red);var first=Pixel(await ReadImage());WriteImage(Colors.Blue);
            RescanChapterCovers();while(coverScanning)await Task.Delay(20);
            var second=Pixel(await ReadImage());measurements.Add(new {scenario="cover-refresh",first,second});
            Check(first.Length==4 && second.Length==4 && first[2]==255 && second[0]==255 && second[2]==0,"替换同名封面后重新扫描能够刷新图片");
        }
        catch(Exception ex){results.Add("FAIL: "+ex);}
        finally
        {
            listeningTestAudio=false;StopListeningForGame();
            Json.Save(Path.Combine(Log.DataDir,"ui-review-measurements.json"),measurements);
            File.WriteAllLines(Path.Combine(Log.DataDir,"ui-review-test.txt"),results);Close();
        }
    }
}
