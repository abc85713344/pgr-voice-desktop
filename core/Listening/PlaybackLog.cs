using System.Text;

namespace PgrVoice.Listening;

/// <summary>
/// 0.4.7 停顿 / 自动处理记录（K）：每次自动选择、按顺序续接、无音频越过、单选项自动继续、探索菜单自动读、
/// 无文本跳到下一小节、玩家反馈，以及任何停下的地方各记一条。CSV（UTF-8，带 BOM，中文表头），只保留最近 MaxEntries 条。
/// </summary>
public static class PlaybackLog
{
    public const int MaxEntries = 5000;
    public static readonly string Header = "时间,类型,模式,配音包,配音包编号,章节,小节,节点,原因,所选选项";
    public static class Types
    {
        public const string AutoSelect = "自动选择";
        public const string OrderContinue = "按顺序续接";
        public const string NoVoicePass = "无配音越过";
        public const string PendingVoicePass = "待配音越过";
        /// <summary>0.4.7 仅字幕（audioStatus "subtitle-only"）按字数停留后越过；与待配音分开统计。</summary>
        public const string SubtitleOnlyPass = "仅字幕越过";
        public const string SingleOption = "单选项自动继续";
        public const string Exploration = "探索菜单自动读";
        public const string DeadEndJump = "无文本跳到下一小节";
        public const string DeadEndChapterEnd = "无文本·本章结束";
        public const string Stop = "停下";
        public const string PlayerFeedback = "玩家反馈";
        /// <summary>0.4.7 游戏模式到达 end 节点：保持自动播放，等待游戏画面定位（原因含终点文字）。</summary>
        public const string EndWait = "终点等待定位";
    }
    static readonly object gate = new();
    static string? path;
    static int lines = -1;
    public static string? FilePath => path;
    public static void Configure(string filePath) { lock (gate) { path = filePath; lines = -1; } }

    public static void Write(string type, string mode, Pack? pack, string chapter, string section, string? nodeId, string reason, string? option = null)
    {
        string row = string.Join(",", new[] { DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), type, mode, pack?.Title ?? "", pack?.Id ?? "",
            chapter, section, nodeId ?? "", reason, option ?? "" }.Select(Csv));
        lock (gate)
        {
            Recent.Add(row); if (Recent.Count > 200) Recent.RemoveAt(0);
            if (path == null) return;
            try
            {
                if (lines < 0) lines = File.Exists(path) ? File.ReadLines(path).Count() : 0;
                if (lines == 0) { File.WriteAllText(path, "\uFEFF" + Header + "\n", new UTF8Encoding(false)); lines = 1; }
                File.AppendAllText(path, row + "\n", new UTF8Encoding(false)); lines++;
                if (lines > MaxEntries + 1000)
                {
                    var keep = File.ReadLines(path).Skip(1).TakeLast(MaxEntries).ToList();
                    File.WriteAllText(path, "\uFEFF" + Header + "\n" + string.Join("\n", keep) + "\n", new UTF8Encoding(false));
                    lines = keep.Count + 1;
                }
            }
            catch { /* 记录失败不影响播放 */ }
        }
    }

    /// <summary>导出全文（含表头与 BOM）。</summary>
    public static string Export()
    {
        lock (gate)
        {
            try { if (path != null && File.Exists(path)) { string text = File.ReadAllText(path, Encoding.UTF8); return text.StartsWith('\uFEFF') ? text : "\uFEFF" + text; } } catch { }
            return "\uFEFF" + Header + "\n" + string.Join("\n", Recent) + "\n";
        }
    }

    /// <summary>最近写入的记录（内存，测试与诊断用）。</summary>
    public static List<string> Recent { get; } = new();

    static string Csv(string? v)
    {
        v = (v ?? "").Replace("\r", " ").Replace("\n", " ");
        return v.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
