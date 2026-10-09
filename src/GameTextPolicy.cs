using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PgrVoice;

public static class GameTextPolicy
{
    const string PlayerNameSlot = "【指挥官姓名】";
    sealed class NameCatalog
    {
        public int SchemaVersion { get; set; }
        public string Placeholder { get; set; } = "";
        public List<NameTemplate> Entries { get; set; } = new();
    }
    sealed class NameTemplate
    {
        public string PackId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public string SectionId { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
        public string Template { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public Regex? Pattern { get; set; }
    }
    static readonly Lazy<Dictionary<(string Pack, string Node), NameTemplate>> NameTemplates = new(LoadNameTemplates);
    static Dictionary<(string Pack, string Node), NameTemplate> LoadNameTemplates()
    {
        var result = new Dictionary<(string Pack, string Node), NameTemplate>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GamePlayerNameTemplates.json");
            if (stream == null) return result;
            var catalog = JsonSerializer.Deserialize<NameCatalog>(stream, Json.Options);
            if (catalog?.SchemaVersion != 1 || catalog.Placeholder != PlayerNameSlot || catalog.Entries == null) return result;
            foreach (var group in catalog.Entries.Where(e => e != null).GroupBy(e => (e.PackId, e.NodeId)))
            {
                // 重复身份的模板也不能靠文件次序选择；更新后的包必须逐字段仍相同。
                if (group.Count() != 1) continue;
                var entry = group.Single();
                if (string.IsNullOrEmpty(entry.PackId) || string.IsNullOrEmpty(entry.NodeId) ||
                    string.IsNullOrEmpty(entry.SectionId) || string.IsNullOrEmpty(entry.Text) ||
                    string.IsNullOrEmpty(entry.Template) || entry.Speaker == null) continue;
                entry.Pattern = PlayerNamePattern(entry.Template);
                if (entry.Pattern != null) result.Add(group.Key, entry);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException)
        { result.Clear(); } // 模板不可用时保留原来的精确匹配，不猜测昵称位置。
        return result;
    }
    static string VisibleStructure(string text) => Regex.Replace(text,
        "</?(?:color|size|b|i|material)(?:=[^>]*)?>", "", RegexOptions.IgnoreCase)
        .Normalize(NormalizationForm.FormKC).Replace('▆', '■').Replace('▇', '■').Replace('█', '■');
    static Regex? PlayerNamePattern(string template)
    {
        if (template.Length > 6000) return null;
        var parts = VisibleStructure(template).Split(PlayerNameSlot, StringSplitOptions.None);
        if (parts.Length < 2 || parts.Length > 9 || parts.Sum(p => p.Count(char.IsLetterOrDigit)) < 12) return null;
        var pattern = new StringBuilder(@"\A\s*");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                pattern.Append(i == 1 ? @"(?<name>[\p{L}\p{M}\p{Nd}_·•・\-]{1,16})\s*" : @"\k<name>\s*");
            // 仅容许固定正文中的布局空白；槽内不能跨空白、逗号或句号吞掉正文。
            foreach (char c in parts[i].Where(c => !char.IsWhiteSpace(c))) pattern.Append(Regex.Escape(c.ToString())).Append(@"\s*");
        }
        return new Regex(pattern.Append(@"\z").ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
    static bool MatchesNamePattern(Regex pattern, string text)
    {
        if (text.Length > 6000) return false;
        try
        {
            var match = pattern.Match(VisibleStructure(text));
            return match.Success && match.Groups["name"].Value.Any(char.IsLetterOrDigit);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
    internal static bool MatchesPlayerNameTemplate(string template, string text)
        => PlayerNamePattern(template) is Regex pattern && MatchesNamePattern(pattern, text);
    static bool MatchesPlayerName(Pack pack, Node node, string text, string speaker)
    {
        if (!NameTemplates.Value.TryGetValue((pack.Id, node.Id), out var entry) || entry.Pattern == null ||
            node.SectionId != entry.SectionId || node.Speaker != entry.Speaker || node.Text != entry.Text ||
            (SpeakerName(speaker).Length > 0 && SpeakerName(node.Speaker) != SpeakerName(speaker))) return false;
        return MatchesNamePattern(entry.Pattern, text);
    }
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
        // 原始提取中的明确姓名槽才可回退；不能把普通“指挥官”称谓或任意姓名当作变量。
        if (matches.Count == 0 && !punctuation)
            matches = pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section &&
                MatchesPlayerName(pack, n, text, speaker)).ToList();
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
