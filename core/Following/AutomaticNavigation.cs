namespace PgrVoice;

public sealed partial class PlaybackEngine
{
    /// <summary>只读预览自动跟随可到达的下一句；菜单、汇合点、未核实边界均需手动确认。</summary>
    public Node? GetAutomaticNextLine()
    {
        if (Mode != RunMode.Following || Current?.Kind != "line" || singleLine || ReviewRoute != null || !Allowed(Current)) return null;
        var option = Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).FirstOrDefault(o => o.PathId == Current.PathId);
        if (Pack.SchemaVersion == 3 && option?.BodyVerified == true && !option.ExitVerified &&
            (Current.NextId == null || !option.SegmentIds.Contains(Current.NextId))) return null;
        string? id = Current.NextId;
        var seen = new HashSet<string>();
        while (id != null && Pack.ById.TryGetValue(id, out var next) && seen.Add(id))
        {
            if (next.Archived || next.SectionId != Current.SectionId || !Allowed(next)) return null;
            if (next.Kind == "line") return next;
            if (next.Kind != "return") return null;
            var exit = Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).FirstOrDefault(o => o.PathId == next.PathId);
            if (exit == null || (Pack.SchemaVersion == 3 ? !exit.BodyVerified || !exit.ExitVerified : !exit.Verified)) return null;
            id = next.NextId;
        }
        return null;
    }

    /// <summary>必须在所有其他推进操作使用的同一串行队列上调用。</summary>
    public bool TryAdvanceAutomatically(string expectedNodeId)
    {
        if (GetAutomaticNextLine()?.Id != expectedNodeId) return false;
        Next();
        return CurrentId == expectedNodeId && Mode == RunMode.Following;
    }
}
