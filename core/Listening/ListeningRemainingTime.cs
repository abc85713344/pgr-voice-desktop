using System.Text;

namespace PgrVoice.Listening;

public enum ListeningTimeBoundary { SectionEnd, Choice, RouteNotice, Completed, WaitingConnection }

public sealed record ListeningRemainingTime(long KnownMilliseconds, int MissingFiles, int UnknownDurations,
    ListeningTimeBoundary Boundary, bool Loading = false, bool AtBoundary = false)
{
    public string DisplayText
    {
        get
        {
            if (Loading) return "剩余时长：正在读取本小节音频…";
            if (Boundary == ListeningTimeBoundary.Completed) return "剩余时长：已听完";
            if (AtBoundary && Boundary == ListeningTimeBoundary.WaitingConnection) return "剩余时长：等待确认后续连接";
            if (AtBoundary) return Boundary == ListeningTimeBoundary.Choice
                ? "剩余时长：选择分支后计算" : "剩余时长：继续通过路线提示后计算";
            string scope = Boundary switch
            {
                ListeningTimeBoundary.Choice => "到下个待选分支",
                ListeningTimeBoundary.RouteNotice => "到下个路线提示",
                ListeningTimeBoundary.WaitingConnection => "到等待续接处",
                _ => "本小节"
            };
            string time = Clock(KnownMilliseconds);
            if (MissingFiles == 0 && UnknownDurations == 0) return $"{scope}剩余约 {time}";
            var unknown = new List<string>();
            if (MissingFiles > 0) unknown.Add($"{MissingFiles} 句缺少音频");
            if (UnknownDurations > 0) unknown.Add($"{UnknownDurations} 句时长未知");
            return $"{scope}已知音频剩余约 {time}（另有{string.Join("、", unknown)}）";
        }
    }

    static string Clock(long milliseconds)
    {
        long seconds = (long)Math.Ceiling(Math.Max(0, milliseconds) / 1000d);
        return seconds >= 3600 ? $"{seconds / 3600}小时{seconds / 60 % 60:00}分{seconds % 60:00}秒"
            : seconds >= 60 ? $"{seconds / 60}分{seconds % 60:00}秒" : $"{seconds}秒";
    }
}

/// <summary>
/// 只读当前听书计划；后台读取当前小节的音频头，不创建播放器、不选择分支。
/// 同一计划与小节只建立一次后缀表，逐秒刷新仅扣除本句已播位置并换算倍速。
/// </summary>
public sealed class ListeningRemainingTimeCache
{
    readonly object gate = new();
    readonly Func<string, long?>? readOtherFormat;
    readonly Dictionary<string, CachedDuration> durations = new(StringComparer.Ordinal);
    Pack? activePack;
    object? activeFixedVoices;
    bool activeFixedVoicesEnabled;
    IReadOnlyList<ListeningItem>? activePlan;
    int sectionStart, sectionEnd;
    Task<SectionTimes>? calculation;
    CancellationTokenSource? cancellation;
    public event Action? Updated;

    public ListeningRemainingTimeCache(Func<string, long?>? readOtherFormat = null) => this.readOtherFormat = readOtherFormat;

    public ListeningRemainingTime Estimate(Pack pack, IReadOnlyList<ListeningItem> plan, int position,
        long positionMilliseconds, double speed = 1)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(plan);
        if (position >= plan.Count) return new(0, 0, 0, ListeningTimeBoundary.Completed);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        if (plan[position].IsBlocking) return new(0, 0, 0, ListeningTimeBoundary.WaitingConnection, AtBoundary: true);
        if (plan[position].Kind == ListeningItemKind.Choice) return new(0, 0, 0, ListeningTimeBoundary.Choice, AtBoundary: true);
        if (plan[position].Kind == ListeningItemKind.Notice) return new(0, 0, 0, ListeningTimeBoundary.RouteNotice, AtBoundary: true);
        Task<SectionTimes> task;
        int local;
        lock (gate)
        {
            if (!ReferenceEquals(activePack, pack) || !ReferenceEquals(activePlan, plan)
                || !ReferenceEquals(activeFixedVoices, pack.FixedVoices) || activeFixedVoicesEnabled != (pack.FixedVoices?.Enabled == true)
                || position < sectionStart || position >= sectionEnd || calculation == null)
            {
                activePack = pack; activePlan = plan;
                activeFixedVoices = pack.FixedVoices; activeFixedVoicesEnabled = pack.FixedVoices?.Enabled == true;
                string section = plan[position].SectionId;
                sectionStart = position; sectionEnd = position + 1;
                while (sectionStart > 0 && plan[sectionStart - 1].SectionId == section) sectionStart--;
                while (sectionEnd < plan.Count && plan[sectionEnd].SectionId == section) sectionEnd++;
                // Snapshot references before leaving the owner thread; plan rebuilds replace the list.
                var entries = plan.Skip(sectionStart).Take(sectionEnd - sectionStart).ToArray();
                cancellation?.Cancel(); cancellation?.Dispose(); cancellation = new();
                var token = cancellation.Token;
                calculation = Task.Run(() => BuildSection(pack, entries, token), token);
                _ = calculation.ContinueWith(_ => Updated?.Invoke(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
            task = calculation; local = position - sectionStart;
        }
        if (!task.IsCompletedSuccessfully) return new(0, 0, 0, ListeningTimeBoundary.SectionEnd, Loading: true);
        var row = task.Result.Rows[local];
        long consumed = Math.Clamp(positionMilliseconds, 0, row.CurrentDuration);
        double rate = double.IsFinite(speed) && speed > 0 ? speed : 1;
        long remaining = (long)Math.Ceiling(Math.Max(0, row.KnownMilliseconds - consumed) / rate);
        return new(remaining, row.MissingFiles, row.UnknownDurations, row.Boundary);
    }

    /// <summary>包音频在原地更新时由调用方显式失效；打开新 Pack 实例也会自动重建。</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            activePack = null; activePlan = null; activeFixedVoices = null; calculation = null;
        }
    }

    SectionTimes BuildSection(Pack pack, ListeningItem[] entries, CancellationToken token)
    {
        var rows = new TimeRow[entries.Length];
        long total = 0; int missing = 0, unknown = 0;
        var boundary = ListeningTimeBoundary.SectionEnd;
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var item = entries[i];
            if (item.Kind != ListeningItemKind.Line)
            {
                total = 0; missing = unknown = 0;
                boundary = item.IsBlocking ? ListeningTimeBoundary.WaitingConnection
                    : item.Kind == ListeningItemKind.Choice ? ListeningTimeBoundary.Choice : ListeningTimeBoundary.RouteNotice;
                rows[i] = new(0, 0, 0, 0, boundary); continue;
            }
            long current = 0;
            if (item.Node?.AudioStatus != "not-spoken")
            {
                string? path = null;
                try { if (item.Node != null) path = pack.ResolveAudio(item.Node); } catch { }
                var value = ReadDuration(path);
                if (value.Missing) missing++;
                else if (value.Milliseconds is not { } duration) unknown++;
                else { current = duration; total += duration; }
            }
            rows[i] = new(total, current, missing, unknown, boundary);
        }
        return new(rows);
    }

    DurationValue ReadDuration(string? path)
    {
        if (path == null) return new(null, true);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return new(null, true);
            long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
            lock (gate)
                if (durations.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified)
                    return new(cached.Milliseconds, false);
            long? duration = AudioDurationMetadata.ReadWaveMilliseconds(path);
            if (duration == null && readOtherFormat != null) duration = readOtherFormat(path);
            if (duration is <= 0) duration = null;
            file.Refresh();
            if (!file.Exists) return new(null, true);
            if (file.Length != length || file.LastWriteTimeUtc.Ticks != modified) return new(null, false);
            lock (gate)
            {
                if (durations.Count >= 8192) durations.Clear();
                durations[path] = new(length, modified, duration);
            }
            return new(duration, false);
        }
        catch { return new(null, !File.Exists(path)); }
    }

    sealed record SectionTimes(TimeRow[] Rows);
    sealed record TimeRow(long KnownMilliseconds, long CurrentDuration, int MissingFiles, int UnknownDurations, ListeningTimeBoundary Boundary);
    sealed record CachedDuration(long Length, long Modified, long? Milliseconds);
    readonly record struct DurationValue(long? Milliseconds, bool Missing);
}

/// <summary>直接从未压缩 WAV 音频头计算时长，跳过数据块；其他格式交给两端原有解码器的元数据接口。</summary>
public static class AudioDurationMetadata
{
    public static long? ReadWaveMilliseconds(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
            if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) return null; // RIFF
            long limit = reader.ReadUInt32() + 8L;
            if (limit > stream.Length || limit < 12 || reader.ReadUInt32() != 0x45564157) return null; // WAVE
            uint sampleRate = 0; ushort blockAlign = 0, format = 0; long data = 0;
            for (int chunks = 0; stream.Position + 8 <= limit && chunks < 4096; chunks++)
            {
                uint kind = reader.ReadUInt32(), size = reader.ReadUInt32();
                long start = stream.Position, end = start + size;
                if (end > limit) return null;
                if (kind == 0x20746d66 && size >= 16) // fmt
                {
                    format = reader.ReadUInt16(); reader.ReadUInt16(); sampleRate = reader.ReadUInt32();
                    reader.ReadUInt32(); blockAlign = reader.ReadUInt16(); reader.ReadUInt16();
                    if (format == 0xfffe && size >= 40) { stream.Position = start + 24; format = reader.ReadUInt16(); }
                }
                else if (kind == 0x61746164) data += size; // data
                stream.Position = end + (size & 1);
            }
            if (format is not (1 or 3) || sampleRate == 0 || blockAlign == 0 || data <= 0 || data % blockAlign != 0) return null;
            return checked((long)Math.Round(data / (double)blockAlign * 1000 / sampleRate));
        }
        catch { return null; }
    }
}
