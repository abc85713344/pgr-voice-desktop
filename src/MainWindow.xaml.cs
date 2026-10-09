using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PgrVoice;
public sealed class LineRow : INotifyPropertyChanged
{
    public static Brush BranchAccent => Theme.Brush("BranchAccent");
    public static Brush BranchFill => Theme.Brush("BranchFill");
    public static Brush RouteFill => Theme.Brush("RouteFill");
    public Node Node { get; }
    readonly Pack pack;
    bool playing;
    public LineRow(Node node, Pack pack) { Node = node; this.pack = pack; }
    public string Text => Node.Text;
    public string Context { get; set; } = "";
    public string EndingText => StoryLinePresentation.IsExplicitEnding(pack, Node) ? "—— 这条路线到此结束，没有下一句 ——" : "";
    public bool IsBranch => Node.Kind == "choice" || Node.Kind == "line" && !string.IsNullOrEmpty(Node.PathId);
    public string Caption => string.Join(" · ", new[] { playing ? "▶ 正在播放" : "", Node.Kind switch { "choice" => "◆ 分支选择", "merge" => "共同线", "gap" => "待续接", "line" when IsBranch => "分支台词", _ => "" }, Node.Speaker, pack.AudioNotice(Node) }.Where(s => !string.IsNullOrEmpty(s)));
    public Brush Accent => IsBranch ? BranchAccent : playing ? Theme.Brush("PlayingAccent") : Theme.Brush("NormalAccent");
    public Brush Border => IsBranch ? BranchAccent : Brushes.Transparent;
    public Brush Background => playing ? Theme.Brush("PlayingFill") : Node.Kind == "choice" ? BranchFill : IsBranch ? RouteFill : Brushes.Transparent;
    public bool Playing { get => playing; set { if (playing == value) return; playing = value; PropertyChanged?.Invoke(this, new(nameof(Caption))); PropertyChanged?.Invoke(this, new(nameof(Background))); PropertyChanged?.Invoke(this, new(nameof(Accent))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
// 章节条目的身份必须稳定；更新进度摘要不能改变 WPF Selector 使用的相等性和哈希。
public sealed class PackChoice(string file, string title) : INotifyPropertyChanged
{
    string resume = "";
    public string File { get; } = file;
    public string Title { get; } = ChapterTitle(title);
    public string Resume
    {
        get => resume;
        set
        {
            if (resume == value) return;
            resume = value;
            PropertyChanged?.Invoke(this, new(nameof(Resume)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayTitle)));
        }
    }
    public string PackId { get; init; } = "";
    public string SortOrder { get; init; } = "";
    public string Category => ChapterCatalog.Category(PackId, Title);
    public string DisplayTitle => Title;
    public static string ChapterTitle(string title)
    {
        return ChapterCatalog.Title(title);
    }
    public override string ToString() => DisplayTitle;
    public event PropertyChangedEventHandler? PropertyChanged;
}
public partial class MainWindow : Window
{
    readonly AudioService audio = new();
    readonly OcrService ocr;
    readonly string stateFile;
    Preferences preferences = new();
    PlaybackEngine? engine;
    KeyboardListener? keyboard;
    RawKeyboardListener? rawKeyboard;
    readonly BranchMenuWindow branchMenu = new();
    readonly ClickZoneWindow clickZone = new();
    readonly ToolTip routeToast = new();
    bool singleResume;
    GameWindow? game;
    bool expanded, ready, closing, testUi, locating;
    string? recordingAction, playingId;
    KeyBindingWindow? keyEditor;
    readonly Dictionary<string,Button> keyButtons = new();
    CancellationTokenSource? ocrCancellation;
    int ocrGeneration;
    double[]? lastRegion;
    (int Width, int Height) lastSize;
    Point? dragStart;
    double startLeft, startTop;
    bool dragged;
    List<LineRow> rows = new();
    bool selectingLibrary, loadingPack;
    readonly DispatcherTimer timer;
    int playCalls;
    DateTime lastDraftCheck, loadedDraftWrite;
    long loadedDraftLength;
    // 音频设备的打开与释放串行放到后台；UI 只读取状态快照。
    readonly SemaphoreSlim audioQueue = new(1, 1);
    long audioRequest;
    volatile bool audioStarting;
    readonly Dictionary<string, string> actionLabels = new()
    {
        ["next"]="游戏同步下一句", ["previous"]="配音上一句", ["manualNext"]="配音下一句", ["replay"]="重播当前句", ["panel"]="展开/收起", ["pause"]="暂停/恢复跟随", ["original"]="游戏原声", ["ocr"]="OCR 定位", ["catalog"]="章节目录", ["reselect"]="重选分支", ["history"]="历史与试听", ["interactions"]="手动选择分支"
    };
    public MainWindow(string[] args)
    {
        testUi = args.Contains("--test-reported-ocr-ui") || args.Contains("--test-story-menus-ui") || args.Contains("--test-interactions-ui") || args.Contains("--test-upgrade-ui") || args.Contains("--test-experience-ui") || args.Contains("--test-ui") || args.Contains("--test-ocr-ui") || args.Contains("--test-keys-ui") || args.Contains("--test-branches-ui") || args.Contains("--test-library-ui") || args.Contains("--test-capture-ui") || args.Contains("--test-theme-ui");
        testUi |= args.Contains("--test-draft-ui") || args.Contains("--test-story-tail-ui");
        testUi |= args.Contains("--test-dialogue-follow-ui");
        testUi |= args.Contains("--test-chapter-switch-ui");
        testUi |= args.Contains("--test-chapter-picker-ui");
        testUi |= args.Contains("--test-section-picker-ui");
        testUi |= args.Contains("--test-branch-mouse-ui");
        testUi |= args.Contains("--test-listening-ui") || args.Contains("--test-autoplay-ui");
        testUi |= args.Contains("--test-player-experience-ui");
        testUi |= args.Contains("--test-onboarding-ui");
        testUi |= args.Contains("--test-library-search-ui");
        testUi |= args.Contains("--test-shortcuts-ui") || args.Contains("--test-action-buttons-ui");
        testUi |= args.Contains("--test-gamepad-ui");
        testUi |= args.Contains("--test-compact-follow-ui") || args.Contains("--test-mouse-follow-ui");
        testUi |= args.Contains("--test-input-switch-ui");
        testUi |= args.Contains("--test-game-text-ui");
        testUi |= args.Contains("--test-game-branch-recovery-ui");
        testUi |= args.Contains("--test-game-location-ui");
        testUi |= args.Contains("--test-text-auto-ui") || args.Contains("--test-route-compat-ui") || args.Contains("--test-story-reference-ui") || args.Contains("--test-preference-validation-ui");
        stateFile = Path.Combine(Log.DataDir, "preferences.json");
        // 在初始化控件、恢复章节可能保存默认设置之前冻结新旧用户判断。
        autoOpenOnboarding = OnboardingStore.ShouldAutoOpen(Log.DataDir);
        bool recoveredSettings = false;
        try { if (File.Exists(stateFile) || File.Exists(stateFile+".bak")) preferences = Json.ReadWithBackup<Preferences>(stateFile, out recoveredSettings); } catch (Exception ex) { Log.Write("settings", ex.Message); }
        if (recoveredSettings && File.Exists(stateFile)) try { File.Copy(stateFile,stateFile+".damaged-"+DateTime.Now.ToString("yyyyMMddHHmmss"),false); } catch { }
        PreferenceValidation.Normalize(preferences);
        preferences.Keys ??= new(); preferences.MenuSelections ??= new();
        preferences.SpeakerVolumes ??= new();
        preferences.GameExecutablePath ??= "";
        if (preferences.DialogueRegion == null || !preferences.DialogueRegion.IsValid) preferences.DialogueRegion = new();
        if (args.Contains("--dialogue-guard") || args.Contains("--dialogue-trial")) preferences.DialogueGuardEnabled = true;
        foreach (var entry in new Preferences().Keys)
            if (!preferences.Keys.ContainsKey(entry.Key))
                preferences.Keys[entry.Key] = Enum.TryParse<Key>(entry.Value, out var defaultKey) && preferences.Keys.Values.Any(value => KeyBindingWindow.Matches(value, defaultKey)) ? "None" : entry.Value;
        ocr = new OcrService(Path.Combine(AppContext.BaseDirectory, "ocr", "PgrOcr.exe"));
        InitializeComponent();
        RefreshGameLocation();
        InitializeExperience();
        InitializeDialogueFollow();
        InitializeAutoPlayback();
        InitializeListeningExperience();
        InitializeGamepad();
        InitializeFollowInputSwitch();
        InitializeMouseFollow();
        InitializeCompactFollowControls();
        InitializeGameText();
        InitializeModeLayout();
        InitializeOnboarding();
        if(recoveredSettings) Tell("已从备份恢复设置与进度，损坏文件已保留。");
        LoadArtwork();
        ArtworkEnabledBox.IsChecked = preferences.ShowArtwork;
        ApplyArtwork();
        branchMenu.Confirm+=ConfirmSmallBranch;
        branchMenu.Cancel+=DismissSmallMenu;
        branchMenu.Interactions+=OpenInteractions;
        branchMenu.NavigationScope+=ToggleNavigationScope;
        branchMenu.ReturnTopic+=ReturnToTopic;
        branchMenu.Locate+=()=>{singleResume=engine?.ReviewRoute!=null;HideBranchMenu();_=Locate();};
        branchMenu.Manual+=OpenManualContinuation;
        branchMenu.Options.SelectionChanged+=(_,_)=>{if(branchMenu.MenuId!=null)preferences.MenuSelections[branchMenu.MenuId]=branchMenu.Options.SelectedIndex;};
        clickZone.Moved += () =>
        {
            if (!ready || loadingPack || !preferences.ClickZoneEnabled) return;
            preferences.ClickZoneLeft = clickZone.Left; preferences.ClickZoneTop = clickZone.Top; Save();
        };
        Left = preferences.Left; Top = preferences.Top;
        ClampPosition();
        VolumeSlider.Value = preferences.Volume; audio.Volume = (float)preferences.Volume / 100;
        OcrEnabledBox.IsChecked = preferences.OcrEnabled;
        ready = true; VolumeLabel.Text = $"{preferences.Volume:0}%"; BuildKeys(); RefreshWindows();
        RefreshKeyHints();
        Loaded += async (_, _) =>
        {
            PermissionLabel.Text = Native.IsElevated(new WindowInteropHelper(this).Handle) switch
            {
                true => "播放器权限：管理员 · 本版本统一以管理员身份运行",
                false => "播放器权限：普通用户 · 请关闭后以管理员身份打开 PgrVoice.exe",
                null => "播放器权限：暂时无法确认 · 本版本按管理员权限启动"
            };
            // 普通截图允许包含播放器；OCR 截图期间临时隐藏自己的窗口。
            try
            {
                keyboard = new KeyboardListener();
                keyboard.ObservedPressed += input => Dispatcher.BeginInvoke(() => HandleGlobal(input.Key, input.Foreground, input.Timestamp, input.Modifiers, input.MessageTime));
                PublishMenuCapture();
                keyboard.MenuPressed+=(key,foreground,epoch)=>Dispatcher.BeginInvoke(()=>
                {
                    if(KeyTesting || !branchMenu.IsVisible || epoch!=branchMenu.Epoch || (!branchMenu.IsNavigation && engine?.MenuWaiting!=true) || foreground!=Native.GetForegroundWindow())return;
                    if(key==Key.Up)branchMenu.Move(-1);else if(key==Key.Down)branchMenu.Move(1);else if(key==Key.Enter)ConfirmSmallBranch();else if(key==Key.Escape)DismissSmallMenu();
                });
                Log.Write("keyboard", keyboard.HookAvailable ? "独立线程菜单键监听已启动；普通推进只使用 Raw Input" : keyboard.HookError ?? "菜单键监听不可用，请用菜单按钮");
                if (!keyboard.HookAvailable) PermissionLabel.Text += "\n" + (keyboard.HookError ?? "菜单拦截不可用，请用鼠标选择配音菜单。");
            }
            catch (Exception ex) { Tell(ex.Message + "，可使用面板按钮"); }
            try
            {
                rawKeyboard = new RawKeyboardListener(new WindowInteropHelper(this).Handle,
                    (RawKeyInput input) => keyboard?.OnRawKey(input));
                rawKeyboard.MouseObserved += input => Dispatcher.BeginInvoke(() => HandleMouseGlobal(input));
                rawKeyboard.MouseGestureObserved += gesture => Dispatcher.BeginInvoke(() => HandleMouseGesture(gesture));
                rawKeyboard.DeviceRemoved += device => keyboard?.ForgetRawDevice(device);
                rawKeyboard.ReadFailed += message => Dispatcher.BeginInvoke(() => { engine?.PauseForBrowse(); HoldDialogue("输入读取失败"); Tell(message); });
                if (rawKeyboard.MouseGestureError is { } gestureError) { mouseFollowNotice = gestureError; RefreshMouseFollow(); Log.Write("mouse-follow", gestureError); }
                Log.Write("keyboard", "实体键盘输入已启动" + (rawKeyboard.MouseAvailable ? "，鼠标跟随可用" : "，鼠标监听未启用"));
            }
            catch (Exception ex) { Log.Write("keyboard-error", ex.ToString()); Tell("键盘跟随未启动：" + ex.Message + "，可使用面板按钮"); }
            int packArgument = Array.IndexOf(args, "--pack");
            string packFile = packArgument >= 0 && packArgument + 1 < args.Length ? args[packArgument + 1] : preferences.PackFile;
            int libraryArgument = Array.IndexOf(args, "--library");
            packFile = LibraryPaths.Restore(packFile, libraryArgument >= 0 && libraryArgument + 1 < args.Length
                ? args[libraryArgument + 1] : null);
            if (libraryArgument >= 0 && libraryArgument + 1 < args.Length && Directory.Exists(args[libraryArgument + 1]))
            {
                SetLibrary(args[libraryArgument + 1]);
                var choices = LibraryBox.Items.OfType<PackChoice>().ToList();
                var initial = choices.FirstOrDefault(x => String.Equals(x.File, packFile, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
                if (initial != null) LoadPack(initial.File);
            }
            else if (File.Exists(packFile)) { SetLibrary(Path.GetDirectoryName(Path.GetFullPath(packFile))!); LoadPack(packFile); }
            else
            {
                string bundled=Path.Combine(AppContext.BaseDirectory,"配音包");
                if(Directory.Exists(bundled))
                {
                    SetLibrary(bundled);
                    if(LibraryBox.Items.OfType<PackChoice>().FirstOrDefault() is { } initial)LoadPack(initial.File);
                }
            }
            if (engine == null) Expand(SettingsTab);
            else if (args.Contains("--panel")) Expand(testUi ? StoryTab : gameTextTab);
            else {ApplyCompactView();ClampPosition();}
            int textSectionArgument = Array.IndexOf(args, "--text-section");
            if (textSectionArgument >= 0 && textSectionArgument + 1 < args.Length &&
                SectionBox.Items.OfType<Section>().FirstOrDefault(s => s.Id == args[textSectionArgument + 1]) is { } textStartSection)
                SectionBox.SelectedItem = textStartSection;
            if (args.Contains("--text-tracking")) Expand(gameTextTab);
            Log.Write("startup", $"窗口已就绪，PID={Environment.ProcessId}，面板={expanded}，位置={Left:0},{Top:0}");
            if (args.Contains("--test-preference-validation-ui")) await RunPreferenceValidationUiTest();
            else if (args.Contains("--test-story-reference-ui")) await RunStoryReferenceUiTest();
            else if (args.Contains("--test-route-compat-ui")) await RunRouteCompatibilityUiTest();
            else if (args.Contains("--test-onboarding-ui")) await RunOnboardingUiTest();
            else if (args.Contains("--test-library-search-ui")) await RunLibrarySearchUiTest();
            else if (args.Contains("--test-player-experience-ui")) await RunPlayerExperienceUiTest();
            else if (args.Contains("--test-text-auto-ui")) await RunTextAutoUiTest();
            else if (args.Contains("--test-game-location-ui")) await RunGameLocationUiTest();
            else if (args.Contains("--test-game-branch-recovery-ui")) await RunGameBranchRecoveryUiTest();
            else if (args.Contains("--test-game-text-ui")) await RunGameTextUiTest();
            else if (args.Contains("--test-input-switch-ui")) await RunHotSwitchUiTest();
            else if (args.Contains("--test-mouse-follow-ui")) await RunMouseFollowUiTest();
            else if (args.Contains("--test-compact-follow-ui")) await RunCompactFollowUiTest();
            else if (args.Contains("--test-gamepad-ui")) await RunGamepadUiTest();
            else if (args.Contains("--test-shortcuts-ui")) await RunShortcutUiTest();
            else if (args.Contains("--test-action-buttons-ui")) await RunActionButtonUiTest();
            else if (args.Contains("--test-listening-ui")) await RunListeningUiTest();
            else if (args.Contains("--test-autoplay-ui")) await RunAutoPlaybackUiTest();
            else if (args.Contains("--test-branch-mouse-ui")) await RunBranchMouseUiTest();
            else if (args.Contains("--test-chapter-picker-ui")) await RunChapterPickerUiTest();
            else if (args.Contains("--test-section-picker-ui")) await RunSectionPickerUiTest();
            else if (args.Contains("--test-chapter-switch-ui")) await RunChapterSwitchUiTest();
            else if (args.Contains("--test-dialogue-follow-ui")) await RunDialogueFollowUiTest();
            else if (args.Contains("--test-reported-ocr-ui")) await RunReportedOcrUiTest(args);
            else if (args.Contains("--test-story-menus-ui")) await RunStoryMenusUiTest(args);
            else if (args.Contains("--test-interactions-ui")) await RunInteractionsUiTest();
            else if (args.Contains("--test-upgrade-ui")) await RunUpgradeUiTest();
            else if (args.Contains("--test-draft-ui")) await RunDraftUiTest();
            else if (args.Contains("--test-experience-ui")) await RunExperienceUiTest();
            else if (args.Contains("--test-story-tail-ui")) await RunStoryTailUiTest();
            else if (args.Contains("--test-theme-ui")) await RunThemeUiTest();
            else if (args.Contains("--test-capture-ui")) await RunCaptureUiTest();
            else if (args.Contains("--test-library-ui")) await RunLibraryUiTest(args);
            else if (args.Contains("--test-branches-ui")) await RunBranchesUiTest();
            else if (args.Contains("--test-keys-ui")) await RunKeysUiTest();
            else if (args.Contains("--test-ocr-ui")) await RunOcrUiTest();
            else if (testUi) await RunUiTest();
            else if (autoOpenOnboarding) OpenOnboarding();
            else if (args.Contains("--locate")) await Locate();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            // 播放现在在后台启动，设备打开完成前 audio.Playing 仍是 false，别把高亮提前清掉。
            if (playingId != null && !audioStarting && !audio.Playing && !testUi) { playingId = null; PaintPlaying(); }
            ReconnectGameIfNeeded(); TickAutomatic(); TickMouseFollow(); UpdateState();
            RefreshDraftIfUpdated();
            if (preferences.ClickZoneEnabled) UpdateClickZone();
        };
        timer.Start();
        Closing += OnClosing;
    }
    void Tell(string text) { Notice.Text = text; Log.Write("status", text); }
    readonly StateSaveQueue stateSaves = new();
    int saveGeneration;
    Exception? saveSnapshotError;
    void Save()
    {
        if (!ready || loadingPack) return;
        saveSnapshotError = null;
        preferences.Left = Left; preferences.Top = Top;
        if(expanded && Width>=420 && Height>=420){preferences.PanelWidth=Width;preferences.PanelHeight=Height;}
        if (engine != null) { preferences.PackId = engine.Pack.Id; preferences.NodeId = engine.CurrentId; preferences.Choices = new(engine.Choices); preferences.Facts=new(engine.Facts);preferences.Heard=new(engine.Heard);preferences.ProgressSchema=engine.Pack.SchemaVersion;preferences.Visits=new(engine.History);preferences.VisitPosition=engine.HistoryPosition; }
        try
        {
            // 所有可变对象在 UI 线程冻结；后台不得再访问 engine 或 preferences。
            var frozen = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences, Json.Options), Json.Options)!;
            var pack = engine?.Pack; var navigation = engine?.Current != null ? engine.ExportNavigation() : null;
            var store = progressStore; int generation = ++saveGeneration;
            _ = stateSaves.Enqueue(() =>
            {
                if (pack != null && navigation != null) store?.Save(pack, navigation);
                Json.Save(stateFile, frozen);
            }, failure =>
            {
                if (failure != null) Log.Write("save-error", failure.ToString());
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (closing || generation != saveGeneration) return;
                    if (failure != null) ShowSaveFailure(failure.Message); else SaveWarning.Visibility = Visibility.Collapsed;
                });
            });
        }
        catch (Exception ex) { saveSnapshotError = ex; ShowSaveFailure(ex.Message); }
    }
    void LoadPack(string file)
    {
        if (TextFollowing) PauseTextPlayback("切换配音章节，文本配音已暂停。");
        StopAutomatic("切换章节，自动播放已关闭。", false);
        StopListeningForGame();
        try
        {
            var pack = DesktopPackLoader.Load(file);
            if(engine!=null)
            {
                Save(); var saveError = stateSaves.Flush();
                if (saveError != null)
                {
                    ShowSaveFailure(saveError.Message); Tell("当前章进度保存失败，已保留当前章节。请处理保存问题后再切章。");
                    selectingLibrary = true;
                    LibraryBox.SelectedItem = LibraryBox.Items.OfType<PackChoice>().FirstOrDefault(x => String.Equals(x.File, preferences.PackFile, StringComparison.OrdinalIgnoreCase));
                    selectingLibrary = false; return;
                }
            }
            loadingPack = true;
            StopPreview(); diagnosticCancellation?.Cancel(); diagnosticReport=null; diagnosticList.ItemsSource=null; diagnosticStatus.Text="";
            HideBranchMenu();CancelOcr(); StopAudio(); playingId = null;
            engine = new PlaybackEngine(pack);
            engine.PlayRequested += PlayNode;
            engine.StopRequested += () => { if (!applyingMemoryLine) CancelInputBranchRecovery("播放已暂停或进入手动操作，分支监听已取消。"); StopAudio(); previewingHistory=false; playingId = null; PaintPlaying(); };
            engine.Changed += EngineChanged;
            if(progressStore!=null)
            {
                if(!progressStore.HasProgress(pack.Id) && preferences.PackId==pack.Id)
                    progressStore.MigrateLegacy(pack,preferences.NodeId,preferences.Choices,preferences.Facts,preferences.Heard,preferences.ProgressSchema,preferences.Visits,preferences.VisitPosition);
                progressStore.TryRestore(engine);
                applyReselectHighlight=engine.CurrentReselectOptionId!=null;singleResume=engine.ReviewRoute!=null;
            }
            preferences.PackFile = file;
            var selected = LibraryBox.Items.OfType<PackChoice>().FirstOrDefault(x => String.Equals(x.File,file,StringComparison.OrdinalIgnoreCase));
            if (selected != null) { selectingLibrary=true;LibraryBox.SelectedItem=selected;selectingLibrary=false; }
            ChapterBox.ItemsSource = pack.Chapters; ChapterBox.SelectedIndex = 0;
            UpdateChapterNavigation();
            PackLabel.Text = PackChoice.ChapterTitle(pack.Title);
            UpdateDraftNotice();
            var packInfo = new FileInfo(file); loadedDraftWrite = packInfo.LastWriteTimeUtc; loadedDraftLength = packInfo.Length;
            if (engine.Current != null) BrowseCurrent();
            Tell("已载入配音包。选择起始台词并确认播放。");
            UpdateState(); loadingPack = false; RefreshHistory(); ChapterResumeLabel.Text=progressStore?.GetSummary(pack)??"";LibraryBox.ToolTip=ChapterResumeLabel.Text.Length>0?"上次位置："+ChapterResumeLabel.Text:"从配音包总目录选择章节";
            if(progressStore?.LastError is {Length:>0} problem) Tell(problem);
            Save();
        }
        catch (Exception ex)
        {
            // 目录中的文件可能在展开后被移走或损坏；读包失败时仍以原引擎的章节为准。
            if (!loadingPack)
            {
                selectingLibrary = true;
                try
                {
                    LibraryBox.SelectedItem = LibraryBox.Items.OfType<PackChoice>().FirstOrDefault(x =>
                        String.Equals(x.File, preferences.PackFile, StringComparison.OrdinalIgnoreCase));
                }
                finally { selectingLibrary = false; }
            }
            Tell("配音包无法打开：" + ex.Message);
        }
        finally
        {
            loadingPack = false;
            if (engine?.Current == null) { CurrentSpeaker.Text = "当前台词"; CurrentText.Text = "请选择起始台词"; }
            RefreshStoryContinuation(); UpdateState();
        }
    }
    void PlayNode(Node? node)
    {
        ClearBranchWaitReason();
        if (engine == null || node == null) return;
        StopListeningForGame();
        if (!applyingMemoryLine && preferences.DialogueGuardEnabled && previousDialogueMode is RunMode.Choice or RunMode.Gap)
        { dialogueDeferredNode = node.Id; HoldDialogue("分支已选择，请先核对路线第一句"); return; }
        dialogueDeferredNode = null;
        StopPreview(); playCalls++;
        try
        {
            if (AutoPlaybackLinePolicy.IsSilentPunctuation(node))
            {
                StopAudio(); playingId = null; PaintPlaying();
                Tell("标点停顿 · " + node.Speaker);
                return;
            }
            var path = engine.Pack.ResolveAudio(node);
            string notice = engine.Pack.AudioNotice(node);
            if (notice.Length > 0) { if (automaticRunning) StopAutomatic("本句录音不可用，自动播放已暂停。"); lastAudioProblem=notice; diagnosticStatus.Text=notice+" · "+node.Speaker+"："+node.Text; Tell(notice + "。可在设置中查看问题或重试本句。"); CaptureBranchPlaybackProblem("本句未能播放：" + notice); return; }
            if (path == null) { if (automaticRunning) StopAutomatic("本句缺少录音，自动播放已暂停。"); CaptureBranchPlaybackProblem("本句缺少可用录音；可手动选择游戏当前续接句。"); return; }
            if (!testUi) StartAudio(path, node.Speaker);
            playingId = node.Id; PaintPlaying();
            Tell("正在播放 · " + node.Speaker);
        }
        catch (Exception ex) { if (automaticRunning) StopAutomatic("本句播放失败，自动播放已暂停。"); lastAudioProblem=ex.Message; diagnosticStatus.Text="播放失败："+ex.Message; Tell("播放失败：" + ex.Message); CaptureBranchPlaybackProblem("本句播放失败：" + ex.Message); }
    }
    void UpdateDraftNotice()
    {
        DraftNotice.Text = engine?.Pack.DraftSummary ?? "";
        DraftNotice.Visibility = engine?.Pack.IsDraft == true ? Visibility.Visible : Visibility.Collapsed;
    }
    void RefreshDraftIfUpdated()
    {
        if (closing || loadingPack || engine?.Pack.IsDraft != true || audioStarting || audio.Playing || previewingHistory ||
            DateTime.UtcNow - lastDraftCheck < TimeSpan.FromSeconds(2)) return;
        lastDraftCheck = DateTime.UtcNow;
        try
        {
            var info = new FileInfo(preferences.PackFile);
            if (!info.Exists || info.LastWriteTimeUtc == loadedDraftWrite && info.Length == loadedDraftLength) return;
            var updated = DesktopPackLoader.Load(preferences.PackFile);
            if (!engine.Pack.TryRefreshDraftAudio(updated, out var reason))
            {
                DraftNotice.Text = engine.Pack.DraftSummary + "\n" + reason;
                return;
            }
            loadedDraftWrite = info.LastWriteTimeUtc; loadedDraftLength = info.Length;
            string? selectedId = (LinesList.SelectedItem as LineRow)?.Node.Id;
            FillLines();
            if (selectedId != null) LinesList.SelectedItem = rows.FirstOrDefault(row => row.Node.Id == selectedId);
            UpdateDraftNotice(); RefreshHistory(); PaintPlaying(); UpdateState();
            Tell("草稿已自动更新，当前位置和分支保持不变。");
        }
        catch (Exception ex)
        {
            // 发布中断或短暂占用不会破坏已经打开的草稿，也不会触发播放。
            DraftNotice.Text = (engine?.Pack.DraftSummary ?? "草稿") + "\n更新暂不可读，继续使用当前版本。";
            Log.Write("draft-refresh", ex.Message);
        }
    }
    // 后台按顺序播放，避免音频设备协商阻塞输入和窗口刷新。
    void StartAudio(string path, string? speaker = null)
    {
        audioSpeaker = speaker;
        audio.Volume = SpeakerVolume.Apply((float)preferences.Volume / 100, preferences.SpeakerVolumes, speaker);
        long request = ++audioRequest;
        bool historyPreview = previewingHistory;
        string outputDevice = preferences.OutputDeviceId;
        audioStarting = true;
        _ = Task.Run(async () =>
        {
            await audioQueue.WaitAsync().ConfigureAwait(false);
            try
            {
                if (request != Interlocked.Read(ref audioRequest)) return;   // 已排了更新的播放请求，这句跳过
                var watch = Stopwatch.StartNew();
                audio.SelectDevice(outputDevice);
                audio.Play(path, request, shouldPlay: () => request == Interlocked.Read(ref audioRequest) && !closing);
                _ = Dispatcher.BeginInvoke(() => { if (!closing) OnAutomaticAudioStarted(request); });
                if (watch.ElapsedMilliseconds >= 80) Log.Write("audio", $"开始播放用了 {watch.ElapsedMilliseconds} 毫秒（已在后台，不再阻塞界面线程）。");
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (closing || request != Interlocked.Read(ref audioRequest)) return;
                    CancelTextAutoAdvance("配音未能开始，本句不会自动点击。");
                    if (automaticRunning) StopAutomatic("配音未能开始，自动播放已暂停。");
                    lastAudioProblem = "无法播放配音：" + ex.Message;
                    diagnosticStatus.Text = lastAudioProblem;
                    if (historyPreview) { previewingHistory = false; historyStatus.Text = "试听失败：" + ex.Message; }
                    Tell("播放失败：" + ex.Message);
                    if (!historyPreview && !ListeningActive) { playingId = null; PaintPlaying(); CaptureBranchPlaybackProblem("本句播放失败：" + ex.Message); }
                });
            }
            finally
            {
                audioQueue.Release();
                if (request == Interlocked.Read(ref audioRequest)) audioStarting = false;
            }
        });
    }
    // 停止也走同一条后台队列，避免界面线程等在“正在打开音频设备”的锁上。
    void StopAudio()
    {
        CancelTextAutoAdvance();
        long request = ++audioRequest;
        audioStarting = false;
        _ = Task.Run(async () =>
        {
            await audioQueue.WaitAsync().ConfigureAwait(false);
            try { if (request == Interlocked.Read(ref audioRequest)) audio.Stop(); } catch { }
            finally { audioQueue.Release(); }
        });
    }
    void EngineChanged()
    {
        if (engine == null) return;
        if (!applyingMemoryLine && !InputBranchRecoveryCurrent())
            CancelInputBranchRecovery("已手动改变播放位置，分支监听已取消。");
        DialogueEngineChanged();
        dismissedInteractionSection=null;
        CancelOcr();
        CurrentSpeaker.Text = engine.Current?.Speaker is { Length: > 0 } speaker ? speaker : "剧情配音";
        CurrentText.Text = engine.Current?.Text ?? "请选择起始台词";
        CurrentLineScroll.ScrollToTop();
        BranchBox.Visibility = engine.Mode == RunMode.Choice && !manualContinuationBrowsing ? Visibility.Visible : Visibility.Collapsed;
        if (engine.Mode == RunMode.Choice)
        {
            BranchList.ItemsSource = engine.AvailableOptions; BranchList.SelectedIndex = 0;
            Tell(engine.Notice);ShowBranchMenu();
        }
        else if (engine.Mode == RunMode.Merge) Tell("已到达共同线汇合点，下一次推进继续共同线。");
        else if (engine.Mode == RunMode.Gap) { Tell(engine.Notice);ShowBranchMenu(); }
        else if (engine.Mode == RunMode.End) Tell("当前所选路线到此结束。可查看本节全部台词，或选择下一小节。");
        if(!engine.MenuWaiting)HideBranchMenu();
        if(engine.Mode is RunMode.Choice or RunMode.Original)routeToast.IsOpen=false;
        RefreshStoryContinuation(); PaintPlaying(); UpdateState(); if(experienceReady && Tabs.SelectedItem==historyTab)RefreshHistory(); Save();
    }
    void UpdateState()
    {
        string status = engine?.Mode switch
        {
            RunMode.Following => expanded ? "选句中 · 跟随暂停" : game == null || !Native.IsWindow(game.Handle) ? connectionNotice : Native.GetForegroundWindow() != game.Handle ? "游戏在后台" : "跟随中",
            RunMode.Paused => "跟随已暂停", RunMode.Choice => "分支待选", RunMode.Merge => "返回共同线", RunMode.Gap => "待手动续接", RunMode.Original => "游戏原声时段", RunMode.End => "所选路线结束", _ => "待开始"
        };
        if (ListeningActive) status = "听书模式";
        else if (automaticRunning) status = automaticWaiting ? "自动 · 等待推进" : "共同线自动播放";
        else if (inputBranchRecovery != null) status = "等待游戏支线对白";
        else if (textArmed) status = GameIsForeground() ? "文字跟随中" : "后台文字跟随中";
        if (!textArmed && preferences.DialogueGuardEnabled && engine?.Mode is RunMode.Following or RunMode.Merge)
            status = dialogueHeld ? "请核对当前句" : dialogueChecking ? "核对对白中" : status;
        if (currentStoryEnding && !ListeningActive) status = "当前路线末句 · " + status;
        if (unconfirmedStoryEnding && !ListeningActive) status = "后续待确认";
        StateText.Text = status;
        bool choice = engine?.Mode == RunMode.Choice;
        bool route = engine?.Mode != RunMode.Original && engine?.Current is { Kind: "line" } current && !string.IsNullOrEmpty(current.PathId);
        bool highlight = choice || route || engine?.Mode == RunMode.Gap;
        CurrentCard.BorderBrush = highlight ? LineRow.BranchAccent : playingId != null ? Theme.Brush("PlayingAccent") : Theme.Brush("Stroke");
        CurrentCard.Background = choice ? LineRow.BranchFill : (Brush)FindResource("CurrentCardNormal");
        StateText.Foreground = highlight ? LineRow.BranchAccent : (Brush)FindResource("MutedText");
        if (route) StateText.Text = "分支台词 · " + status;
        BallView.BorderBrush = highlight ? LineRow.BranchAccent : (Brush)FindResource("NormalAccent");
        BallView.Background = choice ? LineRow.BranchFill : (Brush)FindResource("BallNormal");
        BallGlyph.Foreground = BallState.Foreground = highlight ? LineRow.BranchAccent : (Brush)FindResource("NormalAccent");
        BallState.Text = status.Length > 6 ? status[..6] : status;
        if (engine?.Pack.IsDraft == true) BallState.Text = "草稿·" + status[..Math.Min(3, status.Length)];
        BallGlyph.Text = engine?.Mode switch { RunMode.Choice => "选", RunMode.Original => "原", RunMode.Gap => "?", RunMode.Paused => "Ⅱ", _ => audio.Playing ? "♫" : "声" };
        BallView.ToolTip = status + " · 单击打开台词 · " + KeyName("panel") + " 展开";
        if (engine?.Pack.IsDraft == true) BallView.ToolTip = engine.Pack.DraftSummary + "\n" + BallView.ToolTip;
        UpdateExperienceStatus();
        PublishMenuCapture(); UpdateDialogueMonitor();
    }
    void PaintPlaying()
    {
        foreach (var row in rows) row.Playing = row.Node.Id == playingId;
    }
    void FillLines()
    {
        if (engine == null || SectionBox.SelectedItem is not Section section) return;
        string filter = SearchBox.Text.Trim();
        string? selected = (LinesList.SelectedItem as LineRow)?.Node.Id;
        var terms=filter.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(SearchNormalize).Where(x=>x.Length>0).ToList();
        bool chapter=SearchChapterBox.IsChecked==true;
        rows = engine.Pack.Nodes.Where(n => !n.Archived && (chapter || n.SectionId == section.Id) && n.Kind == "line" && (engine.CanLocate(n) || terms.Count>0 || showAllSectionLines.IsChecked==true && n.Kind=="line" && n.SectionId==section.Id) && terms.All(t=>SearchNormalize(n.Speaker+n.Text).Contains(t))).Select(n => new LineRow(n, engine.Pack)).ToList();
        if(terms.Count>0 || showAllSectionLines.IsChecked==true)foreach(var row in rows)
        {
            var local=engine.Pack.Nodes.Where(n=>!n.Archived&&n.SectionId==row.Node.SectionId&&n.PathId==row.Node.PathId&&n.Kind=="line").ToList();
            int i=local.IndexOf(row.Node);string before=i>0?local[i-1].Text:"",after=i>=0&&i+1<local.Count?local[i+1].Text:"";
            row.Context=NodeLocation(row.Node.Id)+(engine.CanLocate(row.Node)?"":" · 需先确认所属路线")+"\n前："+before+"\n后："+after;
        }
        LinesList.ItemsSource = rows;
        LinesList.SelectedItem = rows.FirstOrDefault(r => r.Node.Id == selected) ?? rows.FirstOrDefault(r => r.Node.Id == engine.CurrentId) ?? rows.FirstOrDefault();
        PaintPlaying();
    }
    void BrowseCurrent()
    {
        if (engine?.Current == null) return;
        var chapter = engine.Pack.Chapters.First(c => c.Sections.Any(s => s.Id == engine.Current.SectionId));
        ChapterBox.SelectedItem = chapter;
        SectionBox.SelectedItem = chapter.Sections.First(s => s.Id == engine.Current.SectionId);
        FillLines();
        string? nearby = showAllSectionLines.IsChecked == true && engine.Mode == RunMode.Gap
            ? engine.History.LastOrDefault(v => engine.Pack.ById.TryGetValue(v.NodeId, out var n) && n.SectionId == engine.Current.SectionId)?.NodeId : engine.CurrentId;
        var item = rows.FirstOrDefault(r => r.Node.Id == nearby) ?? rows.FirstOrDefault(r => r.Node.Id == engine.CurrentId);
        if (item != null)
        {
            LinesList.SelectedItem = item; LinesList.UpdateLayout(); LinesList.ScrollIntoView(item);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>{if(ReferenceEquals(LinesList.SelectedItem,item)){LinesList.UpdateLayout();LinesList.ScrollIntoView(item);}});
        }
    }
    void Expand(TabItem? tab = null)
    {
        HoldTextAutoForFocus();
        if (automaticRunning) StopAutomatic("已展开面板，自动播放暂停。重新开启会先定位当前句。");
        dismissedInteractionSection=null;
        HideBranchMenu();
        ShowInTaskbar = true;
        expanded = true; SetExpandedPanelSize(preferences.PanelWidth,preferences.PanelHeight);
        ApplyCompactLayout();
        BallView.Visibility = Visibility.Collapsed; StripView.Visibility=Visibility.Collapsed; PanelView.Visibility = Visibility.Visible;
        if (tab != null) Tabs.SelectedItem = tab;
        ClampPosition(); Activate();
        if (Tabs.SelectedItem == LocateTab) CandidatesList.Focus();
        else if (Tabs.SelectedItem == SettingsTab) WindowBox.Focus();
        else if (Tabs.SelectedItem == listeningTab) listeningPlay.Focus();
        else if (engine?.Mode == RunMode.Choice && !manualContinuationBrowsing) BranchList.Focus(); else LinesList.Focus();
        UpdateState();
        UpdateClickZone();
    }
    void Collapse(bool restoreFocus=true)
    {
        if(manualContinuationBrowsing){manualContinuationBrowsing=false;ApplyCompactLayout();}
        editingClickZone = false;
        if(engine?.Mode==RunMode.Original)resumeOriginalRequested=false;
        ShowInTaskbar = false;
        StopPreview(); CancelOcr(); expanded = false;
        PanelView.Visibility = Visibility.Collapsed; ApplyCompactView();
        ClampPosition(); Save();
        if (restoreFocus && game != null && Native.IsWindow(game.Handle)) Native.SetForegroundWindow(game.Handle);
        UpdateState();
        UpdateClickZone();
    }
    public void Reveal()
    {
        if (closing) return;
        CancelOcr();
        Show(); WindowState = WindowState.Normal;
        Expand(ListeningActive ? listeningTab : engine == null ? SettingsTab : TextFollowing ? gameTextTab : StoryTab);
        BrowseCurrent();
        Log.Write("startup", "再次打开：已显示现有播放器，没有启动新实例或播放音频。");
    }
    void HideBranchMenu(){if(branchMenu.IsVisible)branchMenu.Dismiss();PublishMenuCapture();}
    void ShowBranchMenu(bool anchors=false)
    {
        if(engine?.MenuWaiting!=true)return;
        if(expanded)Collapse(false);
        int selected=preferences.MenuSelections.GetValueOrDefault(engine.CurrentId!,0);
        if(applyReselectHighlight && engine.CurrentReselectOptionId!=null) {int previous=engine.AvailableOptions.FindIndex(o=>o.Id==engine.CurrentReselectOptionId);if(previous>=0)selected=previous;}
        applyReselectHighlight=false;
        branchMenu.Present(engine,Left+70,Top,selected,anchors);
        branchMenu.SetContinuationFeedback(engine,CurrentBranchWaitReason());
        PublishMenuCapture();
    }
    void OpenStory()
    {
        if (ListeningActive) { Expand(listeningTab); return; }
        if (TextFollowing) { Expand(gameTextTab); return; }
        if(dismissedInteractionSection!=null && engine?.Mode!=RunMode.Original && engine?.Pack.Chapters.SelectMany(c=>c.Sections).Any(s=>s.Id==dismissedInteractionSection)==true){ShowInteractionNavigation(allInteractionMenus,dismissedInteractionSection);return;}
        if(engine?.Current?.Kind is "choice" or "gap" && engine.Mode!=RunMode.Original){engine.OpenMenu();ShowBranchMenu(anchors:engine.Current.MenuType=="exclusive");}
        else {Expand(StoryTab);BrowseCurrent();}
    }
    void ConfirmSmallBranch()
    {
        if(engine==null)return;
        if(branchMenu.IsAnchorView)
        {
            var offer=branchMenu.AnchorOffer;var card=branchMenu.SelectedAnchor;
            if(offer==null || card==null){branchMenu.ShowNavigationNotice("请选择游戏当前显示的完整台词。");return;}
            if(!BranchAnchorPolicy.TryResolve(engine,offer,card.Id,out var line,out string reason))
            {branchMenu.ShowNavigationNotice(reason);return;}
            CancelInputBranchRecovery("已按游戏当前首句确认路线。");StopListeningForGame();StopPreview();CancelOcr();
            if(engine.CurrentId!=card.MenuId && !engine.OpenGameMenu(card.MenuId,offer.SectionId))
            {branchMenu.ShowNavigationNotice(engine.NavigationError);return;}
            int option=engine.AvailableOptions.FindIndex(o=>o.Id==card.OptionId);
            if(option<0 || line==null){branchMenu.ShowNavigationNotice("该首句已失效，请重新打开菜单。");return;}
            if(engine.AvailableOptions[option].TargetId==line.Id)engine.SelectBranch(option);
            else if(!engine.ConfirmGameLine(line.Id)){branchMenu.ShowNavigationNotice(engine.NavigationError);return;}
            DialoguePositionConfirmed();FillLines();
            if(engine.CurrentId==line.Id && engine.Mode==RunMode.Following)
            {singleResume=false;HideBranchMenu();Collapse(false);Tell("已对齐："+line.Speaker+"："+line.Text+"；先播放本句，继续原跟随。");}
            return;
        }
        CancelInputBranchRecovery("已手动选择路线，分支监听已取消。");
        StopListeningForGame();
        if(branchMenu.IsNavigation){ConfirmInteractionNavigation();return;}
        int selected=branchMenu.Options.SelectedIndex;
        if(engine.Mode==RunMode.Gap)engine.SelectContinuation(selected);else engine.SelectBranch(selected);FillLines();
        if(engine.Mode==RunMode.Following)
        {
            singleResume=false;HideBranchMenu();Collapse(false);Tell(engine.Notice);
            routeToast.PlacementTarget=BallView;routeToast.Content=engine.Notice;routeToast.IsOpen=true;
            var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
            timeout.Tick+=(_,_)=>{timeout.Stop();routeToast.IsOpen=false;};timeout.Start();
        }
    }
    void ClampPosition()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var area = SystemParameters.WorkArea;
        // 恢复时至少保证窗口可见；拖拽时使用系统虚拟桌面范围。
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        Left = Math.Clamp(double.IsFinite(Left) ? Left : 60, left, Math.Max(left, left + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(double.IsFinite(Top) ? Top : 100, top, Math.Max(top, top + SystemParameters.VirtualScreenHeight - Height));
    }
    // 只有点在「下一句」热区里才跟着推进。鼠标走 Raw Input 消息，不像低层键盘钩子那样会被
    // 界面线程拖到超时，也完全不干扰游戏自己的点击（热区平时是穿透的）。
    void HandleMouseGlobal(ObservedMouseInput input)
    {
        if (chapterPicker?.IsVisible == true || sectionPicker?.IsVisible == true || experienceDialog?.IsVisible == true) return;
        bool inGame = game != null && input.Foreground == game.Handle && Native.GetForegroundWindow() == game.Handle &&
            !expanded && DesktopAdvanceInput.IsUnobscured(game.Handle, input.X, input.Y);
        ObserveTextAutoInput(inGame, "检测到游戏内手动点击，本句自动点击已取消。");
        if (ListeningActive) return;
        if (automaticRunning) { StopAutomatic("检测到手动点击，已暂停自动播放，请核对当前句。"); return; }
        BeginMouseFollow(input);
    }
    // 热区保持穿透；仅显式调整位置时允许拖动，切回游戏不需要等定时器。
    void UpdateClickZone()
    {
        if (closing) return;
        if (!preferences.ClickZoneEnabled) { if (clickZone.IsVisible) clickZone.Hide(); return; }
        if (!clickZone.IsVisible)
        {
            clickZone.PlaceAt(preferences.ClickZoneLeft, preferences.ClickZoneTop);
            clickZone.Show();
        }
        clickZone.SetDraggable(editingClickZone && expanded);
    }
    bool GameIsForeground() => !expanded && game != null && Native.IsWindow(game.Handle) && Native.GetForegroundWindow() == game.Handle;
    void HandleGlobal(Key key, IntPtr foreground, long timestamp = 0, ModifierKeys modifiers = ModifierKeys.None, uint? messageTime = null)
    {
        if (chapterPicker?.IsVisible == true || sectionPicker?.IsVisible == true || experienceDialog?.IsVisible == true) return;
        bool windowSwitch = modifiers != ModifierKeys.None || key is Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab;
        ObserveTextAutoInput(!windowSwitch && game != null && foreground == game.Handle && GameIsForeground(), "检测到游戏内手动按键，本句自动点击已取消。");
        if (closing || recordingAction != null || Native.GetForegroundWindow() != foreground) return;
        if (IsLocallyHandledShortcut(key, messageTime)) return;
        // 播放器自身的按键只由 WPF 路由，避免同一次本地按键又从 Raw Input 执行一遍。
        if (foreground == new WindowInteropHelper(this).Handle) return;
        if (automaticRunning) { StopAutomatic("检测到手动按键，已暂停自动播放，请核对当前句。"); if(modifiers==ModifierKeys.None && KeyBindingWindow.Matches(preferences.Keys["panel"],key)) OpenStory(); return; }
        if (modifiers != ModifierKeys.None) return;
        string? action = preferences.Keys.FirstOrDefault(k => KeyBindingWindow.Matches(k.Value, key)).Key;
        if (ListeningActive) { if(foreground == game?.Handle) HandleListeningShortcut(action); return; }
        if(KeyTesting && foreground==game?.Handle){HandleTestKey(key);return;}
        if(locating)return;
        // OCR 的设置检查不能被“未绑定”或“面板已展开”的跟随规则吞掉。
        // 软件自己的按键由 PreviewKeyDown 处理，避免同一次 F9 请求两次。
        if (action == "ocr")
        {
            if (foreground == game?.Handle || Native.IsGameWindow(foreground, preferences.GameExecutablePath)) _ = Locate();
            return;
        }
        if (game == null || foreground != game.Handle) return;
        HandleGameShortcut(action, true, timestamp);
    }
    void HandlePreviewKey(object sender, KeyEventArgs e)    {
        if (!ready) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if(HandleTestKey(key)){e.Handled=true;return;}
        if (recordingAction != null)
        {
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (expanded && RouteSettingsControlKey(e, key)) return;
        if(expanded && IsTextInputSource(e.OriginalSource))
        {
            if(key==Key.Enter && SearchBox.IsKeyboardFocusWithin){e.Handled=true;LinesList.Focus();return;}
            if(key is not (>=Key.F1 and <=Key.F24) && key!=Key.Escape)return;
        }
        if(e.IsRepeat && (key==Key.Escape || preferences.Keys.Values.Any(value => KeyBindingWindow.Matches(value, key)))){e.Handled=true;return;}
        string? action = preferences.Keys.FirstOrDefault(k => KeyBindingWindow.Matches(k.Value, key)).Key;
        if (ListeningActive && (!expanded || Tabs.SelectedItem == listeningTab))
        { if (key == Key.Escape) { e.Handled=true; Collapse(false); } else if(HandleListeningShortcut(action)) e.Handled=true; return; }
        if (action == "ocr") { e.Handled = true; _ = Locate(); return; }
        if (!expanded) return;
        if (key == Key.Escape) { e.Handled = true; Collapse(!ListeningActive); return; }
        if(HandleGameShortcut(action)) {e.Handled=true;return;}
        if ((key == Key.Up || key == Key.Down) && Tabs.SelectedItem == LocateTab && !CandidatesList.IsKeyboardFocusWithin) { CandidatesList.Focus(); }
        if (key == Key.Enter && !e.IsRepeat)
        {
            if(Tabs.SelectedItem==historyTab){if(historyList.IsKeyboardFocusWithin){PreviewHistory();e.Handled=true;} return;}
            if(LibraryBox.IsKeyboardFocusWithin){if(ChapterBox.IsVisible)ChapterBox.Focus();else sectionPickerButtons[SectionBox].Focus();e.Handled=true;}
            else if (ChapterBox.IsKeyboardFocusWithin) { sectionPickerButtons[SectionBox].Focus(); e.Handled = true; }
            else if (BranchList.IsKeyboardFocusWithin) { ConfirmBranch(); e.Handled = true; }
            else if (Tabs.SelectedItem == LocateTab) { ConfirmCandidate(); e.Handled = true; }
            else if (Tabs.SelectedItem == StoryTab && e.OriginalSource is not Button) { ConfirmLine(); e.Handled = true; }
        }
    }
    void ConfirmLine()
    {
        if (engine == null || LinesList.SelectedItem is not LineRow row) return;
        CancelInputBranchRecovery("已手动选择台词，分支监听已取消。");
        StopAutomatic("已手动选择台词，自动播放关闭。", false); StopListeningForGame();
        if(showAllSectionLines.IsChecked == true && row.Node.Kind == "line" && !engine.Allowed(row.Node))
        {
            StopPreview(); CancelOcr();
            if(!engine.ConfirmGameLine(row.Node.Id,resumeOriginalRequested)){Tell(engine.NavigationError);return;}
            singleResume=false;resumeOriginalRequested=false;DialoguePositionConfirmed();
            if(engine.Mode==RunMode.Following)Collapse();
            return;
        }
        if(!engine.CanLocate(row.Node)){GuideUnselectedRoute(row.Node);return;}
        StopPreview(); CancelOcr();
        try { if(singleResume || (engine.ReviewRoute!=null && !engine.Allowed(row.Node)))engine.CommitSingle(row.Node.Id);else engine.Commit(row.Node.Id); singleResume=false;DialoguePositionConfirmed();if (engine.Mode == RunMode.Following) Collapse(); } catch (Exception ex) { Tell(ex.Message); }
    }
    void ConfirmBranch()
    {
        CancelInputBranchRecovery("已手动选择路线，分支监听已取消。");
        StopListeningForGame();
        CancelOcr(); engine?.SelectBranch(BranchList.SelectedIndex); FillLines();
        if (engine?.Mode == RunMode.Following) Collapse();
    }
    void Original()
    {
        if (engine == null) return;
        StopListeningForGame();
        singleResume=false;
        CancelOcr();
        if (engine.Mode != RunMode.Original) { resumeOriginalRequested=false;engine.EnterOriginal(); Tell("游戏原声时段：配音与跟随已暂停，再按 " + KeyName("original") + " 选择续接台词。"); }
        else { resumeOriginalRequested=true;Expand(StoryTab); BrowseCurrent(); Tell("选择要续接的具体台词并确认。Esc 取消后仍保持原声模式。"); }
    }
    async Task Locate()
    {
        if (closing || locating) return;
        // 主动定位接管当前操作，旧鼠标松开不能再暂停引擎并取消这次识别。
        ResetFollowInputSession(); ResetDialogueObservation();
        if (automaticRunning || automaticPendingOwner != null) StopAutomatic("重新定位，自动播放已暂停。");
        StopListeningForGame();
        if (engine == null) { Expand(SettingsTab); Tell("请先打开配音包，再选择需要定位的小章节。"); return; }
        var section = expanded ? SectionBox.SelectedItem as Section : null;
        section ??= engine.Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == engine.Current?.SectionId);
        section ??= SectionBox.SelectedItem as Section;
        if (section == null) { Expand(StoryTab); Tell("请先选择要定位的小章节，无需先播放台词。"); return; }
        OcrScope.Text = "查找范围：" + section.Title;
        if (!preferences.OcrEnabled)
        {
            Expand(LocateTab);
            OcrStatus.Text = "OCR 尚未启用。点击“启用并定位”后，只识别这一次画面。";
            Tell(OcrStatus.Text); return;
        }
        if (game == null || !Native.IsWindow(game.Handle))
        {
            var games = Native.Windows().Where(w => Native.IsGameWindow(w.Handle, preferences.GameExecutablePath)).ToList();
            if (games.Count == 1) BindGame(games[0]);
            else { Expand(SettingsTab); Tell("请先打开战双，并在这里绑定游戏窗口，再按定位键。"); return; }
        }
        CancelOcr();
        locating = true;
        UpdateDialogueMonitor();
        engine.SuspendSound();
        int generation = ++ocrGeneration;
        var tokenSource = ocrCancellation = new CancellationTokenSource();
        var owner = engine;
        string sectionId = section.Id;
        string? previousId = owner.CurrentId;
        int callsBefore = playCalls;
        var recognized = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        Log.Write("ocr-request", "开始定位，小章节=" + sectionId);
        OcrPreview.Text = "";
        string? imageFile = null;
        try
        {
            if (Native.IsIconic(game!.Handle)) throw new InvalidOperationException("游戏已最小化，请先恢复游戏画面后再定位。");
            var capture = await CaptureGameForOcr(game.Handle, tokenSource.Token);
            imageFile = capture.Path;
            if (lastSize != (capture.Width, capture.Height)) lastRegion = null;
            lastSize = (capture.Width, capture.Height);
            Expand(LocateTab); CandidatesList.ItemsSource = null;
            OcrStatus.Text = ocr.Running ? "正在定位台词…" : "正在加载 OCR 组件，首次可能稍慢…";
            var candidates = new List<MatchCandidate>();
            if (lastRegion != null)
            {
                var response = await ocr.Recognize(imageFile, lastRegion, "original", tokenSource.Token);
                recognized.AddRange(response.Blocks.Select(b => b.Text));
                candidates = Matcher.Find(owner, sectionId, response.Blocks);
            }
            if (candidates.Count == 0 || candidates[0].Score < .85)
            {
                var response = await ocr.Recognize(imageFile, null, "original", tokenSource.Token);
                recognized.AddRange(response.Blocks.Select(b => b.Text));
                candidates = MergeMatches(candidates, Matcher.Find(owner, sectionId, response.Blocks));
            }
            if (candidates.Count == 0 || candidates[0].Score < .85)
                foreach (var variant in new[] { "invert", "contrast" })
                {
                    var response = await ocr.Recognize(imageFile, null, variant, tokenSource.Token);
                    recognized.AddRange(response.Blocks.Select(b => b.Text));
                    candidates = MergeMatches(candidates, Matcher.Find(owner, sectionId, response.Blocks));
                    if (candidates.Count > 0 && candidates[0].Score >= .85) break;
                }
            if (tokenSource.IsCancellationRequested || generation != ocrGeneration || owner != engine) return;
            CandidatesList.ItemsSource = candidates; CandidatesList.SelectedIndex = candidates.Count > 0 ? 0 : -1;
            OcrPreview.Text = string.Join(Environment.NewLine, recognized.Distinct());
            CandidatesList.Focus();
            OcrStatus.Text = candidates.Count > 0 ? "↑↓选择并确认：台词定位后播放；分支选项只打开菜单。包含本节尚未选择的路线。" : recognized.Count > 0 ? "已读到文字，本节未找到可靠对应。可手动选分支，或检查小章节；下方可查看识别文字。" : "画面没有识别到可用文字。请等待台词完整显示，再点定位。原进度未改变。";
            Tell(OcrStatus.Text);
            Log.Write("ocr-result", $"文字块={recognized.Count}，候选={candidates.Count}，耗时={stopwatch.ElapsedMilliseconds}ms，原进度保留={previousId == owner.CurrentId && callsBefore == playCalls}");
        }
        catch (OperationCanceledException) { if (generation == ocrGeneration) OcrStatus.Text = "识别已取消或超时，手动播放仍可使用。"; }
        catch (Exception ex)
        {
            if (generation == ocrGeneration)
            {
                Show(); Expand(LocateTab); OcrStatus.Text = ex.Message;
                Tell("定位未完成：" + ex.Message); Log.Write("ocr-error", ex.ToString());
            }
        }
        finally
        {
            if (!closing && !IsVisible) Show();
            locating = false;
            if (imageFile != null) try { File.Delete(imageFile); } catch { }
            if (ReferenceEquals(ocrCancellation, tokenSource)) ocrCancellation = null;
            tokenSource.Dispose();
        }
    }
    async Task<(string Path, int Width, int Height)> CaptureGameForOcr(IntPtr handle, CancellationToken cancellation)
    {
        bool restoreMenu = branchMenu.IsVisible;
        try
        {
            branchMenu.Hide(); PublishMenuCapture(); Hide();
            Native.SetForegroundWindow(handle);
            await Task.Delay(150, cancellation);
            return Native.Capture(handle, Path.Combine(Log.DataDir, "capture"));
        }
        finally
        {
            if (!closing)
            {
                Show();
                if (restoreMenu && engine?.MenuWaiting == true && !expanded) branchMenu.Show();
                PublishMenuCapture();
            }
        }
    }
    static List<MatchCandidate> MergeMatches(List<MatchCandidate> first, List<MatchCandidate> second) => first.Concat(second).GroupBy(c => c.Node.Id).Select(g => g.OrderByDescending(c => c.Score).First()).OrderByDescending(c => c.Score).Take(3).ToList();
    void ConfirmCandidate()
    {
        if (engine == null || CandidatesList.SelectedItem is not MatchCandidate candidate) return;
        bool startAutomatic = automaticPendingOwner == engine && DateTime.UtcNow <= automaticPendingDeadline;
        automaticPendingOwner = null; StopListeningForGame();
        lastRegion = candidate.Region;
        StopPreview(); CancelOcr();
        bool located=candidate.Node.Kind=="choice"?engine.OpenGameMenu(candidate.Node.Id,candidate.Node.SectionId):engine.ConfirmGameLine(candidate.Node.Id,resumeOriginalRequested);
        if(!located){Tell(engine.NavigationError);OcrStatus.Text=engine.NavigationError;return;}
        singleResume=false;resumeOriginalRequested=false; DialoguePositionConfirmed(); BrowseCurrent(); Collapse();
        if (startAutomatic) { ocr.Stop(); StartAutomaticAtConfirmedLine(); }
    }
    void CancelOcr()
    {
        ocrGeneration++; ocrCancellation?.Cancel();
        if (CandidatesList != null) CandidatesList.ItemsSource = null;
    }
    void BuildKeys()
    {
        KeysPanel.Children.Clear(); keyButtons.Clear();
        foreach (var action in actionLabels)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            string label = Enum.TryParse<Key>(preferences.Keys[action.Key],out var key) ? KeyBindingWindow.Display(key) : preferences.Keys[action.Key];
            var button = new Button { Content = label + "  · 修改", Width = 145, Tag = action.Key };
            AutomationProperties.SetAutomationId(button, "Key-" + action.Key); keyButtons[action.Key] = button;
            DockPanel.SetDock(button, Dock.Right);
            button.Click += (_, _) => EditKey((string)button.Tag);
            panel.Children.Add(button); panel.Children.Add(new TextBlock { Text = action.Value, VerticalAlignment = VerticalAlignment.Center }); KeysPanel.Children.Add(panel);
        }
    }
    string KeyName(string action) => Enum.TryParse<Key>(preferences.Keys[action],out var key) ? KeyBindingWindow.Display(key) : preferences.Keys[action];
    void RefreshKeyHints()
    {
        CollapseButton.Content = "收起 " + KeyName("panel");
        ReselectButton.Content="重选 "+KeyName("reselect");HistoryButton.Content="历史 "+KeyName("history");
        InteractionsButton.Content="手动选分支 "+KeyName("interactions");
        LocateButton.Content = preferences.OcrEnabled ? "定位当前画面（" + KeyName("ocr") + "）" : "启用并定位";
        OcrInstructions.Text = "先选小章节，再按 " + KeyName("ocr") + " 或点击定位。截图时会切回游戏，确认候选后才播放。";
    }
    void EditKey(string action)
    {
        if (keyEditor != null) return;
        CancelOcr(); recordingAction = action;
        try
        {
            keyEditor = new KeyBindingWindow(this, action, preferences.Keys, actionLabels);
            if (keyEditor.ShowDialog() == true && keyEditor.SelectedKey is Key key)
            {
                preferences.Keys[action] = key.ToString(); Save();
                RefreshKeyHints();
                Tell(actionLabels[action] + "已改为 " + KeyBindingWindow.Display(key) + "，已保存。");
            }
            else Tell("已取消，按键保持不变。");
        }
        finally { keyEditor = null; recordingAction = null; BuildKeys(); keyButtons[action].Focus(); }
    }
    void RefreshWindows()
    {
        var windows = Native.Windows().OrderByDescending(w => GameMatchesPreference(w) || w.ProcessName.Contains("PGR", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("战双")).ToList();
        WindowBox.ItemsSource = windows; WindowBox.SelectedIndex = 0;
        if (game == null && preferences.GameProcess.Length > 0)
        {
            var matches = windows.Where(GameMatchesPreference).ToList();
            if (matches.Count == 1) { game = matches[0]; GameLabel.Text = GameBindingLabel(game); }
        }
    }
    string GameBindingLabel(GameWindow window)
    {
        var playerHandle = new WindowInteropHelper(this).Handle;
        if (Native.IsElevated(window.Handle) == true && Native.IsElevated(playerHandle) == false)
            return window + " · 游戏以管理员权限运行；请关闭播放器后，以管理员身份打开 PgrVoice.exe";
        return window.ToString();
    }
    void BindGame(GameWindow window)
    {
        if (preferences.GameExecutablePath.Length > 0 && !GameMatchesPreference(window))
        { Tell("这个窗口不属于所选游戏目录。请选择该目录启动的战双，或先清除目录选择。"); return; }
        game = window; connectionNotice="已连接游戏"; preferences.GameProcess = window.ProcessName; GameLabel.Text = GameBindingLabel(window);
        ResetFollowInputSession(); ResetDialogueObservation();
        lastRegion = null; Save(); Tell("已连接游戏：" + window.Title);
    }
    void ChapterChanged(object sender, SelectionChangedEventArgs e) { if (!ready) return; CancelOcr(); if (ChapterBox.SelectedItem is Chapter c) { SectionBox.ItemsSource = c.Sections; SectionBox.SelectedIndex = 0; } }
    void LibraryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && !selectingLibrary && LibraryBox.SelectedItem is PackChoice choice &&
            !String.Equals(preferences.PackFile,choice.File,StringComparison.OrdinalIgnoreCase)) LoadPack(choice.File);
    }
    void SetLibrary(string folder)
    {
        var parent = LibraryPaths.Root(folder);
        var candidates = LibraryPaths.Packs(folder);
        if (candidates.Count==0 && File.Exists(Path.Combine(folder,"pack.json"))) candidates.Add(Path.Combine(folder,"pack.json"));
        chapterLibraryFolder = parent;
        selectingLibrary=true;
        LibraryBox.ItemsSource=GroupedChapters(candidates.Select(ReadPackChoice));
        selectingLibrary=false;
    }
    void SectionChanged(object sender, SelectionChangedEventArgs e) { if (!ready) return; CancelOcr(); SearchBox.Clear(); FillLines(); HeaderChapter.Text = SectionBox.SelectedItem is Section section ? SectionDisplayTitle(section.Id) : (engine != null ? PackChoice.ChapterTitle(engine.Pack.Title) : "选择章节，继续你的故事"); }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (ready) FillLines(); }
    void LineSelected(object sender, SelectionChangedEventArgs e)
    {
        // 浏览绝不调用播放；只有下面明确按游戏画面确认后才同步位置。
        if(ConfirmPlayButton!=null)ConfirmPlayButton.Content=showAllSectionLines.IsChecked==true &&
            LinesList.SelectedItem is LineRow row && row.Node.Kind=="line" && engine?.Allowed(row.Node)==false
            ? "游戏正显示这句 · 确认并播放  ↵" : "确认播放选中台词                         ↵";
    }
    void TabChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == Tabs && Tabs.SelectedItem != LocateTab) CancelOcr(); }
    void OpenPackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择单章配音包或包含各章配音包的总目录" };
        if (dialog.ShowDialog(this) != true) return;
        SetLibrary(dialog.FolderName);
        var current=Path.Combine(dialog.FolderName,"pack.json");
        if(File.Exists(current)) LoadPack(current);
        else if(LibraryBox.Items.Count>0) { LibraryBox.SelectedIndex=0;Tell($"已找到 {LibraryBox.Items.Count} 章配音包，可在剧情页切换章节。"); }
        else Tell("没有找到配音包：请选择含 pack.json 的单章目录或其总目录。");
    }
    async Task RunLibraryUiTest(string[] args)
    {
        var report=Path.Combine(Log.DataDir,"library-ui-test.txt");
        try
        {
            int arg=Array.IndexOf(args,"--library");
            if(arg<0||arg+1>=args.Length)throw new Exception("缺少测试配音包总目录");
            SetLibrary(args[arg+1]);
            if(LibraryBox.Items.Count!=40)throw new Exception($"只找到 {LibraryBox.Items.Count} 章");
            int before=playCalls;
            LibraryBox.SelectedIndex=0;await Task.Delay(80);
            if(engine?.Pack.Id!="pgr-ch03")throw new Exception("没有进入第3章");
            LibraryBox.SelectedIndex=39;await Task.Delay(80);
            if(engine?.Pack.Id!="pgr-ch42"||playCalls!=before)throw new Exception("切换第42章失败或误播");
            File.WriteAllText(report,"PASS: 40章总目录按需切换第3和42章，切换保持静音\n");
        }
        catch(Exception ex){File.WriteAllText(report,"FAIL: "+ex+"\n");}
        Close();
    }
    void BindWindowClick(object sender, RoutedEventArgs e) { if (WindowBox.SelectedItem is GameWindow w) BindGame(w); }
    void RefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindows();
    void OcrEnabledChanged(object sender, RoutedEventArgs e) { if (!ready) return; preferences.OcrEnabled = OcrEnabledBox.IsChecked == true; RefreshKeyHints(); if (!preferences.OcrEnabled) { CancelOcr(); ocr.Stop(); } Save(); }
    async void LocateClick(object sender, RoutedEventArgs e) { if (!preferences.OcrEnabled) OcrEnabledBox.IsChecked = true; await Locate(); }
    void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!ready) return; preferences.Volume = e.NewValue; ApplySpeakerVolumes(); VolumeLabel.Text = $"{e.NewValue:0}%"; Save(); }
    void PreviousClick(object sender, RoutedEventArgs e) { PrepareGamePlaybackAction(); engine?.Previous(); DialoguePositionConfirmed(); BrowseCurrent(); }
    void NextClick(object sender, RoutedEventArgs e) { PrepareGamePlaybackAction(); engine?.Next(true); DialoguePositionConfirmed(); BrowseCurrent(); }
    void ReplayClick(object sender, RoutedEventArgs e) { PrepareGamePlaybackAction(); engine?.Replay(); }
    void PauseClick(object sender, RoutedEventArgs e) { PrepareGamePlaybackAction(); engine?.TogglePause(); }
    void OriginalClick(object sender, RoutedEventArgs e) => Original();
    void ConfirmLineClick(object sender, RoutedEventArgs e) => ConfirmLine();
    void LineDoubleClick(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(LinesList, e.OriginalSource as DependencyObject) is ListBoxItem) ConfirmLine(); }
    void ConfirmBranchClick(object sender, RoutedEventArgs e) => ConfirmBranch();
    void BranchDoubleClick(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(BranchList, e.OriginalSource as DependencyObject) is ListBoxItem) ConfirmBranch(); }
    void ConfirmCandidateClick(object sender, RoutedEventArgs e) => ConfirmCandidate();
    void CandidateDoubleClick(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(CandidatesList, e.OriginalSource as DependencyObject) is ListBoxItem) ConfirmCandidate(); }
    void CancelOcrClick(object sender, RoutedEventArgs e) { StopAutomatic("已取消自动播放准备。", false); CancelOcr(); ocr.Stop(); OcrStatus.Text = "已取消，原进度保留。"; }
    void CollapseClick(object sender, RoutedEventArgs e) => Collapse();
    void QuitClick(object sender, RoutedEventArgs e) => Close();
    void OpenStateClick(object sender, RoutedEventArgs e) { Directory.CreateDirectory(Log.DataDir); Process.Start(new ProcessStartInfo("explorer.exe", Log.DataDir) { UseShellExecute = true }); }
    void BallDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); startLeft = Left; startTop = Top; dragged = false; BallView.CaptureMouse(); }
    void BallMove(object sender, MouseEventArgs e)
    {
        if (dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition(this); double dx = p.X - dragStart.Value.X, dy = p.Y - dragStart.Value.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 4) dragged = true;
        if (dragged) { Left += dx; Top += dy; }
    }
    void BallUp(object sender, MouseButtonEventArgs e) { BallView.ReleaseMouseCapture(); if (dragStart != null && !dragged) { OpenStory(); } dragStart = null; ClampPosition(); Save(); }
    void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != Hero; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        DragMove(); ClampPosition(); Save();
    }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        StopAutomatic("自动播放已关闭。"); StopListeningForGame();
        SaveListeningProgress();
        if (listeningSaveWarning.Length > 0)
        { e.Cancel = true; Tell(listeningSaveWarning + "；窗口已保留，请处理后重试退出。"); return; }
        closing = true; Save();
        var error = stateSaves.Flush();
        error ??= saveSnapshotError;
        if (error != null)
        {
            closing = false; e.Cancel = true; ShowSaveFailure(error.Message);
            Tell("进度保存失败，窗口已保留。请检查保存位置后重试退出。"); return;
        }
        StopGameText("文本追踪已关闭。");
        ++audioRequest; diagnosticCancellation?.Cancel(); timer.Stop(); ShutdownGamepad(); dialogueMonitor.Dispose(); CancelOcr();
        ShutdownListening(); rawKeyboard?.Dispose(); keyboard?.Dispose(); branchMenu.Close(); clickZone.Close(); ocr.Dispose(); audio.Dispose();
    }
    async Task RunKeysUiTest()
    {
        var results = new List<string>();
        Window? scene = null;
        try
        {
            Expand(SettingsTab); await Task.Delay(150);
            async Task Edit(string action, Key key, bool save, bool collision = false, bool useList = false)
            {
                var open = Dispatcher.InvokeAsync(() => keyButtons[action].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await Task.Delay(180);
                var editor = keyEditor ?? throw new Exception("没有打开改键窗口");
                try
                {
                    if (!editor.CaptureField.IsKeyboardFocused) throw new Exception("改键窗口未将焦点放到录入框");
                    if (useList)
                    {
                        var box = ((StackPanel)editor.Content).Children.OfType<ComboBox>().Single();
                        box.SelectedItem = box.Items.Cast<KeyBindingWindow.Choice>().Single(c=>c.Key==key);
                    }
                    else
                    {
                        var handle = new WindowInteropHelper(editor).Handle;
                        Native.PostMessage(handle,0x100,new IntPtr(KeyInterop.VirtualKeyFromKey(key)),new IntPtr(1));
                        Native.PostMessage(handle,0x101,new IntPtr(KeyInterop.VirtualKeyFromKey(key)),new IntPtr(unchecked((long)0xC0000001)));
                        await Task.Delay(100);
                    }
                    if (editor.SelectedKey != key) throw new Exception("Windows 按键事件没有录入新键");
                    if (collision == editor.SaveButton.IsEnabled) throw new Exception("重复按键检查错误");
                    if (save) editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else
                    {
                        var handle = new WindowInteropHelper(editor).Handle;
                        Native.PostMessage(handle,0x100,new IntPtr(0x1b),new IntPtr(1));
                        Native.PostMessage(handle,0x101,new IntPtr(0x1b),new IntPtr(unchecked((long)0xC0000001)));
                        for (int wait = 0; editor.IsVisible && wait < 20; wait++) await Task.Delay(50);
                        if (editor.IsVisible) throw new Exception("Esc 没有取消录入");
                    }
                }
                finally { if (editor.IsVisible) editor.Close(); }
                await open;
            }
            int before = playCalls;
            string? position = engine?.CurrentId;
            await Edit("next", Key.J, true);
            stateSaves.Flush();
            if (preferences.Keys["next"] != "J" || Json.Read<Preferences>(stateFile).Keys["next"] != "J") throw new Exception("新键未保存到配置");
            results.Add("PASS: focused dialog receives native keyboard event and persists new binding");
            await Edit("next", Key.F5, false, true);
            if (preferences.Keys["next"] != "J") throw new Exception("冲突键覆盖了配置");
            results.Add("PASS: duplicate key blocks saving and Escape cancels");
            await Edit("next", Key.K, false);
            if (preferences.Keys["next"] != "J") throw new Exception("取消后改动仍被保存");
            results.Add("PASS: cancelling a valid candidate keeps previous binding");
            await Edit("ocr", Key.F10, true, false, true);
            if (preferences.Keys["ocr"] != "F10" || !OcrInstructions.Text.Contains("F10")) throw new Exception("列表改键或界面提示未更新");
            results.Add("PASS: choosing from key list saves and refreshes on-screen shortcut hints");
            if (playCalls != before || engine?.CurrentId != position) throw new Exception("设置按键时误播放或移动进度");
            results.Add("PASS: key editing never plays audio or moves story progress");
            if (engine == null) throw new Exception("没有测试配音包");
            var line = engine.Pack.Nodes.First(n=>n.Kind=="line" && n.PathId=="" && n.NextId!=null && engine.Pack.ById[n.NextId].Kind=="line");
            engine.Commit(line.Id);
            // 仅给测试播放器分派事件，不向真实前台窗口注入任何游戏按键。
            scene = new Window { Title="改键验收模拟游戏", Width=360, Height=200, ShowInTaskbar=false };
            scene.Show(); BindGame(new GameWindow(new WindowInteropHelper(scene).Handle,scene.Title,"KeyFixture")); DialoguePositionConfirmed();
            Collapse(); await PrepareIsolatedTestForeground(scene, "改键模拟游戏无法稳定取得前台");
            if(Native.GetForegroundWindow()!=game.Handle) throw new Exception("模拟游戏未取得前台，不能验证改键的游戏路由");
            HandleGlobal(Key.Space,game.Handle);
            if (engine.CurrentId != line.Id) throw new Exception("旧推进键仍然生效");
            HandleGlobal(Key.J,game.Handle);
            if (engine.CurrentId != line.NextId) throw new Exception("新推进键未生效");
            results.Add("PASS: old follow key stops advancing and new follow key advances exactly once");
            game = null; Expand(SettingsTab); Screenshot("custom-keys.png");
            File.WriteAllLines(Path.Combine(Log.DataDir,"keys-ui-test.txt"),results);
        }
        catch (Exception ex) { File.WriteAllLines(Path.Combine(Log.DataDir,"keys-ui-test.txt"),results.Concat(new[]{"FAIL: "+ex})); }
        finally { keyEditor?.Close(); game = null; scene?.Close(); Close(); }
    }
    async Task RunOcrUiTest()
    {
        var results = new List<string>();
        Window? scene = null;
        try
        {
            if (engine == null) throw new Exception("测试配音包未载入");
            var pack = engine.Pack;
            engine = new PlaybackEngine(pack);
            engine.PlayRequested += PlayNode;
            engine.StopRequested += () => audio.Stop();
            engine.Changed += EngineChanged;
            var line = pack.Nodes.First(n => n.Kind == "line" && n.PathId == "" && n.Text.Contains("那是文明的火种"));
            ChapterBox.SelectedItem = pack.Chapters.First(c => c.Sections.Any(s => s.Id == line.SectionId));
            SectionBox.SelectedItem = pack.Chapters.SelectMany(c => c.Sections).First(s => s.Id == line.SectionId);
            OcrEnabledBox.IsChecked = false;
            game = null;
            Expand(StoryTab);
            var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F9) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            RaiseEvent(keyEvent);
            if (!keyEvent.Handled || Tabs.SelectedItem != LocateTab || !OcrStatus.Text.Contains("尚未启用") || playCalls != 0 || engine.CurrentId != null) throw new Exception("面板 F9 没有给出启用提示，或开始了播放");
            results.Add("PASS: F9 in expanded panel explains disabled OCR without playback");
            OcrEnabledBox.IsChecked = true;
            scene = new Window { Title = "OCR 截图验证", Width = 960, Height = 540, Left = 240, Top = 180, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black,
                Content = new TextBlock { Text = line.Text, FontSize = 32, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            scene.Show();
            game = new GameWindow(new WindowInteropHelper(scene).Handle, scene.Title, "OcrFixture");
            scene.Activate(); Native.SetForegroundWindow(game.Handle); await Task.Delay(180);
            if (Native.GetForegroundWindow() != game.Handle) throw new Exception("测试画面无法获得前台");
            if (engine.GetStoryMenus(line.SectionId).Count>0)
            {
                HandleGlobal(Key.F1,game.Handle);
                if(!branchMenu.IsVisible || !branchMenu.IsNavigation || engine.CurrentId!=null || playCalls!=0)throw new Exception("面板展开时 F1 未能静音打开手动分支目录");
                results.Add("PASS: F1 from bound foreground opens manual branches even when main panel is expanded, without playback");
                HideBranchMenu();Expand(StoryTab);scene.Activate();Native.SetForegroundWindow(game.Handle);await Task.Delay(180);
            }
            // 面板保持展开，但游戏在前台；F9 应仍然截图并识别。
            HandleGlobal(Key.F9, game.Handle);
            var deadline = DateTime.UtcNow.AddSeconds(50);
            while (locating && DateTime.UtcNow < deadline) await Task.Delay(30);
            if (locating || CandidatesList.ItemsSource is not List<MatchCandidate> matches || !matches.Any(c => c.Node.Id == line.Id)) throw new Exception("从游戏前台按 F9 没有得到目标候选：" + OcrStatus.Text);
            if (engine.CurrentId != null || playCalls != 0) throw new Exception("未确认 OCR 结果就改变了进度或播放");
            results.Add("PASS: F9 from bound foreground works with panel expanded and no starting playback");
            if (!OcrPreview.Text.Contains("文明")) throw new Exception("没有显示识别文字");
            results.Add("PASS: real screenshot and packaged OCR display expected candidate and recognized text");
            Screenshot("ocr-candidates.png");
            // 实際点击按钮也应能从软件切回绑定画面，而不要求用户抢切窗口。
            LocateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(50);
            while (locating && DateTime.UtcNow < deadline) await Task.Delay(30);
            if (locating || CandidatesList.ItemsSource is not List<MatchCandidate> buttonMatches || !buttonMatches.Any(c => c.Node.Id == line.Id) || playCalls != 0) throw new Exception("面板定位按钮失败：" + OcrStatus.Text);
            results.Add("PASS: locate button activates bound window and returns candidates without playback");
            var pending = Locate();
            if (SectionBox.Items.Count > 1) SectionBox.SelectedIndex = 1;
            else CancelOcr();
            await pending;
            if (CandidatesList.ItemsSource != null || engine.CurrentId != null || playCalls != 0) throw new Exception("切章节后旧识别结果仍有效");
            results.Add("PASS: changing section during capture cancels stale results and preserves progress");
            scene.WindowState = WindowState.Minimized;
            await Locate();
            if (!OcrStatus.Text.Contains("最小化") || Tabs.SelectedItem != LocateTab || playCalls != 0) throw new Exception("最小化失败原因未显示");
            results.Add("PASS: minimized game shows actionable error without playback");
            File.WriteAllLines(Path.Combine(Log.DataDir, "ocr-ui-test.txt"), results);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Log.DataDir, "ocr-ui-test.txt"), "FAIL: " + ex); }
        finally { game = null; scene?.Close(); Close(); }
    }
    async Task RunCaptureUiTest()
    {
        var results = new List<string>();
        Window? fixture = null;
        try
        {
            int before = playCalls;
            Reveal(); await Task.Delay(200);
            var handle = new WindowInteropHelper(this).Handle;
            if (!IsVisible || !expanded || !ShowInTaskbar || playCalls != before)
                throw new Exception("展开入口未显示面板或触发了播放");
            if (!Native.GetWindowDisplayAffinity(handle, out var affinity) || affinity != 0)
                throw new Exception("主面板仍禁止截图");
            Native.SetForegroundWindow(handle);
            var panel = Native.Capture(handle, Log.DataDir);
            File.Move(panel.Path, Path.Combine(Log.DataDir, "普通截图-面板.png"), true);
            results.Add("PASS: reopening reveals panel and taskbar entry without playback; desktop capture allowed");
            branchMenu.Show();
            if (!Native.GetWindowDisplayAffinity(branchMenu.Handle, out affinity) || affinity != 0)
                throw new Exception("分支菜单仍禁止截图");
            results.Add("PASS: branch menu permits normal screenshots");
            branchMenu.Hide();
            fixture = new Window { Title="截图验收画面", WindowStyle=WindowStyle.None,
                ResizeMode=ResizeMode.NoResize, Left=Left, Top=Top, Width=640, Height=480,
                Background=new SolidColorBrush(Color.FromRgb(16,80,128)), ShowInTaskbar=false };
            fixture.Show(); await Task.Delay(200);
            var fixtureHandle = new WindowInteropHelper(fixture).Handle;
            var capture = await CaptureGameForOcr(fixtureHandle, CancellationToken.None);
            using (var bitmap = new System.Drawing.Bitmap(capture.Path))
            {
                var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                if (pixel.R != 16 || pixel.G != 80 || pixel.B != 128)
                    throw new Exception("OCR 截图包含悬浮面板或没有截到目标窗口");
            }
            File.Move(capture.Path, Path.Combine(Log.DataDir,"OCR截图-排除面板.png"), true);
            if (!IsVisible) throw new Exception("截图后未恢复面板");
            results.Add("PASS: OCR capture excludes overlay and restores it immediately");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await CaptureGameForOcr(fixtureHandle, cancelled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { }
            if (!IsVisible || playCalls != before) throw new Exception("取消截图未恢复面板或发生误播");
            results.Add("PASS: cancellation restores visible panel; no audio or progress changes");
        }
        catch (Exception ex) { results.Add("FAIL: " + ex); }
        finally
        {
            fixture?.Close();
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir,"capture-ui-test.txt"),results);
            Close();
        }
    }
    async Task RunUiTest()
    {
        var results = new List<string>();
        try
        {
            Expand(StoryTab); await Task.Delay(300);
            int before = playCalls;
            if (rows.Count > 2) { LinesList.SelectedIndex = 1; LinesList.ScrollIntoView(rows[^1]); LinesList.SelectedIndex = 2; }
            SearchBox.Text = "旁白"; SearchBox.Clear(); VolumeSlider.Value = 37;
            if (playCalls != before) throw new Exception("浏览触发播放");
            results.Add("PASS: browse/search/select/volume do not play");
            if (engine != null)
            {
                var first = rows.First(r => r.Node.Kind == "line"); LinesList.SelectedItem = first; ConfirmLine();
                if (playCalls != before + 1) throw new Exception("确认没有仅播放一次");
                results.Add("PASS: explicit confirm plays exactly once");
                engine.EnterOriginal(); int originalCalls = playCalls; engine.Next(true); engine.Previous(); engine.Replay();
                if (playCalls != originalCalls) throw new Exception("原声状态误播");
                results.Add("PASS: original voice blocks playback and navigation");
                Expand(StoryTab); BrowseCurrent();
            }
            await Task.Delay(300); Screenshot("panel.png");
            Tabs.SelectedItem = SettingsTab; await Task.Delay(100); Screenshot("settings.png");
            Collapse(); await Task.Delay(100); Screenshot("ball.png");
            File.WriteAllLines(Path.Combine(Log.DataDir, "ui-test.txt"), results);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Log.DataDir, "ui-test.txt"), "FAIL: " + ex); }
        Close();
    }
    void Screenshot(string name)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Log.DataDir); using var output = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(output);
    }
}
