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

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunChapterPickerUiTest()
    {
        var report = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); report.Add("PASS: " + message); }
        T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T value) return value;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
            return null;
        }
        void Wheel(UIElement target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent });
        void Capture(FrameworkElement view, string name)
        {
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
        }
        async Task Browse(ComboBox model, Func<ChapterPickerWindow, Task> action, bool categoryButton = true)
        {
            Exception? failure = null;
            chapterPickerTest = async picker =>
            {
                try { await Task.Delay(100); await action(picker); }
                catch (Exception ex) { failure = ex; if (picker.IsVisible) picker.Cancel(); }
            };
            try { (categoryButton ? chapterPickerButtons[model].Category : chapterPickerButtons[model].Chapter).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { chapterPickerTest = null; }
            await Task.Delay(100);
            if (failure != null) throw failure;
        }
        async Task Choose(ComboBox model, PackChoice target)
        {
            await Browse(model, picker =>
            {
                picker.Categories.SelectedItem = target.Category;
                picker.Chapters.SelectedItem = picker.Chapters.Items.OfType<PackChoice>().Single(p => p.File == target.File);
                picker.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
            });
        }
        try
        {
            Check(testUi && engine != null && LibraryBox.Items.Count > 2, "已载入隔离测试，使用实际配音目录只读验证");
            var originalChoices = LibraryBox.Items.OfType<PackChoice>().ToList();
            var original = originalChoices.First(p => p.File == preferences.PackFile);
            Expand(gameTextTab); UpdateLayout();
            Check(!gameTextLibrary.IsVisible && chapterPickerButtons[gameTextLibrary].Category.IsVisible && chapterPickerButtons[gameTextLibrary].Chapter.IsVisible, "默认游戏配音页使用可见分类/章节按钮，不再使用长下拉框");
            var oldEngine = engine; string oldNode = engine!.CurrentId ?? "", oldPath = preferences.PackFile; int calls = playCalls;
            chapterPickerButtons[gameTextLibrary].Chapter.Focus(); Wheel(chapterPickerButtons[gameTextLibrary].Chapter, -120); Wheel(chapterPickerButtons[gameTextLibrary].Chapter, 120);
            Check(ReferenceEquals(engine, oldEngine) && preferences.PackFile == oldPath && playCalls == calls, "关闭选章窗口时在按钮上滚轮不切章、不发声");
            await Browse(gameTextLibrary, async picker =>
            {
                var expected = ChapterCatalog.Categories.Concat(originalChoices.Select(p => p.Category)).Distinct().OrderBy(ChapterCatalog.CategoryOrder);
                Check(picker.Categories.Items.Cast<string>().SequenceEqual(expected), "完整显示全部13类，主线在最前，包含无已导入章节的分类");
                foreach (var category in ChapterCatalog.Categories.Where(c => !originalChoices.Any(p => p.Category == c)))
                {
                    picker.Categories.SelectedItem = category; picker.UpdateLayout();
                    Check(picker.Chapters.Items.Count == 0 && picker.EmptyMessage.IsVisible && !picker.OpenButton.IsEnabled, category + "空类显示暂无章节，不允许打开");
                    picker.Confirm();
                    Check(picker.IsVisible && picker.Result == null && ReferenceEquals(engine, oldEngine) && preferences.PackFile == oldPath && playCalls == calls, category + "浏览和确认空类不改变播放或进度");
                }
                picker.Categories.SelectedItem = "本我回廊"; picker.UpdateLayout(); Capture(picker, "chapter-picker-empty-category.png");
                picker.Categories.SelectedItem = "主线"; picker.Chapters.UpdateLayout();
                var scroll = Find<ScrollViewer>(picker.Chapters) ?? throw new Exception("找不到章节滚动区域");
                scroll.ScrollToTop(); await Task.Delay(70); double before = scroll.VerticalOffset;
                Wheel(scroll, -120); await Task.Delay(70);
                Check(scroll.VerticalOffset > before && scroll.VerticalOffset - before < scroll.ViewportHeight, "一次真实WPF滚轮事件只滚动一小段，没有整组跳转");
                for (int i = 0; i < 80; i++) Wheel(scroll, -120);
                await Task.Delay(70);
                Check((string?)picker.Categories.SelectedItem == "主线" && picker.Chapters.Items.OfType<PackChoice>().All(p => p.Category == "主线"), "主线滚到底仍只显示主线，不会进入外篇");
                Check(ReferenceEquals(engine, oldEngine) && (engine!.CurrentId ?? "") == oldNode && preferences.PackFile == oldPath && playCalls == calls, "滚动和浏览不改变实际引擎、台词、存档路径或播放次数");
                picker.Categories.SelectedItem = "外篇剧情"; picker.UpdateLayout();
                Check(picker.Chapters.Items.Count > 0 && picker.Chapters.Items.OfType<PackChoice>().All(p => p.Category == "外篇剧情"), "点外篇分类只筛出外篇章节");
                Check(ReferenceEquals(engine, oldEngine) && preferences.PackFile == oldPath, "点分类不自动载入该类第一章");
                picker.Categories.Focus(); picker.GamepadAction("confirm");
                Check(picker.Chapters.IsKeyboardFocusWithin && picker.IsVisible && picker.Result == null, "手柄确认分类只进入章节列表，不提交选章");
                Capture(picker, "chapter-picker-extras.png"); picker.GamepadAction("back");
            });
            Check(ReferenceEquals(engine, oldEngine) && preferences.PackFile == oldPath && playCalls == calls, "取消或手柄返回后保留原章节");
            var extra = originalChoices.First(p => p.Category == "外篇剧情");
            await Choose(gameTextLibrary, extra);
            Check(engine!.Pack.Id == extra.PackId && preferences.PackFile == extra.File && ReferenceEquals(LibraryBox.SelectedItem, extra) && ReferenceEquals(gameTextLibrary.SelectedItem, extra), "默认游戏页确认打开后才切章，按钮模型和引擎一致");
            Check(playCalls == calls, "明确切章仍保持静音");
            Expand(StoryTab); UpdateLayout();
            Check(!LibraryBox.IsVisible && chapterPickerButtons[LibraryBox].Category.IsVisible, "台词页也使用相同分类按钮");
            FocusCatalog(); await Task.Delay(100);
            Check(chapterPickerButtons[LibraryBox].Chapter.IsKeyboardFocusWithin, "F4聚焦可见章节按钮");
            await Choose(LibraryBox, original);
            Check(engine!.Pack.Id == original.PackId && (engine.CurrentId ?? "") == oldNode && playCalls == calls, "台词页选回主线保留原有位置且不播放");
            Save(); Check(stateSaves.Flush() == null, "切换分类和章节后正常保存进度");
            using (var blocked = File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Choose(gameTextLibrary, extra);
                Check(engine.Pack.Id == original.PackId && preferences.PackFile == original.File && ReferenceEquals(LibraryBox.SelectedItem, original), "保存失败时确认切章仍回退原章，按钮不显示错误目标");
            }
            Save(); Check(stateSaves.Flush() == null, "解除存档占用后可正常重试");
            string brokenFile = Path.Combine(Log.DataDir, "broken-picker-pack.json"); File.WriteAllText(brokenFile, "{ invalid json");
            var broken = new PackChoice(brokenFile, "隔离坏包") { PackId = "picker-broken" };
            var originalSource = LibraryBox.ItemsSource;
            string sourceFolder = chapterLibraryFolder; chapterLibraryFolder = ""; // 仅此手工坏包夹具绕过真实目录刷新。
            selectingLibrary = true; LibraryBox.ItemsSource = GroupedChapters(originalChoices.Append(broken)); LibraryBox.SelectedItem = original; selectingLibrary = false;
            try
            {
                await Choose(gameTextLibrary, broken);
                Check(engine.Pack.Id == original.PackId && preferences.PackFile == original.File && ReferenceEquals(LibraryBox.SelectedItem, original), "损坏配音包不会改掉当前章节");
                File.Delete(brokenFile); await Choose(LibraryBox, broken);
                Check(engine.Pack.Id == original.PackId && preferences.PackFile == original.File && playCalls == calls, "目标文件被移走后保持原章且不发声");
            }
            finally { chapterLibraryFolder = sourceFolder; selectingLibrary = true; LibraryBox.ItemsSource = originalSource; LibraryBox.SelectedItem = original; selectingLibrary = false; }
            Expand(listeningTab); RefreshListeningPacks(); UpdateLayout();
            Check(!listeningPacks.IsVisible && chapterPickerButtons[listeningPacks].Category.IsVisible, "听书入口也使用分类/章节按钮");
            var listeningOwner = listeningSession;
            await Browse(listeningPacks, picker => { picker.Categories.SelectedItem = "间章剧情"; picker.Cancel(); return Task.CompletedTask; });
            Check(ReferenceEquals(listeningSession, listeningOwner) && engine.Pack.Id == original.PackId, "听书浏览分类并取消，不影响收听或游戏章节");
            await Choose(listeningPacks, extra);
            Check(listeningSession?.Pack.Id == extra.PackId && !listeningRunning && engine.Pack.Id == original.PackId, "听书确认后静音打开所选章，游戏章节保持独立");
            Expand(gameTextTab); UpdateLayout(); Screenshot("chapter-picker-default-page.png");
            Check(originalChoices.Count == LibraryBox.Items.Count, "三个入口共用完整目录，未过滤或丢失其他分类");

            // 新增包只写隔离测试目录，正式库始终只读。
            string refreshRoot = Path.Combine(Log.DataDir, "fixtures", "chapter-refresh-" + Guid.NewGuid().ToString("N"));
            string WriteRefreshPack(string name, string id, string title)
            {
                string file = Path.Combine(refreshRoot, name, "pack.json");
                Json.Save(file, new Pack
                {
                    Id = id, Title = title,
                    Chapters = new() { new Chapter { Id = "chapter", Title = title, Sections = new() { new Section { Id = "section", Title = "正文", StartId = "line" } } } },
                    Nodes = new() { new Node { Id = "line", SectionId = "section", Speaker = "旁白", Text = "隔离刷新验证正文。" } }
                });
                return file;
            }
            string baseFile = WriteRefreshPack("原有章节", "chapter-refresh-base", "原有刷新验证章节");
            SetLibrary(refreshRoot); LoadPack(baseFile);
            bool timerWasEnabled = gameTextTimer.IsEnabled;
            gameTextTimer.Stop();
            try
            {
                int index = 0;
                foreach (var entry in new[] { (Model: gameTextLibrary, Tab: gameTextTab, Name: "游戏配音"), (Model: LibraryBox, Tab: StoryTab, Name: "台词"), (Model: listeningPacks, Tab: listeningTab, Name: "听书") })
                {
                    Expand(entry.Tab); UpdateLayout();
                    if (ReferenceEquals(entry.Model, listeningPacks)) RefreshListeningPacks();
                    string lateFile = WriteRefreshPack("稍后加入" + ++index, "chapter-refresh-late-" + index, "空滞轮旋之梦");
                    Check(!LibraryBox.Items.OfType<PackChoice>().Any(p => p.File == lateFile), entry.Name + "夹具确实在旧列表建立后才加入新包");
                    var refreshOwner = engine; var refreshNode = engine!.CurrentId; var refreshMode = engine.Mode;
                    var refreshSelected = LibraryBox.SelectedItem; var refreshSection = SectionBox.SelectedItem;
                    string refreshFile = preferences.PackFile; int refreshCalls = playCalls;
                    var listeningBefore = listeningSession; string listeningFileBefore = listeningFile;
                    long listeningOffsetBefore = listeningOffset; bool listeningRunningBefore = listeningRunning;
                    bool automaticBefore = automaticRunning, armedBefore = textArmed;
                    var textOwnerBefore = textOwner; string textSectionBefore = textSection;
                    textArmed = true; textOwner = engine; textSection = "section";
                    try
                    {
                        await Browse(entry.Model, picker =>
                        {
                            picker.Categories.SelectedItem = "联动";
                            Check(picker.Chapters.Items.OfType<PackChoice>().Any(p => p.File == lateFile && p.Title == "空滞轮旋之梦"), entry.Name + "打开选择器立即刷新，新增章节可在联动找到");
                            picker.Cancel(); return Task.CompletedTask;
                        });
                        Check(ReferenceEquals(engine, refreshOwner) && engine.CurrentId == refreshNode && engine.Mode == refreshMode
                            && preferences.PackFile == refreshFile && ReferenceEquals(LibraryBox.SelectedItem, refreshSelected)
                            && ReferenceEquals(SectionBox.SelectedItem, refreshSection) && playCalls == refreshCalls,
                            entry.Name + "刷新并取消保持原引擎、包、节点、小节、选中项和播放次数");
                        Check(textArmed && ReferenceEquals(textOwner, refreshOwner) && textSection == "section" && automaticRunning == automaticBefore
                            && ReferenceEquals(listeningSession, listeningBefore) && listeningFile == listeningFileBefore
                            && listeningOffset == listeningOffsetBefore && listeningRunning == listeningRunningBefore,
                            entry.Name + "刷新并取消不暂停文字监听、自动播放或改动听书会话位置");
                    }
                    finally { textArmed = armedBefore; textOwner = textOwnerBefore; textSection = textSectionBefore; }
                }
            }
            finally { if (timerWasEnabled) gameTextTimer.Start(); }

        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { chapterPickerTest = null; if (chapterPicker?.IsVisible == true) chapterPicker.Close(); }
        Directory.CreateDirectory(Log.DataDir);
        File.WriteAllLines(Path.Combine(Log.DataDir, "chapter-picker-ui-test.txt"), report);
        if (Environment.GetCommandLineArgs().Contains("--test-chapter-switch-ui")) File.WriteAllLines(Path.Combine(Log.DataDir, "chapter-switch-ui-test.txt"), report);
        Close();
    }
}
