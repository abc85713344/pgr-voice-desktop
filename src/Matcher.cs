using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PgrVoice;
public sealed class OcrBlock
{
    public string Text { get; set; } = "";
    public double Score { get; set; }
    public double[][] Box { get; set; } = Array.Empty<double[]>();
}
public sealed record MatchCandidate(Node Node, double Score, string Evidence, double[]? Region, string Context = "")
{
    public override string ToString() => $"{Node.Speaker}  {Node.Text}\n匹配 {Score:P0} · 需确认";
}
public static class Matcher
{
    static OcrBlock[] ReadingOrder(IEnumerable<OcrBlock> input)
    {
        // 战斗字幕的一行常被分成左右两块，其检测框顶部可能差几像素。
        // 按同一行的中心高度聚类后从左读到右，避免把后半句排到前面。
        var rows=new List<List<(OcrBlock Block,double X,double Y,double H)>>();
        var unplaced=new List<OcrBlock>();
        var bounded=new List<(OcrBlock Block,double X,double Y,double H)>();
        foreach(var block in input)
        {
            var points=block.Box.Where(p=>p.Length==2 && p.All(double.IsFinite)).ToArray();
            if(points.Length==0){unplaced.Add(block);continue;}
            double top=points.Min(p=>p[1]),bottom=points.Max(p=>p[1]);
            bounded.Add((block,points.Min(p=>p[0]),(top+bottom)/2,Math.Max(1,bottom-top)));
        }
        foreach(var block in bounded.OrderBy(b=>b.Y))
        {
            var row=rows.LastOrDefault();
            if(row==null || Math.Abs(row.Average(b=>b.Y)-block.Y)>.55*Math.Min(row.Average(b=>b.H),block.H))
            {row=new();rows.Add(row);}
            row.Add(block);
        }
        return rows.SelectMany(r=>r.OrderBy(b=>b.X).Select(b=>b.Block)).Concat(unplaced).ToArray();
    }
    public static string Normalize(string s)
    {
        string value = s.Normalize(NormalizationForm.FormKC);
        string text = new(value.Where(c => char.IsLetterOrDigit(c) || "■□▇█".Contains(c)).Select(char.ToLowerInvariant).ToArray());
        // 纯省略号也可能是一句台词；普通句子的标点仍忽略。
        if (text.Length == 0 && value.Count(c => c == '.') >= 3 && value.All(c => c == '.' || char.IsWhiteSpace(c))) return "…";
        return text;
    }
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1]; cur[0] = i;
            for (int j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
    public static List<MatchCandidate> Find(PlaybackEngine engine, string sectionId, List<OcrBlock> blocks)
        => FindAll(engine, sectionId, blocks).Take(3).ToList();

    /// <summary>
    /// 返回截断前的全部排序候选。自动定位的唯一性和菜单检查必须使用此集合；
    /// 显示用的前三名不能证明不存在其他同文台词或分支菜单。
    /// </summary>
    public static List<MatchCandidate> FindAll(PlaybackEngine engine, string sectionId, List<OcrBlock> blocks)
    {
        var texts = new List<(string Text, OcrBlock[] Blocks)>();
        var sorted = ReadingOrder(blocks.Where(b => b.Score >= .35));
        for (int i = 0; i < sorted.Length; i++)
            for (int count = 1; count <= 4 && i + count <= sorted.Length; count++)
            {
                var group = sorted.Skip(i).Take(count).ToArray();
                texts.Add((Normalize(string.Concat(group.Select(b => b.Text))), group));
            }
        // 定位首先回答“画面是哪一句”，不能因播放器尚未走到某条路线而删掉正文。
        // 只提出候选；确认时由导航器同步明确的位置，未知段落仍单句播放。
        var lines = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == sectionId).ToList();
        int current = lines.FindIndex(n => n.Id == engine.CurrentId);
        var result = new List<MatchCandidate>();
        foreach (var node in lines)
        {
            var target = Normalize(node.Text);
            if (target.Length == 0) continue;
            double best = 0; OcrBlock[] evidence = Array.Empty<OcrBlock>();
            foreach (var candidate in texts)
            {
                double score = Similarity(target, candidate.Text);
                if (candidate.Text.Length >= 8 && target.Contains(candidate.Text)) score = Math.Max(score, .68 + .2 * candidate.Text.Length / target.Length);
                if (score > best) { best = score; evidence = candidate.Blocks; }
            }
            if (best < .58 || (target.Length <= 3 && best < 1)) continue;
            bool speaker = sorted.Any(b => Normalize(b.Text) == Normalize(node.Speaker));
            int distance = current >= 0 ? Math.Abs(lines.IndexOf(node) - current) : 100;
            double ranked = best * .94 + (speaker ? .03 : 0) + .03 / (1 + distance);
            var points = evidence.SelectMany(b => b.Box).Where(p => p.Length == 2).ToArray();
            double[]? region = points.Length == 0 ? null : new[] { points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1]) };
            if(engine.Allowed(node))ranked+=.005;
            result.Add(new(node, ranked, string.Join(" / ", evidence.Select(b => b.Text)), region, engine.LocationContext(node)));
        }
        foreach(var menu in engine.Pack.Nodes.Where(n=>!n.Archived && n.Kind=="choice" && n.SectionId==sectionId))
        {
            var hits=new List<(double Score,OcrBlock[] Blocks)>();
            foreach(var option in menu.Options)
            {
                string label=Normalize(option.Label);
                if(label.Length<2)continue;
                var match=texts.Select(t=>(Score:Similarity(label,t.Text),Blocks:t.Blocks)).OrderByDescending(t=>t.Score).FirstOrDefault();
                if(match.Score>=(label.Length<=3?1:.72))hits.Add(match);
            }
            if(hits.Count==0)continue;
            var evidence=hits.SelectMany(h=>h.Blocks).Distinct().ToArray();
            var points=evidence.SelectMany(b=>b.Box).Where(p=>p.Length==2).ToArray();
            double[]? region=points.Length==0?null:new[]{points.Min(p=>p[0]),points.Min(p=>p[1]),points.Max(p=>p[0]),points.Max(p=>p[1])};
            double score=hits.Max(h=>h.Score)*.94+Math.Min(.06,(hits.Count-1)*.03);
            result.Add(new(menu,score,string.Join(" / ",evidence.Select(b=>b.Text)),region,
                "◆ 分支选项 · 确认只打开菜单，不播放\n"+engine.LocationContext(menu)));
        }
        return result.OrderByDescending(c => c.Score).ToList();
    }
}
