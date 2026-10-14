using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PgrVoice.AndroidApp;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunStoryReferenceUiTest()
    {
        var report = new List<string>(); string? source = null, sourceHash = null;
        void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); report.Add("PASS: " + text); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string Navigation() => JsonSerializer.Serialize(engine!.ExportNavigation(), Json.Options);
        try
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "--story-reference-pack");
            Check(testUi && at >= 0 && at + 1 < args.Length, "独立测试状态、禁用实际音频且显式提供来源");
            source = args[at + 1]; sourceHash = Hash(source);
            LoadPack(source); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var pack = engine!.Pack; const string section = "ch32-71fcf055b68c2cfb1350";
            var menu = pack.ById[section + "-menu-002"];
            Check(BundledStoryReference.TryGetNode(pack, menu, out var reference) && reference != null, "真实32-6菜单准确对应内置资料");
            string detail = reference!.LocationText + "\n\n" + reference.DetailText;
            Check(detail.Contains("471") && detail.Contains("472") && detail.Contains("473"), "详情保留选择471与两项实际后继472/473");
            string full = BundledStoryReference.GetSectionText(pack, section);
            Check(full.Contains("九龙人民不会逃离这颗星球") && full.Contains("万世铭"), "小节全文含用户给出的正文与后续选择");
            string packSnapshot = JsonSerializer.Serialize(pack, Json.Options);
            var addedIds = new[]
            {
                section + "-t307f8181af1adbfd0c6", section + "-t307ad2c136045c71b16",
                section + "-t307111d7f68f927e68f", section + "-t3071dd2eb4f5fc9d6fa"
            };
            var added = addedIds.Select(id => pack.ById[id]).ToArray();
            Check(full.Contains("内置版本 + 当前包补充") && full.Contains("内置的 257 条") && full.Contains("另有 4 条新增记录"), "现行32-6区分257条内置资料与4条包内补充");
            int supplementAt = full.IndexOf("──────── 当前包补充 ────────", StringComparison.Ordinal);
            Check(supplementAt >= 0 && added.All(n => full[supplementAt..].Contains(n.Id) && full[supplementAt..].Contains(n.Text) && full[supplementAt..].Contains(n.Source!)), "四句补充原文、包内节点号与包内来源完整可查");
            Check(full[supplementAt..].Contains("尚未绑定内置资料") && full[supplementAt..].Contains("不表示游戏发生顺序"), "补充明确未绑定及顺序限制，不伪装成内置游戏资料");
            Check(added.All(n => !BundledStoryReference.TryGetNode(pack, n, out _)), "可看补充不授予新增节点内置资料绑定");
            // 以下是独立的显示核验夹具，删节点仅用于模拟旧资料集合，不交给播放/导航引擎。
            Pack DisplayCopy() => JsonSerializer.Deserialize<Pack>(packSnapshot, Json.Options)!;
            var oldDisplay = DisplayCopy(); oldDisplay.Nodes.RemoveAll(n => addedIds.Contains(n.Id));
            string oldFull = BundledStoryReference.GetSectionText(oldDisplay, section);
            Check(oldFull.StartsWith("内置剧情文本 · 只读参考") && oldFull.Contains("正文和选项已与当前小节逐条对应") && !oldFull.Contains("──────── 当前包补充"), "完整匹配旧资料集合保持原有全文和绑定说明");
            Check(BundledStoryReference.GetSectionText(pack, section) == full, "切换旧集合后重新核对现行包，补充未受缓存污染");
            Check(!string.IsNullOrWhiteSpace(BundledStoryReference.GetSummary(pack)), "可显示当前章资料来源版本");
            var line = pack.Nodes.First(n => n.SectionId == section && n.Kind == "line" && n.Text.Contains("九龙人民不会逃离这颗星球"));
            Check(BundledStoryReference.TryGetNode(pack, line, out var located) && located != null && located.LocationText.Contains("472"), "完整台词和说话人对应游戏472身份");
            string originalText = line.Text, originalSpeaker = line.Speaker;
            try
            {
                line.Text += "（测试差异）";
                Check(!BundledStoryReference.TryGetNode(pack, line, out _), "正文不同不误用旧身份");
                Check(BundledStoryReference.GetSectionText(pack, section).Contains("暂不展示已绑定资料"), "共同节点正文冲突时仍拒绝整节已绑定声明");
            }
            finally { line.Text = originalText; }
            try
            {
                line.Speaker = "测试不同角色";
                Check(!BundledStoryReference.TryGetNode(pack, line, out _), "说话人不同不误用旧身份");
                Check(BundledStoryReference.GetSectionText(pack, section).Contains("暂不展示已绑定资料"), "共同节点角色冲突时不沿用全文缓存");
            }
            finally { line.Speaker = originalSpeaker; }
            var changedChoice = DisplayCopy();
            changedChoice.Nodes.Single(n => n.Id == menu.Id).Options[0].Label += "（测试差异）";
            Check(BundledStoryReference.GetSectionText(changedChoice, section).Contains("暂不展示已绑定资料"), "共同菜单选项冲突仍被拒绝");
            var missing = DisplayCopy(); missing.Nodes.RemoveAll(n => n.Id == line.Id);
            Check(BundledStoryReference.GetSectionText(missing, section).Contains("暂不展示已绑定资料"), "旧资料中的台词缺失时不冒称完整对应");
            var changedAddition = DisplayCopy(); changedAddition.Nodes.Single(n => n.Id == added[0].Id).Text = "新增句变更后的显示测试";
            Check(BundledStoryReference.GetSectionText(changedAddition, section).Contains("正文：新增句变更后的显示测试"), "包内补充每次读取当前原文，不缓存旧正文");
            Check(BundledStoryReference.GetSectionText(pack, section) == full && JsonSerializer.Serialize(pack, Json.Options) == packSnapshot, "所有只读核验后实际章节及其完整显示保持不变");
            Check(BundledStoryReference.GetSectionText(pack, "missing-section").Contains("未收录"), "不存在的小节不借用其他小节资料");
            // 目标句在第二个选项的已核实段落内；按正常菜单流程准备路线，不能直接绕过 Commit 的分支限制。
            var options = menu.Options.Where(o => o.PathId == line.PathId && o.BodyVerified && o.SegmentIds.Contains(line.Id)).ToArray();
            Check(options.Length == 1, "目标472台词唯一属于真实菜单的已核实路线");
            var option = options[0];
            Check(engine.OpenStoryMenu(menu.Id, section) && engine.CurrentId == menu.Id && engine.Mode == RunMode.Choice,
                "通过正常导航入口打开目标菜单并等待选择");
            int optionIndex = engine.AvailableOptions.FindIndex(o => o.Id == option.Id);
            Check(optionIndex >= 0, "目标路线在当前菜单可选项中");
            engine.SelectBranch(optionIndex);
            Check(engine.Choices.TryGetValue(menu.Id, out var chosenPath) && chosenPath == option.PathId &&
                engine.CurrentId == option.TargetId && engine.Allowed(line), "正常确认分支后进入正确入口并取得目标句定位资格");
            engine.Commit(line.Id);
            Check(engine.CurrentId == line.Id && engine.Mode == RunMode.Following && engine.Current?.PathId == option.PathId &&
                engine.History.LastOrDefault()?.NodeId == line.Id, "准备完成后实际当前句、路线和导航历史均为目标472台词");
            HideBranchMenu();
            int plays = playCalls; string before = Navigation();
            Exception? dialogError = null;
            experienceDialogTest = dialog =>
            {
                try
                {
                    var window = (StoryReferenceWindow)dialog;
                    void Snapshot(string name)
                    {
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(Log.DataDir, name)); encoder.Save(file);
                    }
                    Check(window.Reader.IsReadOnly && window.Reader.Text.Contains("472") && window.Reader.Text.Contains(line.Text), "真实窗口显示当前原文和节点且正文只读");
                    window.SectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.Reader.Text == full, "点击本小节全文读取同一节完整文本");
                    Snapshot("内置剧情全文.png");
                    window.Search.Text = "九龙人民不会逃离这颗星球";
                    Check(window.FindNext() && window.Reader.SelectedText == window.Search.Text, "查找精确选中目标原文");
                    Check(window.FindNext(), "重复查找可回绕找到原文");
                    window.Search.Text = added[0].Id;
                    Check(window.FindNext() && window.Reader.SelectedText == added[0].Id, "全文查找可定位当前包补充节点");
                    window.Reader.Select(supplementAt, 0);
                    window.Reader.ScrollToLine(Math.Max(0, window.Reader.GetLineIndexFromCharacterIndex(supplementAt)));
                    Snapshot("内置剧情补充.png");
                    window.Search.Text = "不存在的测试词123XYZ"; Check(!window.FindNext(), "未找到给出结果且不改台词");
                    window.Search.Text = ""; Check(!window.FindNext(), "空查找不触发定位");
                    window.CurrentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.Reader.Text.Contains("472") && window.Reader.Text != full, "可返回当前句资料");
                    Check(Navigation() == before && playCalls == plays, "打开切换全文查找均不改路线历史已听或播放次数");
                    Snapshot("内置剧情资料.png");
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(!window.IsVisible, "Esc关闭资料窗口");
                }
                catch (Exception ex) { dialogError = ex; dialog.Close(); }
            };
            // 设置页入口读取实际当前句；目录入口另有相同只读处理。
            StoryTab.IsSelected = false;
            StoryReferenceClick(this, new RoutedEventArgs()); experienceDialogTest = null;
            if (dialogError != null) throw dialogError;
            Check(Navigation() == before && playCalls == plays, "关闭资料后保留实际播放位置与播放次数");
            Check(Hash(source) == sourceHash, "正式章节文件未修改");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        finally
        {
            experienceDialogTest = null; experienceDialog?.Close();
            if (source != null && sourceHash != null && Hash(source) != sourceHash) report.Add("FAIL: 正式来源变化");
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllLines(Path.Combine(Log.DataDir, "story-reference-ui-test.txt"), report);
            Json.Save(Path.Combine(Log.DataDir, "story-reference-ui-result.json"), new { passed = !report.Any(s => s.StartsWith("FAIL:")), checks = report.Count(s => s.StartsWith("PASS:")), source, sourceSha256 = sourceHash, scope = "隔离WPF只读资料、查找及无导航/播放副作用；未做真实游戏通关" });
            Close();
        }
    }
}
