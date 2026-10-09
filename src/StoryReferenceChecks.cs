using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PgrVoice.AndroidApp;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunStoryReferenceUiTest()
    {
        var report = new List<string>(); string? source = null, sourceHash = null;
        void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); report.Add("PASS: " + text); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string Navigation() => JsonSerializer.Serialize(engine!.ExportNavigation(), Json.Options);
        try
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "--story-reference-pack");
            Check(testUi && at >= 0 && at + 1 < args.Length, "独立测试状态、禁用实际音频且显式提供来源");
            source = args[at + 1]; sourceHash = Hash(source);
            LoadPack(source); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var pack = engine!.Pack; const string section = "ch32-71fcf055b68c2cfb1350";
            var menu = pack.ById[section + "-menu-002"];
            Check(BundledStoryReference.TryGetNode(pack, menu, out var reference) && reference != null, "真实32-6菜单准确对应内置资料");
            string detail = reference!.LocationText + "\n\n" + reference.DetailText;
            Check(detail.Contains("471") && detail.Contains("472") && detail.Contains("473"), "详情保留选择471与两项实际后继472/473");
            string full = BundledStoryReference.GetSectionText(pack, section);
            Check(full.Contains("九龙人民不会逃离这颗星球") && full.Contains("万世铭"), "小节全文含用户给出的正文与后续选择");
            Check(!string.IsNullOrWhiteSpace(BundledStoryReference.GetSummary(pack)), "可显示当前章资料来源版本");
            var line = pack.Nodes.First(n => n.SectionId == section && n.Kind == "line" && n.Text.Contains("九龙人民不会逃离这颗星球"));
            Check(BundledStoryReference.TryGetNode(pack, line, out var located) && located != null && located.LocationText.Contains("472"), "完整台词和说话人对应游戏472身份");
            string originalText = line.Text, originalSpeaker = line.Speaker;
            try { line.Text += "（测试差异）"; Check(!BundledStoryReference.TryGetNode(pack, line, out _), "正文不同不误用旧身份"); }
            finally { line.Text = originalText; }
            try { line.Speaker = "测试不同角色"; Check(!BundledStoryReference.TryGetNode(pack, line, out _), "说话人不同不误用旧身份"); }
            finally { line.Speaker = originalSpeaker; }
            Check(BundledStoryReference.GetSectionText(pack, "missing-section").Contains("未收录"), "不存在的小节不借用其他小节资料");
            engine.Commit(line.Id); int plays = playCalls; string before = Navigation();
            Exception? dialogError = null;
            experienceDialogTest = dialog =>
            {
                try
                {
                    var window = (StoryReferenceWindow)dialog;
                    Check(window.Reader.IsReadOnly && window.Reader.Text.Contains("472") && window.Reader.Text.Contains(line.Text), "真实窗口显示当前原文和节点且正文只读");
                    window.SectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.Reader.Text == full, "点击本小节全文读取同一节完整文本");
                    window.Search.Text = "九龙人民不会逃离这颗星球";
                    Check(window.FindNext() && window.Reader.SelectedText == window.Search.Text, "查找精确选中目标原文");
                    Check(window.FindNext(), "重复查找可回绕找到原文");
                    window.Search.Text = "不存在的测试词123XYZ"; Check(!window.FindNext(), "未找到给出结果且不改台词");
                    window.Search.Text = ""; Check(!window.FindNext(), "空查找不触发定位");
                    window.CurrentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.Reader.Text.Contains("472") && window.Reader.Text != full, "可返回当前句资料");
                    Check(Navigation() == before && playCalls == plays, "打开切换全文查找均不改路线历史已听或播放次数");
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(Log.DataDir, "内置剧情资料.png"))) encoder.Save(file);
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(!window.IsVisible, "Esc关闭资料窗口");
                }
                catch (Exception ex) { dialogError = ex; dialog.Close(); }
            };
            // 设置页入口读取实际当前句；目录入口另有相同只读处理。
            StoryTab.IsSelected = false;
            StoryReferenceClick(this, new RoutedEventArgs()); experienceDialogTest = null;
            if (dialogError != null) throw dialogError;
            Check(Navigation() == before && playCalls == plays, "关闭资料后保留实际播放位置与播放次数");
            Check(Hash(source) == sourceHash, "正式章节文件未修改");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            experienceDialogTest = null; experienceDialog?.Close();
            if (source != null && sourceHash != null && Hash(source) != sourceHash) report.Add("FAIL: 正式来源变化");
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir, "story-reference-ui-test.txt"), report);
            Json.Save(Path.Combine(Log.DataDir, "story-reference-ui-result.json"), new { passed = !report.Any(s => s.StartsWith("FAIL:")), checks = report.Count(s => s.StartsWith("PASS:")), source, sourceSha256 = sourceHash, scope = "隔离WPF只读资料、查找及无导航/播放副作用；未做真实游戏通关" });
            Close();
        }
    }
}
