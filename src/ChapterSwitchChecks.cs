using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunChapterSwitchUiTest()
    {
        var report = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); report.Add("PASS: " + message); }
        void SelectChapter(PackChoice target)
        {
            var boxPeer = UIElementAutomationPeer.CreatePeerForElement(LibraryBox) ?? new ComboBoxAutomationPeer(LibraryBox);
            var peer = boxPeer.GetChildren()?.FirstOrDefault(child => child.GetName() == target.ToString())
                ?? throw new InvalidOperationException("找不到目标章节的选择接口");
            var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException("章节选项缺少选择操作");
            selection.Select();
        }
        bool Displays(DependencyObject root, string text)
        {
            if (root is TextBlock label && label.Text == text) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (Displays(VisualTreeHelper.GetChild(root, i), text)) return true;
            return false;
        }
        try
        {
            Check(testUi && engine != null && LibraryBox.Items.Count > 2, "已载入隔离测试目录");
            preferences.DialogueGuardEnabled = true; dialogueHeld = true;
            Expand(StoryTab);
            for (int round = 0; round < 5; round++)
            {
                var current = LibraryBox.SelectedItem as PackChoice ?? throw new InvalidOperationException("当前章节选中项丢失");
                var line = round == 4 ? engine!.Pack.Nodes.First(n => !n.Archived && n.Kind == "end")
                    : engine!.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.PathId == "").Skip(round).First();
                engine.Commit(line.Id); // 模拟已开始阅读后更新该章的恢复摘要。
                if (round < 4 && round % 2 == 0) engine.TogglePause();
                if (round == 4) Check(engine.Mode == RunMode.End, "复现小节结束状态");
                HoldDialogue("章节切换验证");
                Save();
                if (stateSaves.Flush() != null) throw new IOException("准备章节进度失败");
                int beforePlays = playCalls;
                int oldHash = current.GetHashCode();
                var target = LibraryBox.Items.OfType<PackChoice>().First(item => item.File != current.File && (round != 0 || item.PackId == "pgr-ch10"));
                LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout();
                await Task.Delay(100);
                report.Add($"INFO: round={round}, oldHash={oldHash}, newHash={current.GetHashCode()}, selected={((PackChoice?)LibraryBox.SelectedItem)?.PackId}");
                Check(current.GetHashCode() == oldHash && ReferenceEquals(LibraryBox.SelectedItem, current),
                    "刷新上次进度不改变章节条目身份或当前选中项");
                var currentItem = LibraryBox.ItemContainerGenerator.ContainerFromItem(current) as ComboBoxItem;
                Check(currentItem != null && current.Resume.Length > 0 && current.Resume == progressStore!.GetSummary(current.PackId) &&
                    current.DisplayTitle == current.Title && Displays(currentItem, current.Title), "下拉选项只显示章节名，进度摘要仍正常保存");
                var item = LibraryBox.ItemContainerGenerator.ContainerFromItem(target) as ComboBoxItem;
                if (item == null) throw new InvalidOperationException("章节选项未生成");
                item.BringIntoView(); item.UpdateLayout();
                SelectChapter(target);
                LibraryBox.IsDropDownOpen = false;
                await Task.Delay(120);
                Check(engine!.Pack.Id == target.PackId && ReferenceEquals(LibraryBox.SelectedItem, target) &&
                    string.Equals(preferences.PackFile, target.File, StringComparison.OrdinalIgnoreCase),
                    $"第 {round + 1} 次打开下拉并选章后，界面、引擎和保存路径一致：{target.PackId}");
                Check(playCalls == beforePlays, "切章保持静音，不受对白核对暂停或播放暂停限制");
            }
            var savedEngine = engine;
            var savedChoice = LibraryBox.SelectedItem as PackChoice ?? throw new InvalidOperationException("切章后选中项丢失");
            var blockedTarget = LibraryBox.Items.OfType<PackChoice>().First(item => item != savedChoice);
            Check(stateSaves.Flush() == null, "正常连续切章可以保存进度");
            LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout(); await Task.Delay(80);
            using (var blocked = File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SelectChapter(blockedTarget);
                Check(ReferenceEquals(engine, savedEngine) && ReferenceEquals(LibraryBox.SelectedItem, savedChoice) &&
                    preferences.PackFile == savedChoice.File && SaveWarning.Visibility == Visibility.Visible,
                    "存档写入失败时保留原章节，并恢复下拉框选中项");
            }
            LibraryBox.IsDropDownOpen = false;
            Save(); Check(stateSaves.Flush() == null, "解除存档障碍后可以重试保存");
            await Task.Delay(80);
            int sameChapterPlays = playCalls;
            LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout(); await Task.Delay(80);
            SelectChapter(savedChoice); LibraryBox.IsDropDownOpen = false;
            Check(ReferenceEquals(engine, savedEngine) && playCalls == sameChapterPlays, "重复选择当前章保持位置且不播放");
            string expectedNavigation = System.Text.Json.JsonSerializer.Serialize(savedEngine!.ExportNavigation(), Json.Options);

            // 只损坏本测试新建的合成文件，绝不改动传入的真实配音包目录。
            string isolatedFolder = Path.Combine(Log.DataDir, "chapter-read-failure-fixture");
            Directory.CreateDirectory(isolatedFolder);
            string isolatedPackFile = Path.Combine(isolatedFolder, "pack.json");
            var isolatedPack = new Pack
            {
                Id = "chapter-read-failure-fixture", Title = "读取失败隔离测试章",
                Chapters = new() { new Chapter { Id = "fixture-chapter", Title = "隔离章节", Sections = new()
                    { new Section { Id = "fixture-section", Title = "隔离小节", StartId = "fixture-line" } } } },
                Nodes = new() { new Node { Id = "fixture-line", SectionId = "fixture-section", Kind = "line", Text = "只用于章节读取回退测试", NextId = "fixture-end" },
                    new Node { Id = "fixture-end", SectionId = "fixture-section", Kind = "end", Text = "隔离小节结束" } }
            };
            isolatedPack.Validate(); Json.Save(isolatedPackFile, isolatedPack);
            var isolatedChoice = new PackChoice(isolatedPackFile, isolatedPack.Title) { PackId = isolatedPack.Id };
            var originalSource = LibraryBox.ItemsSource;
            var isolatedChoices = LibraryBox.Items.OfType<PackChoice>().Append(isolatedChoice).ToList();
            selectingLibrary = true;
            try { LibraryBox.ItemsSource = isolatedChoices; LibraryBox.SelectedItem = savedChoice; }
            finally { selectingLibrary = false; }
            try
            {
                foreach (bool missing in new[] { false, true })
                {
                    if (missing) File.Delete(isolatedPackFile);
                    else File.WriteAllText(isolatedPackFile, "{ invalid pack json");
                    int beforeFailedLoad = playCalls;
                    LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout(); await Task.Delay(80);
                    SelectChapter(isolatedChoice); LibraryBox.IsDropDownOpen = false;
                    Check(ReferenceEquals(engine, savedEngine) && ReferenceEquals(LibraryBox.SelectedItem, savedChoice) &&
                        preferences.PackFile == savedChoice.File && playCalls == beforeFailedLoad && !selectingLibrary,
                        (missing ? "目标配音包被移走" : "目标配音包损坏") + "时，目录、引擎和保存路径保持原章节且不发声");
                    Json.Save(isolatedPackFile, isolatedPack);
                }
                int beforeRetry = playCalls;
                LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout(); await Task.Delay(80);
                SelectChapter(isolatedChoice); LibraryBox.IsDropDownOpen = false;
                Check(engine!.Pack.Id == isolatedPack.Id && ReferenceEquals(LibraryBox.SelectedItem, isolatedChoice) &&
                    preferences.PackFile == isolatedPackFile && playCalls == beforeRetry,
                    "修复目标文件后可再次选择并静音切换，没有残留选择锁");
                LibraryBox.IsDropDownOpen = true; LibraryBox.UpdateLayout(); await Task.Delay(80);
                SelectChapter(savedChoice); LibraryBox.IsDropDownOpen = false;
                Check(engine!.Pack.Id == savedEngine!.Pack.Id && ReferenceEquals(LibraryBox.SelectedItem, savedChoice) &&
                    preferences.PackFile == savedChoice.File && playCalls == beforeRetry &&
                    System.Text.Json.JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) == expectedNavigation,
                    "读取故障恢复后仍可切回原章，保持原有章节进度");
            }
            finally
            {
                LibraryBox.IsDropDownOpen = false; selectingLibrary = true;
                try { LibraryBox.ItemsSource = originalSource; LibraryBox.SelectedItem = savedChoice; }
                finally { selectingLibrary = false; }
            }
            Expand(StoryTab); await Task.Delay(60); Screenshot("chapter-switch-ui.png");
            Check(SaveWarning.Visibility != Visibility.Visible, "正常切章未触发保存失败");
            report.Add("INFO: 使用实际下拉选项的选择接口，未向游戏或用户正在使用的播放器发送输入。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        Directory.CreateDirectory(Log.DataDir);
        File.WriteAllLines(Path.Combine(Log.DataDir, "chapter-switch-ui-test.txt"), report);
        Close();
    }
}
