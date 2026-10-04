using System.Globalization;

namespace PgrVoice.Following;

public sealed class SubtitleStabilityOptions
{
    /// <summary>忽略几个像素的抗锯齿、压缩或采样抖动。</summary>
    public int MinimumChangedPixels { get; init; } = 8;
    /// <summary>分母只包含两帧白字像素的并集，不使用整张截图面积。</summary>
    public double MinimumChangedInkRatio { get; init; } = .08;
    /// <summary>一个字的变化不能被区域中的大片白色背景稀释。</summary>
    public int DefiniteChangedPixels { get; init; } = 24;
    public int MinimumInkPixels { get; init; } = 12;
    public int RequiredStableObservations { get; init; } = 2;
}

public readonly record struct SubtitleObservation(bool Changed, bool Stable, string FrameKey);

/// <summary>
/// 单线程字幕掩码门控：内部仅保留一个可复用基准缓冲区。
/// 门控不能证明白色像素来自字幕，仍必须经过 OCR 和路线双次确认。
/// </summary>
public sealed class SubtitleStability
{
    readonly SubtitleStabilityOptions options;
    byte[] baseline = Array.Empty<byte>();
    bool hasBaseline, baselineEmpty;
    int stableObservations;
    long revision;
    string frameKey = "subtitle:0";
    public long Revision => revision;

    public SubtitleStability(SubtitleStabilityOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MinimumChangedPixels < 1 || this.options.DefiniteChangedPixels < this.options.MinimumChangedPixels ||
            !double.IsFinite(this.options.MinimumChangedInkRatio) || this.options.MinimumChangedInkRatio is <= 0 or > 1 ||
            this.options.MinimumInkPixels < 1 || this.options.RequiredStableObservations < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "字幕稳定阈值必须为有效的正数。");
    }

    public void Reset()
    {
        hasBaseline = false; stableObservations = 0;
        AdvanceRevision();
    }

    public SubtitleObservation Observe(byte[] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        return Observe(mask.AsSpan());
    }

    public SubtitleObservation Observe(ReadOnlySpan<byte> mask)
    {
        if (mask.Length == 0) throw new ArgumentException("字幕掩码不能为空数组。", nameof(mask));
        int ink = 0, union = 0, difference = 0;
        bool comparable = hasBaseline && baseline.Length == mask.Length;
        for (int i = 0; i < mask.Length; i++)
        {
            bool now = mask[i] != 0;
            if (now) ink++;
            if (!comparable) continue;
            bool before = baseline[i] != 0;
            if (now || before) union++;
            if (now != before) difference++;
        }
        bool empty = ink < options.MinimumInkPixels;
        bool changed = !comparable || empty != baselineEmpty || difference >= options.MinimumChangedPixels &&
            (difference >= options.DefiniteChangedPixels || union > 0 && difference / (double)union >= options.MinimumChangedInkRatio);
        if (changed)
        {
            if (baseline.Length != mask.Length) baseline = new byte[mask.Length];
            mask.CopyTo(baseline);
            hasBaseline = true; baselineEmpty = empty; stableObservations = 0;
            AdvanceRevision();
        }
        else if (empty) stableObservations = 0;
        else if (stableObservations < options.RequiredStableObservations) stableObservations++;
        return new(changed, !empty && stableObservations >= options.RequiredStableObservations, frameKey);
    }

    void AdvanceRevision()
    {
        revision = checked(revision + 1);
        frameKey = "subtitle:" + revision.ToString(CultureInfo.InvariantCulture);
    }
}
