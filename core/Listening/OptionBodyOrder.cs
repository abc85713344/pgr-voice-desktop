using System.Runtime.CompilerServices;

namespace PgrVoice.Listening;

/// <summary>
/// 选项正文“并联”的程序侧容错：部分配音包（外篇/间章的 bw 生成包，共 336 处）把一个选项的多句正文各自直接接到同一出口
/// （如 复述句→gap、回应句→gap），回应句没有任何入边，只按 nextId 走永远读不到。选项的 lineIds 按原文顺序登记了完整正文，
/// 这里在离开正文前补读 lineIds 中排在当前句之后、尚未读过、无任何入边且与当前句接到同一出口的同路线台词。
/// 只补这种“孤立并联句”，不改变任何可达句的顺序；不接同一出口的无入边句（游戏不可达的残留）不补读。
/// 听书规划与游戏伴音共用。
/// </summary>
public static class OptionBodyOrder
{
    static readonly ConditionalWeakTable<Pack, HashSet<string>> incoming = new();

    static HashSet<string> Incoming(Pack pack) => incoming.GetValue(pack, p =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in p.Nodes)
        {
            if (n.Archived) continue;
            if (n.NextId != null) set.Add(n.NextId);
            foreach (var o in n.Options) set.Add(o.TargetId);
            foreach (var m in n.ResumeMenuIds) set.Add(m);
        }
        return set;
    });

    static readonly ConditionalWeakTable<Pack, Dictionary<string, ChoiceOption?>> owners = new();

    /// <summary>当前台词所属选项（同小节内按路线唯一确定；不唯一时为 null）。</summary>
    public static ChoiceOption? Owner(Pack pack, Node line)
    {
        if (line.PathId.Length == 0) return null;
        var map = owners.GetValue(pack, p =>
        {
            var d = new Dictionary<string, ChoiceOption?>(StringComparer.Ordinal);
            foreach (var n in p.Nodes)
            {
                if (n.Archived || n.Kind != "choice") continue;
                foreach (var o in n.Options)
                {
                    string key = n.SectionId + "\n" + o.PathId;
                    d[key] = d.ContainsKey(key) ? null : o;
                }
            }
            return d;
        });
        return map.GetValueOrDefault(line.SectionId + "\n" + line.PathId);
    }

    /// <summary>
    /// line 之后若 nextId 离开该选项正文（不在 lineIds 中），返回 lineIds 中排在 line 之后、未读、无入边的下一句孤立正文；否则 null。
    /// </summary>
    public static Node? NextOrphan(Pack pack, Node line, Func<string, bool> alreadyRead)
    {
        if (line.Kind != "line" || Owner(pack, line) is not { } option) return null;
        int index = option.LineIds.IndexOf(line.Id);
        if (index < 0 || line.NextId != null && option.LineIds.Contains(line.NextId)) return null;
        var into = Incoming(pack);
        for (int i = index + 1; i < option.LineIds.Count; i++)
        {
            string id = option.LineIds[i];
            if (!pack.ById.TryGetValue(id, out var candidate) || candidate.Archived || candidate.Kind != "line" ||
                candidate.SectionId != line.SectionId || candidate.PathId != line.PathId) continue;
            if (into.Contains(id) || alreadyRead(id)) continue;
            // 只补“并联”句：与当前句接到同一出口。其他无入边的 lineIds（如 35-6 N0347＝原表 Action154，游戏不可达的配置残留）不补读。
            if (!string.Equals(candidate.NextId, line.NextId, StringComparison.Ordinal)) continue;
            return candidate;
        }
        return null;
    }
}
