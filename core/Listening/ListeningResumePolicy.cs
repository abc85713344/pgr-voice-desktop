namespace PgrVoice.Listening;

public static class ListeningResumePolicy
{
    public static readonly TimeSpan SentenceRestartAfter = TimeSpan.FromMinutes(5);

    /// <summary>只回到本句开头，不更改路线、节点或游戏配音位置。</summary>
    public static long ResolvePosition(long positionMs, DateTimeOffset? pausedUtc,
        DateTimeOffset nowUtc, bool enabled)
    {
        long position = Math.Max(0, positionMs);
        return enabled && position > 0 && pausedUtc.HasValue && pausedUtc.Value != default &&
            nowUtc >= pausedUtc.Value && nowUtc - pausedUtc.Value >= SentenceRestartAfter ? 0 : position;
    }
}
