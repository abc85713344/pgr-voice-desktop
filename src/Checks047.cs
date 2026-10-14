using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PgrVoice.Listening;

namespace PgrVoice;

/// <summary>
/// 0.4.7 桌面规则验收与运行截图（--test-047-ui --fixtures047 &lt;目录&gt;）：只在隔离测试状态（ui-test-state 或 --state）运行，
/// 目录内放只读夹具副本 ch29.json（现行 29 章）、062.json（EX02：未核续接 / 无文本死角 / 待配音）、ch33.json（33-8 结局分歧）。
/// 截图写入 Log.DataDir\shots047\，结果写 047-ui-test.txt。
/// </summary>
public partial class MainWindow
{
    async Task Run047UiTest(string[] args)
    {
        var report = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); report.Add("PASS: " + label); }
        async Task Drain() => await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        string shots = Path.Combine(Log.DataDir, "shots047"); Directory.CreateDirectory(shots);
        async Task Shot(string name) { await Drain(); await Task.Delay(120); await Drain(); Screenshot(Path.Combine("shots047", name + ".png")); report.Add("SHOT: " + name); }
        async Task WaitFor(Func<bool> condition, int ms, string label)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!condition() && DateTime.UtcNow < end) await Task.Delay(40);
            Check(condition(), label);
        }
        List<string> LogRows() => PlaybackLog.Recent.ToList();
        void Walk(Func<ListeningItem, bool> target, int number = 1)
        {
            var s = listeningSession!;
            for (int g = 0; g < 200000 && s.Current is { } item && !target(item); g++)
            {
                if (item.Kind == ListeningItemKind.Choice || item.IsBlocking)
                { var stop = s.ResolveStop(number); if (stop == null || !s.ApplyStop(stop)) throw new InvalidOperationException("夹具无法越过 " + item.Id); continue; }
                s.MoveNext();
            }
            Check(s.Current != null && target(s.Current), "夹具定位到目标：" + s.Current?.Id);
            MoveListening(() => true);
        }
        Button? FindButton(DependencyObject root, string label)
        {
            if (root is Button b && (b.Content as string) == label) return b;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                if (FindButton(child, label) is { } found) return found;
            return null;
        }
        // 定稿文字（p047.js / p047b.js）：卡内与暂停行的全部文字。
        string CardText()
        {
            var list = new List<string>();
            void Visit(object o) { if (o is TextBlock t && (t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : t.Text) is { Length: > 0 } x) list.Add(x); if (o is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Visit(c); }
            Visit(listeningChoices); Visit(listeningPauseDock); return string.Join("\n", list);
        }
        string[] madeUp = { "不会自动选择", "不会自动选入结局", "不会自动跳小节", "不自动结束本章", "不自动选择", "可立即继续" };
        void NoVariants(string where) { var t = CardText(); var bad = madeUp.Where(t.Contains).ToList(); Check(bad.Count == 0, where + "：无自加暂停变体" + (bad.Count > 0 ? "（" + string.Join("、", bad) + "）" : "")); }
        // 必须改 1：暂停键在默认（600×800）与紧凑（<680）尺寸下都不需滚动即完整可见；紧凑尺寸顺带截图。
        async Task PauseVisible(string shot)
        {
            double keep = Height;
            foreach (double h in new[] { 800.0, 640.0 })
            {
                Height = h; await Drain(); UpdateLayout(); await Drain();
                var pause = FindButton(listeningPauseDock, "暂停") ?? FindButton(listeningPauseDock, "继续");
                var content = (FrameworkElement)listeningTab.Content;
                var at = pause?.TranslatePoint(new Point(0, 0), content) ?? new Point(-1, -1);
                Check(pause != null && pause.IsVisible && at.Y >= 0 && at.Y + pause.ActualHeight <= content.ActualHeight + 1,
                    $"{shot}：窗口高 {h} 时暂停键不滚动即可见（y={at.Y:0} h={pause?.ActualHeight:0} 区域={content.ActualHeight:0}）");
                if (h < 680)
                {
                    await Shot(shot + "-紧凑");
                }
            }
            Height = keep; await Drain();
        }
        try
        {
            if (!testUi) throw new InvalidOperationException("0.4.7 检查只允许在隔离测试入口运行。");
            int dirArg = Array.IndexOf(args, "--fixtures047");
            string dir = dirArg >= 0 && dirArg + 1 < args.Length ? args[dirArg + 1] : Path.Combine(Log.DataDir, "fixtures047");
            string ch29 = Path.Combine(dir, "ch29.json"), ex02 = Path.Combine(dir, "062.json"), ch33 = Path.Combine(dir, "ch33.json");
            Check(File.Exists(ch29) && File.Exists(ex02) && File.Exists(ch33), "夹具齐全：" + dir);
            Width = 600; Height = 800; preferences.BranchAutoSelect = true; preferences.DefaultBranchOption = 1;
            branchAutoSelectBox.IsChecked = true; defaultBranchFirst.IsChecked = true;
            Check(new Preferences().BranchAutoSelect && new Preferences().DefaultBranchOption == 1, "默认设置：分支自动选择开启，默认第一个选项");
            listeningTestAudio = true; listeningDwellScale = 1;

            // 1. 普通分支 15 秒倒计时 + 已自动选择（29-1，手动方式）
            OpenListeningPack(ch29, "ch29"); Expand(listeningTab); listeningOptions.IsExpanded = false;
            Check(listeningSession!.Policy == ListeningBranchPolicy.Manual, "新章默认手动选择");
            Walk(i => i.Kind == ListeningItemKind.Choice && listeningSession!.ResolveStop(1)?.Action == ListeningStopAction.Choose);
            var choiceItem = listeningSession!.Current!; var expected = listeningSession.ResolveStop(1)!;
            StartListening(); await Drain();
            Check(ListeningCounting && listeningCountdown == ListeningText.CountdownSeconds && ListeningCountdownOptionId == expected.OptionId, "待选支线开始 15 秒倒计时，将选默认第一项：" + expected.OptionLabel);
            Check(listeningMessage.Contains("秒后自动选择：" + expected.OptionLabel), "状态行：" + listeningMessage);
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.Stop + ",") && r.Contains("支线待选")), "播放记录写入停下");
            await WaitFor(() => listeningCountdown <= 13, 5000, "倒计时按秒递减"); Check(listeningCountdown >= 10 && ListeningCounting, "倒计时按秒递减：" + listeningCountdown);
            await Shot("01-分支倒计时");
            // 设计稿 16：暂停 / 继续——暂停按钮包在卡内（不是 listeningChoices 的直接 Button 子元素），暂停后秒数停住、不自动选择，点「继续」恢复。
            Check(listeningChoices.Children.OfType<Button>().All(b => b.Content as string is not ("暂停" or "继续")), "暂停按钮不混入选项按钮");
            var pauseButton = FindButton(listeningPauseDock, "暂停"); Check(pauseButton != null, "倒计时卡有「暂停」");
            int stopRows = LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ","));
            pauseButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            Check(LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ",")) == stopRows && LogRows().Any(r => r.Contains("," + ListeningPauseLogType + ",")), "暂停记为「倒计时暂停」，不再多记一条「停下」");
            int frozen = listeningCountdown; await Task.Delay(2300); await Drain();
            Check(listeningCountdownPaused && ListeningCounting && listeningCountdown == frozen && ReferenceEquals(listeningSession.Current, choiceItem), "暂停后倒计时停住（" + frozen + " 秒）、不自动选择");
            Check(listeningMessage == "已暂停：倒计时停住，不自动续接；点「继续」恢复，或直接点选项。" && FindButton(listeningPauseDock, "继续") != null, "暂停状态：状态行逐字为定稿（p047.js:95）与「继续」按钮：" + listeningMessage);
            Check(CardText().Contains("已暂停：倒计时停住，不自动续接；点「继续」恢复。"), "01 暂停行逐字为定稿：" + CardText().Replace("\n", "|"));
            NoVariants("01 暂停");
            await Shot("16-倒计时暂停");
            await PauseVisible("16-倒计时暂停");
            FindButton(listeningPauseDock, "继续")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            await WaitFor(() => listeningCountdown < frozen, 3000, "继续后恢复倒计时");
            Check(!listeningCountdownPaused && ListeningCounting && listeningCountdown < frozen, "点「继续」恢复倒计时：" + listeningCountdown);
            // 主播放按钮与卡片「暂停 / 继续」同一状态、同一逻辑：卡片暂停 → 主按钮「继续」，点主按钮从停住秒数接着倒计时；反之亦然。
            string MainCaption() => listeningPlay.Content as string ?? "";
            string CardCaption() => (FindButton(listeningPauseDock, "暂停") ?? FindButton(listeningPauseDock, "继续"))?.Content as string ?? "";
            void Click(Button? b) => b?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(MainCaption() == "暂停" && CardCaption() == "暂停", "倒计时中主按钮与卡片都显示「暂停」：" + MainCaption() + " / " + CardCaption());
            Click(FindButton(listeningPauseDock, "暂停")); await Drain();
            int held = listeningCountdown;
            Check(listeningCountdownPaused && ListeningCounting && MainCaption() == "继续" && CardCaption() == "继续", "卡片暂停后主按钮也显示「继续」：" + MainCaption() + " / " + CardCaption());
            await Task.Delay(1300); await Drain();
            Check(listeningCountdown == held && ListeningCounting && ReferenceEquals(listeningSession.Current, choiceItem), "暂停期间秒数停住（" + held + " 秒）");
            Click(listeningPlay); await Drain();
            Check(!listeningCountdownPaused && ListeningCounting && listeningCountdown <= held && listeningCountdown >= held - 1 && ReferenceEquals(listeningSession.Current, choiceItem),
                "点主按钮「继续」从停住秒数接着倒计时、未取消：" + held + " → " + listeningCountdown);
            Check(MainCaption() == "暂停" && CardCaption() == "暂停", "恢复后两按钮都回到「暂停」：" + MainCaption() + " / " + CardCaption());
            await WaitFor(() => listeningCountdown < held, 3000, "主按钮继续后倒计时继续递减");
            Click(listeningPlay); await Drain();
            held = listeningCountdown;
            Check(listeningCountdownPaused && ListeningCounting && MainCaption() == "继续" && CardCaption() == "继续", "倒计时中点主按钮按卡片「暂停」处理（冻结、不取消），两按钮都显示「继续」");
            await Task.Delay(1300); await Drain();
            Check(listeningCountdown == held && ListeningCounting, "主按钮暂停后秒数停住（" + held + " 秒）");
            Click(FindButton(listeningPauseDock, "继续")); await Drain();
            Check(!listeningCountdownPaused && ListeningCounting && listeningCountdown >= held - 1 && MainCaption() == "暂停" && CardCaption() == "暂停", "卡片「继续」恢复后主按钮同步回「暂停」");
            await PauseVisible("01-分支倒计时");
            // 必须改 2：F7 / 手柄 East、F5 / 手柄 West、紧凑小窗、改默认分支都跟随暂停状态；暂停期间重播与改设置不解除暂停。
            string CompactCaption() => compactFollowActions.TryGetValue("pause", out var cb) ? cb.Content as string ?? "" : "";
            HandleListeningShortcut("pause"); await Drain(); held = listeningCountdown;
            Check(listeningCountdownPaused && ListeningCounting && MainCaption() == "继续" && CardCaption() == "继续", "F7 / 手柄 East 在倒计时中暂停倒计时（与主按钮同逻辑）");
            Check(CompactCaption() == "继续", "暂停时紧凑小窗暂停键显示「继续」：" + CompactCaption());
            string beforeReplay = listeningMessage;
            HandleListeningShortcut("replay"); await Drain(); await Task.Delay(1300); await Drain();
            Check(listeningMessage == beforeReplay && !listeningMessage.Contains("没有可重播"), "复审 3：停下处按 F5 状态不变：" + listeningMessage);
            Check(listeningCountdownPaused && ListeningCounting && listeningCountdown == held && ReferenceEquals(listeningSession.Current, choiceItem), "暂停时 F5 / 手柄 West 重播不重开倒计时、不解除暂停（" + held + " 秒）");
            int stopsBeforeDefault = LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ","));
            defaultBranchSecond.IsChecked = true; await Drain();
            var second2 = listeningSession.ResolveStop(2);
            Check(listeningCountdownPaused && ListeningCounting && listeningCountdown == held && ListeningCountdownOptionId == second2?.OptionId && MainCaption() == "继续", "暂停时改默认分支：改选第二项，仍暂停、秒数不变");
            defaultBranchFirst.IsChecked = true; await Drain();
            Check(listeningCountdownPaused && ListeningCounting && listeningCountdown == held && ListeningCountdownOptionId == expected.OptionId, "暂停时改回默认第一项，仍暂停");
            Check(LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ",")) == stopsBeforeDefault, "复审 5：停下处改默认分支（暂停中）不多记「停下」");
            // 复审 1：目录试听、「从本句开头」、「重选分支」（打开不选）都保留暂停与剩余秒数，按播放不重开 15 秒。
            var pauseKeepers = new (string Name, Func<Task> Act)[]
            {
                ("目录试听", async () =>
                {
                    var line = listeningSession.Items.First(i => i.Kind == ListeningItemKind.Line && i.SectionId == choiceItem.SectionId && i.NodeId != null).NodeId!;
                    PreviewListeningLine(line); await Drain();
                    Check(listeningCountdownPaused && listeningCountdown == held, "复审 1：目录试听中保留暂停（" + listeningCountdown + " 秒）");
                    if (listeningPreviewTicket != 0) { ListeningPlayClick(); await Drain(); } // 停止试听（试听已结束时不点，避免把「继续」当成恢复）
                }),
                ("从本句开头", async () => { Click(FindButton(listeningOptions, "从本句开头")); await Drain(); }),
                ("重选分支（打开不选）", async () => { ShowListeningRechoices(); await Drain(); if (listeningRechoose.ContextMenu is { } m) m.IsOpen = false; await Drain(); }),
            };
            foreach (var (name, act) in pauseKeepers)
            {
                await act(); await Task.Delay(1200); await Drain();
                Check(listeningCountdownPaused && ListeningCounting && listeningCountdown == held && listeningSession.Current?.Id == choiceItem.Id && MainCaption() == "继续",
                    "复审 1：" + name + "后仍暂停、剩余 " + held + " 秒、主按钮「继续」（现 " + listeningCountdown + " 秒，" + MainCaption() + "）");
            }
            choiceItem = listeningSession.Current!;
            HandleListeningShortcut("pause"); await Drain();
            Check(!listeningCountdownPaused && ListeningCounting && listeningCountdown >= held - 1 && MainCaption() == "暂停" && CompactCaption() == "暂停", "F7 / 手柄 East 在暂停时恢复倒计时，紧凑小窗同步「暂停」");
            // 暂停时换小节：倒计时取消、暂停复位，不自动选择；随后回到原分支点继续后面的检查。
            HandleListeningShortcut("pause"); await Drain();
            string otherSection = (listeningSession.Chapter.Sections.FirstOrDefault(x => x.Id != choiceItem.SectionId && listeningSession.Items.FirstOrDefault(i => i.SectionId == x.Id)?.Kind == ListeningItemKind.Line) ?? listeningSession.Chapter.Sections.First(x => x.Id != choiceItem.SectionId)).Id;
            MoveListening(() => listeningSession!.SeekSection(otherSection)); await Drain(); await Task.Delay(300);
            Check(!ListeningCounting && !listeningCountdownPaused && listeningPauseDock.Children.Count == 0 && MainCaption() == "播放 / 续听" && listeningSession.Current?.SectionId == otherSection, "暂停时换小节：倒计时取消、暂停复位，主按钮回到「播放 / 续听」");
            MoveListening(() => listeningSession!.SeekSection(choiceItem.SectionId));
            string choiceId = choiceItem.Id; Walk(i => i.Id == choiceId); choiceItem = listeningSession.Current!;
            listeningCountdownTickMs = 40; CancelListeningCountdown(); BeginListeningStop();
            await WaitFor(() => !ReferenceEquals(listeningSession.Current, choiceItem), 3000, "倒计时结束自动选择");
            Check(ListeningAutoNotice == ListeningText.AutoSelected + expected.OptionLabel, "显示「已自动选择：" + expected.OptionLabel + "」：" + ListeningAutoNotice);
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.AutoSelect + ",") && r.Contains("倒计时结束")), "播放记录写入自动选择");
            PauseListening(); await Shot("02-已自动选择提示");
            listeningCountdownTickMs = 1000;

            // 用户点击优先：再到下一处待选支线，倒计时中点第二项
            Walk(i => i.Kind == ListeningItemKind.Choice && listeningSession!.ResolveStop(1)?.Action == ListeningStopAction.Choose && i.Options!.Count >= 2);
            var userItem = listeningSession.Current!; StartListening(); await Drain();
            var second = userItem.Options![1];
            ToggleListeningCountdownPause(); await Drain(); Check(listeningCountdownPaused && ListeningCounting, "（暂停时选项）先暂停倒计时");
            var secondButton = listeningChoices.Children.OfType<Button>().FirstOrDefault(b => b.Content as string == second.Label);
            Check(secondButton != null, "倒计时中可点其他选项");
            secondButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            Check(!ReferenceEquals(listeningSession.Current, userItem) && !ListeningCounting && !listeningCountdownPaused && listeningSession.ChoicePoints.Any(p => p.Item.Id == userItem.Id && p.SelectedOptionId == second.Id), "暂停时点选项：立即选择所点项，倒计时取消、暂停复位");
            PauseListening();

            // 2. 无配音签（ch29 无音频文件）：显示字幕、按字数停留后继续
            listeningDwellScale = 1;
            Walk(i => i.Kind == ListeningItemKind.Line && PendingVoice.IsFlagged(i.Node));
            var noVoice = listeningSession.Current!; StartListening(); await Drain();
            Check(listeningRunning && ReferenceEquals(listeningSession.Current, noVoice) && listeningMessage.StartsWith("无配音 · "), "无配音句不跳过：" + listeningMessage);
            Check(listeningTags.Children.Count > 0, "显示「无配音」签");
            await Shot("03-无配音签");
            await WaitFor(() => !ReferenceEquals(listeningSession.Current, noVoice), PendingVoice.DwellMilliseconds(noVoice.Node!.Text) + 1500, "按字数停留后进入下一句");
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.NoVoicePass + ",")), "播放记录写入无配音越过");
            // 手动一次点击越过
            var manualLine = listeningSession.Current; ListeningNextClick(); await Drain();
            Check(!ReferenceEquals(listeningSession.Current, manualLine), "停留中点一次「下一句」即越过");
            PauseListening();

            // 3. 待配音签 + 等待确认续接 + 无文本死角（EX02）
            OpenListeningPack(ex02, null); Expand(listeningTab);
            Walk(i => i.Kind == ListeningItemKind.Line && PendingVoice.IsDraftMissing(i.Node));
            var draft = listeningSession.Current!; StartListening(); await Drain();
            Check(listeningMessage.StartsWith("待配音 · ") && listeningTags.Children.Count > 0, "待配音签与状态行：" + listeningMessage);
            await Shot("04-待配音签");
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.PendingVoicePass + ",") && r.Contains(draft.NodeId)), "播放记录写入待配音越过");
            PauseListening();

            Walk(i => i.Id.StartsWith("notice:bw-158b388dea7d75aa-m000-o00-gap:", StringComparison.Ordinal));
            var order = listeningSession.Current!;
            Check(ListeningCounting && ListeningPendingStop?.Action == ListeningStopAction.OrderContinue, "等待确认续接：定位后即开始倒计时（不必先点播放）");
            var next = listeningNextButton;
            Check(next != null && next.IsEnabled, "「下一句」保持可点");
            Check(listeningChoices.Children.OfType<CutFrame>().Any() && FindButton(listeningChoices, "按顺序继续") != null && FindButton(listeningChoices, "反馈") != null, "续接卡：按顺序继续 + 反馈");
            Check(CardText().Contains("倒计时结束后从本节下一句接着听，并显示「此处分支续接未核实，已按顺序继续」。点「暂停」停住倒计时，不自动续接；再点「继续」恢复。"), "11 正文（未暂停）逐字为定稿（p047b.js:82）");
            NoVariants("11");
            ToggleListeningCountdownPause(); await Drain();
            Check(CardText().Contains("已暂停：倒计时停住，不自动续接；点「继续」恢复。也可直接点「按顺序继续」或「下一句」。") && listeningMessage == "已暂停：倒计时停住，不自动续接；点「继续」恢复。", "11 正文（暂停）逐字为定稿（p047b.js:82），状态行：" + listeningMessage);
            NoVariants("11 暂停");
            await PauseVisible("11-续接未核实-暂停");
            ToggleListeningCountdownPause(); await Drain();
            await Task.Delay(1200); await Shot("05-等待确认续接倒计时"); await PauseVisible("11-续接未核实");
            next!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            Check(!ReferenceEquals(listeningSession.Current, order) && ListeningAutoNotice == ListeningText.OrderContinued, "一次「下一句」立即按顺序继续并提示：" + ListeningAutoNotice);
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.OrderContinue + ",") && r.Contains("用户确认")), "播放记录写入按顺序续接");
            PauseListening(); await Shot("06-已按顺序继续");

            Walk(i => i.Id.StartsWith("notice:bw-d73b039df2d4d232-l4f284b28461dd72a82b2:", StringComparison.Ordinal));
            var dead = listeningSession.Current!;
            Check(ListeningPendingStop is { DeadEnd: true, Action: ListeningStopAction.NextSection } && ListeningCounting, "无文本死角：倒计时后跳到下一小节");
            Check(System.Text.RegularExpressions.Regex.IsMatch(listeningMessage, "^" + ListeningText.DeadEndHint + @"。\d+ 秒后跳到下一小节。$"), "死角状态行为定稿「…请点反馈。N 秒后跳到下一小节。」（p047.js:135）：" + listeningMessage);
            NoVariants("04");
            FindButton(listeningChoices, "反馈")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            Check(LogRows().Any(r => r.Contains("," + PlaybackLog.Types.PlayerFeedback + ",") && r.Contains(dead.SectionTitle.Split(' ')[0])) && ListeningCounting, "反馈写入播放记录，倒计时继续");
            await Shot("07-无文本死角卡"); await PauseVisible("04-无文本死角");
            ToggleListeningCountdownPause(); await Drain();
            {
                var t = CardText();
                Check(t.Contains("倒计时结束后跳到下一小节。") && !t.Contains("也可直接点"), "复审 2：04 暂停时正文不变（「倒计时结束后跳到下一小节。」，无「也可直接点…」）");
                var last = listeningPauseDock.Children.OfType<FrameworkElement>().LastOrDefault() as TextBlock;
                Check(last?.Text.StartsWith("反馈会记录这一处") == true, "复审小问题：04 脚注在暂停行之后（最后）");
            }
            ToggleListeningCountdownPause(); await Drain();
            // 开关关闭：停下等待
            int stopsBeforeToggle = LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ","));
            branchAutoSelectBox.IsChecked = false; await Drain();
            Check(!preferences.BranchAutoSelect && !ListeningCounting && listeningMessage.Contains("分支自动选择已关闭"), "关闭分支自动选择后停下等待");
            await Shot("08-开关关闭后等待");
            branchAutoSelectBox.IsChecked = true; await Drain();
            Check(ListeningCounting, "重新开启后恢复倒计时");
            Check(LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ",")) == stopsBeforeToggle, "复审 5：停下处关 / 开分支自动选择不多记「停下」");
            listeningCountdownTickMs = 40; CancelListeningCountdown(); BeginListeningStop();
            await WaitFor(() => !ReferenceEquals(listeningSession.Current, dead), 3000, "死角倒计时结束跳下一小节");
            Check(ListeningAutoNotice?.StartsWith("已跳到下一小节") == true && LogRows().Any(r => r.Contains("," + PlaybackLog.Types.DeadEndJump + ",")), "跳到下一小节并记录：" + ListeningAutoNotice);
            PauseListening(); listeningCountdownTickMs = 1000;
            {
                // 复审 4：默认分支策略下，暂停时改默认分支也换成新计划（SetPolicy），并保留暂停。
                listeningPolicy.SelectedIndex = 1; await Drain();
                var ps = listeningSession!;
                for (int g = 0; g < 200000 && ps.Current is { } it && !(it.IsBlocking && ps.ResolveStop(ListeningDefaultNumber) != null); g++)
                { if (it.Kind == ListeningItemKind.Choice || it.IsBlocking) { var st = ps.ResolveStop(1); if (st == null || !ps.ApplyStop(st)) break; continue; } ps.MoveNext(); }
                if (ps.Current is { IsBlocking: true } stopAt)
                {
                    MoveListening(() => true); await Drain();
                    if (!ListeningCounting) { CancelListeningCountdown(); BeginListeningStop(); await Drain(); }
                    if (!listeningCountdownPaused) { ToggleListeningCountdownPause(); await Drain(); }
                    string id4 = listeningSession.Current!.Id; int held4 = listeningCountdown; int stops4 = LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ","));
                    defaultBranchSecond.IsChecked = true; await Drain();
                    Check(listeningSession.Policy == ListeningBranchPolicy.Second && listeningCountdownPaused && ListeningCounting && listeningCountdown == held4 && listeningSession.Current?.Id == id4,
                        "复审 4：暂停时改默认分支，计划换成第二项（" + listeningSession.Policy + "），仍暂停 " + listeningCountdown + "/" + held4 + " 秒，" + id4);
                    defaultBranchFirst.IsChecked = true; await Drain();
                    Check(listeningSession.Policy == ListeningBranchPolicy.First && listeningCountdownPaused && listeningCountdown == held4, "复审 4：改回第一项，计划同步、仍暂停");
                    Check(LogRows().Count(r => r.Contains("," + PlaybackLog.Types.Stop + ",")) == stops4, "复审 5：默认分支策略下改默认分支不多记「停下」");
                }
                else report.Add("NOTE: 复审 4 夹具在默认分支策略下找不到停下处（本章节）");
                PauseListening(); listeningPolicy.SelectedIndex = 0; await Drain();
            }

            // 4. 结局分歧自动选择（33-8）：只听所选结局，不串接
            OpenListeningPack(ch33, "ch33"); Expand(listeningTab);
            Walk(i => i.Kind == ListeningItemKind.Choice && i.Node != null && MenuShape.Of(listeningSession!.Pack, i.Node).Ending);
            var ending = listeningSession.Current!; var endingStop = listeningSession.ResolveStop(1)!;
            StartListening(); await Drain();
            Check(ListeningCounting && ListeningCountdownOptionId == endingStop.OptionId, "结局分歧同样倒计时，将选：" + endingStop.OptionLabel);
            Check(System.Text.RegularExpressions.Regex.IsMatch(listeningMessage, @"^遇到支线，\d+ 秒后自动选择：.+（开启结局·[^）]+）$"), "结局状态行为定稿「遇到支线，N 秒后自动选择：…（开启结局·X）」（p047.js:124）：" + listeningMessage);
            NoVariants("03");
            await Task.Delay(1100); await Shot("09-结局分歧倒计时"); await PauseVisible("03-结局分歧");
            listeningCountdownTickMs = 40; CancelListeningCountdown(); BeginListeningStop();
            await WaitFor(() => !ReferenceEquals(listeningSession.Current, ending), 3000, "结局倒计时结束自动选择");
            var other = ending.Options!.Where(o => o.Id != endingStop.OptionId).Select(o => o.TargetId).ToHashSet();
            Check(ListeningAutoNotice == ListeningText.AutoSelected + endingStop.OptionLabel && !listeningSession.Items.Skip(listeningSession.Position).Any(i => i.NodeId != null && other.Contains(i.NodeId)), "只听所选结局，不串接其他结局");
            PauseListening(); await Shot("10-结局已自动选择");
            listeningCountdownTickMs = 1000;

            // 设计稿 12：听书「EXPLORE · 探索菜单逐项读」卡（29-1 营地：回环选项按列出顺序各读一次，出口最后读）。只读显示，不改收听计划。
            OpenListeningPack(ch29, "ch29"); Expand(listeningTab); listeningOptions.IsExpanded = false;
            Walk(i => i.Kind == ListeningItemKind.Line && ListeningExploreState() is { } v && v.Heard >= 1 && v.Rows.Any(r => r.State == ExploreState.Now && !r.Exit));
            await Drain();
            var exploreView = ListeningExploreState()!;
            Check(listeningExploreCard.Visibility == Visibility.Visible && exploreView.Total >= 2 && exploreView.Rows.Count(r => r.State == ExploreState.Now) == 1,
                "12 探索卡显示：已听 " + exploreView.Heard + " / " + exploreView.Total + "，正在收听 " + exploreView.Rows.First(r => r.State == ExploreState.Now).Label);
            Check(exploreView.Rows.Any(r => r.Exit && r.State == ExploreState.Todo), "12 出口行最后读（未到）");
            report.Add("EXPLORE: " + string.Join(" | ", exploreView.Rows.Select(r => r.Label + "=" + r.State + (r.Exit ? "(出口)" : ""))) + (exploreView.Hint != null ? " · 提示：" + exploreView.Hint : ""));
            await Shot("L12-探索菜单逐项读");
            Check(ExploreHintStillUseful("建议先找栗西", new[] { new ExploreRow("栗西 · 了解", 3, 3, ExploreState.Done, false, null) }) == false
                && ExploreHintStillUseful("建议先找栗西", new[] { new ExploreRow("栗西 · 了解", 3, 1, ExploreState.Now, false, null) })
                && (exploreView.Hint == null || ExploreHintStillUseful(exploreView.Hint, exploreView.Rows)), "复审小问题：建议的人物听过（✓）后隐藏「建议先找…」：" + (exploreView.Hint ?? "（已隐藏）"));
            { double keep = Height; Height = 640; await Drain(); UpdateLayout(); await Shot("L12-探索菜单逐项读-紧凑"); Height = keep; await Drain(); }
            Walk(i => ListeningExploreState() == null && i.Kind == ListeningItemKind.Line);
            Check(listeningExploreCard.Visibility == Visibility.Collapsed, "走出探索菜单后卡片收起");

            // 5. 设置：分支与播放记录（开关 + 导出）
            string? exported = ExportPlaybackLog(Log.DataDir);
            Check(exported != null && File.ReadAllText(exported).Contains(PlaybackLog.Types.AutoSelect) && File.ReadAllBytes(exported).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "一键导出播放记录（UTF-8 BOM CSV）");
            Expand(SettingsTab); branchSettingsExpander.IsExpanded = true; await Drain(); branchSettingsExpander.BringIntoView(); await Shot("11-设置-分支与播放记录");
            report.Add("LOG: " + exported);

            // 设计稿 14 / 15：游戏伴读的探索菜单卡（桌面浮动菜单，29-1 营地）。
            {
                StopListeningForGame(); LoadPack(ch29);
                var campMenu = engine!.Pack.Nodes.First(n => n.Kind == "choice" && n.Text == "营地 → 选择人物");
                var leave = engine.Pack.Nodes.First(n => n.Kind == "choice" && n.SectionId == campMenu.SectionId && n.Options.Any(o => o.Label == "再等等……") && n.Options.Any(o => o.Label == "离开营地"));
                List<string> Texts(DependencyObject root)
                {
                    var list = new List<string>();
                    void Visit(DependencyObject d) { if (d is TextBlock t) list.Add(string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) is { Length: > 0 } r2 ? r2 : t.Text); if (d is ContentControl { Content: string c }) list.Add(c); foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Visit(child); }
                    Visit(root); return list;
                }
                async Task MenuShot(string name)
                {
                    await Drain(); branchMenu.UpdateLayout(); await Task.Delay(120); await Drain();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(branchMenu.ActualWidth), (int)Math.Ceiling(branchMenu.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render((System.Windows.Media.Visual)branchMenu.Content); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(shots, name + ".png"))) png.Save(stream);
                    report.Add("SHOT: " + name);
                }
                Check(engine.OpenGameMenu(campMenu.Id, campMenu.SectionId), "打开 29-1 营地人物菜单");
                // 现行第29章包的选项名为「搭话（栗西 · 了解情况）」等，旧包没有「搭话（」前缀。
                var lixi = campMenu.Options.First(o => o.Label.Contains("栗西 · 了解", StringComparison.Ordinal)); var derrick = campMenu.Options.First(o => o.Label.Contains("戴里克", StringComparison.Ordinal));
                DesktopExploration.Add(engine, campMenu.Id, lixi.Id);
                var firstLine = engine.Pack.ById[derrick.TargetId];
                DesktopExploration.RecordJump(engine, campMenu.Id, new(derrick.Id, derrick.Label, firstLine.Speaker + "「" + firstLine.Text + "」"));
                DesktopExploration.SetFollowUnavailable(engine, campMenu.Id, false);
                branchMenu.ExplorePlayingOptionId = derrick.Id; // 只为截图：播放选项正文时浮动菜单实际是收起的
                branchMenu.Present(engine, Left + 40, Top + 40, 0); await Drain();
                var t14 = Texts(branchMenu);
                Check(branchMenu.IsExploreView, "14 探索卡视图");
                Check(t14.Any(t => t.StartsWith("已根据游戏画面跳转：" + derrick.Label, StringComparison.Ordinal)) && t14.Any(t => t.StartsWith("读到：", StringComparison.Ordinal)), "14 已根据游戏画面跳转 + 读到");
                Check(t14.Any(t => t.StartsWith("还有 ", StringComparison.Ordinal) && t.EndsWith(" 段没听", StringComparison.Ordinal)), "14 还有 N 段没听");
                Check(t14.Contains("✓ 已听") && t14.Contains("▶ 正在播放") && t14.Contains("未听") && t14.Contains("A") && t14.Contains("B") && t14.Contains("C"), "14 行：A/B/C 键位字、✓ 已听 / ▶ 正在播放 / 未听");
                Check(t14.Contains("换人") && t14.Contains("重新识别") && t14.Contains("随时可选") && t14.Contains("EXIT"), "14 换人 / 重新识别、出口随时可选");
                Check(!t14.Any(t => t.Contains("主线") || t.Contains("闲聊")), "不显示主线 / 闲聊标识");
                Check(!branchMenu.ExploreFallbackVisible, "能读取游戏画面时不显示兜底卡");
                await MenuShot("L14-探索自动跟随卡");
                branchMenu.ExplorePlayingOptionId = null; DesktopExploration.SetFollowUnavailable(engine, campMenu.Id, true);
                branchMenu.Present(engine, Left + 40, Top + 40, 0); await Drain();
                var fb = Texts(branchMenu);
                Check(branchMenu.ExploreFallbackVisible && fb.Contains("没能从游戏画面认出是谁") && fb.Contains("请点选正在交谈的人物，或开口后点「重新识别」。") && fb.Contains(derrick.Label), "14 兜底卡：没能从游戏画面认出是谁 + 人物选择");
                await MenuShot("L14-兜底-没能认出");
                branchMenu.Dismiss();
                Check(engine.OpenGameMenu(leave.Id, leave.SectionId), "打开 29-1 离开营地 / 再等等菜单");
                branchMenu.Present(engine, Left + 40, Top + 40, 0); await Drain();
                var t15 = Texts(branchMenu);
                Check(!t15.Any(t => t.Contains("主线") || t.Contains("闲聊")), "15 标题不带「[主线剧情]」等标识：" + string.Join("|", t15.Where(t => t.Contains("主线") || t.Contains("闲聊"))));
                Check(BranchMenuWindow.LoopPlace("营地 → 选择人物") == "营地" && BranchMenuWindow.LoopPlace("[折叠] → 选择路线") == null && BranchMenuWindow.LoopPlace("请选择游戏中相同的路线 → 选项") == null && BranchMenuWindow.LoopPlace("再等等") == null
                    && BranchMenuWindow.LoopReturnLabel(null) == "回到菜单" && BranchMenuWindow.LoopReturnLabel("营地") == "回到营地", "15 地点推不出来时只写「回到菜单」，不编地名");
                // 去掉「[主线剧情]」只作用于 12 / 14 / 15 卡标题：小节列表与其余小节标题照旧。
                var camp = engine.Pack.Chapters.SelectMany(c => c.Sections).First(x => x.Id == campMenu.SectionId);
                Check(camp.Title.Contains("[主线剧情]") && SectionDisplayTitle(camp.Id) == SectionDisplay.Title(engine.Pack.Chapters.SelectMany(c => c.Sections), camp.Id) && SectionBox.Items.OfType<Section>().Where(x => x.Id == camp.Id).All(x => x.ToString() == camp.Title)
                    && BranchMenuWindow.ExploreMenuName("[折叠] → 选择路线", camp.Title) == BranchMenuWindow.StripKindTag(camp.Title) && !BranchMenuWindow.StripKindTag(camp.Title)!.Contains("["),
                    "卡标题去标签不影响小节列表 / 页头小节名：原名「" + camp.Title + "」，页头「" + SectionDisplayTitle(camp.Id) + "」，小节列表「" + string.Join("|", SectionBox.Items.OfType<Section>().Where(x => x.Id == camp.Id)) + "」");
                Check(t15.Contains("回到营地") && t15.Contains("随时可选"), "15 回环行右侧「回到营地」、出口行「随时可选」：" + string.Join("|", t15.Where(t => t.Length < 40).Skip(Math.Max(0, t15.Count - 30))));
                await MenuShot("L15-回环与出口");
                // 15 未听：营地人物菜单里未读的人物行右侧「未听」（上面 14 已检）。
                branchMenu.Dismiss();
            }

            // 6. 游戏配音终点规则（文字跟随，GameTextReader 由隔离样本替身）：末句停留后进入等待，提示终点文字、写记录，
            //    画面仍是末句时继续等，读到下一小节首句 / 出口后的下一句自动接上（自动切小节）。
            string gameEnd = Path.Combine(dir, "game-end.json");
            Check(File.Exists(gameEnd), "终点夹具存在：" + gameEnd);
            StopListeningForGame(); LoadPack(gameEnd);
            Check(engine?.Pack.Id == "synthetic-game-end", "载入终点等待夹具");
            Expand(gameTextTab); textAutoBox.IsChecked = false;
            string sampleText = "", sampleSpeaker = ""; long fakeNow = 1_000_000;
            gameTextSampleTest = p => ReferenceEquals(p, textProbes[0]) && sampleText.Length > 0 ? new GameTextSample(true, sampleText, false, "", true, sampleSpeaker) : new GameTextSample(false, "", false, "隔离测试");
            textAutoClockTest = () => fakeNow;
            void SelectSection(string id)
            {
                var chapter = engine!.Pack.Chapters.First(c => c.Sections.Any(x => x.Id == id));
                ChapterBox.SelectedItem = chapter; SectionBox.SelectedItem = chapter.Sections.First(x => x.Id == id);
            }
            async Task ShowGameLine(string id)
            {
                var n = engine!.Pack.ById[id]; sampleText = n.Text; sampleSpeaker = n.Speaker;
                await WaitFor(() => engine.CurrentId == id, 4000, "文字跟随定位到 " + id + "（" + gameTextNotice.Text + "）");
            }
            try
            {
                SelectSection("syn-sec"); ArmTextPlayback(); Check(textArmed, "开启文字跟随");
                await ShowGameLine("syn-sec-s9");
                int endRows = LogRows().Count(r => r.Contains("," + PlaybackLog.Types.EndWait + ","));
                fakeNow += 60_000;
                await WaitFor(() => GameEndWaiting != null, 4000, "小节末句停留结束后进入终点等待");
                Check(GameEndWaiting!.Hint == "原表终点" && gameEndWaitCard.Visibility == Visibility.Visible && gameEndWaitTitle.Text == "原表终点", "等待卡显示终点自己的文字「原表终点」");
                Check(textArmed && !gameTextNotice.Text.Contains("本段已") && !gameTextNotice.Text.Contains("剧情已结束"), "文字跟随保持开启，不显示旧的结束提示：" + gameTextNotice.Text);
                Check(LogRows().Count(r => r.Contains("," + PlaybackLog.Types.EndWait + ",") && r.Contains("原表终点")) == endRows + 1, "写一条「终点等待定位」记录（含终点文字）");
                await Shot("12-终点等待卡");
                await Task.Delay(300);
                Check(GameEndWaiting != null && engine!.CurrentId == "syn-sec-s9", "画面仍是末句时继续等待");
                await ShowGameLine("syn-sec2-a");
                Check(GameEndWaiting == null && gameEndWaitCard.Visibility == Visibility.Collapsed && textSection == "syn-sec2" && (SectionBox.SelectedItem as Section)?.Id == "syn-sec2" && textArmed,
                    "读到下一小节首句后自动接上并切到下一小节");
                PauseTextPlayback("隔离测试：重置"); sampleText = ""; await Task.Delay(150);
                SelectSection("syn-sec"); ArmTextPlayback();
                await ShowGameLine("syn-sec-n2");
                fakeNow += 60_000;
                await WaitFor(() => GameEndWaiting != null, 4000, "选项出口末句后进入终点等待");
                Check(GameEndWaiting!.Hint == PgrVoice.Following.GameEndWaitPolicy.FallbackHint && gameEndWaitTitle.Text == PgrVoice.Following.GameEndWaitPolicy.FallbackHint, "无文字的选项出口终点用兜底提示");
                Check(StateText.Text.EndsWith("终点 · 等待游戏画面定位", StringComparison.Ordinal), "悬浮球状态显示终点等待：" + StateText.Text);
                Expand(gameTextTab); await Drain(); gameEndWaitCard.BringIntoView(); await Shot("13-终点等待卡-展开");
                Check(GameEndWaiting != null && gameEndWaitCard.Visibility == Visibility.Visible, "展开面板后仍在等待并显示卡片");
                var connectButton = FindButton(gameEndWaitCard, "连接普通对白"); var keptWait = GameEndWaiting;
                Check(connectButton != null, "等待卡有「连接普通对白」入口");
                connectButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                Check(textArmed && ReferenceEquals(GameEndWaiting, keptWait) && gameEndWaitCard.Visibility == Visibility.Visible, "点「连接普通对白」开启监听并保留终点等待");
                await ShowGameLine("syn-sec-l9");
                Check(GameEndWaiting == null && textArmed, "读到出口后的下一句自动接上");
            }
            finally { PauseTextPlayback("隔离终点检查结束"); gameTextSampleTest = null; textAutoClockTest = null; }
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally { listeningTestAudio = false; listeningCountdownTickMs = 1000; StopListeningForGame(); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "047-ui-test.txt"), report);
        Close();
    }
}
