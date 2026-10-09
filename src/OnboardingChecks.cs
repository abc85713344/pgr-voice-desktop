using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunOnboardingUiTest()
    {
        var results = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); results.Add("PASS: " + label); }
        IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target) yield return target;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        void Click(Window dialog, string text)
        {
            dialog.UpdateLayout(); var button = Descendants<Button>(dialog).Single(b => b.Content?.ToString() == text);
            Check(button.IsEnabled, text + "入口可操作"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Image(Window window, string name)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("仅可在隔离状态检查引导");
            var fresh = Path.Combine(Log.DataDir, "first-run"); Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(fresh, "player.log"), "独立测试启动日志");
            Check(OnboardingStore.ShouldAutoOpen(fresh), "只有启动日志的新安装仍显示引导");
            File.WriteAllText(Path.Combine(fresh, "preferences.json.bak"), "{}");
            Check(!OnboardingStore.ShouldAutoOpen(fresh), "仅有旧设置备份的升级用户不自动弹出");
            var progressOnly = Path.Combine(Log.DataDir, "progress-only"); Directory.CreateDirectory(Path.Combine(progressOnly, "progress"));
            File.WriteAllText(Path.Combine(progressOnly, "progress", "saved.json"), "{}");
            Check(!OnboardingStore.ShouldAutoOpen(progressOnly), "只有旧进度的用户也不被当成全新安装");
            string fixture = Path.Combine(Log.DataDir, "fixtures", "guide"); Directory.CreateDirectory(fixture);
            File.WriteAllBytes(Path.Combine(fixture, "trial.bin"), new byte[] { 1 });
            var guide = new Pack
            {
                Id = "onboarding-guide", Title = "首次试听测试章", Root = fixture,
                Chapters = new() { new() { Id = "chapter", Title = "引导测试", Sections = new() { new() { Id = "section", Title = "测试小节", StartId = "guide-a" } } } },
                Nodes = new() {
                    new() { Id = "guide-a", SectionId = "section", Speaker = "测试队员", Text = "这是一句用于界面验收的试听台词。", Audio = "trial.bin", NextId = "guide-b" },
                    new() { Id = "guide-b", SectionId = "section", Speaker = "测试队员", Text = "这一句故意缺少音频。", Audio = "missing.wav" }
                }
            };
            guide.Validate(); string guideFile = Path.Combine(fixture, "pack.json"); Json.Save(guideFile, guide);
            string oldFolder = Path.Combine(Log.DataDir, "fixtures", "old"); Directory.CreateDirectory(oldFolder);
            var original = new Pack { Id = "onboarding-original", Title = "已有进度测试章", Root = oldFolder,
                Chapters = new() { new() { Id = "old-chapter", Sections = new() { new() { Id = "old-section", Title = "原小节", StartId = "old-a" } } } },
                Nodes = new() { new() { Id = "old-a", SectionId = "old-section", Text = "保留原有游戏位置。" } } };
            original.Validate(); string originalFile = Path.Combine(oldFolder, "pack.json"); Json.Save(originalFile, original);
            LoadPack(originalFile); engine!.Commit("old-a");
            string originalId = engine.Pack.Id, originalNode = engine.CurrentId!; int originalCalls = playCalls;
            listeningTestAudio = true;
            var store = new OnboardingStore(Log.DataDir);
            Exception? error = null;
            int attempts = 0;
            onboardingPreviewTest = (_, _) => ++attempts == 1 ? Task.FromException(new IOException("模拟声音设备不可用")) : Task.CompletedTask;
            onboardingFolderTest = _ => null;
            experienceDialogTest = async dialog =>
            {
                var wizard = (OnboardingWindow)dialog;
                try
                {
                    Check(experienceDialog == wizard && engine.Mode == RunMode.Paused, "打开引导占用独立输入窗口并暂停游戏会话");
                    Image(wizard, "引导-用途.png"); wizard.ChoosePurpose("listening");
                    wizard.PickDirectory(); Check(wizard.SelectedPackFile == "" && wizard.Step == 1, "取消目录选择留在原步骤且不误记成功");
                    onboardingFolderTest = _ => fixture; wizard.PickDirectory();
                    Check(wizard.SelectedPackFile == guideFile && engine.Pack.Id == originalId && engine.CurrentId == originalNode, "选择引导章节不提交正式游戏位置");
                    Image(wizard, "引导-章节.png"); Click(wizard, "选好了，去试听");
                    Check(!wizard.Heard.IsEnabled, "还没试听不能确认听到");
                    await wizard.StartTrial(); Check(!wizard.Heard.IsEnabled && wizard.Notice.Contains("模拟声音设备不可用"), "试听失败留在当前步骤并允许重试");
                    await wizard.StartTrial(); Check(wizard.Heard.IsEnabled && attempts == 2, "有效试听完成后才允许用户确认");
                    Image(wizard, "引导-试听.png");
                    wizard.Lines.SelectedIndex = 1; Check(!wizard.Heard.IsEnabled, "切换台词取消上一句试听确认资格");
                    await wizard.StartTrial(); Check(!wizard.Heard.IsEnabled && wizard.Notice.Contains("没有可用音频"), "缺音频不会当成试听成功");
                    wizard.Lines.SelectedIndex = 0; wizard.Volume.Value = 0; await wizard.StartTrial();
                    Check(!wizard.Heard.IsEnabled && attempts == 2, "静音时不发起试听或允许确认");
                    wizard.Volume.Value = 80;
                    Click(wizard, "上一步"); Click(wizard, "选好了，去试听");
                    Check(wizard.Step == 2 && !wizard.Heard.IsEnabled, "返回后可重建试听界面且需要重新试听");
                    Check(engine.Pack.Id == originalId && engine.CurrentId == originalNode && playCalls == originalCalls, "全程选章和试听没有改变游戏位置或触发主播放器");
                    // 用旧票据模拟关闭后的迟到完成，验证没有新的导航或完成状态。
                    long ticket = wizard.PreviewGeneration; wizard.Close(); wizard.CompletePreview(ticket);
                    Check(!wizard.Heard.IsEnabled && !wizard.OpenSelectedChapter, "关闭后的迟到回调不能标完成或打开章节");
                }
                catch (Exception ex) { error = ex; wizard.Close(); }
            };
            OpenOnboarding(); if (error != null) throw error;
            Check(store.Load().Status == "dismissed" && !OnboardingStore.ShouldAutoOpen(Log.DataDir), "中途关闭记为未完成，不会下次强制弹出");
            onboardingPreviewTest = (_, _) => Task.CompletedTask;
            experienceDialogTest = async dialog =>
            {
                var wizard = (OnboardingWindow)dialog;
                try
                {
                    Check(wizard.Step == 2 && wizard.SelectedPackFile == guideFile, "主动重开恢复章节与未完成步骤");
                    await wizard.StartTrial(); wizard.ConfirmHeard();
                    Check(wizard.Step == 3 && store.Load().Status == "in-progress", "用户确认听到后进入完成页，尚未提交章节");
                    File.Move(Path.Combine(fixture, "trial.bin"), Path.Combine(fixture, "trial-held.bin"));
                    wizard.Finish(); Check(wizard.IsVisible && wizard.Step == 2 && store.Load().Status == "in-progress", "确认后音频丢失不能误记完成，会回试听步骤");
                    File.Move(Path.Combine(fixture, "trial-held.bin"), Path.Combine(fixture, "trial.bin"));
                    await wizard.StartTrial(); wizard.ConfirmHeard();
                    wizard.Finish();
                }
                catch (Exception ex) { error = ex; wizard.Close(); }
            };
            OpenOnboarding(); if (error != null) throw error;
            Check(store.Load().Status == "completed" && listeningSession?.Pack.Id == guide.Id, "明确完成才打开选中的听书章节");
            Check(engine.Pack.Id == originalId && engine.CurrentId == originalNode, "听书完成引导仍保留游戏位置");
            experienceDialogTest = async dialog =>
            {
                var wizard = (OnboardingWindow)dialog;
                try
                {
                    Check(wizard.Step == 0, "已完成用户主动重开可以重新选用途");
                    wizard.ChoosePurpose("game"); wizard.SelectPack(guideFile); Click(wizard, "选好了，去试听");
                    await wizard.StartTrial(); wizard.ConfirmHeard(); Image(wizard, "引导-游戏下一步.png");
                    Check(wizard.Purpose == "game" && wizard.Step == 3 && !automaticRunning && !TextFollowing, "游戏用途展示下一步但不自动连接或开始跟随");
                    wizard.Skip();
                }
                catch (Exception ex) { error = ex; wizard.Close(); }
            };
            OpenOnboarding(); if (error != null) throw error;
            Check(store.Load().Status == "skipped" && engine.Pack.Id == originalId, "跳过明确保存状态且不提交所选游戏章");
            results.Add("说明：试听采用受控音频替身验证界面与票据，不代表真人听感或真实游戏联调。");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally { experienceDialogTest = null; onboardingFolderTest = null; onboardingPreviewTest = null; experienceDialog?.Close(); listeningTestAudio = false; }
        File.WriteAllLines(Path.Combine(Log.DataDir, "onboarding-ui-test.txt"), results); Close();
    }
}
