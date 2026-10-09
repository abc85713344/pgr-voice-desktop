using System.Text;

namespace PgrVoice;

/// <summary>打开反馈时冻结内容，后续播放和包更新不会改变反馈对象。</summary>
public sealed record LineFeedbackSnapshot
{
    public DateTimeOffset CapturedUtc { get; init; }
    public string AppVersion { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Mode { get; init; } = "";
    public string PackId { get; init; } = "";
    public string PackTitle { get; init; } = "";
    public string Revision { get; init; } = "";
    public string NavigationFingerprint { get; init; } = "";
    public string ChapterTitle { get; init; } = "";
    public string SectionId { get; init; } = "";
    public string SectionTitle { get; init; } = "";
    public string NodeId { get; init; } = "";
    public string Speaker { get; init; } = "";
    public string Text { get; init; } = "";
    public string PathId { get; init; } = "";
    public string AudioReference { get; init; } = "";
    public string AudioStatus { get; init; } = "";
    public string RecentContext { get; init; } = "";
}

public static class LineFeedback
{
    public static IReadOnlyList<string> Categories { get; } =
        Array.AsReadOnly(new[] { "播错了", "没声音", "跟丢了", "读音问题", "其他" });

    public static LineFeedbackSnapshot Capture(Pack pack, Node node, string appVersion,
        string platform, string mode, IEnumerable<Node>? recentNodes = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(node);
        if (!pack.ById.TryGetValue(node.Id, out var canonical) || !ReferenceEquals(canonical, node))
            throw new ArgumentException("反馈台词不属于当前配音包。", nameof(node));
        var chapter = pack.Chapters.FirstOrDefault(c => c.Sections.Any(s => s.Id == node.SectionId));
        var section = chapter?.Sections.FirstOrDefault(s => s.Id == node.SectionId);
        // 仅收录调用方提供的真实历史，绝不按文件排列猜前后句或跨分支寻找上下文。
        var context = (recentNodes ?? Array.Empty<Node>())
            .Where(n => n != null && n.Id != node.Id && n.Kind == "line" &&
                pack.ById.TryGetValue(n.Id, out var original) && ReferenceEquals(n, original))
            .TakeLast(3).Select(n => $"[{n.Id}] {SpeakerVolume.DisplayName(n.Speaker)}：{n.Text}");
        var audio = pack.ResolveAudio(node);
        string status = node.AudioStatus ?? "未标注";
        status += audio == null ? "；未引用音频" : File.Exists(audio) ? "；文件存在" : "；文件缺失";
        return new()
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            AppVersion = appVersion, Platform = platform, Mode = mode,
            PackId = pack.Id, PackTitle = pack.Title, Revision = pack.Delivery?.Revision ?? "",
            NavigationFingerprint = PlaybackEngine.NavigationFingerprint(pack),
            ChapterTitle = chapter?.Title ?? "", SectionId = node.SectionId,
            SectionTitle = section?.Title ?? "", NodeId = node.Id, Speaker = node.Speaker,
            Text = node.Text, PathId = node.PathId, AudioReference = node.Audio ?? "",
            AudioStatus = status, RecentContext = string.Join(Environment.NewLine, context)
        };
    }

    public static string Format(LineFeedbackSnapshot snapshot, string category, string? comment)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.AppendLine("战双剧情配音 · 台词反馈");
        text.AppendLine($"问题：{(Categories.Contains(category) ? category : "其他")}");
        text.AppendLine($"补充说明：{(string.IsNullOrWhiteSpace(comment) ? "未填写" : comment.Trim())}");
        text.AppendLine();
        text.AppendLine($"软件：{snapshot.AppVersion} · {snapshot.Platform}");
        text.AppendLine($"模式：{snapshot.Mode}");
        text.AppendLine($"记录时间（UTC）：{snapshot.CapturedUtc:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"配音包：{snapshot.PackTitle}");
        text.AppendLine($"章节：{snapshot.ChapterTitle}");
        text.AppendLine($"小节：{snapshot.SectionTitle}");
        text.AppendLine($"角色：{SpeakerVolume.DisplayName(snapshot.Speaker)}");
        text.AppendLine($"原文：{snapshot.Text}");
        text.AppendLine();
        text.AppendLine($"配音包编号：{snapshot.PackId}");
        text.AppendLine($"包版本：{(string.IsNullOrEmpty(snapshot.Revision) ? "未标注" : snapshot.Revision)}");
        text.AppendLine($"小节编号：{snapshot.SectionId}");
        text.AppendLine($"台词编号：{snapshot.NodeId}");
        text.AppendLine($"路线编号：{snapshot.PathId}");
        text.AppendLine($"音频引用：{snapshot.AudioReference}");
        text.AppendLine($"音频状态：{snapshot.AudioStatus}");
        text.AppendLine($"导航指纹：{snapshot.NavigationFingerprint}");
        if (snapshot.RecentContext.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("最近实际记录的台词（不代表剧情中相邻）：");
            text.AppendLine(snapshot.RecentContext);
        }
        return text.ToString();
    }
}
