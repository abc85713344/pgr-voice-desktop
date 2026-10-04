using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace PgrVoice;

public sealed record DiagnosticProgress(int CompletedLines, int TotalLines, string NodeId);
public sealed record DiagnosticIssue(string Category, string CategoryLabel, string NodeId, string SectionId, string Speaker, string Text, string? Audio, string Detail);
public sealed class PackDiagnosticReport
{
    public string PackId { get; set; } = "";
    public string PackTitle { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }
    public int TotalLines { get; set; }
    public int ReadableLines { get; set; }
    public int CheckedFiles { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
    public List<DiagnosticIssue> Issues { get; set; } = new();
    public string Summary => $"检查 {TotalLines} 句，音频可读取 {ReadableLines} 句，其他 {Issues.Count} 句见分类；共检查 {CheckedFiles} 个独立音频路径。";
}

public static class PlayerDiagnostics
{
    public static Task<PackDiagnosticReport> ScanAsync(Pack pack, CancellationToken cancellationToken, IProgress<DiagnosticProgress>? progress = null)
    {
        // 保留调用时的章，切换界面后不会突然开始检查下一章。
        var nodes = pack.Nodes.Where(n => !n.Archived && n.Kind == "line").ToArray();
        return Task.Run(() => Scan(pack, nodes, cancellationToken, progress), cancellationToken);
    }
    static PackDiagnosticReport Scan(Pack pack, Node[] nodes, CancellationToken token, IProgress<DiagnosticProgress>? progress)
    {
        var report = new PackDiagnosticReport { PackId = pack.Id, PackTitle = pack.Title, TotalLines = nodes.Length };
        var checkedFiles = new Dictionary<string, (string Category, string Label, string Detail)>(StringComparer.OrdinalIgnoreCase);
        var buffer = new float[32768];
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            (string Category, string Label, string Detail) result;
            string? file;
            try { file = pack.ResolveAudio(node); }
            catch (Exception ex) { Add(node, ("invalid-path", "音频路径无效", ex.Message)); continue; }
            if (file == null)
            {
                result = node.AudioStatus switch
                {
                    "draft-missing" => ("draft-missing", "草稿缺句，等待补配", node.AudioReason ?? "保留正文，补齐后自动更新。"),
                    "not-spoken" => ("not-spoken", "无可朗读正文", node.AudioReason ?? "来源将此句标记为无可朗读正文。"),
                    "generation-failed" => ("generation-failed", "补配未通过核验", node.AudioReason ?? "尚未接入可播放录音。"),
                    "not-found" => ("not-found", "未找到对应配音", node.AudioReason ?? "尚未绑定可靠录音。"),
                    _ => ("unbound", "配音待核对", node.AudioReason ?? "尚未绑定可靠录音。")
                };
            }
            else if (!checkedFiles.TryGetValue(file, out result))
            {
                result = CheckFile(file, buffer, token);
                checkedFiles[file] = result;
                report.CheckedFiles++;
            }
            Add(node, result);
        }
        token.ThrowIfCancellationRequested();
        report.CheckedAt = DateTimeOffset.Now;
        return report;

        void Add(Node node, (string Category, string Label, string Detail) result)
        {
            if (result.Category == "readable") report.ReadableLines++;
            else report.Issues.Add(new(result.Category, result.Label, node.Id, node.SectionId, node.Speaker, node.Text, node.Audio, result.Detail));
            report.Counts[result.Category] = report.Counts.GetValueOrDefault(result.Category) + 1;
            progress?.Report(new(report.ReadableLines + report.Issues.Count, nodes.Length, node.Id));
        }
    }
    static (string Category, string Label, string Detail) CheckFile(string path, float[] buffer, CancellationToken token)
    {
        try
        {
            var before = new FileInfo(path);
            long length = before.Length; DateTime written = before.LastWriteTimeUtc;
            if (length == 0) return ("unreadable", "音频无法读取", "文件为空。");
            using var audio = new AudioFileReader(path);
            long samples = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = audio.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                samples += count;
            }
            if (samples == 0) return ("unreadable", "音频无法读取", "解码后没有音频采样。");
            var after = new FileInfo(path);
            if (after.Length != length || after.LastWriteTimeUtc != written) return ("changed-file", "检查时音频发生变化", "请在文件更新完成后重新检查。");
            return ("readable", "音频可读取", "完整文件已解码；此检查不判断台词内容或音色是否正确。");
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return ("missing-file", "音频文件缺失", "绑定路径下没有找到文件。"); }
        catch (DirectoryNotFoundException) { return ("missing-file", "音频文件缺失", "绑定路径的目录不存在。"); }
        catch (Exception ex) { return ("unreadable", "音频无法读取", ex.Message); }
    }
    public static void Export(PackDiagnosticReport report, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase)) { Json.Save(fullPath, report); return; }
        var text = new StringBuilder();
        text.AppendLine("战双配音包检查报告").AppendLine(report.PackTitle).AppendLine(report.Summary)
            .AppendLine("检查时间：" + report.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .AppendLine("检查范围：当前配音包中未归档的台词。可读取不代表台词内容、角色或音色已核对正确。").AppendLine();
        foreach (var group in report.Issues.GroupBy(i => i.CategoryLabel))
        {
            text.AppendLine($"【{group.Key}】{group.Count()} 句");
            foreach (var issue in group) text.AppendLine($"{issue.SectionId} / {issue.NodeId} / {issue.Speaker}").AppendLine(issue.Text).AppendLine("音频：" + (issue.Audio ?? "未绑定")).AppendLine("原因：" + issue.Detail).AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text.ToString(), new UTF8Encoding(false));
    }
}
