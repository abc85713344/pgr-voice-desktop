namespace PgrVoice.Listening;

public sealed record ListeningDirectoryEntry(string Key, string? ItemId, string? NodeId,
    int Number, string Speaker, string Text, bool IsChoice, bool IsPreview);

/// <summary>完整正文的只读目录。看见正文不等于它已进入当前收听计划。</summary>
public sealed record ListeningDirectory(string SectionId, int LineCount,
    IReadOnlyList<ListeningDirectoryEntry> Entries, string? PendingKey, string? PendingItemId)
{
    public string? TargetFor(string key)
    {
        var entry = Entries.FirstOrDefault(e => e.Key == key);
        return entry?.IsPreview == true ? PendingItemId : entry?.ItemId;
    }

    public static ListeningDirectory Create(ListeningSession session, string sectionId)
    {
        if (!session.Chapter.Sections.Any(s => s.Id == sectionId))
            return new(sectionId, 0, Array.Empty<ListeningDirectoryEntry>(), null, null);
        var available = session.Items.Where(i => i.Kind == ListeningItemKind.Line && i.SectionId == sectionId)
            .ToDictionary(i => i.NodeId, StringComparer.Ordinal);
        var pending = session.Items.FirstOrDefault(i => i.SectionId == sectionId && i.Kind == ListeningItemKind.Choice);
        var source = session.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == sectionId).ToArray();
        var entries = source.Select((node, index) => new ListeningDirectoryEntry("node:" + node.Id,
            available.GetValueOrDefault(node.Id)?.Id, node.Id, index + 1,
            string.IsNullOrWhiteSpace(node.Speaker) ? "旁白" : node.Speaker, node.Text,
            false, !available.ContainsKey(node.Id))).ToList();
        string? pendingKey = null;
        if (pending != null)
        {
            pendingKey = "choice:" + pending.Id;
            int before = session.Pack.Nodes.TakeWhile(n => n.Id != pending.NodeId)
                .Count(n => !n.Archived && n.Kind == "line" && n.SectionId == sectionId);
            entries.Insert(Math.Min(before, entries.Count), new(pendingKey, pending.Id, pending.NodeId, 0,
                "待选互动 / 分支", string.Join(" / ", (pending.Options ?? Array.Empty<ChoiceOption>()).Select(o => o.Label)), true, false));
        }
        return new(sectionId, source.Length, entries, pendingKey, pending?.Id);
    }
}
