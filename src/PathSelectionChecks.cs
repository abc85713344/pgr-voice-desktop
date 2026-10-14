using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunPathSelectionUiTest(string[] args)
    {
        var report = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); report.Add("PASS: " + message); }
        try
        {
            int index = Array.IndexOf(args, "--pack");
            Check(index >= 0 && index + 1 < args.Length, "传入明确的启动章节");
            string requested = args[index + 1], canonical = Path.GetFullPath(requested);
            var expected = DesktopPackLoader.Load(canonical);
            int plays = playCalls;
            string? position = engine?.CurrentId;
            async Task CheckSelection(string phase)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(engine?.Pack.Id == expected.Id, phase + "：实际载入请求的章节");
                Check(LibraryBox.SelectedItem is PackChoice chosen && LibraryPaths.SameFile(chosen.File, requested), phase + "：章节选择与播放章节一致");
                Check(gameTextLibrary.SelectedItem is PackChoice mirrored && mirrored.PackId == expected.Id, phase + "：游戏配音页同步正确章节");
                foreach (var model in new[] { LibraryBox, gameTextLibrary })
                {
                    var choice = (PackChoice)model.SelectedItem;
                    Check(chapterPickerButtons[model].Chapter.Content is TextBlock text && text.Text == choice.DisplayTitle,
                        phase + "：可见选章按钮显示正确标题");
                }
                Check(LibraryPaths.SameFile(preferences.PackFile, canonical), phase + "：保存路径可准确恢复");
                Check(playCalls == plays && engine?.CurrentId == position, phase + "：载入不自动播放或移动进度");
            }
            await CheckSelection("程序启动");
            Check(LibraryPaths.SameFile(canonical, canonical.Replace('\\', '/')), "正反斜杠指向同一文件");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, canonical);
            Check(LibraryPaths.SameFile(relative, canonical), "相对路径与绝对路径指向同一文件");
            Check(!LibraryPaths.SameFile(canonical, Path.Combine(Path.GetDirectoryName(canonical)!, "different.json")), "不同文件不能混为一章");
            Check(!LibraryPaths.SameFile("", canonical), "空路径不能匹配有效章节");
            LoadPack(canonical.Replace('\\', '/'));
            await CheckSelection("再次载入正斜杠路径");
            LoadPack(relative);
            await CheckSelection("再次载入相对路径");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { File.WriteAllLines(Path.Combine(Log.DataDir, "path-selection-ui-test.txt"), report); Close(); }
    }
}
