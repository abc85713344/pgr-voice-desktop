using System;
using System.Linq;
using System.Windows;

namespace PgrVoice;

// Only explicit, usable end edges are described as endings. Missing connections stay unknown.
static class StoryLinePresentation
{
    public static bool IsExplicitEnding(Pack pack, Node node)
    {
        if (node.Archived || node.Kind != "line" || node.NextId == null ||
            !pack.ById.TryGetValue(node.NextId, out var next) || next.Archived ||
            next.Kind != "end" || next.SectionId != node.SectionId) return false;
        if (node.PathId.Length == 0) return true;
        var owners = pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options)
            .Where(o => o.PathId == node.PathId).ToList();
        return owners.Count == 1 && (pack.SchemaVersion == 3
            ? owners[0].BodyVerified && owners[0].ExitVerified && owners[0].SegmentIds.Contains(node.Id)
            : owners[0].Verified);
    }
}

public partial class MainWindow
{
    bool currentStoryEnding;
    bool unconfirmedStoryEnding;

    void RefreshStoryContinuation()
    {
        if (StoryContinuationNotice == null || NextLineButton == null) return;
        currentStoryEnding = engine?.Current is { } node && engine.Mode != RunMode.Original &&
            StoryLinePresentation.IsExplicitEnding(engine.Pack, node) && engine.Allowed(node) &&
            engine.ReviewRoute == null && !engine.ExportNavigation().Current.SingleLine;
        bool ended = engine?.Mode == RunMode.End && engine.Current?.Kind == "end";
        unconfirmedStoryEnding = engine?.Mode == RunMode.End && !ended && !currentStoryEnding;
        StoryContinuationNotice.Text = currentStoryEnding
            ? "当前路线的最后一句，后面没有下一句。听完后可选择下一小节。"
            : ended ? "当前路线已结束。可选择下一小节，或重选其它分支。"
            : unconfirmedStoryEnding ? "后续连接尚未确认，请按游戏当前台词定位。" : "";
        StoryContinuationNotice.Visibility = currentStoryEnding || ended || unconfirmedStoryEnding ? Visibility.Visible : Visibility.Collapsed;
        NextLineButton.Content = currentStoryEnding || ended ? "已到末句" : unconfirmedStoryEnding ? "后续待确认" : "配音下一句";
        NextLineButton.IsEnabled = !currentStoryEnding && !ended && !unconfirmedStoryEnding;
        NextLineButton.ToolTip = currentStoryEnding || ended
            ? "当前路线没有下一句；可重播、重选分支或选择下一小节。" : null;
    }
}
