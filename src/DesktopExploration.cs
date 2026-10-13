using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 游戏配音（桌面）：探索菜单逐项读完的记忆与建议项（与安卓 ExplorationMemory / MenuShape.NextExploration 同规则：
/// 先读未读回环，空回环视为已读，回环读完后按默认分支取出口；只有一个选项时取它；
/// 同回应隐藏：回到菜单时与刚选项回应相同的选项隐藏，回应已读的回环不再建议；问答菜单首次按默认分支选）。不依赖 WPF，可在核心测试中直接验证。
/// </summary>
public static class DesktopExploration
{
    static readonly ConditionalWeakTable<PlaybackEngine, Dictionary<string, HashSet<string>>> table = new();
    static readonly ConditionalWeakTable<PlaybackEngine, Dictionary<string, string>> lastChosen = new();
    /// <summary>本菜单最近一次选的项（同回应隐藏只看刚选项，与游戏 OnEnter 相同）。</summary>
    public static string? Last(PlaybackEngine engine, string menuId)
    {
        var map = lastChosen.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (map) return map.TryGetValue(menuId, out var id) ? id : null;
    }
    /// <summary>
    /// 0.4.7 同回应隐藏：回到菜单时与刚选项回应相同的选项（如 19-14 两个错误答案）在游戏里已隐藏；返回这些选项在 AvailableOptions 中的下标。
    /// 刚选项没有同回应的其他选项时为空（普通探索菜单不变）。
    /// </summary>
    public static IReadOnlyList<int> HiddenIndexes(PlaybackEngine engine)
    {
        if (engine.Mode != RunMode.Choice || engine.Current is not { Kind: "choice" } menu) return Array.Empty<int>();
        MenuShapeInfo shape;
        try { shape = MenuShape.Of(engine.Pack, menu); } catch { return Array.Empty<int>(); }
        var visible = shape.VisibleAfter(Last(engine, menu.Id));
        if (visible.Count == shape.Options.Count) return Array.Empty<int>();
        var available = engine.AvailableOptions; var hidden = new List<int>();
        for (int i = 0; i < available.Count; i++)
            if (shape.Options.Any(o => o.Id == available[i].Id) && visible.All(o => o.Id != available[i].Id)) hidden.Add(i);
        return hidden.Count < available.Count ? hidden : Array.Empty<int>();
    }
    public static IReadOnlySet<string> Heard(PlaybackEngine engine, string menuId)
    {
        var map = table.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (map) return map.TryGetValue(menuId, out var set) ? new HashSet<string>(set, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }
    public static void Add(PlaybackEngine engine, string menuId, string optionId)
    {
        var map = table.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (map) { if (!map.TryGetValue(menuId, out var set)) map[menuId] = set = new(StringComparer.Ordinal); set.Add(optionId); }
        var last = lastChosen.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (last) last[menuId] = optionId;
    }

    /// <summary>0.4.7 设计稿 14：分支监听按游戏对话框接上某个探索选项（「已根据游戏画面跳转：…／读到：…」）。</summary>
    public sealed record Jump(string OptionId, string OptionLabel, string Quote);
    static readonly ConditionalWeakTable<PlaybackEngine, Dictionary<string, Jump>> jumps = new();
    static readonly ConditionalWeakTable<PlaybackEngine, HashSet<string>> followUnavailable = new();
    public static void RecordJump(PlaybackEngine engine, string menuId, Jump jump)
    {
        var map = jumps.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (map) map[menuId] = jump;
    }
    public static Jump? LastJump(PlaybackEngine engine, string menuId)
    {
        var map = jumps.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (map) return map.TryGetValue(menuId, out var jump) ? jump : null;
    }
    /// <summary>本菜单无法按游戏画面自动跟随（读不到游戏对话框，如非原生客户端）：显示兜底卡「没能从游戏画面认出是谁」。</summary>
    public static void SetFollowUnavailable(PlaybackEngine engine, string menuId, bool unavailable)
    {
        var set = followUnavailable.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (set) { if (unavailable) set.Add(menuId); else set.Remove(menuId); }
    }
    public static bool FollowUnavailable(PlaybackEngine engine, string menuId)
    {
        var set = followUnavailable.GetValue(engine, _ => new(StringComparer.Ordinal));
        lock (set) return set.Contains(menuId);
    }

    public sealed record Suggestion(int Index, ChoiceOption Option, string Kind, bool EmptyExit, string Notice);

    /// <summary>当前待选菜单的建议项：单选项 / 探索菜单；普通菜单返回 null（按用户上次选择）。</summary>
    public static Suggestion? Suggest(PlaybackEngine engine, int defaultNumber)
    {
        if (engine.Mode != RunMode.Choice || engine.Current is not { Kind: "choice" } menu) return null;
        var available = engine.AvailableOptions;
        if (available.Count == 0) return null;
        MenuShapeInfo shape;
        try { shape = MenuShape.Of(engine.Pack, menu); } catch { return null; }
        if (available.Count == 1)
            return new(0, available[0], PlaybackLog.Types.SingleOption, false, "只有一个选项：「" + available[0].Label + "」，确认后继续。");
        // 同回应隐藏后只剩一个选项（如 19-14 答错回来只剩正确答案）：按单选项处理。
        var hidden = HiddenIndexes(engine);
        if (hidden.Count > 0 && available.Count - hidden.Count == 1)
        {
            int only = Enumerable.Range(0, available.Count).First(i => !hidden.Contains(i));
            return new(only, available[only], PlaybackLog.Types.SingleOption, shape.Exploration && shape.IsEmptyExit(available[only]),
                "与刚才回应相同的选项已隐藏，只剩「" + available[only].Label + "」，确认后继续。");
        }
        if (!shape.Exploration) return null;
        var heard = new HashSet<string>(Heard(engine, menu.Id), StringComparer.Ordinal);
        var next = MenuShape.NextExploration(shape, heard, defaultNumber);
        if (next == null) return null;
        int index = available.FindIndex(o => o.Id == next.Id);
        if (index < 0) return null;
        bool loop = shape.Loops.Any(o => o.Id == next.Id), empty = shape.IsEmptyExit(next);
        if (shape.Quiz && !heard.Any(id => shape.Options.Any(o => o.Id == id && !shape.EmptyLoops.Contains(o))))
            return new(index, next, PlaybackLog.Types.AutoSelect, empty, "问答菜单：按默认分支第" + (defaultNumber == 2 ? "二" : "一") + "项「" + next.Label + "」" +
                (loop ? "；答错会回到本菜单，之后只剩正确答案。" : "。"));
        string notice = loop ? "探索菜单：建议先读未读选项「" + next.Label + "」，读完会回到本菜单。"
            : empty ? "探索菜单：各选项已读，出口「" + next.Label + "」没有台词，确认后" + ListeningText.EmptyExitContinue.Replace("探索菜单出口无台词，", "") + "。"
            : "探索菜单：各选项已读，建议离开：「" + next.Label + "」。";
        return new(index, next, PlaybackLog.Types.Exploration, empty, notice);
    }
}

