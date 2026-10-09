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
    async Task RunSectionPickerUiTest()
    {
        var report = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); report.Add("PASS: " + message); }
        void Capture(FrameworkElement view, string name)
        {
            view.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
        }
        async Task Browse(ComboBox model, Func<SectionPickerWindow, Task> action, bool keyboard = false)
        {
            Exception? failure = null;
            sectionPickerTest = async picker => { try { await Task.Delay(60); await action(picker); } catch (Exception ex) { failure = ex; if (picker.IsVisible) picker.Cancel(); } };
            try
            {
                var button = sectionPickerButtons[model];
                if (keyboard)
                {
                    button.Focus();
                    var preview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    button.RaiseEvent(preview);
                    Check(!preview.Handled && button.IsKeyboardFocusWithin, "小节按钮Enter不被旧下拉框键盘逻辑截走");
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                }
                else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { sectionPickerTest = null; }
            await Task.Delay(60); if (failure != null) throw failure;
        }
        async Task Choose(ComboBox model, Section section)
        {
            await Browse(model, picker =>
            {
                picker.Groups.SelectedItem = picker.Groups.Items.Cast<SectionDisplayGroup>().Single(g => g.Sections.Contains(section));
                picker.Segments.SelectedItem = picker.Segments.Items.Cast<SectionPickerChoice>().Single(c => c.Section.Id == section.Id);
                picker.Confirm(); return Task.CompletedTask;
            });
        }
        try
        {
            Check(testUi, "使用隔离测试设置，实际配音包只读");
            var choice = LibraryBox.Items.OfType<PackChoice>().Single(p => p.Title == "准星所向");
            LibraryBox.SelectedItem = choice;
            var owner = engine!; var pack = owner.Pack; string? node = owner.CurrentId; int calls = playCalls;
            string fingerprint = PlaybackEngine.NavigationFingerprint(pack);
            var original = SectionBox.SelectedItem as Section;
            Check(pack.Chapters.SelectMany(c => c.Sections).Count() == 80, "真实准星所向包含80个底层片段，未删改原包");
            Expand(StoryTab); UpdateLayout();
            Check(!SectionBox.IsVisible && sectionPickerButtons[SectionBox].IsVisible, "台词页改为小节按钮，不再暴露80项重复下拉列表");
            sectionPickerButtons[SectionBox].RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            Check(ReferenceEquals(SectionBox.SelectedItem, original) && owner.CurrentId == node && playCalls == calls, "按钮上的滚轮不更换小节、不播放");
            await Browse(SectionBox, picker =>
            {
                var groups = picker.Groups.Items.Cast<SectionDisplayGroup>().ToArray();
                Check(groups.Select(g => g.Title).SequenceEqual(new[] { "鬣狗", "扳手", "苏菲亚", "灾厄", "仇敌", "真相", "那之后的某日" }), "准星所向按Wiki目录顺序显示7个原始小节");
                Check(groups.SelectMany(g => g.Sections).Count() == 80 && groups.SelectMany(g => g.Sections).Select(s => s.Id).Distinct().Count() == 80, "80个原片段恰好保留一次，无遗漏无重复");
                picker.Groups.SelectedItem = groups.Single(g => g.Title == "鬣狗");
                var segments = picker.Segments.Items.Cast<SectionPickerChoice>().ToArray();
                Check(segments.Length == 7 && segments.Select(s => s.Label).Distinct().Count() == 7, "鬣狗的7段用不同台词预览区分");
                Check(segments.All(s => !s.Label.Contains("当前画面定位")), "片段预览不再显示重复的内部定位标题");
                Check(ReferenceEquals(SectionBox.SelectedItem, original) && owner.CurrentId == node && playCalls == calls, "浏览分组和片段均不提交位置或触发播放");
                picker.Groups.Focus(); picker.GamepadAction("confirm");
                Check(picker.IsVisible && picker.Segments.IsKeyboardFocusWithin && picker.Result == null, "手柄确认小节只聚焦片段列表");
                Capture(picker, "section-picker-hyena.png"); picker.GamepadAction("back"); return Task.CompletedTask;
            }, keyboard: true);
            Check(ReferenceEquals(SectionBox.SelectedItem, original) && owner.CurrentId == node, "取消或返回保留原来浏览和播放位置");
            var hyena = SectionDisplay.Groups(pack.Chapters.SelectMany(c => c.Sections)).Single(g => g.Title == "鬣狗");
            var seen = new HashSet<string>();
            foreach (var segment in hyena.Sections)
            {
                await Choose(SectionBox, segment);
                Check(ReferenceEquals(SectionBox.SelectedItem, segment) && rows.Where(r => r.Node.Kind == "line").All(r => r.Node.SectionId == segment.Id), "选择预览后保持原片段身份：" + hyena.Sections.ToList().IndexOf(segment));
                foreach (var row in rows.Where(r => r.Node.Kind == "line")) seen.Add(row.Node.Id);
            }
            Check(seen.Count == 45, "鬣狗7段45条台词全部仍可浏览，后续正文未被隐藏");
            Check(((TextBlock)sectionPickerButtons[SectionBox].Content).Text == "鬣狗" && HeaderChapter.Text == "鬣狗", "小节按钮与标题只显示鬣狗原名");
            Check(ReferenceEquals(engine, owner) && owner.CurrentId == node && playCalls == calls, "确认查看任一片段仍只浏览，不改变实际播放或进度");
            Expand(gameTextTab); UpdateLayout();
            Check(!gameTextSection.IsVisible && sectionPickerButtons[gameTextSection].IsVisible, "实际游戏配音页也使用相同小节入口");
            await Choose(gameTextSection, hyena.Sections[2]);
            Check(ReferenceEquals(SectionBox.SelectedItem, hyena.Sections[2]) && ReferenceEquals(gameTextSection.SelectedItem, hyena.Sections[2]), "游戏页确认后双向模型仍选中准确原片段");
            Check(rows.Any(r => r.Node.Kind == "line") && owner.CurrentId == node && playCalls == calls, "游戏页查看后段台词不发声、不推进");
            OpenListeningPack(choice.File, pack.Chapters.First().Id); Expand(listeningTab); UpdateLayout();
            var listeningOwner = listeningSession!; var currentItem = listeningOwner.Current?.Id;
            Check(!listeningSections.IsVisible && sectionPickerButtons[listeningSections].IsVisible, "听书入口同样隐藏重复小节列表");
            var listeningHyena = SectionDisplay.Groups(listeningOwner.Chapter.Sections).Single(g => g.Title == "鬣狗");
            await Browse(listeningSections, picker => { Check(picker.Groups.Items.Count == 7, "听书小节入口也只有7项"); picker.Cancel(); return Task.CompletedTask; });
            await Choose(listeningSections, listeningHyena.Sections[4]);
            Check((listeningSections.SelectedItem as ListeningEntry)?.Id == listeningHyena.Sections[4].Id && listeningLines.Items.Count > 0, "听书查看后续片段，正文仍完整可达");
            Check(ReferenceEquals(listeningOwner, listeningSession) && listeningOwner.Current?.Id == currentItem && !listeningRunning && owner.CurrentId == node && playCalls == calls, "听书浏览不更改收听位置或游戏播放");
            Check(PlaybackEngine.NavigationFingerprint(pack) == fingerprint, "所有选择操作后原导航边界和指纹保持不变");
            Expand(StoryTab); Screenshot("section-picker-story-page.png");
            var exChoice = LibraryBox.Items.OfType<PackChoice>().Single(p => p.Title == "EX03 古铭遗章");
            LibraryBox.SelectedItem = exChoice;
            var exOwner = engine!; string? exNode = exOwner.CurrentId; var exSection = SectionBox.SelectedItem;
            string[] expectedEx = { "EX03-1尘封的记忆", "EX03-2剧院危机", "EX03-3逆流而行", "EX03-4变天", "EX03-5重聚", "EX03-6长坂坡", "EX03-7断绝", "EX03-8问答", "EX03-9风暴前夕", "EX03-10九龙之志", "EX03-11传承", "EX03-12归乡", "EX03-13万世铭", "EX03-14未名战争", "EX03-15意外相遇", "EX03-16防线", "EX03-17小小的约定", "EX03-18太阿的使命", "EX03-19告别", "EX03-1沉默", "EX03-2追放", "EX03-3同归", "青衣少保", "镇垣墙" };
            await Browse(SectionBox, picker =>
            {
                var groups = picker.Groups.Items.Cast<SectionDisplayGroup>().ToArray();
                Check(groups.Select(g => g.Title).SequenceEqual(expectedEx), "EX03按Wiki及库街区普通1至19、隐藏1至3、Wiki梨园墟的目录顺序展示");
                Check(groups.SelectMany(g => g.Sections).Select(s => s.Id).OrderBy(s => s).SequenceEqual(exOwner.Pack.Chapters.SelectMany(c => c.Sections).Select(s => s.Id).OrderBy(s => s)), "EX03排序后全部原片段仍保留且没有重复");
                picker.Groups.SelectedItem = groups.First(); picker.UpdateLayout(); Capture(picker, "section-picker-ex03-ordered.png");
                picker.Groups.SelectedItem = groups[19]; picker.Groups.ScrollIntoView(groups.Last()); picker.UpdateLayout(); Capture(picker, "section-picker-ex03-hidden.png");
                picker.Cancel(); return Task.CompletedTask;
            });
            Check(ReferenceEquals(engine, exOwner) && exOwner.CurrentId == exNode && ReferenceEquals(SectionBox.SelectedItem, exSection) && playCalls == calls, "EX03查看新目录并取消不更换原进度、不播放");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { sectionPickerTest = null; if (sectionPicker?.IsVisible == true) sectionPicker.Close(); }
        Directory.CreateDirectory(Log.DataDir); File.WriteAllLines(Path.Combine(Log.DataDir, "section-picker-ui-test.txt"), report); Close();
    }
}
