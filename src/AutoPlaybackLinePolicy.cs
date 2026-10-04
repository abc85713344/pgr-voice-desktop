using System;
using System.Linq;

namespace PgrVoice;

internal static class AutoPlaybackLinePolicy
{
    public const int SilentPauseMilliseconds = 1200;

    // 只识别非空的纯标点正文；动作说明、数字、空白及非标点符号不属于停顿。
    public static bool IsPunctuationText(string text) => !string.IsNullOrWhiteSpace(text) &&
        text.Any(char.IsPunctuation) && text.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c));

    // 已声明录音时仍走正常播放与文件校验，不能掩盖损坏的配音包。
    public static bool IsSilentPunctuation(Node node) => node.Kind == "line" &&
        string.IsNullOrWhiteSpace(node.Audio) && IsPunctuationText(node.Text);

    public static bool CanPlay(Pack pack, Node node) => IsSilentPunctuation(node) ||
        pack.AudioNotice(node).Length == 0 && pack.ResolveAudio(node) != null;
}
