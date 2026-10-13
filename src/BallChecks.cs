using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PgrVoice;

public partial class MainWindow
{
    /// <summary>悬浮球 14-02A 验收：收起后截取真实 64×64 悬浮球，并把全部状态按 3x 渲染成对照图（只写入隔离测试目录）。</summary>
    async Task RunBallUiTest()
    {
        var report = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        try
        {
            if (!testUi) throw new InvalidOperationException("悬浮球验收只允许在隔离测试入口运行。");
            timer.Stop();
            preferences.CompactMode = "ball"; Collapse(false); UpdateState(); UpdateLayout(); await Task.Delay(200);
            Check(BallView.IsVisible && Width == 64 && Height == 64, "收起后显示 64×64 悬浮球");
            Check(BallArt.Label.Length <= 6 || BallArt.Label.StartsWith("草稿·"), "标签不超过 6 字");
            Check(!BallArt.IsHitTestVisible, "线稿不拦截单击 / 拖动");
            Screenshot("ball-02a-idle.png");
            Save3x(BallArt.Glyph, BallArt.Label, "ball-02a-idle-3x.png");
            var states = new (string glyph, BallIcon icon, string label, bool hl, bool fill, bool orig, bool draft, bool play, bool wavy)[]
            {
                ("声", BallIcon.Text, "待开始", false, false, false, false, false, false),
                ("♫", BallIcon.Note, "跟随中", false, false, false, false, true, true),
                ("Ⅱ", BallIcon.Pause, "跟随已暂停", false, false, false, false, false, false),
                ("选", BallIcon.Text, "分支待选", true, true, false, false, false, true),
                ("?", BallIcon.Text, "待手动续接", true, false, false, false, false, true),
                ("原", BallIcon.Text, "游戏原声时段", false, false, true, false, false, true),
                ("声", BallIcon.Text, "草稿·待开始", false, false, false, true, false, false),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Hud.Solid("#E8EBEE") };
            foreach (var s in states)
            {
                var art = new BallArt { Glyph = s.glyph, Icon = s.icon, Label = s.label, Highlight = s.hl, Filled = s.fill, Original = s.orig, Draft = s.draft, Wavy = s.wavy };
                art.Playing = s.play;
                row.Children.Add(new CutFrame { Width = 64, Height = 64, Margin = new Thickness(8), Cut = 13, Corners = CutCorners.Diagonal,
                    Background = s.fill ? LineRow.BranchFill : (Brush)FindResource("BallNormal"), BorderBrush = s.hl ? Theme.Brush("Brass") : (Brush)FindResource("NormalAccent"), Child = art });
            }
            Render(row, 3, "ball-02a-states-3x.png"); Render(row, 1, "ball-02a-states-1x.png");
            report.Add("PASS: 状态对照图已输出");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "ball-ui-test.txt"), report); Close(); }

        void Save3x(string glyph, string label, string name)
        {
            var frame = new CutFrame { Width = 64, Height = 64, Cut = 13, Corners = CutCorners.Diagonal, Background = BallView.Background, BorderBrush = BallView.BorderBrush,
                Child = new BallArt { Glyph = glyph, Icon = BallArt.Icon, Label = label, Highlight = BallArt.Highlight, Filled = BallArt.Filled, Original = BallArt.Original, Draft = BallArt.Draft, Wavy = BallArt.Wavy } };
            Render(frame, 3, name);
        }
        void Render(FrameworkElement element, double scale, string name)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); element.Arrange(new Rect(element.DesiredSize)); element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
        }
    }
}
