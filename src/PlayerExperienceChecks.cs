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
    // 仅由 --test-player-experience-ui 配合 --test-state 启动；音频采用独立假播放，不操作游戏。
    async Task RunPlayerExperienceUiTest()
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
        void Invoke(DependencyObject parent, string label)
        {
            UpdateLayout();
            var button = Elements<Button>(parent).Single(b => b.Content?.ToString() == label);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void SaveDialogImage(Window dialog, string name)
        {
            dialog.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(output);
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("新体验检查仅限隔离测试。");
            listeningTestAudio = true;
            string root = Path.Combine(Log.DataDir, "fixtures", "player-experience"); Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "test-audio.bin"), new byte[] { 1 });
            var pack = new Pack
            {
                Id = "player-experience-check", Title = "体验功能检查", Root = root,
                Chapters = new() { new() { Id = "book", Title = "测试大章", Sections = new() { new() { Id = "section", Title = "测试小节", StartId = "a" } } } },
                Nodes = new()
                {
                    new() { Id = "a", SectionId = "section", Speaker = "露西亚", Text = "固定的第一句。", Audio = "test-audio.bin", NextId = "b" },
                    new() { Id = "b", SectionId = "section", Speaker = "丽芙", Text = "后来的第二句。", Audio = "test-audio.bin" }
                }
            };
            pack.Validate(); string file = Path.Combine(root, "pack.json"); Json.Save(file, pack); LoadPack(file);
            engine!.Commit("a"); var frozen = CaptureGameFeedback(false)!; engine.Commit("b");
            Check(frozen.NodeId == "a" && frozen.Text == "固定的第一句。", "游戏反馈冻结捕获时的台词，不随播放位置移动");
            OpenHistory(); historyList.SelectedItem = historyList.Items.OfType<HistoryItem>().Single(i => i.Kind == "line" && engine.History[i.Index].NodeId == "a");
            string? gameNode = engine.CurrentId; int gameCalls = playCalls;
            var historySnapshot = CaptureGameFeedback(true)!;
            Check(historySnapshot.NodeId == "a" && engine.CurrentId == gameNode && playCalls == gameCalls, "历史选中句反馈只取选中记录，不提交位置或播放");
            Exception? dialogError = null;
            experienceDialogTest = dialog =>
            {
                try
                {
                    var feedback = (LineFeedbackWindow)dialog;
                    feedback.Category.SelectedItem = "没声音"; feedback.Comment.Text = "这句没有响。";
                    Check(feedback.Preview.Text.Contains("问题：没声音") && feedback.Preview.Text.Contains("这句没有响。") && feedback.Preview.Text.Contains("台词编号：a"), "历史反馈按钮打开可编辑类别、补充说明与准确预览");
                    Check(engine.CurrentId == gameNode && playCalls == gameCalls, "填写、预览反馈不会恢复历史位置或发声");
                    SaveDialogImage(feedback, "台词反馈预览.png");
                }
                catch (Exception ex) { dialogError = ex; }
                finally { dialog.Close(); }
            };
            Invoke((DependencyObject)historyTab.Content, "反馈选中句");
            if (dialogError != null) throw dialogError;
            experienceDialogTest = null;

            // 用真实的文字跟随待点击对象验证“打开反馈即暂停”，环境及点击均是隔离替身。
            gameTextTimer.Stop(); engine.Commit("a"); textOwner = engine; textSection = "section"; textArmed = true;
            var feedbackProbe = textProbes[0]; feedbackProbe.Active = true;
            feedbackProbe.Observed = engine.Current!.Text; feedbackProbe.ObservedSpeaker = engine.Current.Speaker;
            textAutoBox.IsChecked = true;
            long feedbackClock = 10000; int feedbackClicks = 0;
            textAutoEnvironmentTest = () => true; textAutoForegroundTest = () => true; textAutoClockTest = () => feedbackClock;
            textAutoSampleTest = () => new(true, feedbackProbe.Observed, true, Active: true, Speaker: feedbackProbe.ObservedSpeaker);
            textAutoClickTest = () => { feedbackClicks++; return true; };
            CaptureTextAutoLine(feedbackProbe, engine, feedbackProbe.Observed, feedbackProbe.ObservedSpeaker);
            long pendingTicket = audioRequest; OnTextAudioCompleted(pendingTicket);
            Check(textArmed && textAutoLine?.CompletedAt != null, "反馈测试建立正在跟随且已播完等待自动推进的真实状态");
            gameNode = engine.CurrentId; gameCalls = playCalls;
            experienceDialogTest = dialog =>
            {
                try
                {
                    Check(!textArmed && textAutoLine == null && engine.Mode == RunMode.Paused, "打开游戏反馈暂停文字跟随并取消待自动推进");
                    feedbackClock += 60000; OnTextAudioCompleted(pendingTicket); TickTextAutoAdvance();
                    Check(feedbackClicks == 0 && engine.CurrentId == gameNode && playCalls == gameCalls, "填写反馈期间迟到完成回调和到期检查不会点击或换句");
                }
                catch (Exception ex) { dialogError = ex; }
                finally { dialog.Close(); }
            };
            Expand(gameTextTab); Invoke((DependencyObject)memoryFollowPanel, "反馈当前句");
            if (dialogError != null) throw dialogError;
            experienceDialogTest = null; feedbackClock += 60000; TickTextAutoAdvance();
            Check(!textArmed && textAutoLine == null && feedbackClicks == 0 && engine.Mode == RunMode.Paused, "关闭游戏反馈不会自动恢复跟随或补执行点击");
            textAutoEnvironmentTest = null; textAutoForegroundTest = null; textAutoClockTest = null;
            textAutoSampleTest = null; textAutoClickTest = null; textAutoBox.IsChecked = false;
            engine.Restore("b"); gameNode = engine.CurrentId; gameCalls = playCalls;

            OpenListeningPack(file, "book");
            Check(preferences.SmartListeningResume && smartListeningResume.IsChecked == true, "听书智能续听默认开启且设置已接通");
            Expand(listeningTab); StartListening(); listeningOffset = 1500;
            long activeListeningTicket = listeningTicket;
            Check(listeningRunning && listeningSession!.Current?.NodeId == "a", "听书反馈测试从实际正在播放A句的状态开始");
            experienceDialogTest = dialog =>
            {
                try
                {
                    Check(!listeningRunning && listeningOffset == 1500 && listeningPausedUtc.HasValue, "打开听书反馈立即暂停并保存本句播放位置与暂停时刻");
                    OnListeningCompleted(activeListeningTicket);
                    Check(listeningSession!.Current?.NodeId == "a" && listeningOffset == 1500 && !listeningRunning,
                        "反馈窗口内旧自然完成回调不能推进A句或播放B句");
                    Check(((LineFeedbackWindow)dialog).Preview.Text.Contains("台词编号：a"), "听书反馈仍固定为暂停前捕获的A句");
                }
                catch (Exception ex) { dialogError = ex; }
                finally { dialog.Close(); }
            };
            listeningOptions.IsExpanded = true; UpdateLayout();
            Check(listeningRunning && listeningTicket == activeListeningTicket,
                "展开收听设置仅显示操作入口，不暂停或推进正在收听的句子");
            Invoke((DependencyObject)listeningTab.Content, "反馈当前句");
            if (dialogError != null) throw dialogError;
            experienceDialogTest = null;
            OnListeningCompleted(activeListeningTicket);
            Check(!listeningRunning && listeningSession!.Current?.NodeId == "a" && listeningOffset == 1500, "关闭听书反馈后仍停在A句，须主动续听");
            var oldPause = DateTimeOffset.UtcNow.AddMinutes(-6); listeningPausedUtc = oldPause; SaveListeningProgress();
            PauseListening(); PauseListening();
            Check(listeningPausedUtc == oldPause && ListeningProgress!.Resume!.PausedUtc == oldPause, "重复暂停与保存保留首次暂停时刻");
            OpenListeningPack(file, "book");
            Check(listeningPausedUtc == oldPause && listeningOffset == 1500, "重开听书章节保留原暂停时间及句内断点");
            StartListening();
            Check(listeningOffset == 0 && listeningSession!.Current!.NodeId == "a" && listeningMessage.Contains("本句开头"), "离开超过5分钟从同一句开头续听并显示说明");
            listeningOffset = 1800; PauseListening(); StartListening();
            Check(listeningOffset == 1800, "短暂停留按原毫秒位置续听");
            PauseListening(); listeningPausedUtc = oldPause; smartListeningResume.IsChecked = false; StartListening();
            Check(!preferences.SmartListeningResume && listeningOffset == 1800, "关闭智能续听后长暂停也保留句内断点");
            PauseListening(); smartListeningResume.IsChecked = true;
            listeningBookmarkName.Text = "准确书签"; AddListeningBookmark();
            listeningPausedUtc = oldPause; RestoreListeningBookmark(); SaveListeningProgress(); OpenListeningPack(file, "book");
            listeningPausedUtc = oldPause; StartListening();
            Check(listeningOffset == 1800 && !listeningExactResumePosition, "主动书签位置跨保存重开仍按原位置恢复，并在播放后消费豁免");
            PauseListening(); listeningPausedUtc = oldPause; StartListening();
            Check(listeningOffset == 0, "书签实际播放之后再次长暂停正常回到本句开头");
            PauseListening();

            var listeningSnapshot = CaptureListeningFeedback();
            Check(listeningSnapshot?.Mode == "听书" && listeningSnapshot.NodeId == "a" && engine.CurrentId == gameNode, "听书反馈取独立听书当前句，不取游戏当前句");
            MoveListening(() => listeningSession!.SeekNode("b"));
            Check(listeningExactResumePosition && listeningOffset == 0 && engine.CurrentId == gameNode && playCalls == gameCalls, "主动听书定位保持准确位置且完全不改变游戏进度");

            preferences.Volume = 80; preferences.SpeakerVolumes.Clear();
            experienceDialogTest = dialog =>
            {
                try
                {
                    var volumes = (SpeakerVolumeWindow)dialog; volumes.Search.Text = "露西亚"; volumes.Volume.Value = 25;
                    Check(volumes.Values.GetValueOrDefault("露西亚") == 25 && !preferences.SpeakerVolumes.ContainsKey("露西亚"), "角色音量编辑先保存在窗口，尚未确认不修改设置");
                    SaveDialogImage(volumes, "角色独立音量.png");
                    Invoke(dialog, "保存");
                }
                catch (Exception ex) { dialogError = ex; dialog.Close(); }
            };
            OpenSpeakerVolumes(); if (dialogError != null) throw dialogError;
            experienceDialogTest = null;
            audioSpeaker = "露西亚"; ApplySpeakerVolumes();
            Check(Math.Abs(audio.Volume - .2f) < .0001 && Math.Abs(listeningAudio.Volume - .8f) < .0001, "角色音量乘总音量，游戏与听书分别使用各自当前角色");
            preferences.SpeakerVolumes["丽芙"] = 0; ApplySpeakerVolumes();
            Check(listeningAudio.Volume == 0 && Math.Abs(audio.Volume - .2f) < .0001, "单角色静音不影响另一端另一角色");
            experienceDialogTest = dialog =>
            {
                var volumes = (SpeakerVolumeWindow)dialog; volumes.Search.Text = "露西亚"; volumes.Volume.Value = 99; dialog.Close();
            };
            OpenSpeakerVolumes(); experienceDialogTest = null;
            Check(preferences.SpeakerVolumes["露西亚"] == 25, "取消角色音量窗口不提交临时调整");
            preferences.Volume = 80; preferences.SpeakerVolumes.Clear(); ApplySpeakerVolumes(); Save();
            Expand(listeningTab); await Task.Delay(80); Screenshot("智能续听与反馈.png");
            report.Add("说明：以上为本地 WPF 接线和独立状态验证，未进行真人听审或真实游戏跟随实测。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            experienceDialogTest = null; experienceDialog?.Close(); listeningTestAudio = false; StopListeningForGame();
            textAutoEnvironmentTest = null; textAutoForegroundTest = null; textAutoClockTest = null;
            textAutoSampleTest = null; textAutoClickTest = null;
        }
        File.WriteAllLines(Path.Combine(Log.DataDir, "player-experience-ui-test.txt"), report); Close();
    }
}
