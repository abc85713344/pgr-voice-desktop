using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PgrVoice;

public partial class MainWindow
{
    readonly LibrarySearchIndex librarySearchIndex = new();

    void OpenLibrarySearch()
    {
        if (experienceDialog != null) return;
        // 不刷新当前下拉框、不暂停或载入包；只有明确打开结果才进入选句流程。
        string folder = chapterLibraryFolder;
        var known = LibraryBox.Items.OfType<PackChoice>().Select(p => p.File).ToArray();
        IReadOnlyList<string> Sources() => folder.Length > 0 ? LibraryPaths.Packs(folder) : known;
        var dialog = new LibrarySearchWindow(librarySearchIndex, Sources);
        ShowExperienceDialog(dialog);
        if (dialog.Result is { } result) _ = OpenLibrarySearchResult(result);
    }

    async Task OpenLibrarySearchResult(LibrarySearchResult result)
    {
        try
        {
            if (engine?.Mode == RunMode.Original) { Tell("游戏原声时段只预览搜索结果；请先结束原声时段，再打开目标台词。"); return; }
            var previousEngine = engine; string? previousNode = engine?.CurrentId;
            var previousListening = listeningSession; string? previousListeningNode = listeningSession?.Current?.NodeId;
            // 磁盘重新校验先于任何播放状态变更；过期结果不切章、不选路线。
            var target = await Task.Run(() => LibrarySearchIndex.Resolve(result));
            if (closing) return;
            if (engine != previousEngine || engine?.CurrentId != previousNode || listeningSession != previousListening || listeningSession?.Current?.NodeId != previousListeningNode)
            { Tell("播放位置已变化，本次没有打开旧搜索结果；请重新确认目标台词。"); return; }
            if (engine?.Mode == RunMode.Original) { Tell("游戏原声时段只预览搜索结果；请先结束原声时段，再打开目标台词。"); return; }
            if (listeningRunning && !PauseListening()) { Tell("听书位置未能保存，请处理后再打开搜索结果。"); return; }
            if (TextFollowing) PauseTextPlayback("已打开搜索结果，文字跟随暂停。");
            StopAutomatic("已打开搜索结果，自动播放暂停。", false);
            StopListeningForGame(); StopPreview(); CancelOcr(); engine?.PauseForBrowse();
            // 导航指纹不包含正文与音频；明确打开时总是重载，防止复用同图的旧清单。
            var engineBeforeLoad = engine;
            LoadPack(result.PackFile);
            if (engine == null || ReferenceEquals(engineBeforeLoad, engine) || engine.Pack.Id != target.Pack.Id ||
                PlaybackEngine.NavigationFingerprint(engine.Pack) != PlaybackEngine.NavigationFingerprint(target.Pack) ||
                !String.Equals(preferences.PackFile, result.PackFile, StringComparison.OrdinalIgnoreCase) ||
                !engine.Pack.ById.TryGetValue(result.NodeId, out var node) || node.SectionId != result.SectionId ||
                node.PathId != result.PathId || node.Text != result.Text || node.Speaker != result.Speaker)
            { Tell("当前章节未能打开该结果，请重新搜索；没有确认播放或改变路线。"); return; }
            var chapter = engine.Pack.Chapters.First(c => c.Id == result.ChapterId);
            ChapterBox.SelectedItem = chapter;
            SectionBox.SelectedItem = chapter.Sections.First(s => s.Id == result.SectionId);
            SearchChapterBox.IsChecked = false;
            // 搜索节点有时不在当前路线可定位范围，仍显示准确原节点，交给既有确认入口处理。
            SearchBox.Text = result.Text;
            FillLines();
            var selected = rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
            if (selected == null)
            {
                selected = new LineRow(node, engine.Pack);
                rows.Insert(0, selected); LinesList.ItemsSource = null; LinesList.ItemsSource = rows;
            }
            LinesList.SelectedItem = selected; LinesList.ScrollIntoView(selected); Expand(StoryTab);
            Tell("已打开并选中搜索台词，保持暂停。核对路线后，点击“确认播放选中台词”播放。");
        }
        catch (Exception ex) { Tell("搜索结果暂不能打开：" + ex.Message); }
    }
}
