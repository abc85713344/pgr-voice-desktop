using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunLibrarySearchUiTest()
    {
        var report = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        try
        {
            if (!testUi) throw new InvalidOperationException("仅允许隔离测试");
            string root = Path.Combine(Log.DataDir, "fixtures", "library-search");
            string WritePack(string folder, string id)
            {
                string file = Path.Combine(root, folder, "pack.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var pack = new Pack { Id = id, Title = folder, Chapters = new() { new() { Id = "chapter", Title = folder,
                    Sections = new() { new() { Id = "section", Title = "搜索小节", StartId = "line" } } } },
                    Nodes = new() { new() { Id = "line", SectionId = "section", Speaker = "露西亚", Text = "测试跨章正文。", NextId = "end" },
                        new() { Id = "branch", SectionId = "section", Speaker = "露西亚", Text = "测试跨章正文。", PathId = "other" },
                        new() { Id = "end", SectionId = "section", Kind = "end" } } };
                File.WriteAllText(file, JsonSerializer.Serialize(pack, Json.Options)); return file;
            }
            string first = WritePack("第一章", "search-first"), second = WritePack("第二章", "search-second");
            SetLibrary(root); LoadPack(first); Expand(StoryTab);
            var original = engine!; string originalState = JsonSerializer.Serialize(original.ExportNavigation()); int plays = playCalls;
            listeningSession = new ListeningSession(Pack.Load(first), "chapter"); listeningOffset = 1234;
            var originalListening = listeningSession; var originalListeningNode = listeningSession.Current?.NodeId;
            var selected = (await librarySearchIndex.SearchAsync(new[] { first, second }, "跨章")).Results.First(r => r.PackFile == second && r.NodeId == "branch");
            Exception? dialogError = null;
            experienceDialogTest = async dialog =>
            {
                try
                {
                    var searchWindow = (LibrarySearchWindow)dialog;
                    searchWindow.Query.Text = "露西亚 跨章"; await searchWindow.RunSearch();
                    Check(searchWindow.Results.Items.Count == 4, "真实搜索窗口跨两章展示重复分支，未合并同文");
                    searchWindow.Results.SelectedItem = searchWindow.Results.Items.OfType<LibrarySearchResult>().First(r => r.PackFile == second && r.NodeId == "branch");
                    Check(searchWindow.Preview.Text.Contains("台词编号：branch") && searchWindow.Preview.Text.Contains("路线：other"), "选中结果预览原节点和路线");
                    searchWindow.Results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(searchWindow), Environment.TickCount, Key.Enter)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(searchWindow.Result == null && engine == original && originalState == JsonSerializer.Serialize(engine.ExportNavigation()) && plays == playCalls,
                        "输入、搜索、选中与回车只预览，不切包、改路线或发声");
                    Check(listeningSession == originalListening && listeningSession.Current?.NodeId == originalListeningNode && listeningOffset == 1234,
                        "搜索和预览保留独立听书会话及句内断点");
                    searchWindow.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(searchWindow.ActualWidth), (int)Math.Ceiling(searchWindow.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(searchWindow);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(Log.DataDir, "全库搜索预览.png"))) png.Save(output);
                    searchWindow.Close();
                }
                catch (Exception ex) { dialogError = ex; dialog.Close(); }
            };
            OpenLibrarySearch(); experienceDialogTest = null; if (dialogError != null) throw dialogError;
            Check(engine == original && originalState == JsonSerializer.Serialize(engine.ExportNavigation()), "关闭搜索保留原游戏位置");
            engine!.EnterOriginal(); var originalMode = JsonSerializer.Serialize(engine.ExportNavigation());
            await OpenLibrarySearchResult(selected);
            Check(engine == original && originalMode == JsonSerializer.Serialize(engine.ExportNavigation()) && playCalls == plays,
                "原声时段拒绝搜索跨章定位，保留原声和当前章节");
            LoadPack(first);
            await OpenLibrarySearchResult(selected);
            Check(engine?.Pack.Id == "search-second" && (LinesList.SelectedItem as LineRow)?.Node.Id == "branch", "明确打开后准确选中另一章的分支节点");
            Check(engine!.Choices.Count == 0 && engine.CurrentId != "branch" && playCalls == plays && !textArmed,
                "打开结果保持静音，不自动确认分支或改变为目标播放位置");
            var next = engine; string nextState = JsonSerializer.Serialize(next.ExportNavigation());
            File.AppendAllText(second, "\n"); await OpenLibrarySearchResult(selected);
            Check(engine == next && nextState == JsonSerializer.Serialize(next.ExportNavigation()) && Notice.Text.Contains("已经更新"), "过期结果打开前被拒绝，当前状态不变");
            var contentUpdate = Pack.Load(second);
            contentUpdate.ById["branch"].Audio = "updated-fixture.wav";
            contentUpdate.ById["line"].Text = "另一句正文已更新。";
            File.WriteAllText(second, JsonSerializer.Serialize(contentUpdate, Json.Options));
            var newContentResult = (await librarySearchIndex.SearchAsync(new[] { second }, "跨章", refresh: true)).Results.First(r => r.NodeId == "branch");
            await OpenLibrarySearchResult(newContentResult);
            Check(engine != next && engine!.Pack.ById["branch"].Audio == "updated-fixture.wav" && engine.Pack.ById["line"].Text == "另一句正文已更新。" && playCalls == plays,
                "导航图未变但音频或其他正文更新时，明确打开仍重载新清单且不发声");
            next = engine!;
            var changedGraph = Pack.Load(second); changedGraph.ById["line"].NextId = "branch";
            File.WriteAllText(second, JsonSerializer.Serialize(changedGraph, Json.Options));
            var newGraphResult = (await librarySearchIndex.SearchAsync(new[] { second }, "跨章", refresh: true)).Results.First(r => r.NodeId == "branch");
            string oldFingerprint = PlaybackEngine.NavigationFingerprint(engine!.Pack);
            stateSaves.Flush();
            // 只锁隔离设置临时文件，模拟保存失败使 LoadPack 保留旧包；真实用户配置不参与。
            using (var locked = new FileStream(stateFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await OpenLibrarySearchResult(newGraphResult);
                Check(engine == next && PlaybackEngine.NavigationFingerprint(engine.Pack) == oldFingerprint && Notice.Text.Contains("当前章节未能打开"),
                    "重载因保存失败保留旧包时，不能因目标节点同文而误认新导航图已打开");
            }
            Save(); stateSaves.Flush();
            report.Add("说明：合成包与隔离状态验证；未读取正式配音库或操作真实游戏。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { experienceDialogTest = null; experienceDialog?.Close(); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "library-search-ui-test.txt"), report); Close();
    }
}
