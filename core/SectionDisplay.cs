using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PgrVoice;

public sealed record SectionDisplayGroup(string Title, IReadOnlyList<Section> Sections);

/// <summary>只整理目录显示；原始小节、节点和导航边界始终保持不变。</summary>
public static class SectionDisplay
{
    static readonly Regex FragmentId = new(@"^(?<root>.+)(?<suffix>-position-[0-9a-fA-F]{12}|-m[0-9]{3})$", RegexOptions.CultureInvariant);
    static readonly IReadOnlyDictionary<string, OrderEntry> SourceOrder = LoadSourceOrder();

    sealed class OrderTable
    {
        public List<OrderEntry> Rows { get; set; } = new();
    }
    sealed class OrderEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Domain { get; set; } = "";
        public int Rank { get; set; }
        public string? DisplayTitle { get; set; }
    }

    static IReadOnlyDictionary<string, OrderEntry> LoadSourceOrder()
    {
        using var stream = typeof(SectionDisplay).Assembly.GetManifestResourceStream("PgrVoice.SectionOrder.json");
        if (stream == null) return new Dictionary<string, OrderEntry>(StringComparer.Ordinal);
        var table = JsonSerializer.Deserialize<OrderTable>(stream);
        return (table?.Rows ?? new()).Where(r => r.Id.Length > 0 && r.Domain.Length > 0)
            .GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    static OrderEntry? Order(Section root) =>
        SourceOrder.TryGetValue(root.Id, out var entry) && root.Title == entry.Title ? entry : null;

    static List<Section> InSourceOrder(List<Section> roots)
    {
        var result = roots.ToList();
        // 每个来源章只重排自己的已识别位置；其他章和未知小节保持原位。
        // 不能全局按关卡数字或 Rank 排序，普通/隐藏重号的顺序由来源目录确定。
        var known = roots.Select((root, index) => (Root: root, Index: index, Order: Order(root)))
            .Where(item => item.Order != null).ToList();
        foreach (var domain in known.GroupBy(item => item.Order!.Domain, StringComparer.Ordinal))
        {
            if (domain.Select(item => item.Root.Id).Distinct(StringComparer.Ordinal).Count() != domain.Count()) continue;
            var sorted = domain.OrderBy(item => item.Order!.Rank).Select(item => item.Root).ToArray();
            int next = 0;
            foreach (var item in domain) result[item.Index] = sorted[next++];
        }
        return result;
    }

    public static IReadOnlyList<SectionDisplayGroup> Groups(IEnumerable<Section> sections)
    {
        var items = sections.ToList();
        var byId = items.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var roots = new List<Section>();
        var members = new Dictionary<Section, List<Section>>();
        foreach (var section in items)
        {
            var root = section;
            var match = FragmentId.Match(section.Id);
            if (match.Success && byId.TryGetValue(match.Groups["root"].Value, out var parent) && parent.Title.Length > 0)
            {
                string ending = match.Groups["suffix"].Value.StartsWith("-position-", StringComparison.Ordinal)
                    ? " · 当前画面定位" : " · 条件路线";
                if (section.Title == parent.Title + ending) root = parent;
            }
            if (!members.TryGetValue(root, out var group))
            {
                roots.Add(root); members[root] = group = new();
            }
            group.Add(section);
        }
        return InSourceOrder(roots).Select(root => new SectionDisplayGroup(Order(root)?.DisplayTitle ?? root.Title, members[root].AsReadOnly()))
            .ToList().AsReadOnly();
    }

    public static string Title(IEnumerable<Section> sections, string sectionId) =>
        Groups(sections).FirstOrDefault(g => g.Sections.Any(s => s.Id == sectionId))?.Title ?? "";

    public static string SegmentLabel(Pack pack, SectionDisplayGroup group, Section section)
    {
        int index = -1;
        for (int i = 0; i < group.Sections.Count; i++)
            if (ReferenceEquals(group.Sections[i], section) || group.Sections[i].Id == section.Id) { index = i; break; }
        if (index < 0) throw new ArgumentException("小节不属于该显示分组。", nameof(section));
        string number = (index + 1).ToString("D2");
        var nodes = pack.Nodes.Where(n => !n.Archived && n.SectionId == section.Id).ToList();
        var start = nodes.FirstOrDefault(n => n.Id == section.StartId);
        var line = start?.Kind == "line" ? start : nodes.FirstOrDefault(n => n.Kind == "line");
        var menu = start?.Kind == "choice" ? start : line == null ? nodes.FirstOrDefault(n => n.Kind == "choice") : null;
        if (menu != null)
        {
            string options = string.Join(" / ", menu.Options.Select(o => Clean(o.Label)).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
            if (options.Length > 0) return number + " · " + Short(options, 66);
        }
        if (line == null) return number + " · " + (menu != null ? "互动选择" : "暂无台词");
        string preview = Preview(line, 48);
        if (!HasWords(line.Text))
        {
            Node? context = nodes.SkipWhile(n => !ReferenceEquals(n, line)).Skip(1)
                .FirstOrDefault(n => n.Kind == "line" && HasWords(n.Text));
            for (int i = index + 1; i < group.Sections.Count && context == null; i++)
                context = pack.Nodes.FirstOrDefault(n => !n.Archived && n.Kind == "line" && n.SectionId == group.Sections[i].Id && HasWords(n.Text));
            string relation = "后文";
            if (context == null)
            {
                relation = "前文";
                for (int i = index - 1; i >= 0 && context == null; i--)
                    context = pack.Nodes.LastOrDefault(n => !n.Archived && n.Kind == "line" && n.SectionId == group.Sections[i].Id && HasWords(n.Text));
            }
            if (context != null) preview = Preview(line, 16) + "（" + relation + "：" + Preview(context, 40) + "）";
        }
        return number + " · " + preview;
    }

    static string Preview(Node node, int length)
    {
        string speaker = Clean(node.Speaker), text = Clean(node.Text);
        return Short((speaker.Length > 0 ? speaker + "：" : "") + text, length);
    }
    static bool HasWords(string text) => Clean(text).Any(char.IsLetterOrDigit);
    static string Clean(string text) => Regex.Replace(Regex.Replace(text, "<[^>]*>", ""), @"\s+", " ").Trim();
    static string Short(string text, int length)
    {
        var runes = text.EnumerateRunes().ToArray();
        return runes.Length <= length ? text : string.Concat(runes.Take(length).Select(r => r.ToString())) + "…";
    }
}
