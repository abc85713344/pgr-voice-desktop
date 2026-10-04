namespace PgrVoice.Listening;

public enum ListeningBranchPolicy { First, All, Manual }
public enum ListeningItemKind { Line, Choice, Notice }

/// <summary>配音包原节点的只读播放视图；音频缺失也保留 Line，由音频适配层报告并跳过。</summary>
public sealed record ListeningItem(
    string Id, ListeningItemKind Kind, Node? Node, string SectionId, string SectionTitle,
    int LineNumber, string BranchLabel, string Notice,
    string? ChoiceKey = null, IReadOnlyList<ChoiceOption>? Options = null)
{
    public string NodeId => Node?.Id ?? "";
    public string PositionLabel => LineNumber > 0 ? $"{SectionTitle} · 第 {LineNumber} 句" : SectionTitle;
}

public sealed class ListeningSnapshot
{
    public int Version { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string ChapterId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public ListeningBranchPolicy Policy { get; set; } = ListeningBranchPolicy.First;
    public string? ItemId { get; set; }
    public string? NodeId { get; set; }
    public string SectionId { get; set; } = "";
    public string SectionTitle { get; set; } = "";
    public int LineNumber { get; set; }
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public long PositionMs { get; set; }
    public bool Completed { get; set; }
    public Dictionary<string, string> Choices { get; set; } = new();
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ListeningBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ListeningSnapshot Snapshot { get; set; } = new();
}

public sealed class ListeningChapterProgress
{
    public ListeningSnapshot? Resume { get; set; }
    public List<ListeningBookmark> Bookmarks { get; set; } = new();
}

public sealed class ListeningProgressDocument
{
    public int Version { get; set; } = 1;
    public string PackId { get; set; } = "";
    public Dictionary<string, ListeningChapterProgress> Chapters { get; set; } = new();
    public ListeningChapterProgress ForChapter(string chapterId)
    {
        if (!Chapters.TryGetValue(chapterId, out var progress))
            Chapters[chapterId] = progress = new();
        return progress;
    }
}
