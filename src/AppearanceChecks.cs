using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace PgrVoice;
public partial class MainWindow
{
    async Task RunThemeUiTest()
    {
        var results = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        try
        {
            Directory.CreateDirectory(Log.DataDir);
            Expand(StoryTab); await Task.Delay(150);
            int calls = playCalls; string? position = engine?.CurrentId;
            Check(HeroArtwork.Source != null && preferences.ShowArtwork, "内置配图未加载或旧配置没有默认开启插画");
            ArtworkEnabledBox.IsChecked = false;
            stateSaves.Flush(); Check(HeroArtwork.Visibility == Visibility.Collapsed && !Json.Read<Preferences>(stateFile).ShowArtwork, "关闭配图未保存");
            ArtworkEnabledBox.IsChecked = true;
            stateSaves.Flush(); Check(HeroArtwork.Visibility == Visibility.Visible && Json.Read<Preferences>(stateFile).ShowArtwork, "恢复配图未保存");
            Check(ChapterSelector.Visibility == Visibility.Collapsed && ChapterColumn.ActualWidth == 0, "单章包章节选择重复显示");
            Check(playCalls == calls && engine?.CurrentId == position, "改变外观触发了播放或进度变动");
            results.Add("PASS: offline art, artwork preference roundtrip, single chapter selector, silent appearance changes");
            // 在本机 DPI 下按目标屏幕的逻辑工作区排版，再按目标 DPI 渲染；不改变系统显示设置。
            var longest = engine!.Pack.Nodes.Where(n => n.Kind == "line").MaxBy(n => n.Text.Length)!;
            CurrentSpeaker.Text = longest.Speaker; CurrentText.Text = string.Join(" ", Enumerable.Repeat(longest.Text, 4));
            foreach (var screen in new[] { (1920,1080,1.0), (1920,1080,1.5), (1920,1080,2.0), (2560,1440,1.0), (2560,1440,1.5), (2560,1440,2.0), (3840,2160,1.5), (3840,2160,2.0), (3840,2160,2.25) })
            {
                Width = Math.Min(580, screen.Item1 / screen.Item3 - 20);
                Height = Math.Min(760, (screen.Item2 - 64) / screen.Item3 - 20);
                ApplyCompactLayout(); UpdateLayout(); await Task.Delay(30);
                var point = PlaybackBar.TranslatePoint(new Point(0, PlaybackBar.ActualHeight), this);
                Check(point.Y <= ActualHeight + 1 && ConfirmPlayButton.ActualHeight >= 28 && LinesList.ActualHeight >= 80,
                    $"布局被挤压 {screen}: footer={point.Y:0}/{ActualHeight:0}, list={LinesList.ActualHeight:0}");
                Check(CurrentLineScroll.ScrollableHeight > 0, "长台词没有可滚动空间");
                CurrentLineScroll.ScrollToEnd(); UpdateLayout();
                Check(CurrentLineScroll.VerticalOffset > 0, "长台词无法滚动到底部");
                CurrentLineScroll.ScrollToHome();
                UpdateLayout(); await Task.Delay(30);
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * screen.Item3), (int)Math.Ceiling(ActualHeight * screen.Item3), 96 * screen.Item3, 96 * screen.Item3, PixelFormats.Pbgra32);
                bmp.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
                using var stream = File.Create(Path.Combine(Log.DataDir, $"layout-{screen.Item1}x{screen.Item2}-{screen.Item3*100:0}.png")); png.Save(stream);
                results.Add($"PASS: simulated {screen.Item1}x{screen.Item2} {screen.Item3*100:0}% — list {LinesList.ActualHeight:0} DIP, long text scrolls, playback controls visible");
            }
            Expand(StoryTab); var line = rows.First(r => r.Node.Kind == "line");
            LinesList.SelectedItem = line; engine.Commit(line.Node.Id); Expand(StoryTab); BrowseCurrent(); await Task.Delay(80);
            Screenshot("主面板.png");
            ArtworkEnabledBox.IsChecked = false; Screenshot("无插画.png"); ArtworkEnabledBox.IsChecked = true;
            Expand(SettingsTab); await Task.Delay(50); Screenshot("设置.png");
            Collapse(false); Screenshot("悬浮球.png");
            var menu = engine.Pack.Nodes.First(n => n.Kind == "choice" && !n.Archived && n.PathId == "");
            engine.Commit(menu.Id); Expand(StoryTab); BrowseCurrent(); await Task.Delay(80); Screenshot("分支面板.png");
            Collapse(false); ShowBranchMenu(); await Task.Delay(80);
            var branchImage = new RenderTargetBitmap((int)branchMenu.ActualWidth,(int)branchMenu.ActualHeight,96,96,PixelFormats.Pbgra32);
            branchImage.Render(branchMenu); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(branchImage));
            using (var stream = File.Create(Path.Combine(Log.DataDir,"分支菜单.png"))) encoder.Save(stream);
            Screenshot("分支悬浮球.png");
            results.Add("PASS: actual WPF screenshots captured for main, settings, no-art, branch panel/menu and floating states");
            var historyLine = engine.Pack.Nodes.First(n => n.Kind == "line" && n.PathId == "" && n.NextId != null && engine.Pack.ById[n.NextId].Kind == "line");
            engine.Commit(historyLine.Id); engine.Next(true); Save();
            stateSaves.Flush(); var saved = Json.Read<Preferences>(stateFile);
            Check(saved.Visits.Count >= 2, "没有形成可回退的访问记录");
            calls = playCalls;
            LoadPack(saved.PackFile);
            Check(playCalls == calls && engine.CurrentId == saved.NodeId && engine.HistoryPosition == saved.VisitPosition && engine.History.Count == saved.Visits.Count, "重新载入时访问记录被覆盖或发生误播");
            engine.Previous();
            Check(engine.CurrentId == saved.Visits[saved.VisitPosition-1].NodeId, "重新载入后的上一句无法回退");
            results.Add("PASS: reloading keeps saved visit history and position, stays silent, and previous follows actual history");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally { File.WriteAllLines(Path.Combine(Log.DataDir,"theme-ui-test.txt"),results); Close(); }
    }
}
