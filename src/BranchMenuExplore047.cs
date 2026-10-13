using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 设计稿 14 / 15：游戏伴读的探索菜单卡（桌面浮动菜单）。
/// 卡头「探索 · EXPLORE · AUTO-FOLLOW」+「还有 N 段没听」；标题「小节号 菜单名」；说明「跟随游戏画面自动跳转；读取失败时可手动点选。」；
/// 青绿框「已根据游戏画面跳转：…／读到：…」（来自分支监听按游戏对话框接上的那次）；行：A/B/C 键位字、✓ 已听 / ▶ 正在播放 / 未听，
/// 回环行 ↻ +「回到营地」（营地名取自本节菜单名），出口行 EXIT +「随时可选」；底部「换人」（人物 / 话题，原 Interactions）与「重新识别」（原定位游戏当前台词）。
/// 兜底「没能从游戏画面认出是谁」：分支监听无法读取游戏画面（非原生客户端）时出现，点人物即选中并确认。不显示主线 / 闲聊标识。
/// </summary>
public sealed partial class BranchMenuWindow
{
    readonly TextBlock exploreRemainText = new() { FontSize = 11, Foreground = Theme.Brush("BrassText"), VerticalAlignment = VerticalAlignment.Center };
    Border exploreRemain = null!;
    readonly TextBlock exploreJumpHead = new() { FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("TealText"), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock exploreJumpQuote = new() { FontSize = 11, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    CutFrame exploreJump = null!;
    readonly WrapPanel exploreFallbackChips = new();
    CutFrame exploreFallback = null!;
    /// <summary>正在播放的探索选项（▶ 正在播放）。桌面游戏模式播放选项正文时浮动菜单是收起的，目前只有测试截图会设它。</summary>
    public string? ExplorePlayingOptionId { get; set; }
    public bool IsExploreView { get; private set; }
    public bool ExploreFallbackVisible => exploreFallback.Visibility == Visibility.Visible;

    void InitializeExplore(StackPanel header, DockPanel badgeRow)
    {
        exploreRemain = new Border { BorderBrush = Theme.Brush("Brass"), BorderThickness = new Thickness(1), Background = Theme.Brush("BranchFill"), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Child = exploreRemainText };
        DockPanel.SetDock(exploreRemain, Dock.Right); badgeRow.Children.Insert(0, exploreRemain);
        var jumpHead = new DockPanel();
        jumpHead.Children.Add(Docked(new System.Windows.Shapes.Path { Data = Icons.Locate, Stroke = Theme.Brush("TealText"), StrokeThickness = 1.2, Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top }, Dock.Left));
        jumpHead.Children.Add(exploreJumpHead);
        exploreJump = new CutFrame { Background = Theme.Brush("TealFill"), BorderBrush = Theme.Brush("Teal"), OutlineBrush = Hud.Solid("#9CC3C0"), EdgeWidth = 3, Cut = 7, Corners = CutCorners.BottomRight, Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
            Child = new StackPanel { Children = { jumpHead, exploreJumpQuote } } };
        int at = header.Children.IndexOf(notice); header.Children.Insert(at + 1, exploreJump);
        var fb = new StackPanel();
        fb.Children.Add(new TextBlock { Text = "FALLBACK · 读取失败时", FontFamily = Theme.Font("LabelFont"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("AccentText") });
        fb.Children.Add(new TextBlock { Text = "没能从游戏画面认出是谁", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        fb.Children.Add(new TextBlock { Text = "请点选正在交谈的人物，或开口后点「重新识别」。", FontSize = 11.5, Foreground = Theme.Brush("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        fb.Children.Add(exploreFallbackChips);
        exploreFallback = new CutFrame { Background = Hud.Solid("#F4F5F7"), BorderBrush = Theme.Brush("Stroke"), OutlineBrush = Theme.Brush("Stroke"), EdgeWidth = 0, Cut = 10, Corners = CutCorners.BottomRight, Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed, Child = fb };
        System.Windows.Automation.AutomationProperties.SetName(exploreFallback, "没能从游戏画面认出是谁");
        header.Children.Insert(header.Children.IndexOf(exploreJump) + 1, exploreFallback);
    }

    /// <summary>非探索菜单 / 其他视图：收起探索卡的附加部分，恢复原按钮文字。</summary>
    void ResetExplore()
    {
        IsExploreView = false;
        exploreRemain.Visibility = exploreJump.Visibility = exploreFallback.Visibility = Visibility.Collapsed;
        locateButton.Content = "定位游戏当前台词"; notice.ToolTip = null;
    }
    /// <summary>设计稿 14 .xr：细描边行、右下切角感由行内容承担；选中 = 红描边 + 左侧 3px 红条（.xr.on）。</summary>
    static readonly Lazy<Style> ExploreRowStyle = new(() =>
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 5)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        var border = new FrameworkElementFactory(typeof(Border), "row");
        border.SetValue(Border.BackgroundProperty, Brushes.White);
        border.SetValue(Border.BorderBrushProperty, Hud.Solid("#D5DADF"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new Thickness(0));
        border.SetValue(FrameworkElement.MinHeightProperty, 40.0);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Hud.Solid("#A3283A"), "row"));
        selected.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(3, 1, 1, 1), "row"));
        template.Triggers.Add(selected);
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, .55));
        template.Triggers.Add(off);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    });

    /// <summary>探索菜单的卡头、标题、跳转框与兜底卡。</summary>
    void PresentExploreHeader(PlaybackEngine engine, Node menu, MenuShapeInfo shape, Func<ChoiceOption, bool> heard)
    {
        IsExploreView = true;
        var section = engine.Pack.Chapters.SelectMany(c => c.Sections).FirstOrDefault(s => s.Id == menu.SectionId);
        string number = SectionNumber(section?.Title ?? "");
        string name = ExploreMenuName(menu.Text, section?.Title);
        title.Text = name == StripKindTag(section?.Title) || number.Length == 0 ? name : number + " " + name; // 无名菜单退回小节名时不重复小节号
        badge.Text = "探索"; headerTag.Content = Archive.StripedTag("EXPLORE · AUTO-FOLLOW");
        var available = engine.AvailableOptions;
        int left = available.Count(o => shape.Loops.Any(l => l.Id == o.Id) && !heard(o) && o.Id != ExplorePlayingOptionId);
        exploreRemainText.Inlines.Clear();
        exploreRemainText.Inlines.Add(new System.Windows.Documents.Run("还有 "));
        exploreRemainText.Inlines.Add(new System.Windows.Documents.Run(left.ToString()) { FontWeight = FontWeights.Bold });
        exploreRemainText.Inlines.Add(new System.Windows.Documents.Run(" 段没听"));
        exploreRemain.Visibility = Visibility.Visible;
        notice.Text = "跟随游戏画面自动跳转；读取失败时可手动点选。\n↑↓选择 · Enter确认 · Esc收起";
        notice.ToolTip = string.IsNullOrWhiteSpace(engine.Notice) ? null : engine.Notice;
        // 行样式换成设计稿 14 的 .xr（键位字代替 01 / 02 序号）；按钮用两列紧凑排列，给人物行留出高度。
        Options.ItemContainerStyle = ExploreRowStyle.Value; LayoutFooter(true);
        var jump = DesktopExploration.LastJump(engine, menu.Id);
        exploreJump.Visibility = jump == null ? Visibility.Collapsed : Visibility.Visible;
        if (jump != null) { exploreJumpHead.Text = "已根据游戏画面跳转：" + jump.OptionLabel; exploreJumpQuote.Text = "读到：" + jump.Quote; }
        peopleButton.Content = "换人"; locateButton.Content = "重新识别";
        exploreFallbackChips.Children.Clear();
        bool failed = DesktopExploration.FollowUnavailable(engine, menu.Id);
        exploreFallback.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed)
            for (int i = 0; i < available.Count; i++)
            {
                var o = available[i]; if (heard(o) && !shape.Exits.Any(x => x.Id == o.Id)) continue;
                bool exit = shape.Exits.Any(x => x.Id == o.Id); int index = i;
                var chip = new Button { Content = o.Label, Focusable = false, MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 6, 4, 0), FontSize = 12,
                    Background = exit ? Theme.Brush("BranchFill") : Brushes.White, BorderBrush = exit ? Theme.Brush("Brass") : Theme.Brush("Stroke"), Foreground = exit ? Theme.Brush("BrassText") : Theme.Brush("Fg"), BorderThickness = new Thickness(1) };
                chip.Click += (_, _) => { Options.SelectedIndex = index; if (CanConfirm) Confirm?.Invoke(); };
                exploreFallbackChips.Children.Add(chip);
            }
    }
    /// <summary>界面不显示主线 / 闲聊标识：去掉小节名末尾的「[主线剧情]」等方括号标签。</summary>
    internal static string? StripKindTag(string? title) => title == null ? null : System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\[［【][^\]］】]*[\]］】]\s*$", "").Trim();
    /// <summary>「营地 → 选择人物」→「营地 · 选择人物」；无名菜单（[折叠] / 选择路线）退回小节名。</summary>
    internal static string ExploreMenuName(string text, string? sectionTitle)
    {
        var parts = (text ?? "").Split('→').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith("[", StringComparison.Ordinal)).ToList();
        if (parts.Count == 0 || parts.All(p => p == "选择路线")) return StripKindTag(sectionTitle) ?? "探索";
        return string.Join(" · ", parts);
    }
    /// <summary>回环行右侧去向：「回到营地」——取本节菜单名里的场所（如「营地 → 选择人物」的「营地」），推不出来时为「回到菜单」。</summary>
    /// <summary>菜单名里的场所：「场所 → 菜单」的第一段；「[折叠]」「请选择…」「选择路线」等不是场所，返回 null（不编地名）。</summary>
    internal static string? LoopPlace(string? menuText)
    {
        var text = menuText ?? ""; if (!text.Contains('→')) return null;
        var first = text.Split('→')[0].Trim();
        return first.Length == 0 || first.StartsWith("[", StringComparison.Ordinal) || first.StartsWith("请", StringComparison.Ordinal) || first == "选择路线" ? null : first;
    }
    /// <summary>回环行去向文字：有场所为「回到营地」，推不出来时只写「回到菜单」。</summary>
    internal static string LoopReturnLabel(string? place) => place != null ? "回到" + place : "回到菜单";
    static string LoopReturnText(PlaybackEngine engine, Node menu)
    {
        // 场所 = 本菜单名第一段；本菜单没有（如「[折叠] → 选择路线」）时取本节探索菜单（如「营地 → 选择人物」）；都没有时「回到菜单」。
        bool Exploring(Node n) { try { return MenuShape.Of(engine.Pack, n).Exploration; } catch { return false; } }
        string? place = LoopPlace(menu.Text) ?? engine.Pack.ById.Values.Where(n => n.Kind == "choice" && n.SectionId == menu.SectionId && LoopPlace(n.Text) != null && Exploring(n)).Select(n => LoopPlace(n.Text)).FirstOrDefault();
        return LoopReturnLabel(place);
    }

    /// <summary>探索菜单的一行（设计稿 14 .xr / 15 dk）：键位字 A/B/C、名称、右侧状态。</summary>
    FrameworkElement ExploreOptionRow(PlaybackEngine engine, Node menu, ChoiceOption o, int index, bool isLoop, bool isExit, bool heard, string state, bool returnLoop)
    {
        bool playing = o.Id == ExplorePlayingOptionId;
        var dock = new DockPanel { MinHeight = 30 };
        FrameworkElement right;
        if (isExit) right = heard ? MarkText("✓ 已选", "#2F7F7A", true) : MarkText("随时可选", "#7F6223", false);
        else if (playing) right = MarkText("▶ 正在播放", "#B33144", true);
        else if (heard) right = MarkText("✓ 已听", "#2F7F7A", true);
        else if (returnLoop) right = MarkText(LoopReturnText(engine, menu), "#7A8591", false);
        else right = MarkText("未听", "#7A8591", false);
        dock.Children.Add(Docked(right, Dock.Right));
        if (isExit)
        {
            dock.Children.Add(Docked(new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,2.5 H2.5 V13.5 H6 M7,8 H14 M11,5 L14,8 L11,11"), Stroke = Hud.Solid("#7F6223"), StrokeThickness = 1.4, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
            dock.Children.Add(Docked(new TextBlock { Text = "EXIT", FontFamily = Theme.Font("LabelFont"), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Hud.Solid("#7F6223"), Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
        }
        else
        {
            var key = new TextBlock { Text = ((char)('A' + Math.Min(index, 25))).ToString(), FontFamily = Theme.Font("NumberFont"), FontSize = 17, FontWeight = FontWeights.Bold, Width = 18, VerticalAlignment = VerticalAlignment.Center,
                Foreground = playing ? Hud.Solid("#A3283A") : heard ? Hud.Solid("#8A96A3") : Theme.Brush("Brass"), Margin = new Thickness(0, 0, 8, 0) };
            if (!returnLoop) dock.Children.Add(Docked(key, Dock.Left)); // 设计稿 15：回环行只有 ↻，不标键位字
            if (returnLoop)
                dock.Children.Add(Docked(new System.Windows.Shapes.Path { Data = Icons.Replay, Stroke = Theme.Brush("Slate"), StrokeThickness = 1.3, Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "回环：读完回到本菜单" }, Dock.Left));
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = o.Label + state, FontSize = 13, FontWeight = heard && !isExit ? FontWeights.Normal : FontWeights.SemiBold, Foreground = heard && !isExit ? Hud.Solid("#5C6670") : Theme.Brush("Fg"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(o.Preview) && !heard)
            words.Children.Add(new TextBlock { Text = o.Preview, ToolTip = o.Preview, FontSize = 11, Foreground = Theme.Brush("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(words);
        var row = new Border { Child = dock, Padding = new Thickness(10, 5, 10, 5), MinHeight = 38 };
        if (heard && !isExit) row.Background = Hud.Solid("#EEF3F2");
        else if (isExit && !heard) row.Background = Hud.Solid("#F3ECDC");
        return row;
    }
    static TextBlock MarkText(string text, string color, bool bold) => new() { Text = text, FontSize = 11, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Hud.Solid(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
}
