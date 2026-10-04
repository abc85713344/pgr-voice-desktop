using System;
using System.Collections.Generic;
using System.Linq;

namespace PgrVoice;

// 仅描述白色像素的形态证据，不识字，也不证明新台词已完整显示。
public enum DialogueFrameVerdict { NoChange, Typing, Advanced, Uncertain }

public sealed record DialogueFrameMask(int Width, int Height, double ElapsedMs, byte[] Pixels);

public sealed record DialogueFrameMetrics(
    double ElapsedMs, int BeforeInkPixels, int AfterInkPixels, int RetainedInkPixels,
    int AddedInkPixels, int RemovedInkPixels, double RetainedOldInkRatio,
    double NewInkRatio, double ChangedInkRatio, DialogueFrameVerdict Evidence);

public sealed record DialogueFrameDecision(
    DialogueFrameVerdict Verdict, string Reason, IReadOnlyList<DialogueFrameMetrics> Frames);

public sealed class DialogueFrameAnalysisOptions
{
    public int MinimumInkPixels { get; init; } = 80;
    public double MinimumInkDensity { get; init; } = 0.001;
    public double MaximumInkDensity { get; init; } = 0.30;
    public double UnchangedInkRatio { get; init; } = 0.06;
    public double MinimumTypingRetention { get; init; } = 0.90;
    public double MinimumTypingGrowth { get; init; } = 0.15;
    public double MaximumAdvanceRetention { get; init; } = 0.55;
    public double MinimumAdvanceNewInkRatio { get; init; } = 0.45;
    public int MinimumAddedInkPixels { get; init; } = 32;
    public double MaximumTerminalChangeRatio { get; init; } = 0.08;
    public double MinimumEvidenceSpanMs { get; init; } = 80;
}

public static class DialogueFrameAnalysis
{
    // 输入须为逐行自顶向下的 BGR24 或 BGRA32；Alpha 不参与白字判断。
    public static DialogueFrameMask CreateWhiteMask(int width, int height, int stride,
        ReadOnlySpan<byte> pixels, double elapsedMs = 0, int bytesPerPixel = 3)
    {
        if (width <= 0 || height <= 0 || bytesPerPixel is not (3 or 4) ||
            stride < (long)width * bytesPerPixel ||
            (long)stride * (height - 1) + (long)width * bytesPerPixel > pixels.Length ||
            (long)width * height > int.MaxValue || !double.IsFinite(elapsedMs))
            throw new ArgumentException("白字检测图像的尺寸、步长或采样时间无效。");
        var mask = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * bytesPerPixel;
            int low = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            int high = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            if (low >= 170 && high - low <= 45) mask[y * width + x] = 1;
        }
        return new(width, height, elapsedMs, mask);
    }

    // 捕获端仍需检查前帧新鲜度、前台/遮挡、几何和输入会话是否有效。
    // 这里故意不把“稳定”解释为打字完毕；只用它确认替换证据连续存在。
    public static DialogueFrameDecision Analyze(DialogueFrameMask? before,
        IReadOnlyList<DialogueFrameMask>? afterFrames, DialogueFrameAnalysisOptions? options = null)
    {
        var settings = options ?? new();
        ValidateOptions(settings);
        var metrics = new List<DialogueFrameMetrics>();
        DialogueFrameDecision Unknown(string reason) => new(DialogueFrameVerdict.Uncertain, reason, metrics.ToArray());
        if (!Valid(before)) return Unknown("按键前图像无效。");
        if (afterFrames == null || afterFrames.Count < 2) return Unknown("缺少连续多帧证据。");
        int beforeInk = CountInk(before!);
        if (!UsableInk(before!, beforeInk, settings)) return Unknown("按键前白字过少、区域过亮或接近空白。");

        double previousTime = before!.ElapsedMs;
        foreach (var frame in afterFrames)
        {
            if (!Valid(frame) || frame.Width != before.Width || frame.Height != before.Height)
                return Unknown("采样图像无效或区域尺寸改变。");
            if (frame.ElapsedMs <= previousTime) return Unknown("采样时间无效或不是连续的独立采样。");
            previousTime = frame.ElapsedMs;
            var difference = Compare(before, frame, settings);
            metrics.Add(difference);
            if (!UsableInk(frame, difference.AfterInkPixels, settings))
                return Unknown("采样出现空白、白字过少或区域过亮，可能处于转场。");
        }
        if (afterFrames[^1].ElapsedMs - afterFrames[^2].ElapsedMs < settings.MinimumEvidenceSpanMs)
            return Unknown("末两帧时间过近，连续证据不足。");
        if (metrics.All(m => m.Evidence == DialogueFrameVerdict.NoChange))
            return new(DialogueFrameVerdict.NoChange, "采样期间白字形态没有明显变化。", metrics.ToArray());

        bool typingSeen = metrics.Any(m => m.Evidence == DialogueFrameVerdict.Typing);
        bool advanceSeen = metrics.Any(m => m.Evidence == DialogueFrameVerdict.Advanced);
        if (typingSeen && advanceSeen) return Unknown("同一次输入同时出现补字和替换证据，不能确定推进次数。");
        if (metrics.Any(m => m.Evidence == DialogueFrameVerdict.Uncertain))
            return Unknown("变化幅度或旧字保留比例处于不确定区间。");

        var target = advanceSeen ? DialogueFrameVerdict.Advanced : DialogueFrameVerdict.Typing;
        bool started = false;
        foreach (var item in metrics)
        {
            if (item.Evidence == target) started = true;
            else if (started) return Unknown("变化后又回到原图形态，证据不连续。");
        }
        if (metrics[^1].Evidence != target || metrics[^2].Evidence != target)
            return Unknown("末两帧未同时支持同一种变化。");

        // 新白字可以继续增加，但已出现的字再次大量消失意味着混合转场。
        int firstChanged = metrics.FindIndex(m => m.Evidence == target);
        for (int i = firstChanged + 1; i < afterFrames.Count; i++)
        {
            var step = Compare(afterFrames[i - 1], afterFrames[i], settings);
            if (step.RetainedOldInkRatio < settings.MinimumTypingRetention)
                return Unknown("后续采样又丢失已出现的白字，可能包含转场或多次推进。");
        }
        if (target == DialogueFrameVerdict.Typing)
            return new(target, "旧白字基本保留且持续出现新增白字，符合补字形态；未确认文字完整。", metrics.ToArray());

        var terminal = Compare(afterFrames[^2], afterFrames[^1], settings);
        if (terminal.ChangedInkRatio > settings.MaximumTerminalChangeRatio)
            return Unknown("旧字替换后末两帧仍明显变化，暂不确认替换。" );
        return new(target, "旧白字大量消失且新白字出现，连续末帧支持一次文字替换；未识别台词内容或完整性。", metrics.ToArray());
    }

    static DialogueFrameMetrics Compare(DialogueFrameMask before, DialogueFrameMask after, DialogueFrameAnalysisOptions options)
    {
        int oldCount = 0, newCount = 0, retained = 0;
        for (int i = 0; i < before.Pixels.Length; i++)
        {
            bool a = before.Pixels[i] != 0, b = after.Pixels[i] != 0;
            if (a) oldCount++;
            if (b) newCount++;
            if (a && b) retained++;
        }
        int added = newCount - retained, removed = oldCount - retained;
        double retention = oldCount == 0 ? 0 : (double)retained / oldCount;
        double newRatio = newCount == 0 ? 0 : (double)added / newCount;
        double change = (double)(added + removed) / Math.Max(1, oldCount + newCount - retained);
        var evidence = DialogueFrameVerdict.Uncertain;
        if (change <= options.UnchangedInkRatio) evidence = DialogueFrameVerdict.NoChange;
        else if (retention >= options.MinimumTypingRetention &&
            added >= options.MinimumAddedInkPixels && newCount >= oldCount * (1 + options.MinimumTypingGrowth))
            evidence = DialogueFrameVerdict.Typing;
        else if (retention <= options.MaximumAdvanceRetention && newRatio >= options.MinimumAdvanceNewInkRatio &&
            added >= options.MinimumAddedInkPixels && removed >= options.MinimumAddedInkPixels)
            evidence = DialogueFrameVerdict.Advanced;
        return new(after.ElapsedMs, oldCount, newCount, retained, added, removed, retention, newRatio, change, evidence);
    }

    static bool Valid(DialogueFrameMask? frame) => frame != null && frame.Pixels != null && frame.Width > 0 &&
        frame.Height > 0 && (long)frame.Width * frame.Height == frame.Pixels.Length && double.IsFinite(frame.ElapsedMs);

    static int CountInk(DialogueFrameMask frame)
    {
        int count = 0;
        foreach (byte pixel in frame.Pixels) if (pixel != 0) count++;
        return count;
    }

    static bool UsableInk(DialogueFrameMask frame, int count, DialogueFrameAnalysisOptions settings)
    {
        double density = (double)count / frame.Pixels.Length;
        return count >= settings.MinimumInkPixels && density >= settings.MinimumInkDensity && density <= settings.MaximumInkDensity;
    }

    static void ValidateOptions(DialogueFrameAnalysisOptions settings)
    {
        static bool Ratio(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
        if (settings.MinimumInkPixels < 1 || settings.MinimumAddedInkPixels < 1 ||
            !Ratio(settings.MinimumInkDensity) || !Ratio(settings.MaximumInkDensity) ||
            settings.MinimumInkDensity >= settings.MaximumInkDensity ||
            !Ratio(settings.UnchangedInkRatio) || !Ratio(settings.MinimumTypingRetention) ||
            !Ratio(settings.MinimumTypingGrowth) || !Ratio(settings.MaximumAdvanceRetention) ||
            !Ratio(settings.MinimumAdvanceNewInkRatio) || !Ratio(settings.MaximumTerminalChangeRatio) ||
            settings.MaximumAdvanceRetention >= settings.MinimumTypingRetention ||
            !double.IsFinite(settings.MinimumEvidenceSpanMs) || settings.MinimumEvidenceSpanMs <= 0)
            throw new ArgumentException("白字检测判据配置无效。");
    }
}
