using System;
using System.Collections.Generic;
using System.Linq;

namespace PgrVoice;

public sealed partial class PlaybackEngine
{
    // 玩家明确指认的游戏菜单，不等于剧情任务已完成。
    public HashSet<string> ObservedMenus { get; private set; } = new();

    public bool OpenGameMenu(string menuId, string? sectionId = null)
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段不能切换分支，请先结束原声时段。");
        string? section = StorySection(sectionId);
        if (!Pack.ById.TryGetValue(menuId, out var menu) || menu.Archived || menu.Kind != "choice" || menu.SectionId != section)
            return FailNavigation("请选择当前小节的有效分支菜单。");
        BeginCorrection(); StopRequested?.Invoke(); DiscardFuture();
        ObservedMenus.Add(menu.Id); CurrentId = menu.Id; PendingMenu = menu.Id;
        Mode = RunMode.Choice; singleLine = false; ReviewRoute = null;
        CurrentReselectOptionId = menu.Options.FirstOrDefault(o => Choices.GetValueOrDefault(menu.Id) == o.PathId)?.Id;
        Notice = "已按游戏画面定位：" + menu.Text + "。请选择游戏中的同一选项；正文未核实的内容仍只允许单句确认。";
        Changed?.Invoke(); return true;
    }

    public bool ConfirmGameLine(string nodeId, bool resumeOriginal = false)
    {
        if (Mode == RunMode.Original && !resumeOriginal) return FailNavigation("游戏原声时段不能播放，请先结束原声时段。");
        if (!Pack.ById.TryGetValue(nodeId, out var node) || node.Archived || node.Kind != "line")
            return FailNavigation("这条台词已失效，请重新定位。");
        var owners = Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == node.SectionId && n.Options.Any(o => o.PathId == node.PathId)).ToList();
        if (node.PathId.Length > 0 && owners.Count != 1) return FailNavigation("无法唯一确认这句所属路线，请手动选择分支。");
        var menu = owners.FirstOrDefault(); var option = menu?.Options.First(o => o.PathId == node.PathId);
        bool segment = option == null || (Pack.SchemaVersion == 3 ? option.BodyVerified && option.SegmentIds.Contains(node.Id) : option.Verified);
        if (!segment && !option!.LineIds.Contains(node.Id)) return FailNavigation("该句未包含在路线正文中，请检查配音包。");
        BeginCorrection(); StopRequested?.Invoke(); DiscardFuture();
        singleLine = false; ReviewRoute = null;
        if (node.PathId.Length > 0)
        {
            ObservedMenus.Add(menu!.Id); PendingMenu = menu.Id;
            if (segment) Choices[menu.Id] = option!.PathId;
            else
            {
                ReviewRoute = option!.Id; singleLine = true; CurrentId = node.Id; Mode = RunMode.Following; Record(node);
                Notice = "已确认单句；下一次推进返回所属菜单，不越过未核实的内容。";
                PlayRequested?.Invoke(node); Changed?.Invoke(); return true;
            }
        }
        AdvanceTo(node); return true;
    }

    public string LocationContext(Node node)
    {
        var labels = new List<string>(); var path = node.PathId; var seen = new HashSet<string>();
        while (path.Length > 0 && seen.Add(path))
        {
            var owner = Pack.Nodes.FirstOrDefault(n => !n.Archived && n.Kind == "choice" && n.SectionId == node.SectionId && n.Options.Any(o => o.PathId == path));
            if (owner == null) break;
            var option = owner.Options.First(o => o.PathId == path); labels.Add(option.Label); path = owner.PathId;
        }
        labels.Reverse();
        string route = labels.Count > 0 ? "路线：" + string.Join(" → ", labels) : "共同剧情";
        if(node.Kind=="line")route="本节第 "+(Pack.Nodes.Where(n=>!n.Archived && n.Kind=="line" && n.SectionId==node.SectionId).ToList().FindIndex(n=>n.Id==node.Id)+1)+" 句 · "+route;
        if (node.Kind == "choice") return route + "\n选项：" + string.Join(" ／ ", node.Options.Select(o => o.Label));
        var before = Pack.Nodes.FirstOrDefault(n => !n.Archived && n.Kind == "line" && n.PathId == node.PathId && n.NextId == node.Id);
        var after = node.NextId != null ? Pack.ById.GetValueOrDefault(node.NextId) : null;
        return route + (before != null ? "\n前：" + before.Speaker + "：" + before.Text : "") +
            (after?.Kind == "line" && after.PathId == node.PathId ? "\n后：" + after.Speaker + "：" + after.Text : "") +
            (!Allowed(node) ? "\n确认后按游戏位置同步；未核实段落只播放这一句。" : "");
    }
}
