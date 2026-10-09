using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

internal sealed class OnboardingState
{
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "not-started";
    public string Purpose { get; set; } = "";
    public int Step { get; set; }
    public string Folder { get; set; } = "";
    public string PackFile { get; set; } = "";
    public string NodeId { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

internal sealed class OnboardingStore(string directory)
{
    internal string FilePath => Path.Combine(directory, "onboarding.json");
    internal OnboardingState Load()
    {
        try { if (File.Exists(FilePath) || File.Exists(FilePath + ".bak")) return Json.ReadWithBackup<OnboardingState>(FilePath, out _); }
        catch (Exception ex) { Log.Write("onboarding", "上回引导记录暂不能读取：" + ex.Message); }
        return new();
    }
    internal void Save(OnboardingState state) { state.UpdatedUtc = DateTimeOffset.UtcNow; Json.Save(FilePath, state); }
    internal static bool ShouldAutoOpen(string directory)
    {
        // App 已经可能写入启动日志，目录是否存在不能代表是否用过播放器。
        foreach (string name in new[] { "preferences.json", "preferences.json.bak", "onboarding.json", "onboarding.json.bak" })
            if (File.Exists(Path.Combine(directory, name))) return false;
        try
        {
            foreach (string subdirectory in new[] { "progress", "listening" })
            {
                string path = Path.Combine(directory, subdirectory);
                if (Directory.Exists(path) && Directory.EnumerateFiles(path).Any(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))) return false;
            }
        }
        catch (Exception) { return false; } // 不能判断旧数据时不强制打断用户。
        return true;
    }
}

public partial class MainWindow
{
    bool autoOpenOnboarding;
    readonly Button onboardingButton = new() { Content = "三步上手：先听到一句配音", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
    Func<Window, string?>? onboardingFolderTest;
    Func<string, long, Task>? onboardingPreviewTest;
    void InitializeOnboarding()
    {
        onboardingButton.Click += (_, _) => OpenOnboarding();
        SettingsContent.Children.Insert(1, onboardingButton);
    }
    void OpenOnboarding()
    {
        if (experienceDialog != null) { experienceDialog.Activate(); return; }
        autoOpenOnboarding = false;
        if (listeningRunning) PauseListening();
        if (TextFollowing) PauseTextPlayback("正在进行上手引导，配音已暂停；关闭后可主动恢复。");
        CancelTextAutoAdvance("正在进行上手引导，待执行的自动点击已取消。");
        if (automaticRunning || automaticPendingOwner != null || automaticPreparingOwner != null)
            StopAutomatic("正在进行上手引导，自动播放已暂停。", false);
        StopPreview(); CancelOcr(); engine?.PauseForBrowse(); StopAudio(); ResetFollowInputSession(); HideBranchMenu();
        var store = new OnboardingStore(Log.DataDir);
        var dialog = new OnboardingWindow(store, LibraryBox.Items.OfType<PackChoice>().ToArray(),
            (owner, choices, selected) => PickOnboardingChapter(owner, choices, selected),
            owner =>
            {
                if (testUi && onboardingFolderTest != null) return onboardingFolderTest(owner);
                var chooser = new Microsoft.Win32.OpenFolderDialog { Title = "选择单章配音包或包含各章配音包的总目录" };
                return chooser.ShowDialog(owner) == true ? chooser.FolderName : null;
            }, ReadPackChoice, preferences.OutputDeviceId, (int)Math.Round(preferences.Volume), preferences.SpeakerVolumes,
            testUi ? onboardingPreviewTest : null);
        if (ShowExperienceDialog(dialog) == true && dialog.OpenSelectedChapter && dialog.SelectedPackFile.Length > 0)
        {
            // 只有明确的“完成并打开这一章”才提交章节；浏览、试听、跳过均不走这里。
            preferences.OutputDeviceId = dialog.SelectedOutput; VolumeSlider.Value = dialog.SelectedVolume; Save(); RefreshAudioDevices();
            if (dialog.Purpose == "listening")
            {
                SetLibrary(Path.GetDirectoryName(dialog.SelectedPackFile)!);
                OpenListeningPack(dialog.SelectedPackFile, null); Expand(listeningTab);
                Tell("已打开所选章节，点击“播放 / 续听”开始；试听没有改动原有进度。");
            }
            else
            {
                SetLibrary(Path.GetDirectoryName(dialog.SelectedPackFile)!); LoadPack(dialog.SelectedPackFile); Expand(gameTextTab);
                Tell("已打开所选章节。请先打开战双；需要时在设置中连接游戏，再选择跟随方式并点击开始。");
            }
        }
        else if (dialog.OpenSoundSettings)
        { Expand(SettingsTab); ExpandContainingSettings(AppearanceSettingsContent); }
    }
    PackChoice? PickOnboardingChapter(Window owner, IReadOnlyList<PackChoice> choices, PackChoice? selected)
    {
        if (chapterPicker != null) return null;
        var picker = new ChapterPickerWindow(choices, selected, false) { Owner = owner }; chapterPicker = picker;
        if (testUi && chapterPickerTest != null) picker.Loaded += (_, _) => chapterPickerTest(picker);
        try { return picker.ShowDialog() == true ? picker.Result : null; }
        finally { chapterPicker = null; ResetGamepadContext(); }
    }
}
