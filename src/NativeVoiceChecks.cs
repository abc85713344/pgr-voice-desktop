using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    // 仅由 --test-native-voice-ui 配合 --test-state 启动：用预装原配表里《繁星谣》一句的真实身份造两句夹具（原配一句、普通一句），
    // 核开关、静音、小台词条/悬浮球样式、设置勾选框与章节选择里的按章切换；不播放真实音频、不操作游戏。
    async Task RunNativeVoiceUiTest()
    {
        var report = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
                foreach (var nested in Elements<T>(child)) yield return nested;
            }
        }
        IEnumerable<T> Logical<T>(DependencyObject parent) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                if (child is T item) yield return item;
                foreach (var nested in Logical<T>(child)) yield return nested;
            }
        }
        void SaveImage(Window window, string name)
        {
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(output);
        }
        const string packId = "voice-v2-P8R_W_a884d86780cd7660", nativeId = "bw-a7dc2e9ba0fe48a6-lfbf37f055b0f9c5f8211";
        ChapterPickerWindow? picker = null;
        try
        {
            if (!testUi) throw new InvalidOperationException("只能在隔离测试状态运行。");
            var catalog = NativeVoiceCatalog.Shared;
            Check(catalog != null && catalog.Statistics.ConfiguredEntries == 1696 && catalog.Statistics.RejectedEntries == 0, "预装原配表 1696 句全部载入，无被拒条目");
            var entry = catalog!.FindConfiguredVoice(packId, nativeId) ?? throw new InvalidOperationException("缺少《繁星谣》原配条目");
            Check(NativeVoiceCount(packId) == 3, "《繁星谣》原配 3 句");
            string root = Path.Combine(Log.DataDir, "fixtures", "native-voice"); Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "test-audio.bin"), new byte[] { 1 });
            var pack = new Pack
            {
                SchemaVersion = 3, Id = packId, Title = "原配静音检查", Root = root,
                Chapters = new() { new() { Id = "book", Title = "原配检查", Sections = new() { new() { Id = entry.SectionId, Title = "原配小节", StartId = "plain" } } } },
                Nodes = new()
                {
                    new() { Id = "plain", SectionId = entry.SectionId, PathId = entry.PathId, Speaker = entry.Speaker, Text = "没有原配的一句。", Audio = "test-audio.bin", NextId = nativeId },
                    new() { Id = nativeId, SectionId = entry.SectionId, PathId = entry.PathId, Speaker = entry.Speaker, Text = entry.Text, RawText = entry.RawText, Audio = "test-audio.bin" }
                }
            };
            pack.Validate(); string file = Path.Combine(root, "pack.json"); Json.Save(file, pack); LoadPack(file);
            preferences.NativeVoiceMute = false; preferences.NativeVoiceDubPacks.Clear();
            engine!.Commit(nativeId);
            Check(!NativeVoiceLine(engine.Current), "总开关关闭时原配句照常播配音包");
            preferences.NativeVoiceMute = true;
            Check(NativeVoiceLine(engine.Current), "总开关打开后原配句判为游戏原配");
            Check(!NativeVoiceLine(engine.Pack.ById["plain"]), "不在原配表里的句子照常播配音包");
            var tampered = engine.Pack.ById[nativeId]; string text = tampered.Text; tampered.Text += "（改）";
            Check(!NativeVoiceLine(tampered), "正文与原配表不一致时不静音");
            tampered.Text = text;

            preferences.Volume = 80; preferences.SpeakerVolumes.Clear();
            playingNative = true; ApplySpeakerVolumes();
            Check(audio.Volume == 0, "原配句播放中调音量或角色音量，配音包仍保持静音");
            playingNative = false; ApplySpeakerVolumes();
            Check(Math.Abs(audio.Volume - .8f) < .0001, "普通句恢复原音量");

            UpdateState(); RefreshCompactFollowControls();
            Check(StripState.Text.Contains(NativeVoiceStatus) && StripState.Text.StartsWith(NativeVoiceWave), "小台词条状态显示声波与“正在听游戏原配”");
            Check(ReferenceEquals(StripState.Foreground, Theme.Brush("TealText")) && ReferenceEquals(StripView.BorderBrush, Theme.Brush("Teal")), "小台词条换青色");
            Check(BallArt.Glyph == "原" && ReferenceEquals(BallView.BorderBrush, Theme.Brush("Teal")), "悬浮球显示“原”与青色外圈");
            string mode = preferences.CompactMode; preferences.CompactMode = "strip"; Collapse(false); await Task.Delay(200);
            RefreshCompactFollowControls(); SaveImage(this, "小台词条-原配.png");
            preferences.CompactMode = "ball"; Collapse(false); await Task.Delay(200); UpdateState(); SaveImage(this, "悬浮球-原配.png");
            preferences.CompactMode = mode;

            SetNativeVoiceForPack(packId, false);
            Check(!NativeVoiceLine(engine.Current) && preferences.NativeVoiceDubPacks.Contains(packId), "本章选“用配音包”后原配句照常播配音包");
            RefreshCompactFollowControls();
            Check(!StripState.Text.Contains(NativeVoiceStatus) && ReferenceEquals(StripState.Foreground, Theme.Brush("MutedText")), "本章用配音包时小台词条恢复原样");
            Check(BallArt.Glyph != "原" || engine.Mode == RunMode.Original, "本章用配音包时悬浮球不显示原配");
            SetNativeVoiceForPack(packId, true);
            Check(NativeVoiceLine(engine.Current), "本章改回“用原配”");

            engine.Commit("plain"); UpdateState(); RefreshCompactFollowControls();
            Check(!StripState.Text.Contains(NativeVoiceStatus) && BallArt.Glyph != "原", "切到普通句后台词条与悬浮球恢复原样");

            UpdateLayout();
            var box = Logical<CheckBox>(this).Single(b => b.Content?.ToString() == "游戏有原配时静音配音包");
            var tip = Logical<TextBlock>(this).Single(t => t.Text.StartsWith("请在游戏设置里打开角色语音")).Parent as Border;
            Check(tip != null, "设置页有打开后提醒");
            box.IsChecked = true; box.IsChecked = false;
            Check(!preferences.NativeVoiceMute && tip!.Visibility == Visibility.Collapsed, "取消勾选关闭原配静音并收起提醒");
            box.IsChecked = true;
            Check(preferences.NativeVoiceMute && tip!.Visibility == Visibility.Visible, "重新勾选打开原配静音并显示提醒");

            var choices = new List<PackChoice> { new(file, "原配静音检查") { PackId = packId }, new(Path.Combine(root, "other.json"), "无原配章节") { PackId = "native-voice-none" } };
            picker = new ChapterPickerWindow(choices, choices[0], false) { Owner = this };
            picker.NativeVoiceCount = NativeVoiceCount; picker.NativeVoiceToggles = preferences.NativeVoiceMute;
            picker.NativeVoiceUsesNative = id => preferences.NativeVoiceDubPacks?.Contains(id) != true;
            picker.SetNativeVoiceUse = SetNativeVoiceForPack;
            picker.Show(); await Task.Delay(300); picker.UpdateLayout();
            var rows = Elements<ChapterRowView>(picker).ToList();
            var toggles = Elements<Button>(picker).Where(b => b.Content is "用原配" or "用配音包").ToList();
            Check(rows.Count >= 1 && toggles.Count == 1 && (string)toggles[0].Content == "用原配", $"章节选择在有原配的章节显示“用原配”切换（行 {rows.Count}，切换键 {toggles.Count}）");
            Check(Elements<TextBlock>(picker).Count(t => t.Text == "原配 3") == 1, "有原配的章节显示“原配 3”");
            SaveImage(picker, "章节选择-原配.png");
            toggles[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); picker.UpdateLayout();
            var after = Elements<Button>(picker).Single(b => b.Content is "用原配" or "用配音包");
            Check((string)after.Content == "用配音包" && preferences.NativeVoiceDubPacks.Contains(packId) && picker.IsVisible, "点切换改为“用配音包”，不打开章节也不关闭窗口");
            after.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); picker.UpdateLayout();
            Check(Elements<Button>(picker).Single(b => b.Content is "用原配" or "用配音包").Content as string == "用原配" && !preferences.NativeVoiceDubPacks.Contains(packId), "再点改回“用原配”");
            picker.Close(); picker = null;
            report.Add("说明：以上为本地 WPF 接线和独立状态验证，未播放真实音频，未在真实游戏中跟随实测。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            picker?.Close();
            preferences.NativeVoiceMute = false; preferences.NativeVoiceDubPacks.Clear(); playingNative = false;
        }
        File.WriteAllLines(Path.Combine(Log.DataDir, "native-voice-ui-test.txt"), report); Close();
    }
}
