using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    // 仅由隔离 --test-preference-validation-ui / --test-state 调用，使用真实控件回调与保存队列。
    async Task RunPreferenceValidationUiTest()
    {
        var report = new List<string>();
        void Check(bool valid, string label)
        { if (!valid) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        async Task Drain()
        { await Task.Delay(60); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        async Task<Preferences> Saved(string label)
        {
            var error = stateSaves.Flush(); await Drain();
            Check(error == null && saveSnapshotError == null, label + "：真实保存队列成功");
            return Json.Read<Preferences>(stateFile);
        }
        string temp = stateFile + ".tmp";
        bool ownBlockedTemp = false;
        try
        {
            Check(testUi && Path.GetFullPath(stateFile).StartsWith(Path.GetFullPath(Log.DataDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "仅使用本次独立测试设置");
            var defaults = new Preferences();
            string originalDefaults = JsonSerializer.Serialize(defaults, Json.Options);
            Check(!PreferenceValidation.Normalize(defaults) && originalDefaults == JsonSerializer.Serialize(defaults, Json.Options), "有效默认配置无需修改");
            string missingFile = Path.Combine(Log.DataDir, "old-minimal-settings.json");
            File.WriteAllText(missingFile, "{\"volume\": 37, \"keys\": {\"next\":\"J\",\"original\":\"None\"}}");
            var old = Json.Read<Preferences>(missingFile); PreferenceValidation.Normalize(old);
            Check(old.Volume == 37 && old.Keys["next"] == "J" && old.Keys["original"] == "None" && old.SmartListeningResume, "旧JSON缺少新字段时保留音量、改键与显式未绑定并使用默认新字段");
            var nulls = JsonSerializer.Deserialize<Preferences>("""
                {"visits":null,"facts":null,"heard":null,"menuSelections":null,"choices":null,
                 "speakerVolumes":null,"keys":null,"gamepadBindings":null,"packFile":null,"packId":null,
                 "nodeId":null,"outputDeviceId":null,"gameProcess":null,"gameExecutablePath":null,
                 "gamepadDeviceName":null,"compactMode":null,"gamepadGlyphStyle":null,
                 "gamepadAdvanceButton":null,"gamepadModifier":null,"dialogueRegion":null}
                """, Json.Options)!;
            Check(PreferenceValidation.Normalize(nulls), "显式null字段被识别为需要修正");
            Check(nulls.Visits != null && nulls.Facts != null && nulls.Heard != null && nulls.MenuSelections != null && nulls.Choices != null && nulls.SpeakerVolumes != null && nulls.Keys != null && nulls.GamepadBindings != null, "所有非空容器补齐");
            Check(nulls.GameProcess == "" && nulls.GamepadDeviceName == "" && nulls.GameExecutablePath == "" && nulls.PackFile == "" && nulls.OutputDeviceId == "" && nulls.PackId == "", "所有持久化的非空名称与路径安全补齐");
            Check(nulls.NodeId == null && nulls.DialogueRegion.IsValid && nulls.GamepadModifier == "Back" && nulls.GamepadAdvanceButton == "South", "可空位置保留null，区域与手柄前缀沿用原安全回退");
            Check(!PreferenceValidation.Normalize(nulls), "归一结果幂等，不重复改写合法配置");
            var preserved = new Preferences
            {
                Left = -1920, Top = -100, PanelWidth = 360, PanelHeight = 460,
                ClickZoneLeft = -1440, ClickZoneTop = -80, Volume = 42, ReadingScale = 1.25,
                AutomaticDelaySeconds = .6, TextAutoDelaySeconds = 2, CompactMode = "strip", GamepadGlyphStyle = "playstation",
                GamepadDeviceName = "用户的手柄", GameProcess = "用户游戏", GameExecutablePath = "Z:\\游戏\\game.exe",
                Keys = new() { ["next"] = "J", ["pause"] = "None", ["legacy-action"] = "not-a-key" },
                SpeakerVolumes = new() { ["角色甲"] = 0, ["角色乙"] = 37 },
                GamepadBindings = new() { ["panel"] = "Start", ["original"] = "None", ["legacy-action"] = "not-a-button" },
                Choices = new() { ["menu"] = "option" }, Facts = new() { "fact" }, Heard = new() { "line" },
                ProgressSchema = 91, VisitPosition = -7, NodeId = "legacy-node"
            };
            var keys = preserved.Keys; var choices = preserved.Choices; var facts = preserved.Facts;
            string beforePreserved = JsonSerializer.Serialize(preserved, Json.Options);
            Check(!PreferenceValidation.Normalize(preserved) && beforePreserved == JsonSerializer.Serialize(preserved, Json.Options), "合法自定义值、未绑定与未知旧动作原样保留");
            Check(ReferenceEquals(keys, preserved.Keys) && ReferenceEquals(choices, preserved.Choices) && ReferenceEquals(facts, preserved.Facts), "合法容器引用、旧导航版本与位置不被重建或猜修");
            foreach (var item in new[] { (Input: -12d, Expected: 0d), (Input: 1000d, Expected: 100d), (Input: 1e308, Expected: 100d), (Input: double.NaN, Expected: 80d), (Input: double.PositiveInfinity, Expected: 80d) })
            {
                var value = new Preferences { Volume = item.Input }; PreferenceValidation.Normalize(value);
                Check(value.Volume == item.Expected && float.IsFinite((float)value.Volume / 100), "异常总音量安全归一：" + item.Input);
            }
            var ranges = new Preferences
            {
                ReadingScale = 999, AutomaticDelaySeconds = -5, TextAutoDelaySeconds = 500,
                CompactMode = "unknown", GamepadGlyphStyle = "unknown", Left = double.NaN, Top = double.PositiveInfinity,
                PanelWidth = -1, PanelHeight = double.NaN, ClickZoneLeft = double.NaN, ClickZoneTop = double.PositiveInfinity,
                DialogueRegion = new(-1, 0, 2, 2), SpeakerVolumes = new() { ["低"] = -7, ["高"] = 500 },
                Keys = new() { ["next"] = null!, ["panel"] = "None" }, GamepadBindings = new() { ["pause"] = null!, ["panel"] = "None" }
            };
            PreferenceValidation.Normalize(ranges);
            Check(ranges.ReadingScale == 1 && ranges.AutomaticDelaySeconds == 1 && ranges.TextAutoDelaySeconds == 1.5 && ranges.CompactMode == "ball" && ranges.GamepadGlyphStyle == "auto", "非法显示与等待选项和界面的安全默认一致");
            Check(ranges.Left == 60 && ranges.Top == 100 && ranges.PanelWidth == 580 && ranges.PanelHeight == 760 && ranges.ClickZoneLeft == -1 && ranges.ClickZoneTop == -1 && ranges.DialogueRegion.IsValid, "非法坐标和区域回退，合法负副屏坐标已另验保留");
            Check(ranges.SpeakerVolumes["低"] == 0 && ranges.SpeakerVolumes["高"] == 100, "角色音量按原播放值域归一");
            Check(ranges.Keys["next"] == "None" && ranges.Keys["panel"] == "None" && ranges.GamepadBindings["pause"] == "None" && ranges.GamepadBindings["panel"] == "None", "空动作保持不触发，不擅自启用默认键");
            string backupFile = Path.Combine(Log.DataDir, "read-backup-check.json");
            File.WriteAllText(backupFile, "{"); File.WriteAllText(backupFile + ".bak", "{\"volume\": 23, \"gameProcess\": null}");
            var recovered = Json.ReadWithBackup<Preferences>(backupFile, out bool usedBackup); PreferenceValidation.Normalize(recovered);
            Check(usedBackup && recovered.Volume == 23 && recovered.GameProcess == "" && File.ReadAllText(backupFile) == "{", "损坏主JSON恢复有效备份后归一，不改坏原件");

            string fixtureRoot = Path.Combine(Log.DataDir, "fixtures", "preference-validation");
            Directory.CreateDirectory(fixtureRoot);
            File.WriteAllBytes(Path.Combine(fixtureRoot, "test-audio.bin"), new byte[] { 1 });
            var fixture = new Pack
            {
                Id = "preference-validation", Title = "设置保存隔离检查", Root = fixtureRoot,
                Chapters = new() { new() { Id = "chapter", Title = "测试章节", Sections = new() { new() { Id = "section", Title = "测试小节", StartId = "first" } } } },
                Nodes = new()
                {
                    new() { Id = "first", SectionId = "section", Speaker = "旁白", Text = "设置检查固定起点。", Audio = "test-audio.bin", NextId = "second" },
                    new() { Id = "second", SectionId = "section", Speaker = "旁白", Text = "设置调整不能跳到这一句。", Audio = "test-audio.bin" }
                }
            };
            fixture.Validate(); string fixtureFile = Path.Combine(fixtureRoot, "pack.json");
            Json.Save(fixtureFile, fixture); LoadPack(fixtureFile); engine!.Commit("first");
            Check(engine.CurrentId == "first" && engine.History.Count > 0 && !audio.Playing, "建立真实Core位置与历史基线，testUi只记录播放请求");
            Save(); await Saved("控件检查前基线");
            Expand(SettingsTab); await Drain();
            int calls = playCalls; string node = engine.CurrentId!;
            string navigation = JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
            var scale = AppearanceSettingsContent.Children.OfType<ComboBox>().Single(x => x.Items.Count == 4 && x.Items[0]?.ToString() == "100%");
            var compact = AppearanceSettingsContent.Children.OfType<ComboBox>().Single(x => x.Items.Count == 2 && x.Items[0]?.ToString() == "收起为悬浮球");
            for (int i = 0; i < 4; i++)
            {
                if (scale.SelectedIndex == i) scale.SelectedIndex = (i + 1) % 4;
                scale.SelectedIndex = i; var saved = await Saved("阅读字号" + i);
                Check(saved.ReadingScale == new[] { 1d, 1.25, 1.5, 2d }[i] && preferences.ReadingScale == saved.ReadingScale, "真实字号控件回调写盘并可重读");
            }
            for (int i = 0; i < 2; i++)
            {
                if (compact.SelectedIndex == i) compact.SelectedIndex = 1 - i;
                compact.SelectedIndex = i; var saved = await Saved("收起方式" + i);
                Check(saved.CompactMode == (i == 0 ? "ball" : "strip"), "真实收起方式控件写盘并可重读");
            }
            foreach (double volume in new[] { 0d, 37d, 100d })
            {
                if (VolumeSlider.Value == volume) VolumeSlider.Value = volume == 0 ? 1 : 0;
                VolumeSlider.Value = volume; var saved = await Saved("总音量" + volume);
                Check(saved.Volume == volume && preferences.Volume == volume && VolumeLabel.Text == $"{volume:0}%", "真实音量控件、显示与盘档一致");
            }
            for (int i = 0; i < 4; i++)
            {
                if (automaticDelay.SelectedIndex == i) automaticDelay.SelectedIndex = (i + 1) % 4;
                automaticDelay.SelectedIndex = i; var saved = await Saved("旧自动播放延迟" + i);
                Check(saved.AutomaticDelaySeconds == new[] { .6, 1, 1.5, 2 }[i], "真实旧自动延迟选择写盘");
            }
            for (int i = 0; i < 3; i++)
            {
                if (textAutoDelay.SelectedIndex == i) textAutoDelay.SelectedIndex = (i + 1) % 3;
                textAutoDelay.SelectedIndex = i; var saved = await Saved("文字跟随延迟" + i);
                Check(saved.TextAutoDelaySeconds == new[] { 1d, 1.5, 2d }[i] && TextAutoDelayMs == (int)(saved.TextAutoDelaySeconds * 1000), "真实文字跟随延迟、运行值与盘档一致");
            }
            foreach (bool enabled in new[] { false, true })
            {
                if (smartListeningResume.IsChecked == enabled) smartListeningResume.IsChecked = !enabled;
                smartListeningResume.IsChecked = enabled; var saved = await Saved("智能续听" + enabled);
                Check(saved.SmartListeningResume == enabled, "真实智能续听开关写入通用偏好");
            }
            foreach (bool enabled in new[] { true, false })
            {
                if (textAutoBox.IsChecked == enabled) textAutoBox.IsChecked = !enabled;
                textAutoBox.IsChecked = enabled; var saved = await Saved("自动下一句开关" + enabled);
                Check(saved.TextAutoAdvanceEnabled == enabled && !automaticRunning && textAutoLine == null, "只改自动下一句偏好，不开始播放或排队点击");
            }
            Check(playCalls == calls && engine.CurrentId == node && JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) == navigation && !audio.Playing && !listeningRunning, "所有设置回调不播放、不提交游戏位置、不启动听书");

            Save(); await Saved("注入失败前保存");
            byte[] fileBefore = File.ReadAllBytes(stateFile);
            Check(!File.Exists(temp) && !Directory.Exists(temp), "故障注入临时路径原先不存在");
            Directory.CreateDirectory(temp); ownBlockedTemp = true;
            VolumeSlider.Value = 41;
            var failure = stateSaves.Flush(); await Drain();
            Check(failure != null && File.ReadAllBytes(stateFile).SequenceEqual(fileBefore) && preferences.Volume == 41, "真实设置写入失败保旧文件、保本次内存值且回调不外抛");
            Check(SaveWarning.Visibility == Visibility.Visible && SaveWarning.Text.Contains("尚未保存"), "保存失败显示独立警告");
            string warning = SaveWarning.Text; Tell("随后普通状态提示"); UpdateState(); await Drain();
            Check(SaveWarning.Visibility == Visibility.Visible && SaveWarning.Text == warning, "普通业务状态刷新不覆盖保存失败警告");
            Directory.Delete(temp); ownBlockedTemp = false;
            Save(); var retried = await Saved("解除故障重试");
            Check(retried.Volume == 41 && SaveWarning.Visibility == Visibility.Collapsed, "真实重试成功写入本次值并清独立警告");
            Check(playCalls == calls && !automaticRunning && !audio.Playing && !listeningRunning, "写失败与恢复全过程不播放");
            scale.SelectedIndex = 3; await Saved("最终200%阅读截图");
            Check(CurrentText.FontSize == 30, "最终200%阅读字号实际应用到当前台词");
            Screenshot("设置保存与恢复.png");
            report.Add("说明：真实WPF控件、旧JSON和文件保存队列的独立状态检查；未操作真实游戏、用户进度或实体手柄。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            if (ownBlockedTemp && Directory.Exists(temp)) Directory.Delete(temp);
            Save(); stateSaves.Flush(); await Drain();
        }
        File.WriteAllLines(Path.Combine(Log.DataDir, "preference-validation-ui-test.txt"), report);
        Close();
    }
}
