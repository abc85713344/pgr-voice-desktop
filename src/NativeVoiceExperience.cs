using System;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

/// <summary>“游戏有原配时静音配音包”：只在边玩边听生效；听书、试听、历史预览照常出声。</summary>
public partial class MainWindow
{
    internal const string NativeVoiceStatus = "正在听游戏原配";
    internal const string NativeVoiceWave = "▂▅▃▆";
    bool playingNative;
    // 原配句按游戏配置时长推算的原声结束时刻（毫秒时钟）；不知道时长或不是原配为 0。
    long nativeVoiceUntil;
    const int NativeVoiceTailMilliseconds = 250;
    long NativeClockMs() => TextAutoNow();
    void MarkNativeVoiceStart(Node? node, bool native) =>
        nativeVoiceUntil = native && NativeVoiceCatalog.Shared?.FindLineVoice(engine?.Pack, node)?.NativeDurationMs is int ms ? NativeClockMs() + ms + NativeVoiceTailMilliseconds : 0;
    /// <summary>静音占位播完后，游戏原声预计还要多久结束；自动推进先等完这段，再按用户设置的间隔点下一句。</summary>
    int NativeVoiceRemainingMs() => playingNative && nativeVoiceUntil > 0 ? (int)Math.Clamp(nativeVoiceUntil - NativeClockMs(), 0, 50_000) : 0;

    /// <summary>当前章这一句由游戏播原配（开关打开、本章没选“用配音包”、且是清单核过的原配台词）。</summary>
    bool NativeVoiceLine(Node? node) =>
        engine != null && preferences.NativeVoiceMute && preferences.NativeVoiceDubPacks?.Contains(engine.Pack.Id) != true &&
        NativeVoiceCatalog.Shared?.FindLineVoice(engine.Pack, node) != null;

    static int NativeVoiceCount(string packId) => NativeVoiceCatalog.Shared?.CountConfiguredVoices(packId) ?? 0;

    void SetNativeVoiceForPack(string packId, bool useNative)
    {
        preferences.NativeVoiceDubPacks ??= new();
        if (useNative) preferences.NativeVoiceDubPacks.Remove(packId); else preferences.NativeVoiceDubPacks.Add(packId);
        Save(); UpdateState();
    }

    void AddNativeVoiceSettings(Panel panel)
    {
        int total = NativeVoiceCatalog.Shared?.Statistics.ConfiguredEntries ?? 0;
        var box = new CheckBox { Content = "游戏有原配时静音配音包", IsChecked = preferences.NativeVoiceMute, Margin = new Thickness(0, 10, 0, 2) };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = $"已收录 {total} 句游戏原配。轮到这些句子时配音包静音，由游戏播原声；听书不受影响。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("MutedText") });
        var tip = new Border
        {
            Background = Theme.Brush("TealFill"), Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock { Text = "请在游戏设置里打开角色语音；游戏语音关掉时，这些句子两边都没有声音。可在章节选择里按章选择“用原配 / 用配音包”。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Brush("TealText") },
            Visibility = preferences.NativeVoiceMute ? Visibility.Visible : Visibility.Collapsed
        };
        panel.Children.Add(tip);
        void Set(bool on)
        {
            preferences.NativeVoiceMute = on; tip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Save(); UpdateState();
        }
        box.Checked += (_, _) => Set(true);
        box.Unchecked += (_, _) => Set(false);
    }
}
