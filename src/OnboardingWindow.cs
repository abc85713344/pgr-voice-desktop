using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

internal sealed class OnboardingWindow : ExperienceDialog
{
    readonly OnboardingStore store;
    readonly OnboardingState state;
    readonly Func<Window, IReadOnlyList<PackChoice>, PackChoice?, PackChoice?> pickChapter;
    readonly Func<Window, string?> pickFolder;
    readonly Func<string, PackChoice> describe;
    readonly IReadOnlyDictionary<string, int> speakerVolumes;
    readonly Func<string, long, Task>? previewTest;
    readonly AudioService preview = new();
    readonly List<PackChoice> choices;
    readonly StackPanel body = new();
    readonly TextBlock heading = new(), note = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button back = new() { Content = "上一步" };
    internal readonly ComboBox Sections = new(), Lines = new(), Devices = new();
    internal readonly Slider Volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    internal readonly Button Trial = new() { Content = "试听这一句" }, Heard = new() { Content = "我听到了", IsEnabled = false };
    Pack? pack;
    long generation;
    bool closed, heard, trialReady, populating;
    string output;
    double volume;
    internal bool OpenSelectedChapter { get; private set; }
    internal bool OpenSoundSettings { get; private set; }
    internal string SelectedPackFile => state.PackFile;
    internal string Purpose => state.Purpose;
    internal string SelectedOutput => output;
    internal int SelectedVolume => (int)Math.Round(volume);
    internal int Step => state.Step;
    internal string Notice => note.Text;
    internal long PreviewGeneration => generation;
    internal Node? SelectedNode => (Lines.SelectedItem as LineChoice)?.Node;
    sealed record SectionChoice(Section Section) { public override string ToString() => Section.Title; }
    sealed record LineChoice(Node Node) { public override string ToString() => (Node.Speaker.Length > 0 ? Node.Speaker + "：" : "") + (Node.Text.Length > 90 ? Node.Text[..90] + "…" : Node.Text); }

    internal OnboardingWindow(OnboardingStore store, IReadOnlyList<PackChoice> available,
        Func<Window, IReadOnlyList<PackChoice>, PackChoice?, PackChoice?> pickChapter,
        Func<Window, string?> pickFolder, Func<string, PackChoice> describe, string output,
        int volume, IReadOnlyDictionary<string, int> speakerVolumes, Func<string, long, Task>? previewTest = null)
        : base("三步上手：先听到一句配音")
    {
        this.store = store; this.pickChapter = pickChapter; this.pickFolder = pickFolder; this.describe = describe;
        this.output = output; this.volume = volume; this.speakerVolumes = speakerVolumes; this.previewTest = previewTest;
        choices = available.ToList(); state = store.Load();
        if (state.Status == "completed") { state.Step = 0; state.Purpose = ""; }
        state.Status = "in-progress"; state.Step = Math.Clamp(state.Step, 0, 2);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        heading.FontSize = 23; heading.Margin = new Thickness(0, 0, 0, 18); root.Children.Add(heading);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        note.Margin = new Thickness(0, 0, 0, 12); footer.Children.Add(note);
        var actions = new WrapPanel(); footer.Children.Add(actions); actions.Children.Add(back);
        back.Margin = new Thickness(0, 0, 10, 0); back.Padding = new Thickness(16, 8, 16, 8);
        back.Click += (_, _) => { StopTrial(); heard = false; state.Step = Math.Max(0, state.Step - 1); Persist(); Render(); };
        AddButton(actions, "暂时跳过", Skip);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        preview.PlaybackCompleted += id => Dispatch(() => CompletePreview(id));
        preview.PlaybackRequestFailed += (id, reason) => Dispatch(() => FailPreview(id, reason));
        Trial.Click += async (_, _) => await StartTrial();
        Heard.Click += (_, _) => ConfirmHeard();
        Sections.SelectionChanged += (_, _) => { if (!populating) FillLines(); };
        Lines.SelectionChanged += (_, _) => { if (!populating) { StopTrial(); state.NodeId = SelectedNode?.Id ?? ""; Persist(); ShowLine(); } };
        Devices.SelectionChanged += (_, _) => { if (!populating && Devices.SelectedItem is AudioDeviceOption device) { StopTrial(); output = device.Id; } };
        Volume.ValueChanged += (_, _) => { if (!populating) { StopTrial(); this.volume = Volume.Value; } };
        Closed += (_, _) =>
        {
            closed = true; StopTrial(); preview.Dispose();
            if (state.Status == "in-progress") state.Status = "dismissed";
            Persist();
        };
        if (File.Exists(state.PackFile))
            try { pack = DesktopPackLoader.Load(state.PackFile); } catch { state.PackFile = ""; state.NodeId = ""; }
        if (pack == null && state.Step > 1) state.Step = 1;
        Persist(); Render();
    }
    void Dispatch(Action action) { if (!closed) Dispatcher.BeginInvoke(() => { if (!closed) action(); }); }
    void Persist()
    {
        try { store.Save(state); }
        catch (Exception ex) { Log.Write("onboarding", ex.Message); note.Text = "引导记录暂时无法保存，本次仍可继续。"; }
    }
    Button AddButton(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 12), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
    void Text(string text, int size = 14)
    { body.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) }); }
    internal void ChoosePurpose(string purpose)
    { StopTrial(); state.Purpose = purpose; state.Step = 1; heard = false; Persist(); Render(); }
    void Render()
    {
        body.Children.Clear(); note.Text = "随时可以跳过，之后在设置里重新打开。";
        back.IsEnabled = state.Step > 0;
        heading.Text = state.Step switch { 0 => "1 / 3　你想怎么用？", 1 => "2 / 3　准备一个章节", 2 => "3 / 3　试着听一句", _ => "已经听到配音了" };
        if (state.Step == 0)
        {
            Text("先选一种用法。你随时可以在播放器里切换。", 16);
            AddButton(body, "单独听剧情", () => ChoosePurpose("listening")); Text("像听书一样播放，不需要打开游戏。", 13);
            AddButton(body, "边玩边听", () => ChoosePurpose("game")); Text("先确认声音，再连接游戏并选择跟随方式。", 13);
        }
        else if (state.Step == 1)
        {
            Text("软件和章节配音分别提供。已有章节无需重新下载。", 16);
            if (choices.Count > 0) AddButton(body, "选择已有章节…", () =>
            { var chosen = pickChapter(this, choices, choices.FirstOrDefault(c => c.File == state.PackFile)); if (chosen != null) SelectPack(chosen.File); });
            AddButton(body, "选择配音包目录…", PickDirectory);
            Text("下载的章节 ZIP 先解压，再选择解压后的单章目录或包含各章的总目录。还没有配音包时，请从软件发布者提供的配音分享入口获取。", 13);
            Text(pack == null ? "当前还没有选中章节。" : "已选：" + ChapterCatalog.Title(pack.Title));
            var next = AddButton(body, "选好了，去试听", () => { if (pack != null) { state.Step = 2; Persist(); Render(); } });
            next.IsEnabled = pack != null;
        }
        else if (state.Step == 2 && pack != null)
        {
            Text(ChapterCatalog.Title(pack.Title), 17); Text("选择小节和一句台词。试听只播这一句，不改变原有播放进度。", 13);
            populating = true;
            Sections.ItemsSource = pack.Chapters.SelectMany(c => c.Sections).Select(s => new SectionChoice(s)).ToArray();
            Sections.SelectedItem = Sections.Items.OfType<SectionChoice>().FirstOrDefault(s => pack.ById.GetValueOrDefault(state.NodeId)?.SectionId == s.Section.Id) ?? Sections.Items.OfType<SectionChoice>().FirstOrDefault();
            Sections.Margin = new Thickness(0, 0, 0, 10); Lines.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(Sections); body.Children.Add(Lines);
            preview.SelectDevice(output); Devices.ItemsSource = preview.GetDevices();
            Devices.SelectedItem = Devices.Items.OfType<AudioDeviceOption>().FirstOrDefault(d => d.Id == output);
            Text("声音输出", 13); body.Children.Add(Devices);
            Text("试听音量（0—100%）", 13); Volume.Value = volume; body.Children.Add(Volume);
            populating = false; FillLines();
            var actions = new WrapPanel(); body.Children.Add(actions);
            foreach (var button in new[] { Trial, Heard }) { (button.Parent as Panel)?.Children.Remove(button); button.Padding = new Thickness(16, 10, 16, 10); button.Margin = new Thickness(0, 12, 10, 8); actions.Children.Add(button); }
            AddButton(body, "没有声音，帮我检查", ShowHelp);
        }
        else if (state.Step == 3)
        {
            Text(state.Purpose == "game" ? "接下来连接游戏" : "可以开始听剧情了", 18);
            Text(state.Purpose == "game"
                ? "先打开战双并进入这章剧情。完成后会打开游戏配音页；在设置中选择游戏目录或窗口，再选择适合的跟随方式并主动开始。暂不使用跟随时也能手动播放。"
                : "完成后会打开刚才选中的章节。点击“播放 / 续听”开始；已有听书进度会保留。", 15);
            AddButton(body, "完成并打开这一章", Finish);
            if (state.Purpose == "game") Text("这一步没有替你连接游戏或开启自动点击。", 13);
        }
    }
    internal void PickDirectory()
    {
        string? folder = pickFolder(this); if (folder == null) return;
        try
        {
            var direct = Path.Combine(folder, "pack.json");
            var files = File.Exists(direct) ? new List<string> { direct } : LibraryPaths.Packs(folder);
            if (files.Count == 0) { note.Text = "这个目录没有找到章节。请先解压章节 ZIP，再选择单章目录或章节总目录。"; return; }
            var found = files.Select(describe).ToArray();
            if (found.Length == 1) { if (SelectPack(found[0].File)) state.Folder = folder; }
            else
            {
                var chosen = pickChapter(this, found, null); if (chosen == null) return;
                if (SelectPack(chosen.File)) state.Folder = folder;
            }
            foreach (var item in found) if (!choices.Any(c => c.File == item.File)) choices.Add(item);
            Persist();
        }
        catch (Exception ex) { note.Text = "暂时无法读取这个目录：" + ex.Message; }
    }
    internal bool SelectPack(string path)
    {
        try
        {
            var selected = DesktopPackLoader.Load(path); StopTrial(); pack = selected;
            state.PackFile = path; state.NodeId = ""; heard = false; Persist(); Render(); return true;
        }
        catch (Exception ex) { note.Text = "章节暂时无法读取：" + ex.Message; return false; }
    }
    void FillLines()
    {
        StopTrial(); populating = true;
        Lines.ItemsSource = pack?.Nodes.Where(n => n.Kind == "line" && n.SectionId == (Sections.SelectedItem as SectionChoice)?.Section.Id).Select(n => new LineChoice(n)).ToArray();
        Lines.SelectedItem = Lines.Items.OfType<LineChoice>().FirstOrDefault(l => l.Node.Id == state.NodeId) ?? Lines.Items.OfType<LineChoice>().FirstOrDefault();
        state.NodeId = SelectedNode?.Id ?? ""; populating = false; Persist(); ShowLine();
    }
    void ShowLine() { Trial.IsEnabled = SelectedNode != null; note.Text = SelectedNode is { } node ? node.Speaker + "：" + node.Text : "这个小节没有可试听的台词，请选另一个小节。"; }
    internal async Task StartTrial()
    {
        StopTrial(); var node = SelectedNode;
        if (pack == null || node == null) return;
        string? file;
        try { file = pack.ResolveAudio(node); }
        catch (Exception ex) { note.Text = "这一句音频暂不可用：" + ex.Message; return; }
        if (file == null || !File.Exists(file)) { note.Text = "这一句没有可用音频。请换一句，或重新下载并解压完整章节。"; return; }
        float effective = SpeakerVolume.Apply((float)volume / 100, speakerVolumes, node.Speaker);
        if (effective <= 0) { note.Text = "总音量或这个角色的音量为 0。请调高音量，或从设置调整角色音量后重试。"; return; }
        long id = generation; Trial.IsEnabled = false; note.Text = "正在试听这一句，播完后请确认是否听到。";
        try
        {
            preview.SelectDevice(output); preview.Volume = effective;
            if (previewTest != null) { await previewTest(file, id); CompletePreview(id); }
            else await Task.Run(() => preview.Play(file, id, shouldPlay: () => !closed && Volatile.Read(ref generation) == id));
        }
        catch (Exception ex) { FailPreview(id, "试听失败：" + ex.Message); }
    }
    internal void CompletePreview(long id)
    {
        if (closed || id != generation || state.Step != 2) return;
        trialReady = true; Heard.IsEnabled = true; Trial.IsEnabled = true; note.Text = "这一句已播放完。你听到声音了吗？";
    }
    void FailPreview(long id, string reason)
    { if (!closed && id == generation) { StopTrial(); note.Text = reason; } }
    void StopTrial()
    { Interlocked.Increment(ref generation); trialReady = false; Heard.IsEnabled = false; Trial.IsEnabled = true; preview.Stop(); }
    internal void ConfirmHeard()
    {
        if (!trialReady || !Heard.IsEnabled || SelectedNode == null) return;
        StopTrial(); heard = true; state.Step = 3; Persist(); Render();
    }
    void ShowHelp()
    {
        StopTrial();
        note.Text = "请确认耳机/扬声器已接好、Windows 音量没有静音，并选择正确的声音输出。调高这里的音量后再次试听；角色音量为 0 时需到设置调整。音频文件缺失则重新解压章节。";
    }
    internal void Finish()
    {
        if (!heard || pack == null || state.Step != 3) return;
        try
        {
            var latest = DesktopPackLoader.Load(state.PackFile);
            var original = pack.ById.GetValueOrDefault(state.NodeId);
            var current = latest.ById.GetValueOrDefault(state.NodeId);
            if (original == null || current == null || original.Text != current.Text || original.Speaker != current.Speaker || original.Audio != current.Audio || latest.ResolveAudio(current) is not string path || !File.Exists(path))
                throw new InvalidDataException("章节或试听音频已变化，请重新试听。");
        }
        catch (Exception ex)
        {
            heard = false; state.Step = 2; Persist(); Render(); note.Text = "暂时无法完成：" + ex.Message; return;
        }
        state.Status = "completed"; Persist(); OpenSelectedChapter = true; DialogResult = true;
    }
    internal void Skip() { state.Status = "skipped"; Persist(); Close(); }
}
