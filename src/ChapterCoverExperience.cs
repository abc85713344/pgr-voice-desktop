using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace PgrVoice;

// 章节封面：与角色头像相同的本机文件夹链（指定文件夹 → 配音总目录内 → 配音总目录旁 → AppData），文件夹名为「章节封面」。
// 匹配顺序见 ChapterCover.CandidateNames（小节封面优先 → 完整章节名 → 配音包文件夹名 → 主线第NN章 等 → 编号 → id）；封面在后台解码并缓存。
public partial class MainWindow
{
    const int CoverDecodeHeight = 420;
    ChapterCoverIndex chapterCovers = ChapterCoverIndex.Empty;
    readonly List<FileSystemWatcher> coverWatchers = new();
    readonly DispatcherTimer coverDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly HashSet<string> portraitPending = new(StringComparer.OrdinalIgnoreCase);
    long coverScan;
    bool coverScanning, coverReady;
    string coverSourceKey = "\0";
    readonly CheckBox coversBox = new() { Content = "在标题区显示章节封面", Margin = new Thickness(0, 2, 0, 6) };
    readonly TextBox coverFolderBox = new() { IsReadOnly = true, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock coverStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    void AddChapterCoverSettings(StackPanel panel)
    {
        AddHeading(panel, "章节封面");
        panel.Children.Add(coversBox);
        panel.Children.Add(new TextBlock
        {
            Text = "封面放在「章节封面」文件夹，文件名可用完整章节名（如 01 涂鸦艺术.png）、主线第01章.png、主线第35章_覆海归途.png、外篇EX00.png、间章_青鸟.png、配音包文件夹名或章节编号。小节封面可放在以章节名命名的子文件夹里。也可用 aliases.json 指定。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "未指定时与头像相同顺序查找：配音总目录内 → 配音总目录旁（如 D:\\配音总目录\\章节封面）→ %AppData%\\PgrVoice\\章节封面。找不到封面时显示默认标题样式。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("SubtleText"), Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(coverFolderBox);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        AddButton(buttons, "选择封面文件夹…", ChooseCoverFolder);
        AddButton(buttons, "打开封面文件夹", OpenCoverFolder);
        AddButton(buttons, "重新扫描封面", () => RescanChapterCovers());
        panel.Children.Add(coverStatus);
        coversBox.IsChecked = preferences.ShowChapterCovers;
        coversBox.Checked += (_, _) => SetCharacterArtOption(() => preferences.ShowChapterCovers = true);
        coversBox.Unchecked += (_, _) => SetCharacterArtOption(() => preferences.ShowChapterCovers = false);
        UpdateCoverFolderBox();
        coverDebounce.Tick += (_, _) => { coverDebounce.Stop(); RescanChapterCovers(); };
        Closed += (_, _) => { coverDebounce.Stop(); DisposeCoverWatchers(); };
        PortraitImages.Resolver = ResolvePortrait;
        coverReady = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InitializeThemeChrome));
        RescanChapterCovers();
    }

    void UpdateCoverFolderBox() => coverFolderBox.Text = preferences.ChapterCoverFolder.Length > 0 ? preferences.ChapterCoverFolder : "（自动查找）";

    void RescanChapterCovers()
    {
        if (closing || !coverReady) return;
        long scan = ++coverScan;
        // 同名图片也可能已替换；只清理封面缓存，头像和立绘缓存继续复用。
        foreach (string key in characterArtCache.Keys.Where(key => key.StartsWith(CoverDecodeHeight + "|", StringComparison.Ordinal)).ToArray())
        { characterArtCache.Remove(key); characterArtOrder.Remove(key); }
        coverSourceKey = chapterLibraryFolder + "|" + preferences.PackFile + "|" + preferences.ChapterCoverFolder;
        var folders = CharacterArt.SearchFolders(preferences.ChapterCoverFolder, CharacterArtLibraryRoot(), CharacterArtAppData(), CharacterArt.CoverFolderName);
        coverScanning = true; coverStatus.Text = "正在扫描章节封面…"; coverStatus.Foreground = Theme.Brush("SubtleText");
        Task.Run(() => new ChapterCoverIndex(folders, BundledArt)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (closing || scan != coverScan) return;
            coverScanning = false;
            chapterCovers = task.Status == TaskStatus.RanToCompletion ? task.Result : ChapterCoverIndex.Empty;
            wantedCover = "\0";
            WatchChapterCovers(folders);
            UpdateCoverFolderBox();
            string builtIn = chapterCovers.BundledCount > 0 ? $"程序内置 {chapterCovers.BundledCount} 张封面" : "";
            if (chapterCovers.ExistingFolders.Count == 0)
            {
                coverStatus.Foreground = Theme.Brush(builtIn.Length > 0 ? "TealText" : "MutedText");
                coverStatus.Text = (builtIn.Length > 0 ? $"正在使用{builtIn}。" : "未找到封面文件夹，标题区使用默认样式。") +
                    "如需替换或补充，可新建以下任一文件夹并放入封面（本机封面优先）：\n" + string.Join("\n", folders);
            }
            else
            {
                coverStatus.Foreground = Theme.Brush("TealText");
                coverStatus.Text = $"已找到 {chapterCovers.Count} 张封面" + (chapterCovers.Aliases.Count > 0 ? $"、{chapterCovers.Aliases.Count} 条别名" : "") +
                    "\n来自：" + string.Join("；", chapterCovers.ExistingFolders) + (builtIn.Length > 0 ? $"；{builtIn}（本机优先）" : "");
            }
            UpdateCharacterVisuals();
            Log.Write("chapter-cover", $"扫描完成：封面 {chapterCovers.Count}，文件夹 {string.Join("；", chapterCovers.ExistingFolders)}");
        }));
    }

    void WatchChapterCovers(IReadOnlyList<string> folders)
    {
        DisposeCoverWatchers();
        void Changed() { try { Dispatcher.BeginInvoke(() => { if (closing) return; coverDebounce.Stop(); coverDebounce.Start(); }); } catch (Exception) { } }
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
                watcher.EnableRaisingEvents = true; coverWatchers.Add(watcher);
            }
            catch (Exception ex) { Log.Write("chapter-cover", "无法监视 " + folder + "：" + ex.Message); }
        }
    }

    void DisposeCoverWatchers()
    {
        foreach (var watcher in coverWatchers) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch (Exception) { } }
        coverWatchers.Clear();
    }

    void ChooseCoverFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择章节封面文件夹（文件名为章节名或编号）" };
        if (preferences.ChapterCoverFolder.Length > 0 && Directory.Exists(preferences.ChapterCoverFolder)) dialog.InitialDirectory = preferences.ChapterCoverFolder;
        if (dialog.ShowDialog(this) != true) return;
        preferences.ChapterCoverFolder = dialog.FolderName; Save(); RescanChapterCovers();
    }

    void OpenCoverFolder()
    {
        try
        {
            string? folder = chapterCovers.ExistingFolders.FirstOrDefault();
            if (folder == null)
            {
                var candidates = CharacterArt.SearchFolders(preferences.ChapterCoverFolder, CharacterArtLibraryRoot(), CharacterArtAppData(), CharacterArt.CoverFolderName);
                folder = preferences.ChapterCoverFolder.Length > 0 ? candidates.First() : candidates.Count >= 3 ? candidates[1] : candidates.LastOrDefault();
                if (folder == null) { Tell("还没有可用的封面文件夹，请先选择配音包或指定文件夹。"); return; }
                Directory.CreateDirectory(folder);
            }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Tell("无法打开封面文件夹：" + ex.Message); }
    }

    // 列表行与小队条的头像：只返回已缓存图片；未缓存时后台加载一次，完成后通知各头像重绘。
    ImageSource? ResolvePortrait(string speaker)
    {
        if (!preferences.ShowSpeakerPortraits || string.IsNullOrWhiteSpace(speaker)) return null;
        string? path = characterArt.FindAvatar(speaker);
        if (path == null) return null;
        string key = PortraitDecodeHeight + "|" + path;
        if (characterArtCache.TryGetValue(key, out var image)) return image;
        if (portraitPending.Add(path)) LoadCharacterImage(path, PortraitDecodeHeight, _ => { portraitPending.Remove(path); PortraitImages.RaiseChanged(); });
        return null;
    }

    // 立绘/头像文件名「角色·称号」中的称号作为名牌旁的机体名，例如 露西亚·鸦羽.png。
    void UpdateFrameName(string? path)
    {
        string name = path == null ? "" : Path.GetFileNameWithoutExtension(path);
        int dot = name.IndexOfAny(new[] { '·', '•', '・' });
        string frame = dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].Trim() : "";
        CurrentFrameName.Text = frame;
        CurrentFrameName.Visibility = frame.Length > 0 && Height >= 560 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 小节标题里的编号（如「普通 · 1-1 灰鸦过境」→「1-1」），用于列表分区读数。
    static string SectionCode(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"(?<![\w-])([A-Za-z]{0,3}-?\d+(?:-\d+)+|[A-Za-z]{1,3}\d+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    static string PackFolderName(string packFile)
    {
        try
        {
            if (packFile.Length == 0) return "";
            string folder = Directory.Exists(packFile) ? packFile : Path.GetDirectoryName(packFile) ?? "";
            return Path.GetFileName(folder.TrimEnd('\\', '/'));
        }
        catch (Exception) { return ""; }
    }

    static string CategoryEnglish(string category) => category switch
    {
        "主线" => "MAIN STORY", "外篇剧情" => "EX STORY", "间章剧情" => "INTERLUDE", "浮点纪实" => "FLOATING RECORD",
        "本我回廊" => "EGO CORRIDOR", "空中花园" => "BABYLONIA", "联动" => "COLLAB", "好感剧情" => "COMMUNICATION",
        _ => "STORY VOICE"
    };

    // 标题区：章节封面横幅或默认冷灰白标题；描边大章节号、小节标签、灰鸦小队高亮与播放开关状态。
    void UpdateHeaderVisuals(string active)
    {
        if (!coverReady) return;
        if (!coverScanning && chapterLibraryFolder + "|" + preferences.PackFile + "|" + preferences.ChapterCoverFolder != coverSourceKey) RescanChapterCovers();
        bool listeningHeader = Tabs.SelectedItem == listeningTab;
        var pack = listeningHeader ? listeningSession?.Pack : engine?.Pack;
        string packFile = listeningHeader ? listeningFile : preferences.PackFile;
        string chapterTitle = pack == null ? "" : PackChoice.ChapterTitle(listeningHeader ? listeningSession!.ChapterTitle : pack.Title);
        var (number, name) = ChapterCover.Split(chapterTitle);
        string category = pack == null ? "" : ChapterCatalog.Category(pack.Id, chapterTitle);
        HeroTitle.Text = pack == null ? "战双配音" : name.Length > 0 ? name : chapterTitle;
        string display = ChapterCover.DisplayNumber(number);
        bool compact = CompactPanelLayout, veryCompact = VeryCompactPanelLayout;
        HeroNumber.Text = display;
        HeroNumber.FontSize = compact ? 62 : 92;
        HeroNumber.Visibility = display.Length > 0 && !veryCompact ? Visibility.Visible : Visibility.Collapsed;
        HeroKicker.Text = pack == null ? "GRAY RAVEN · STORY VOICE" : (category.Length > 0 && category != ChapterCatalog.Other ? category + " · " : "") + CategoryEnglish(category);
        HeroNumberChipText.Text = display.Length == 0 ? "" : char.IsDigit(display[0]) ? "CH." + display : display;
        HeroNumberChip.Visibility = display.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var section = listeningHeader ? pack?.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == listeningSession?.Current?.SectionId) : SectionBox.SelectedItem as Section;
        HeroSectionKind.Text = listeningHeader ? "LISTEN" : section != null ? "SECTION" : "CHAPTER";

        string sectionTitle = section != null ? SectionDisplayTitle(section.Id, pack) : "";
        string coverKey = preferences.ShowChapterCovers && pack != null ? pack.Id + "|" + chapterTitle + "|" + section?.Id + "|" + packFile : "";
        if (coverKey != wantedCover)
        {
            wantedCover = coverKey;
            string? path = coverKey.Length == 0 ? null : chapterCovers.Find(chapterTitle, pack!.Id, sectionTitle, section?.Id, PackFolderName(packFile), category);
            if (path == null) HeroCover.Source = null;
            else LoadCharacterImage(path, CoverDecodeHeight, image =>
            {
                if (wantedCover != coverKey) return;
                HeroCover.Source = image; ApplyHeroCover();
                if (image != null && !Hud.ReduceMotion) HeroCoverLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            });
        }
        ApplyHeroCover();

        // 小队条：当前说话人亮起暗红框，其余淡化。
        foreach (var member in new[] { SquadLucia, SquadLiv, SquadLee })
        {
            bool on = active.Length > 0 && (active == member.Speaker || active.StartsWith(member.Speaker + "·", StringComparison.Ordinal));
            member.Accent = on ? Theme.Brush("Accent") : null; member.Opacity = on || active.Length == 0 ? 1 : 0.62;
        }

        // 次级开关：跟随中为青灰，原声为暗红，其余为 OFF。
        var mode = engine?.Mode;
        Ui.SetTone(PauseButton, mode == RunMode.Following ? "teal" : "off");
        Ui.SetStateText(PauseButton, mode == RunMode.Following ? "跟随中" : mode == RunMode.Paused ? "已暂停" : "OFF");
        Ui.SetTone(OriginalButton, mode == RunMode.Original ? "red" : "off");
        Ui.SetStateText(OriginalButton, mode == RunMode.Original ? "ON" : "OFF");
        LinesHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        string count = $"{LinesList.Items.Count:000} LINES";
        LinesCount.Text = section != null ? (SectionCode(sectionTitle) is { Length: > 0 } code ? code + " · " : "") + count : count;
        if (ConfirmPlayButton.ActualWidth > 0) ManualPlaybackButtons.CenterGap = ConfirmLabelGap();
    }

    // 铭牌中央文字（含两侧 18 边距与余量）所占宽度；节点只需避开这段，不必让出整条铭牌。
    double ConfirmLabelGap()
    {
        var label = FindLabel(ConfirmPlayButton);
        double text = label is { Parent: FrameworkElement host } && host.ActualWidth > 0 ? host.ActualWidth : 180;
        return Math.Min(ConfirmPlayButton.ActualWidth + 24, text + 36 + 28);
        static TextBlock? FindLabel(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is TextBlock text) return text;
                if (FindLabel(child) is { } found) return found;
            }
            return null;
        }
    }

    void ApplyHeroCover()
    {
        bool cover = preferences.ShowChapterCovers && HeroCover.Source != null && Hero.Height >= 60;
        HeroCoverLayer.Visibility = cover ? Visibility.Visible : Visibility.Collapsed;
        HeroFallback.Visibility = HeroFallbackFade.Visibility = cover ? Visibility.Collapsed : Visibility.Visible;
        if (cover) HeroStandee.Visibility = Visibility.Collapsed;
        var light = (Brush)Hud.Solid("#F4F6F8");
        HeroTitleStack.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, cover ? light : Theme.Brush("Fg"));
        HeroNumber.Stroke = cover ? Hud.Solid("#8CFFFFFF") : Hud.Solid("#3D2A3139");
        HeroEmblem.FrameBrush = cover ? light : null;
        HeroSectionPlate.Background = cover ? Theme.Brush("Accent") : Theme.Brush("Plate");
        SquadLabel.Foreground = cover ? light : Theme.Brush("Plate");
        // 默认标题区右侧是灰鸦插画：小队条加一层浅色衬底保证可读；封面模式下透明。
        SquadStrip.Background = cover ? null : Hud.Solid("#D9F2F3F5");
        SquadSub.Foreground = cover ? Hud.Solid("#B3F4F6F8") : Theme.Brush("SubtleText");
        HeroCorruption.Opacity = cover ? 0.6 : 0.85;
        UpdateHeroArchive();
    }

    // F1b 档案徽记：只占标题文字右侧的空位（左侧留 16 的间距，永不压到标题 / SECTION 行）；
    // 可见宽度不足 110 或标题区被压矮（紧凑 / 续接模式）时隐藏。封面模式降低不透明度并加浅色柔光，立绘显示时再淡一些。
    void UpdateHeroArchive()
    {
        const double Bleed = 26, Full = 160, Min = 128;
        double heroWidth = Hero.ActualWidth, heroHeight = Hero.ActualHeight > 0 ? Hero.ActualHeight : Hero.Height;
        double textRight = HeroTitleStack.Margin.Left + HeroTitleStack.ActualWidth + 16;
        double width = Math.Min(Full, heroWidth - textRight + Bleed);
        bool trimmed = TrimTips.IsTrimmed(HeroTitle) || HeaderChapter.IsVisible && TrimTips.IsTrimmed(HeaderChapter);
        Hero.ToolTip = trimmed ? HeroTitle.Text + (HeaderChapter.IsVisible ? "\n" + HeaderChapter.Text : "") : null;
        bool show = heroWidth > 0 && heroHeight >= 100 && width >= Min;
        HeroArchive.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        HeroArchive.Width = width;
        bool cover = HeroCoverLayer.Visibility == Visibility.Visible;
        HeroArchiveVeil.Visibility = cover ? Visibility.Visible : Visibility.Collapsed;
        HeroArchiveEmblem.Opacity = cover ? 0.78 : HeroStandee.Visibility == Visibility.Visible ? 0.45 : 0.62;
    }

    // 音量行与次级开关同一行（开关在左，音量在右）；剧情页以外没有开关时回到独立一行。
    double volumeRowTop = 8;
    void AlignVolumeRow()
    {
        var deck = ManualPlaybackButtons;
        if (deck.IsVisible && deck.SecondRowWidth > 0)
        {
            double height = VolumeRow.ActualHeight > 0 ? VolumeRow.ActualHeight : 20;
            VolumeRow.Margin = new Thickness(deck.SecondRowWidth + 14, -(deck.SecondRowHeight + height) / 2, 0, 3);
        }
        else VolumeRow.Margin = new Thickness(0, volumeRowTop, 0, 3);
    }

    // 工具条：所有按钮（含代码添加的）使用带线性图标与按键角标的芯片样式；组标签在极紧凑高度下隐藏。
    HorizontalStrip? toolbarStrip;
    void InitializeThemeChrome()
    {
        // 工具条装进横向滑动条：常规高度照旧换行；极紧凑高度改为单行横滑，把高度让给台词列表。
        int toolbarIndex = ToolbarHost.Children.IndexOf(NavigationToolbar);
        ToolbarHost.Children.Remove(NavigationToolbar);
        toolbarStrip = new HorizontalStrip(NavigationToolbar, dark: false) { Wrap = true };
        ToolbarHost.Children.Insert(toolbarIndex, toolbarStrip);
        foreach (var child in NavigationToolbar.Children.OfType<FrameworkElement>())
        {
            if (child is Button button)
            {
                if (button.Style != Theme.Style("ToolChip")) { button.Style = Theme.Style("ToolChip"); button.ClearValue(MarginProperty); }
                if (Ui.GetIcon(button) == null) Ui.SetIcon(button, ToolIcon(button.Content?.ToString() ?? ""));
            }
            else if (child is CheckBox box) { box.Style = Theme.Style("ToggleChip"); box.Margin = new Thickness(0, 0, 5, 5); }
        }
        ManualPlaybackButtons.SecondRowChanged += AlignVolumeRow;
        ManualPlaybackButtons.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(AlignVolumeRow);
        VolumeRow.SizeChanged += (_, e) => { if (e.HeightChanged) AlignVolumeRow(); };
        Hero.SizeChanged += (_, _) => UpdateHeroArchive();
        HeroTitleStack.SizeChanged += (_, _) => UpdateHeroArchive();
        foreach (var text in new[] { HeroTitle, HeaderChapter })
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(text,
                (_, _) => Dispatcher.BeginInvoke(UpdateHeroArchive, System.Windows.Threading.DispatcherPriority.Loaded));
        HeroStandee.IsVisibleChanged += (_, _) => UpdateHeroArchive();
        ApplyCompactLayout();
    }

    static Geometry ToolIcon(string label) => label switch
    {
        _ when label.StartsWith("换人") => Icons.People,
        _ when label.StartsWith("重选") || label.Contains("目录") => Icons.List,
        _ when label.StartsWith("历史") => Icons.History,
        _ when label.StartsWith("撤销") => Icons.Undo,
        _ when label.StartsWith("记住") => Icons.Bookmark,
        _ when label.StartsWith("回到") || label.Contains("定位") => Icons.Locate,
        _ when label.StartsWith("反馈") => Icons.Feedback,
        _ when label.Contains("搜索") => Icons.Search,
        _ => Icons.Book,
    };
}
