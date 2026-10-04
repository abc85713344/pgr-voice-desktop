using System;

namespace PgrVoice;

internal static class LibraryPackOrdering
{
    // 主线以故事中的章号排序；旧包没有 delivery.sortOrder，不能与新包直接比较路径。
    internal static string Key(string packId, string existingOrder)
    {
        if (packId.StartsWith("voice-v2-MAIN", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(packId.AsSpan("voice-v2-MAIN".Length), out var main)
            && main is >= 1 and <= 2)
            return $"0-{main:000}";

        if (packId.StartsWith("pgr-ch", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(packId.AsSpan("pgr-ch".Length), out var chapter)
            && chapter is >= 3 and <= 42)
            return $"0-{chapter:000}";

        if (packId.StartsWith("voice-v2-ER", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(packId.AsSpan("voice-v2-ER".Length), out var extra)
            && extra >= 0)
            return $"1-{extra:000}";

        return "2-" + existingOrder;
    }
}
