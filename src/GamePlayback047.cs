using System;
using System.Linq;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    const string GameLogMode = "游戏配音";
    void GameLog(string type, Node? node, string reason, string option = "")
    {
        if (engine == null) return;
        var section = engine.Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == node?.SectionId);
        var chapter = engine.Pack.Chapters.FirstOrDefault(c => c.Sections.Contains(section!));
        PlaybackLog.Write(type, automaticRunning ? "自动播放" : GameLogMode, engine.Pack, chapter?.Title ?? "", section?.Title ?? node?.SectionId ?? "", node?.Id ?? "", reason.Replace("\n", " "), option);
    }
    /// <summary>菜单弹出时：单选项 / 探索菜单预选建议项并说明；普通菜单保持上次选择。</summary>
    int GameMenuSuggestion(int selected)
    {
        if (engine == null) return selected;
        var suggestion = DesktopExploration.Suggest(engine, preferences.DefaultBranchOption);
        if (suggestion == null) return selected;
        Tell(suggestion.Notice);
        return suggestion.Index;
    }
    /// <summary>确认菜单选项前记下探索菜单已读项；空出口另记一条。</summary>
    void RecordGameMenuChoice(int index)
    {
        if (engine?.Mode != RunMode.Choice || engine.Current is not { Kind: "choice" } menu) return;
        var options = engine.AvailableOptions;
        if (index < 0 || index >= options.Count) return;
        var option = options[index];
        MenuShapeInfo? shape = null;
        try { shape = MenuShape.Of(engine.Pack, menu); } catch { }
        DesktopExploration.Add(engine, menu.Id, option.Id);
        if (shape == null) return;
        var same = shape.SameResponseOthers(option);
        if (same.Count > 0 && shape.Loops.Any(o => o.Id == option.Id))
            GameLog(PlaybackLog.Types.Exploration, menu, "同回应隐藏：回到菜单后不再显示「" + string.Join("」「", same.Select(o => o.Label)) + "」", option.Label);
        foreach (var empty in shape.EmptyLoops) if (empty.Id == option.Id) GameLog(PlaybackLog.Types.Exploration, menu, "探索菜单空回环，记为已读", option.Label);
        if (shape.Exploration && shape.IsEmptyExit(option)) GameLog(PlaybackLog.Types.Exploration, menu, ListeningText.EmptyExitContinue, option.Label);
        else if (shape.Single) GameLog(PlaybackLog.Types.SingleOption, menu, "仅一个选项", option.Label);
    }
    /// <summary>无音频台词（0.4.7 J）：显示字幕，按字数停留，不停自动播放（待配音 / 无配音 / 仅字幕）。</summary>
    bool GameSilentDwell(Node node) => engine != null && PendingVoice.Applies(engine.Pack, node);
    /// <summary>无音频台词的签文字：「待配音」/「无配音」；有音频或仅字幕（不标签）时为 null。</summary>
    string? GameVoiceTag(Node node) => GameSilentDwell(node) ? PendingVoice.VoiceTag(node) : null;
    void GameDeadEndFeedback()
    {
        var node = engine?.Current;
        GameLog(PlaybackLog.Types.PlayerFeedback, node, "后续没有配音文本，游戏里可能还有内容");
        automaticRecoveryText.Text = "已记录反馈（" + (node?.SectionId ?? "") + "）。可在「设置 · 分支与播放记录」导出播放记录发给作者。\n" + automaticRecoveryText.Text;
        Tell("已记录反馈。");
    }
    const string GameDeadEndText = ListeningText.DeadEndHint + "。\n本次尚未点击游戏下一句；请在游戏中继续，可在台词页点「反馈」。";
}
