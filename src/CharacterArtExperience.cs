using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace PgrVoice;

// 角色头像与立绘：先读用户本机文件夹，其次用程序内置图片包（Assets/BundledArt.zip：头像 + 章节封面），都没有时显示字标。
// 查找顺序见 CharacterArt.SearchFolders；索引在后台建立，图片在后台解码并缓存，文件夹变化时自动重新扫描。
public partial class MainWindow
{
    const int PortraitDecodeHeight = 128, StandeeDecodeHeight = 480, CharacterArtCacheSize = 64;
    static readonly Lazy<ArtBundle> bundledArt = new(LoadBundledArt);
    static ArtBundle BundledArt => bundledArt.Value;
    static ArtBundle LoadBundledArt()
    {
        try
        {
            var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("PgrVoice.BundledArt.zip");
            var bundle = new ArtBundle(stream);
            if (bundle.IsEmpty) Log.Write("character-art", "内置图片包缺失或无法读取，只使用本机文件夹。");
            return bundle;
        }
        catch (Exception ex) { Log.Write("character-art", "内置图片包读取失败：" + ex.Message); return ArtBundle.None; }
    }
    CharacterArtIndex characterArt = CharacterArtIndex.Empty;
    readonly List<FileSystemWatcher> characterArtWatchers = new();
    readonly DispatcherTimer characterArtDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly Dictionary<string, BitmapSource?> characterArtCache = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> characterArtOrder = new();
    long characterArtScan;
    bool characterArtReady, characterArtScanning;
    string characterArtSourceKey = "\0", wantedPortrait = "\0", wantedStandee = "\0", wantedCover = "\0";
    readonly SpeakerPortrait listeningPortrait = new() { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    readonly CheckBox portraitsBox = new() { Content = "显示说话人头像（缺图时显示字标）", Margin = new Thickness(0, 2, 0, 6) };
    readonly CheckBox standeeBox = new() { Content = "在标题区显示角色立绘", Margin = new Thickness(0, 2, 0, 6) };
    readonly Slider standeeOpacitySlider = new() { Minimum = 0, Maximum = 0.8, SmallChange = 0.05, LargeChange = 0.1, IsSnapToTickEnabled = true, TickFrequency = 0.05 };
    readonly TextBlock standeeOpacityLabel = new() { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox characterArtFolderBox = new() { IsReadOnly = true, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock characterArtStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox reduceMotionBox = new() { Content = "减少动画", Margin = new Thickness(0, 2, 0, 4) };

    static string CharacterArtAppData() { try { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch (Exception) { return ""; } }

    void InitializeCharacterArt()
    {
        // 听书卡：头像放在左列，不增加卡片高度。
        if (listeningPlayerCard.Child is UIElement player)
        {
            listeningPlayerCard.Child = null;
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
            var side = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; side.Children.Add(listeningPortrait);
            grid.Children.Add(side); Grid.SetColumn(player, 1); grid.Children.Add(player);
            listeningPlayerCard.Child = grid;
        }
        listeningPlayerCard.BracketBrush = Theme.Brush("Slate");

        var panel = CharacterArtSettingsContent;
        panel.Children.Add(portraitsBox); panel.Children.Add(standeeBox);
        var opacityRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var opacityTitle = new TextBlock { Text = "立绘透明度", FontSize = 11, Foreground = Theme.Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(opacityTitle, Dock.Left); DockPanel.SetDock(standeeOpacityLabel, Dock.Right);
        standeeOpacityLabel.Style = Theme.Style("NumberText"); standeeOpacityLabel.Foreground = Theme.Brush("Plate");
        opacityRow.Children.Add(opacityTitle); opacityRow.Children.Add(standeeOpacityLabel); opacityRow.Children.Add(standeeOpacitySlider);
        panel.Children.Add(opacityRow);
        AddHeading(panel, "图片文件夹");
        panel.Children.Add(new TextBlock
        {
            Text = "程序已内置全部角色头像与章节封面；本机文件夹里的同名图片优先，可用来替换或补充（不会上传）。文件名即角色名，例如 丽芙.png、露西亚.jpg（支持 png / jpg / jpeg / webp）；立绘放在同一文件夹的「立绘」子文件夹。可用 aliases.json 把台词里的别名指向文件名。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "未指定时自动依次查找：配音总目录内的「角色头像」→ 配音总目录旁的「角色头像」（如 D:\\配音总目录\\角色头像）→ %AppData%\\PgrVoice\\角色头像。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("SubtleText"), Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(characterArtFolderBox);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        AddButton(buttons, "选择文件夹…", ChooseCharacterArtFolder);
        AddButton(buttons, "打开文件夹", OpenCharacterArtFolder);
        AddButton(buttons, "重新扫描", () => RescanCharacterArt());
        AddButton(buttons, "恢复默认", ResetCharacterArt);
        panel.Children.Add(characterArtStatus);
        AddChapterCoverSettings(panel);
        AddHeading(panel, "动画");
        panel.Children.Add(reduceMotionBox);
        panel.Children.Add(new TextBlock { Text = "关闭章节封面淡入等过渡动画；系统关闭动画效果时也会自动减少。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText") });

        portraitsBox.IsChecked = preferences.ShowSpeakerPortraits;
        standeeBox.IsChecked = preferences.ShowStandee;
        standeeOpacitySlider.Value = preferences.StandeeOpacity;
        standeeOpacityLabel.Text = $"{preferences.StandeeOpacity * 100:0}%";
        reduceMotionBox.IsChecked = preferences.ReduceMotion;
        UpdateCharacterArtFolderBox();
        portraitsBox.Checked += (_, _) => SetCharacterArtOption(() => preferences.ShowSpeakerPortraits = true);
        portraitsBox.Unchecked += (_, _) => SetCharacterArtOption(() => preferences.ShowSpeakerPortraits = false);
        standeeBox.Checked += (_, _) => SetCharacterArtOption(() => preferences.ShowStandee = true);
        standeeBox.Unchecked += (_, _) => SetCharacterArtOption(() => preferences.ShowStandee = false);
        standeeOpacitySlider.ValueChanged += (_, e) =>
        {
            standeeOpacityLabel.Text = $"{e.NewValue * 100:0}%";
            SetCharacterArtOption(() => preferences.StandeeOpacity = Math.Clamp(Math.Round(e.NewValue, 2), 0, 0.8));
        };
        reduceMotionBox.Checked += (_, _) => SetCharacterArtOption(() => preferences.ReduceMotion = true);
        reduceMotionBox.Unchecked += (_, _) => SetCharacterArtOption(() => preferences.ReduceMotion = false);
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(() => Hud.SetReduceMotion(preferences.ReduceMotion)); };
        Hud.SetReduceMotion(preferences.ReduceMotion);

        branchMenu.Portraits = BranchPortrait;
        characterArtDebounce.Tick += (_, _) => { characterArtDebounce.Stop(); RescanCharacterArt(); };
        Activated += (_, _) => UpdateCharacterMotion();
        Deactivated += (_, _) => UpdateCharacterMotion();
        Closed += (_, _) => { characterArtDebounce.Stop(); DisposeCharacterArtWatchers(); };
        characterArtReady = true;
        ApplyCharacterArtLayout();
        RescanCharacterArt();
    }

    void SetCharacterArtOption(Action change)
    {
        if (!characterArtReady) return;
        change(); Hud.SetReduceMotion(preferences.ReduceMotion);
        wantedPortrait = wantedStandee = "\0"; UpdateCharacterVisuals(); Save();
    }

    // 配音总目录：优先使用已打开的章节总目录，否则由当前配音包位置推断。
    string CharacterArtLibraryRoot()
    {
        if (chapterLibraryFolder.Length > 0) return chapterLibraryFolder;
        try
        {
            if (preferences.PackFile.Length == 0) return "";
            string folder = Directory.Exists(preferences.PackFile) ? preferences.PackFile : Path.GetDirectoryName(preferences.PackFile) ?? "";
            return folder.Length == 0 ? "" : LibraryPaths.Root(folder);
        }
        catch (Exception) { return ""; }
    }

    void RescanCharacterArt()
    {
        if (closing) return;
        long scan = ++characterArtScan;
        characterArtSourceKey = chapterLibraryFolder + "|" + preferences.PackFile + "|" + preferences.CharacterArtFolder;
        var folders = CharacterArt.SearchFolders(preferences.CharacterArtFolder, CharacterArtLibraryRoot(), CharacterArtAppData());
        characterArtScanning = true; characterArtStatus.Text = "正在扫描角色图片…"; characterArtStatus.Foreground = Theme.Brush("SubtleText");
        Task.Run(() => new CharacterArtIndex(folders, BundledArt)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (closing || scan != characterArtScan) return;
            characterArtScanning = false;
            characterArt = task.Status == TaskStatus.RanToCompletion ? task.Result : CharacterArtIndex.Empty;
            characterArtCache.Clear(); characterArtOrder.Clear(); portraitPending.Clear();
            wantedPortrait = wantedStandee = wantedCover = "\0";
            WatchCharacterArt(folders);
            UpdateCharacterArtStatus(folders);
            UpdateCharacterVisuals(); PortraitImages.RaiseChanged();
            Log.Write("character-art", $"扫描完成：头像 {characterArt.AvatarCount}，立绘 {characterArt.StandeeCount}，文件夹 {string.Join("；", characterArt.ExistingFolders)}");
        }));
    }

    void UpdateCharacterArtStatus(IReadOnlyList<string> folders)
    {
        UpdateCharacterArtFolderBox();
        string builtIn = characterArt.BundledAvatarCount > 0 ? $"程序内置 {characterArt.BundledAvatarCount} 个头像" : "";
        if (characterArt.ExistingFolders.Count == 0)
        {
            characterArtStatus.Foreground = Theme.Brush(builtIn.Length > 0 ? "TealText" : "MutedText");
            characterArtStatus.Text = (builtIn.Length > 0 ? $"正在使用{builtIn}（缺图时显示字标）。" : "未找到图片文件夹，正在使用字标。") +
                "如需替换或补充，可新建以下任一文件夹并放入图片（本机图片优先）：\n" + string.Join("\n", folders);
            return;
        }
        characterArtStatus.Foreground = Theme.Brush("TealText");
        characterArtStatus.Text = $"已找到 {characterArt.AvatarCount} 个头像、{characterArt.StandeeCount} 张立绘" +
            (characterArt.Aliases.Count > 0 ? $"、{characterArt.Aliases.Count} 条别名" : "") +
            "\n来自：" + string.Join("；", characterArt.ExistingFolders) + (builtIn.Length > 0 ? $"；{builtIn}（本机优先）" : "");
    }

    void UpdateCharacterArtFolderBox() =>
        characterArtFolderBox.Text = preferences.CharacterArtFolder.Length > 0 ? preferences.CharacterArtFolder : "（自动查找）";

    void WatchCharacterArt(IReadOnlyList<string> folders)
    {
        DisposeCharacterArtWatchers();
        void Changed() { try { Dispatcher.BeginInvoke(() => { if (closing) return; characterArtDebounce.Stop(); characterArtDebounce.Start(); }); } catch (Exception) { } }
        foreach (string folder in folders)
        {
            try
            {
                FileSystemWatcher watcher;
                if (Directory.Exists(folder))
                    watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                else if (Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent))
                    watcher = new FileSystemWatcher(parent, Path.GetFileName(folder)) { NotifyFilter = NotifyFilters.DirectoryName };
                else continue;
                watcher.Created += (_, _) => Changed(); watcher.Deleted += (_, _) => Changed();
                watcher.Renamed += (_, _) => Changed(); watcher.Changed += (_, _) => Changed();
                watcher.Error += (_, _) => Changed();
                watcher.EnableRaisingEvents = true; characterArtWatchers.Add(watcher);
            }
            catch (Exception ex) { Log.Write("character-art", "无法监视 " + folder + "：" + ex.Message); }
        }
    }

    void DisposeCharacterArtWatchers()
    {
        foreach (var watcher in characterArtWatchers) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch (Exception) { } }
        characterArtWatchers.Clear();
    }

    void ChooseCharacterArtFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择角色头像文件夹（文件名为角色名；立绘放在「立绘」子文件夹）" };
        if (preferences.CharacterArtFolder.Length > 0 && Directory.Exists(preferences.CharacterArtFolder)) dialog.InitialDirectory = preferences.CharacterArtFolder;
        if (dialog.ShowDialog(this) != true) return;
        preferences.CharacterArtFolder = dialog.FolderName; Save(); RescanCharacterArt();
    }

    void OpenCharacterArtFolder()
    {
        try
        {
            string? folder = characterArt.ExistingFolders.FirstOrDefault();
            if (folder == null)
            {
                // 还没有任何文件夹：在配音总目录旁（未知时为 AppData）新建一个空文件夹，方便放入图片。
                var candidates = CharacterArt.SearchFolders(preferences.CharacterArtFolder, CharacterArtLibraryRoot(), CharacterArtAppData());
                folder = preferences.CharacterArtFolder.Length > 0 ? candidates.First() : candidates.Count >= 3 ? candidates[1] : candidates.LastOrDefault();
                if (folder == null) { Tell("还没有可用的图片文件夹，请先选择配音包或指定文件夹。"); return; }
                Directory.CreateDirectory(folder);
            }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Tell("无法打开图片文件夹：" + ex.Message); }
    }

    void ResetCharacterArt()
    {
        characterArtReady = false;
        preferences.ShowSpeakerPortraits = true; preferences.ShowStandee = true; preferences.StandeeOpacity = 0.55;
        preferences.CharacterArtFolder = ""; preferences.ReduceMotion = false;
        preferences.ShowChapterCovers = true; preferences.ChapterCoverFolder = "";
        portraitsBox.IsChecked = true; standeeBox.IsChecked = true; standeeOpacitySlider.Value = 0.55; reduceMotionBox.IsChecked = false; coversBox.IsChecked = true;
        standeeOpacityLabel.Text = "55%";
        characterArtReady = true; Hud.SetReduceMotion(false); Save(); RescanCharacterArt(); RescanChapterCovers();
    }

    // 后台解码（DecodePixelHeight 限制内存）；失败（含系统缺少 WebP 解码器）按缺图处理。
    static (BitmapSource? Image, string Error) DecodeCharacterImage(string path, int height)
    {
        try
        {
            byte[] bytes;
            if (ArtBundle.IsBundled(path))
            {
                bytes = BundledArt.Read(path) ?? Array.Empty<byte>();
                if (bytes.Length == 0) return (null, "内置图片缺失");
            }
            else
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 32L * 1024 * 1024) return (null, "文件不存在或超过 32 MB");
                bytes = File.ReadAllBytes(path);
            }
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream; image.DecodePixelHeight = height; image.EndInit(); image.Freeze();
            return (image, "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    void LoadCharacterImage(string path, int height, Action<BitmapSource?> apply)
    {
        string key = height + "|" + path;
        if (characterArtCache.TryGetValue(key, out var cached))
        {
            characterArtOrder.Remove(key); characterArtOrder.AddLast(key); apply(cached); return;
        }
        long scan = characterArtScan, imageCoverScan = coverScan;
        Task.Run(() => DecodeCharacterImage(path, height)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (closing || scan != characterArtScan || (height == CoverDecodeHeight && imageCoverScan != coverScan)) return;
            var (image, error) = task.Status == TaskStatus.RanToCompletion ? task.Result : (null, "解码失败");
            if (image == null) Log.Write("character-art", "无法读取 " + path + "：" + error + "；改用字标。");
            characterArtCache[key] = image; characterArtOrder.Remove(key); characterArtOrder.AddLast(key);
            while (characterArtOrder.Count > CharacterArtCacheSize) { characterArtCache.Remove(characterArtOrder.First!.Value); characterArtOrder.RemoveFirst(); }
            apply(image);
        }));
    }

    // 分支首句卡片用：只取已缓存的头像，未缓存时后台加载，下次显示即可用；关闭头像时不显示头像列。
    (bool Show, ImageSource? Image) BranchPortrait(string speaker)
    {
        if (!preferences.ShowSpeakerPortraits) return (false, null);
        string? path = characterArt.FindAvatar(speaker);
        if (path == null) return (true, null);
        if (characterArtCache.TryGetValue(PortraitDecodeHeight + "|" + path, out var image)) return (true, image);
        LoadCharacterImage(path, PortraitDecodeHeight, _ => { });
        return (true, null);
    }

    string? ActiveSpeaker() => ListeningActive ? listeningSession?.Current?.Node?.Speaker : engine?.Current?.Speaker;

    void ApplyCharacterArtLayout()
    {
        if (!characterArtReady) return;
        bool compact = CompactPanelLayout, veryCompact = VeryCompactPanelLayout, show = preferences.ShowSpeakerPortraits;
        CurrentPortrait.Visibility = show && !veryCompact ? Visibility.Visible : Visibility.Collapsed;
        CurrentPortrait.Width = compact ? 40 : 64; CurrentPortrait.Height = compact ? 46 : 74;
        CurrentPortrait.ShowSerial = false;
        listeningPortrait.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        listeningPortrait.Width = compact ? 36 : 44; listeningPortrait.Height = compact ? 42 : 52;
        PortraitImages.Enabled = show;
        var rowPortraits = show ? Visibility.Visible : Visibility.Collapsed;
        if (!Equals(Application.Current.Resources["PortraitVisibility"], rowPortraits)) Application.Current.Resources["PortraitVisibility"] = rowPortraits;
        SquadStrip.Visibility = show && !compact ? Visibility.Visible : Visibility.Collapsed;
        StripPortrait.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StripBody.Margin = new Thickness(show ? 43 : 0, 0, 0, 0);
    }

    // 由 UpdateState 调用（每 250ms 及状态变化时），只比较字符串；只有说话人改变时才查找/加载图片。
    void UpdateCharacterVisuals()
    {
        if (!characterArtReady) return;
        if (!characterArtScanning && chapterLibraryFolder + "|" + preferences.PackFile + "|" + preferences.CharacterArtFolder != characterArtSourceKey) RescanCharacterArt();
        ApplyCharacterArtLayout();
        string storySpeaker = engine?.Current?.Speaker ?? "", listenSpeaker = listeningSession?.Current?.Node?.Speaker ?? "";
        string active = ListeningActive ? listenSpeaker : storySpeaker;
        bool storyPlaying = playingId != null, listenPlaying = listeningRunning;
        CurrentPortrait.Speaker = storySpeaker; CurrentPortrait.IsPlaying = storyPlaying;
        CurrentPortrait.Accent = engine?.Mode is RunMode.Choice or RunMode.Gap ? LineRow.BranchAccent : null;
        listeningPortrait.Speaker = listenSpeaker; listeningPortrait.IsPlaying = listenPlaying;
        StripPortrait.Speaker = active; StripPortrait.IsPlaying = ListeningActive ? listenPlaying : storyPlaying;
        StripPortrait.Accent = !ListeningActive && engine?.MenuWaiting == true ? LineRow.BranchAccent : null;

        string avatarKey = preferences.ShowSpeakerPortraits ? storySpeaker + "\u0001" + listenSpeaker + "\u0001" + active : "";
        if (avatarKey != wantedPortrait)
        {
            wantedPortrait = avatarKey;
            ApplyAvatar(CurrentPortrait, storySpeaker); ApplyAvatar(listeningPortrait, listenSpeaker); ApplyAvatar(StripPortrait, active);
        }
        string standeeKey = preferences.ShowStandee ? active : "";
        if (standeeKey != wantedStandee)
        {
            wantedStandee = standeeKey;
            string? path = preferences.ShowStandee ? characterArt.FindStandee(active) : null;
            if (path == null) HeroStandee.Source = null;
            else LoadCharacterImage(path, StandeeDecodeHeight, image => { if (wantedStandee == standeeKey) HeroStandee.Source = image; });
        }
        HeroStandee.Opacity = preferences.StandeeOpacity;
        HeroStandee.Visibility = preferences.ShowStandee && HeroStandee.Source != null && Hero.Height >= 80 ? Visibility.Visible : Visibility.Collapsed;

        UpdateHeaderVisuals(active);
        UpdateCharacterMotion();
    }

    void ApplyAvatar(SpeakerPortrait portrait, string speaker)
    {
        string? path = preferences.ShowSpeakerPortraits ? characterArt.FindAvatar(speaker) : null;
        portrait.Tag = path;
        if (ReferenceEquals(portrait, CurrentPortrait)) UpdateFrameName(path);
        if (path == null) { portrait.Source = null; return; }
        LoadCharacterImage(path, PortraitDecodeHeight, image => { if (Equals(portrait.Tag, path)) portrait.Source = image; });
    }

    // 冷灰白主题没有循环动画（波形已移除）；保留入口供激活/失焦时刷新静态状态。
    void UpdateCharacterMotion() { }
}
