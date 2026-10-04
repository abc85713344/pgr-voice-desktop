using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PgrVoice;

public static class GameTextPolicy
{
    public static string Normalize(string text)
    {
        string plain = Regex.Replace(text, "</?(?:color|size|b|i|material)(?:=[^>]*)?>", "", RegexOptions.IgnoreCase);
        // 游戏与台本使用不同的实心遮挡块；统一字形但保留每一个块，不能删掉被遮挡部分。
        return Matcher.Normalize(plain.Replace('▆', '■').Replace('▇', '■').Replace('█', '■'));
    }
    public static string SpeakerName(string text) => Regex.Replace(text, "<[^>]+>", "").Trim();
    public static Node? Match(Pack pack, string section, string text, out string reason, string speaker = "")
    {
        string normalized = Normalize(text);
        string visible = SpeakerName(text);
        bool punctuation = AutoPlaybackLinePolicy.IsPunctuationText(visible);
        if (normalized.Length == 0 && !punctuation) { reason = "当前句只有符号，继续等待下一句。"; return null; }
        // 纯标点必须保留原形精确匹配，不能把去标点后的空串互相匹配。
        var matches = pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section &&
            (punctuation ? SpeakerName(n.Text) == visible : Normalize(n.Text) == normalized)).ToList();
        // 先保留标点区分“维多利亚？！”与“维多利亚！”，仅格式差异再退回规范化匹配。
        var exact = matches.Where(n => SpeakerName(n.Text) == SpeakerName(text)).ToList();
        if (exact.Count > 0) matches = exact;
        if (matches.Count > 1 && SpeakerName(speaker).Length > 0)
            matches = matches.Where(n => SpeakerName(n.Speaker).Equals(SpeakerName(speaker), StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            reason = matches.Count == 0 ? "当前正文与角色暂未匹配，继续等待下一句；请核对章节和小节。" : "当前正文与角色仍对应多个位置，暂不播放这句，继续等待下一句。";
            return null;
        }
        reason = ""; return matches[0];
    }
}
