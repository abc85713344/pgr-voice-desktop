namespace PgrVoice;

/// <summary>角色标签精确匹配；不推断身份，不把别名或相似名字自动合并。</summary>
public static class SpeakerVolume
{
    public static string Key(string? speaker) => speaker?.Trim() ?? "";
    public static string DisplayName(string? speaker) => Key(speaker) is { Length: > 0 } name ? name : "未标注角色";
    public static int GetPercent(IReadOnlyDictionary<string, int>? volumes, string? speaker) =>
        volumes != null && volumes.TryGetValue(Key(speaker), out int percent) ? Math.Clamp(percent, 0, 100) : 100;

    public static float Apply(float masterVolume, IReadOnlyDictionary<string, int>? volumes, string? speaker) =>
        (float.IsFinite(masterVolume) ? Math.Clamp(masterVolume, 0f, 1f) : 1f) * GetPercent(volumes, speaker) / 100f;

    public static void Set(IDictionary<string, int> volumes, string? speaker, int percent)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        string key = Key(speaker);
        int value = Math.Clamp(percent, 0, 100);
        if (value == 100) volumes.Remove(key);
        else volumes[key] = value;
    }
}
