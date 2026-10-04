using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PgrVoice;

public partial class MainWindow
{
    readonly CheckBox textAutoBox = new() { Content = "配音播完后自动点击下一句", Margin = new Thickness(0, 8, 0, 4) };
    readonly ComboBox textAutoDelay = new() { ItemsSource = new[] { "1 秒", "1.5 秒（默认）", "2 秒" }, SelectedIndex = 1, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Button textAutoPosition = new() { Content = "设置自动点击位置", Margin = new Thickness(0, 5, 0, 3) };
    readonly TextBlock textAutoStatus = new() { Text = "自动点击未开启。", TextWrapping = TextWrapping.Wrap };
    sealed record TextAutoLine(PlaybackEngine Owner, string Node, TextProbe Probe, string Text, string Speaker,
        long AudioTicket, IntPtr Window, DesktopAdvanceInput.PixelRect Bounds, DesktopAdvanceInput.PixelRect Zone)
    {
        public long? CompletedAt;
        public long ClickAt;
        public bool FocusHeld;
        public bool SilentPause;
    }
    TextAutoLine? textAutoLine;
    // 隔离验收替换环境、内存样本与点击；生产运行无法使用这些替身。
    Func<bool>? textAutoEnvironmentTest;
    Func<bool>? textAutoForegroundTest;
    Func<GameTextSample>? textAutoSampleTest;
    Func<bool>? textAutoClickTest;
    Func<long>? textAutoClockTest;
    long TextAutoNow() => testUi && textAutoClockTest != null ? textAutoClockTest() : (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    int TextAutoDelayMs => new[] { 1000, 1500, 2000 }[Math.Clamp(textAutoDelay.SelectedIndex, 0, 2)];

    void InitializeTextAutoAdvance(StackPanel panel)
    {
        textAutoBox.IsChecked = preferences.TextAutoAdvanceEnabled;
        textAutoDelay.SelectedIndex = Array.IndexOf(new[] { 1d, 1.5, 2d }, preferences.TextAutoDelaySeconds);
        if (textAutoDelay.SelectedIndex < 0) textAutoDelay.SelectedIndex = 1;
        panel.Children.Add(textAutoBox);
        var options = new WrapPanel { Margin = new Thickness(0, 3, 0, 4) };
        options.Children.Add(new TextBlock { Text = "播完等待", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        textAutoDelay.Width = 160; textAutoDelay.Margin = new Thickness(0, 0, 8, 0); options.Children.Add(textAutoDelay);
        textAutoPosition.Margin = new Thickness(0); textAutoPosition.Padding = new Thickness(8, 6, 8, 6); options.Children.Add(textAutoPosition); panel.Children.Add(options);
        textAutoStatus.FontSize = 11; textAutoStatus.Foreground = Theme.Brush("MutedText"); panel.Children.Add(textAutoStatus);
        textAutoStatus.Text = textAutoBox.IsChecked == true ? "已开启；设置位置后，从当前句开始配音。" : "关闭时由你点击游戏，配音仍会自动跟随。";
        panel.Children.Add(new TextBlock { Text = "切出游戏时继续配音、暂缓点击；回到游戏后核对并重新等待。遇到分支等你选择。请关闭游戏自带自动播放。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 3, 0, 2) });
        textAutoBox.Checked += (_, _) => { preferences.TextAutoAdvanceEnabled = true; CancelTextAutoAdvance(); textAutoStatus.Text = "已开启。点击开始 / 恢复配音，从当前句开始。"; UpdateGameTextModeLabel(); Save(); };
        textAutoBox.Unchecked += (_, _) => { preferences.TextAutoAdvanceEnabled = false; CancelTextAutoAdvance(); textAutoStatus.Text = "关闭时由你点击游戏，配音仍会自动跟随。"; UpdateGameTextModeLabel(); Save(); };
        textAutoDelay.SelectionChanged += (_, _) =>
        {
            preferences.TextAutoDelaySeconds = TextAutoDelayMs / 1000d;
            if (textAutoLine is { CompletedAt: not null, SilentPause: false } line) line.ClickAt = line.CompletedAt.Value + TextAutoDelayMs;
            Save();
        };
        textAutoPosition.Click += (_, _) =>
        {
            CancelTextAutoAdvance();
            clickZoneBox.IsChecked = true;
            editingClickZone = !editingClickZone; UpdateClickZone();
            if (!editingClickZone) { preferences.ClickZoneLeft = clickZone.Left; preferences.ClickZoneTop = clickZone.Top; Save(); }
            textAutoPosition.Content = editingClickZone ? "完成点击位置设置" : "设置自动点击位置";
            textAutoStatus.Text = editingClickZone ? "把“下”字标记拖到游戏里可点击继续的位置，然后点击完成。" : "位置已保存。点击开始 / 恢复配音。";
        };
        audio.PlaybackCompleted += ticket => Dispatcher.BeginInvoke(() => OnTextAudioCompleted(ticket));
        audio.PlaybackRequestFailed += (ticket, reason) => Dispatcher.BeginInvoke(() =>
        { if (textAutoLine?.AudioTicket == ticket) CancelTextAutoAdvance("配音播放中断，本句不会自动点击。" + reason); });
    }
    void CancelTextAutoAdvance(string? reason = null)
    {
        bool pending = textAutoLine != null; textAutoLine = null;
        if (pending && reason != null) { textAutoStatus.Text = reason; Log.Write("text-auto-cancel", reason); }
    }
    bool TextAutoHasFocus() => testUi && textAutoForegroundTest != null ? textAutoForegroundTest() : GameIsForeground();
    void HoldTextAutoForFocus()
    {
        if (textAutoLine is not { } line || line.FocusHeld) return;
        line.FocusHeld = true;
        textAutoStatus.Text = "配音与文字跟随继续；回到游戏后，核对当前句并重新等待再点击。";
        Log.Write("text-auto-background", "保留当前配音，等待游戏回到前台。");
    }
    void ObserveTextAutoInput(bool gameInput, string reason)
    {
        if (textAutoLine == null) return;
        // 从其他窗口点回游戏的第一下可能仅激活窗口，也可能推进对白。
        // 保留监听并重新校验文字；如果真的换句，旧音频票据会被换句流程取消。
        if (!gameInput || textAutoLine.FocusHeld) HoldTextAutoForFocus();
        else CancelTextAutoAdvance(reason);
    }
    void CaptureTextAutoLine(TextProbe probe, PlaybackEngine owner, string text, string speaker)
    {
        CancelTextAutoAdvance();
        if (textAutoBox.IsChecked != true) return;
        var bounds = default(DesktopAdvanceInput.PixelRect); var zone = default(DesktopAdvanceInput.PixelRect);
        bool simulated = testUi && textAutoEnvironmentTest != null;
        if (!simulated && (game == null || !preferences.ClickZoneEnabled || !clickZone.IsVisible || editingClickZone ||
            !DesktopAdvanceInput.ClientBounds(game.Handle, out bounds) || !DesktopAdvanceInput.GetWindowRect(new WindowInteropHelper(clickZone).Handle, out zone)))
        { textAutoStatus.Text = "配音继续播放；请先设置自动点击位置。"; return; }
        textAutoLine = new(owner, owner.CurrentId!, probe, text, speaker, audioRequest, game?.Handle ?? IntPtr.Zero, bounds, zone);
        if (owner.Current is { } node && AutoPlaybackLinePolicy.IsSilentPunctuation(node))
        {
            textAutoLine.SilentPause = true;
            textAutoLine.CompletedAt = TextAutoNow();
            textAutoLine.ClickAt = textAutoLine.CompletedAt.Value + AutoPlaybackLinePolicy.SilentPauseMilliseconds;
            textAutoStatus.Text = "标点停顿 · 1.2 秒后核对并继续。";
            return;
        }
        textAutoStatus.Text = "等当前配音播完，再等待 " + (TextAutoDelayMs / 1000d).ToString("0.#") + " 秒。";
    }
    bool TextAutoEnvironment(TextAutoLine line)
    {
        if (testUi && textAutoEnvironmentTest != null) return textAutoEnvironmentTest();
        if (closing || locating || ListeningActive || KeyTesting || recordingAction != null || branchMenu.IsVisible || GamepadActiveInput ||
            game?.Handle != line.Window || !Native.IsGameWindow(line.Window, preferences.GameExecutablePath) ||
            !preferences.ClickZoneEnabled || !clickZone.IsVisible || !clickZone.IsClickThrough || editingClickZone) return false;
        return DesktopAdvanceInput.ClientBounds(line.Window, out var bounds) && bounds.Equals(line.Bounds) &&
            DesktopAdvanceInput.GetWindowRect(new WindowInteropHelper(clickZone).Handle, out var zone) && zone.Equals(line.Zone) &&
            bounds.Contains((zone.Left + zone.Right) / 2, (zone.Top + zone.Bottom) / 2);
    }
    bool TextAutoCurrent(TextAutoLine line)
    {
        if (closing || !textArmed || textAutoBox.IsChecked != true || engine != line.Owner || textOwner != line.Owner ||
            engine.CurrentId != line.Node || engine.Mode != RunMode.Following || audioRequest != line.AudioTicket ||
            engine.Current?.SectionId != textSection || textActiveConflict ||
            !line.Probe.Active || line.Probe.Observed != line.Text || line.Probe.ObservedSpeaker != line.Speaker ||
            textProbes.Where(p => p.Active).Any(p => p.Observed != line.Text || p.ObservedSpeaker != line.Speaker)) return false;
        var sample = testUi && textAutoSampleTest != null ? textAutoSampleTest() : line.Probe.Reader?.Sample();
        return sample is { Valid: true, Active: true } && sample.Text == line.Text && sample.Speaker == line.Speaker;
    }
    bool TextAutoCanContinue(TextAutoLine line)
    {
        var current = line.Owner.Current;
        if (current?.NextId == null || !line.Owner.Pack.ById.TryGetValue(current.NextId, out var next) || next.Archived ||
            next.Kind != "line" || next.SectionId != textSection || next.PathId != current.PathId)
        { CancelTextAutoAdvance("本段已到分支或边界，请手动继续；读到新台词后会接上配音。"); return false; }
        if (!AutoPlaybackLinePolicy.CanPlay(line.Owner.Pack, next))
        { CancelTextAutoAdvance("下一句暂缺可用配音，请手动继续。后面的台词仍会自动匹配。"); return false; }
        return true;
    }
    void OnTextAudioCompleted(long ticket)
    {
        if (textAutoLine is not { } line || ticket != line.AudioTicket || line.CompletedAt != null) return;
        try
        {
            if (!TextAutoCurrent(line)) { CancelTextAutoAdvance("当前句已变化，本次自动点击已取消。"); return; }
            if (!TextAutoCanContinue(line)) return;
            line.CompletedAt = TextAutoNow(); line.ClickAt = line.CompletedAt.Value + TextAutoDelayMs;
            textAutoStatus.Text = "配音已播完，等待 " + (TextAutoDelayMs / 1000d).ToString("0.#") + " 秒后点击下一句。";
            Log.Write("text-auto-completed", $"节点={line.Node}\t请求={ticket}\t等待毫秒={TextAutoDelayMs}");
            if (!TextAutoHasFocus()) { line.FocusHeld = false; HoldTextAutoForFocus(); }
        }
        catch (Exception ex) { CancelTextAutoAdvance("本次自动点击已取消：" + ex.Message); }
    }
    void TickTextAutoAdvance()
    {
        if (textAutoLine is not { } line) return;
        try
        {
            if (!TextAutoCurrent(line)) { CancelTextAutoAdvance("当前句或播放状态已变化，自动点击已取消。"); return; }
            if (!TextAutoHasFocus()) { HoldTextAutoForFocus(); return; }
            if (!TextAutoEnvironment(line)) { CancelTextAutoAdvance("游戏或点击位置已变化，自动点击已取消；请核对位置后恢复。"); return; }
            if (line.FocusHeld)
            {
                line.FocusHeld = false;
                if (line.CompletedAt != null) line.ClickAt = TextAutoNow() +
                    (line.SilentPause ? AutoPlaybackLinePolicy.SilentPauseMilliseconds : TextAutoDelayMs);
                textAutoStatus.Text = line.CompletedAt == null ? "已回到游戏，等待当前配音播完。" : "已回到游戏，当前句一致，重新等待后点击下一句。";
                Log.Write("text-auto-foreground", textAutoStatus.Text);
                return;
            }
            if (line.CompletedAt == null || TextAutoNow() < line.ClickAt) return;
            if (!TextAutoCanContinue(line)) return;
            // 先消费本句资格，任何失败、回调重复或点击后未换句都不补点。
            textAutoLine = null;
            bool clicked; string error = "本次点击未完成，请手动继续。";
            if (testUi && textAutoClickTest != null) clicked = textAutoClickTest();
            else
            {
                clickZone.Hide();
                try { clicked = DesktopAdvanceInput.TryClick(line.Window, line.Bounds, (line.Zone.Left + line.Zone.Right) / 2, (line.Zone.Top + line.Zone.Bottom) / 2, preferences.GameExecutablePath, out error); }
                finally { if (!closing) UpdateClickZone(); }
            }
            textAutoStatus.Text = clicked ? "已点击一次，等待游戏出现新台词。" : error;
            Log.Write(clicked ? "text-auto-click" : "text-auto-click-failed", $"节点={line.Node}\t请求={line.AudioTicket}\t播完后经过毫秒={TextAutoNow() - line.CompletedAt.Value}\t{textAutoStatus.Text}");
        }
        catch (Exception ex) { textAutoLine = null; textAutoStatus.Text = "自动点击已取消：" + ex.Message; Log.Write("text-auto-error", ex.Message); }
    }
}
