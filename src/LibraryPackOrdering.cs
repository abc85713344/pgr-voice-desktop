using System;

namespace PgrVoice;

internal static class LibraryPackOrdering
{
    // 主线以故事中的章号排序；旧包没有 delivery.sortOrder，不能与新包直接比较路径。
    internal static string Key(string packId, string existingOrder)
    {
        return ChapterCatalog.SortKey(packId, "", existingOrder);
    }
}
