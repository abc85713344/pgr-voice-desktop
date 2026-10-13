using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 快捷面板 18a「探索中」列表（设计稿 18a / 14）：只读公共状态（引擎当前菜单、MenuShape 回环 / 出口、DesktopExploration 已读、引擎已听路线），
/// 不改 BranchMenuWindow。行：A/B/C 序号、↻ 回环 / [→ EXIT 出口、✓ 已听 / 未听 / ▶ 正在播放 / 随时可选；不显示主线 / 闲聊标识。
/// 点击与分支窗一致：单击选中（分支窗正显示同一菜单时同步其选中项），双击确认——走分支窗同一条 ConfirmSmallBranch；
/// 正在播放某段（菜单不是当前位置）时双击另一行，先按「手动选分支」同样的 OpenGameMenu 打开该菜单，再确认。
/// </summary>
public partial class MainWindow
{
    public sealed record QuickExploreRow(int Index, ChoiceOption Option, bool Loop, bool Exit, bool Heard, bool Playing, bool ReturnLoop);
    string? quickExploreMenuId;
    bool? quickExploreView;               // null = 自动（有探索菜单就显示）；false = 用户切回对白跟随；true = 用户点了探索面板
    int quickExploreSelected = -1, quickExploreLastClick = -1;
    DateTime quickExploreLastClickAt = DateTime.MinValue;
    readonly Dictionary<string, MenuShapeInfo?> quickShapes = new();

    MenuShapeInfo? QuickShape(Node menu)
    {
        if (engine == null) return null;
        string key = engine.Pack.Id + "\n" + menu.Id;
        if (quickShapes.TryGetValue(key, out var cached)) return cached;
        MenuShapeInfo? shape; try { shape = MenuShape.Of(engine.Pack, menu); } catch { shape = null; }
        return quickShapes[key] = shape;
    }
    static bool QuickOptionContains(ChoiceOption option, Node node) =>
        option.LineIds.Contains(node.Id) || option.SegmentIds.Contains(node.Id) || option.PathId.Length > 0 && node.PathId == option.PathId;

    /// <summary>当前探索菜单：引擎停在探索菜单上，或正在播放最近一个探索菜单里的某段回环。</summary>
    Node? QuickExploreMenu()
    {
        if (engine == null) return null;
        if (engine.Mode == RunMode.Choice && engine.Current is { Kind: "choice" } current && QuickShape(current)?.Exploration == true)
        {
            if (quickExploreMenuId != current.Id) { quickExploreMenuId = current.Id; quickExploreView = quickExploreView == true ? true : null; quickExploreSelected = -1; }
            return current;
        }
        if (quickExploreMenuId != null && engine.Pack.ById.TryGetValue(quickExploreMenuId, out var menu) && engine.Current is { } node &&
            engine.Mode is RunMode.Following or RunMode.Paused && menu.Options.Any(o => QuickOptionContains(o, node)))
            return menu;
        return null;
    }
    bool QuickExploreMenuIsCurrent(Node menu) => engine?.Mode == RunMode.Choice && engine.CurrentId == menu.Id;

    List<QuickExploreRow> QuickExploreRows(Node menu)
    {
        var rows = new List<QuickExploreRow>();
        if (engine == null || QuickShape(menu) is not { } shape) return rows;
        bool current = QuickExploreMenuIsCurrent(menu);
        var options = current ? engine.AvailableOptions : menu.Options.ToList();
        var hidden = current ? DesktopExploration.HiddenIndexes(engine) : Array.Empty<int>();
        var heardIds = DesktopExploration.Heard(engine, menu.Id);
        for (int i = 0; i < options.Count; i++)
        {
            if (hidden.Contains(i)) continue;
            var o = options[i];
            bool loop = shape.Loops.Any(x => x.Id == o.Id), exit = !loop && shape.Exits.Any(x => x.Id == o.Id);
            bool heard = engine.Heard.Contains(o.PathId) || heardIds.Contains(o.Id);
            // ▶ 正在播放：分支窗记下的 ExplorePlayingOptionId（第一步 14 卡同一状态），或引擎正在该选项正文里
            bool playing = branchMenu.ExplorePlayingOptionId == o.Id || !current && engine.Current is { } node && QuickOptionContains(o, node);
            rows.Add(new(i, o, loop, exit, heard, playing, loop && shape.EmptyLoops.Any(x => x.Id == o.Id)));
        }
        return rows;
    }
    string QuickExploreSignature()
    {
        var menu = QuickExploreMenu();
        if (menu == null) return "";
        return menu.Id + "|" + engine!.Mode + "|" + engine.CurrentId + "|" + quickExploreSelected + "|" + DesktopExploration.LastJump(engine, menu.Id)?.OptionId + "|" + DesktopExploration.FollowUnavailable(engine, menu.Id) + "|" +
            string.Join(",", QuickExploreRows(menu).Select(r => r.Index + (r.Heard ? "h" : "") + (r.Playing ? "p" : "")));
    }

    FrameworkElement QuickExploreList(Node menu)
    {
        var list = new StackPanel();
        var rows = QuickExploreRows(menu);
        int letter = 0;
        foreach (var row in rows)
        {
            var r = row;
            string key = r.Exit || r.ReturnLoop ? "" : ((char)('A' + Math.Min(letter++, 25))).ToString(); // 与分支窗 14 卡一致：出口 / 空回环不占键位字
            var grid = new Grid { MinHeight = 30 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brass = Theme.Brush("BrassText"); var muted = Theme.Brush("SubtleText"); var red = Hud.Solid("#A3283A"); var teal = Theme.Brush("TealText");
            grid.Children.Add(new TextBlock { Text = key, FontFamily = Theme.Font("LabelFont"), FontSize = 11, Foreground = r.Playing ? red : muted, VerticalAlignment = VerticalAlignment.Center });
            FrameworkElement mark = r.Exit
                ? new Border { BorderBrush = Theme.Brush("Brass"), BorderThickness = new Thickness(1), Background = Theme.Brush("BranchFill"), Padding = new Thickness(3, 0, 4, 1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "[→ EXIT", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = brass } }
                : r.Playing ? new TextBlock { Text = "▶", FontSize = 11, Foreground = red, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }
                : r.Heard ? new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = teal, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }
                : r.Loop || r.ReturnLoop ? new TextBlock { Text = "↻", FontSize = 13, Foreground = muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "回环：读完回到本菜单" }
                : new TextBlock { Text = "", Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(mark, 1); grid.Children.Add(mark);
            var label = new TextBlock { Text = r.Option.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Foreground = r.Heard && !r.Exit && !r.Playing ? muted : Theme.Brush("Fg"), FontWeight = r.Playing || r.Exit ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetColumn(label, 2); grid.Children.Add(label);
            var tag = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Text = r.Exit ? (r.Heard ? "✓ 已选" : "随时可选") : r.Playing ? "▶ 正在播放" : r.Heard ? "✓ 已听" : r.ReturnLoop ? QuickLoopReturnText(menu) : "未听",
                Foreground = r.Exit ? (r.Heard ? teal : brass) : r.Playing ? red : r.Heard ? teal : muted, FontWeight = r.Playing || r.Heard ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetColumn(tag, 3); grid.Children.Add(tag);
            bool selected = quickExploreSelected == r.Index;
            var frame = new CutFrame { Cut = 6, Corners = CutCorners.BottomRight, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 4), Child = grid,
                BorderBrush = selected ? Theme.Brush("Slate") : r.Playing ? red : r.Exit ? Theme.Brush("Brass") : Theme.Brush("Stroke"),
                EdgeWidth = r.Playing || selected ? 3 : 0, OutlineBrush = Theme.Brush("Stroke") };
            var button = QuickUi.Plate(frame, r.Exit ? "出口：随时可选，选后离开本菜单（双击确认）" : "单击选中，双击确认进入（与分支窗相同）", () => QuickExploreClick(menu, r.Index), QuickExploreRowName(r, rows));
            QuickUi.Hover(button, frame, r.Exit ? Theme.Brush("BranchFill") : selected ? Theme.Brush("Surface") : Brushes.White, Theme.Brush("Surface"), Theme.Brush("SlateFill"));
            list.Children.Add(button);
        }
        return list;
    }
    /// <summary>自检和读屏用的行名：「探索项 A 丽芙」/「探索项 EXIT 离开营地」/「探索项 ↻ 再等等」。</summary>
    internal static string QuickExploreRowName(QuickExploreRow r, List<QuickExploreRow> rows)
    {
        int letter = rows.TakeWhile(x => x != r).Count(x => !x.Exit && !x.ReturnLoop);
        return "探索项 " + (r.Exit ? "EXIT" : r.ReturnLoop ? "↻" : ((char)('A' + Math.Min(letter, 25))).ToString()) + " " + r.Option.Label;
    }
    /// <summary>「还有 N 段没听」：与分支窗 14 卡同一口径（回环、未听、不在播放）。</summary>
    int QuickExploreUnheard(Node menu) => QuickExploreRows(menu).Count(r => r.Loop && !r.Heard && !r.Playing);
    /// <summary>标题：沿用分支窗 14 卡的菜单名（「营地 → 选择人物」→「营地 · 选择人物」，去掉 [主线剧情] 等标签）。</summary>
    string QuickExploreTitle(Node menu)
    {
        var section = engine!.Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(x => x.Id == menu.SectionId);
        return BranchMenuWindow.ExploreMenuName(menu.Text, section?.Title);
    }
    /// <summary>空回环行去向（设计稿 15）：「回到营地」取本节「场所 → 菜单」名的场所，没有时「回到本菜单」。与分支窗同一规则（那边是私有方法，这里按同样规则只读计算）。</summary>
    string QuickLoopReturnText(Node menu)
    {
        // 与分支窗同一规则（BranchMenuWindow.LoopPlace / LoopReturnLabel）：推不出场所时「回到菜单」，不编地名
        string? place = BranchMenuWindow.LoopPlace(menu.Text) ?? engine!.Pack.Nodes.Where(n => n.Kind == "choice" && n.SectionId == menu.SectionId && BranchMenuWindow.LoopPlace(n.Text) != null && QuickShape(n)?.Exploration == true).Select(n => BranchMenuWindow.LoopPlace(n.Text)).FirstOrDefault();
        return BranchMenuWindow.LoopReturnLabel(place);
    }

    void QuickExploreClick(Node menu, int index)
    {
        var now = DateTime.UtcNow;
        bool second = quickExploreLastClick == index && (now - quickExploreLastClickAt).TotalMilliseconds <= System.Windows.Forms.SystemInformation.DoubleClickTime;
        quickExploreLastClick = second ? -1 : index; quickExploreLastClickAt = now;
        quickExploreSelected = index;
        if (branchMenu.IsVisible && !branchMenu.IsNavigation && !branchMenu.IsAnchorView && branchMenu.MenuId == menu.Id && index < branchMenu.Options.Items.Count)
            branchMenu.Options.SelectedIndex = index;
        if (second) QuickExploreConfirm(menu, index);
        RefreshQuick(true);
    }
    /// <summary>确认一项：与分支窗双击 / Enter 同一条 ConfirmSmallBranch，不另写分支逻辑。</summary>
    internal bool QuickExploreConfirm(Node menu, int index)
    {
        if (engine == null) return false;
        if (!QuickExploreMenuIsCurrent(menu))
        {
            StopPreview(); CancelOcr();
            if (!engine.OpenGameMenu(menu.Id, menu.SectionId)) { Tell(engine.NavigationError); return false; }
            singleResume = false; FillLines();
        }
        if (!branchMenu.IsVisible || branchMenu.IsNavigation || branchMenu.IsAnchorView || branchMenu.MenuId != menu.Id) ShowBranchMenu();
        if (!branchMenu.IsVisible || branchMenu.MenuId != menu.Id || index >= branchMenu.Options.Items.Count) return false;
        branchMenu.Options.SelectedIndex = index;
        ConfirmSmallBranch();
        quickExploreSelected = -1;
        if (game != null && Native.IsWindow(game.Handle) && !expanded) Native.SetForegroundWindow(game.Handle);
        return true;
    }
}
