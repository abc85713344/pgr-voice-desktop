namespace PgrVoice.Listening;

public enum ListeningBranchPolicy { First, All, Manual }
public enum ListeningItemKind { Line, Choice, Notice }
public enum ListeningWaitingKind { None, UnverifiedExit, ScopeBoundary, InvalidConnection, UnsupportedNode, TraversalLimit, UnverifiedBody }

/// <summary>配音包原节点的只读播放视图；音频缺失也保留 Line，由音频适配层报告并跳过。</summary>
public sealed record ListeningItem(
    string Id, ListeningItemKind Kind, Node? Node, string SectionId, string SectionTitle,
    int LineNumber, string BranchLabel, string Notice,
    string? ChoiceKey = null, IReadOnlyList<ChoiceOption>? Options = null,
    ListeningWaitingKind WaitingKind = ListeningWaitingKind.None)
{
    public string NodeId => Node?.Id ?? "";
    public bool IsBlocking => WaitingKind != ListeningWaitingKind.None;
    public string PositionLabel => LineNumber > 0 ? $"{SectionTitle} · 第 {LineNumber} 句" : SectionTitle;
}

/// <summary>
/// 当前计划实际经过的选择点。已确认菜单不占用播放项目，Position 可相同，Order 保留真实访问先后。
/// Options 是本次访问可选的完整列表；返回菜单时不重新加入以前访问已听过的选项。
/// </summary>
public sealed record ListeningChoicePoint(ListeningItem Item, int Position, string? SelectedOptionId,
    bool IsResolved, int Order, bool IsSkipped = false)
{
    public string ChoiceKey => Item.ChoiceKey!;
    public string SectionId => Item.SectionId;
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
    public DateTimeOffset? PausedUtc { get; set; }
    public bool ExactResumePosition { get; set; }
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
