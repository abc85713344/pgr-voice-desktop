namespace PgrVoice.Listening;

/// <summary>
/// 0.4.7：配音包把尚未录音的台词标为已有的 audioStatus "draft-missing"（通常配 audioReason "待配音"）。这类句子没有音频时不打断播放：照常显示字幕，
/// 按字数停留（约每字 0.25 秒，至少 2 秒）后自动进入下一句；手动 / 点按跟随一次“下一句”即可越过。
/// 其他状态（not-found、generation-failed、未设置等）有正文却没有音频的句子仍按原规则处理；纯标点句保持原有短停顿。听书与游戏伴音共用。
/// 0.4.7 仅字幕：audioStatus "subtitle-only"（audioReason「仅字幕」，没有 audio / audioBinding / audioEvidence）——本来就不配音的系统提示等。
/// 同样显示字幕、按同一公式停留后继续（手动一次越过），但不标「待配音 / 无配音」签、不算缺句 / 待补，播放记录记「仅字幕越过」。
/// </summary>
public static class PendingVoice
{
    public const string Status = "draft-missing"; // 沿用播放器已有的缺句状态（Domain.AudioNotice「缺句 · …」），不新增取值
    /// <summary>仅字幕：不需要配音的台词（如系统提示）。</summary>
    public const string SubtitleOnlyStatus = "subtitle-only";
    /// <summary>仅字幕句的说明（诊断 / 提示处用，不称缺失）。</summary>
    public const string SubtitleOnlyNotice = "仅字幕 · 无需配音";
    public const int MillisecondsPerCharacter = 250;
    public const int MinimumMilliseconds = 2000;

    /// <summary>
    /// 0.4.7（J）：没有可用音频时按“显示字幕、按字数停留后继续”处理的台词——所有台词（draft-missing、not-found、未设置等），
    /// 纯标点句（短停顿）与 not-spoken（动作 / 战斗提示，本来不朗读、不显示停留）除外。不判断音频文件是否存在。
    /// </summary>
    public static bool IsFlagged(Node? node) => node is { Kind: "line", Archived: false } && node.AudioStatus != "not-spoken" && !IsPunctuationOnly(node.Text);

    /// <summary>待配音（draft-missing）标「待配音 · 」，其余无音频标「无配音 · 」。</summary>
    public static bool IsDraftMissing(Node? node) => node?.AudioStatus == Status;
    /// <summary>仅字幕（audioStatus "subtitle-only"）的台词。</summary>
    public static bool IsSubtitleOnly(Node? node) => node is { Kind: "line" } && node.AudioStatus == SubtitleOnlyStatus;
    public const string NoVoiceTag = "无配音 · ";
    /// <summary>状态行前缀：待配音「待配音 · 」，仅字幕不加任何签（空串），其余无音频「无配音 · 」。</summary>
    public static string Tag(Node? node) => IsSubtitleOnly(node) ? "" : IsDraftMissing(node) ? ListeningText.PendingVoiceTag : NoVoiceTag;
    /// <summary>无音频台词的签文字：「待配音」/「无配音」；仅字幕没有签（null）。</summary>
    public static string? VoiceTag(Node? node) => IsSubtitleOnly(node) ? null : IsDraftMissing(node) ? "待配音" : "无配音";
    /// <summary>无音频越过时的播放记录类型：仅字幕越过 / 待配音越过 / 无配音越过。</summary>
    public static string PassType(Node? node) => IsSubtitleOnly(node) ? PlaybackLog.Types.SubtitleOnlyPass : IsDraftMissing(node) ? PlaybackLog.Types.PendingVoicePass : PlaybackLog.Types.NoVoicePass;
    /// <summary>
    /// 是否算作缺配音（待补 / 缺句统计、待配音清单、诊断缺失列表、反馈“缺音”）：台词、未归档、没有引用音频，且不是 not-spoken、不是仅字幕。
    /// </summary>
    public static bool CountsAsMissing(Node? node) => node is { Kind: "line", Archived: false } && string.IsNullOrWhiteSpace(node.Audio) &&
        node.AudioStatus != "not-spoken" && node.AudioStatus != SubtitleOnlyStatus;
    /// <summary>提示 / 诊断用的音频说明：仅字幕给「仅字幕 · 无需配音」，其余同 <see cref="Pack.AudioNotice"/>。</summary>
    public static string Notice(Pack pack, Node node) => IsSubtitleOnly(node) && string.IsNullOrWhiteSpace(node.Audio) ? SubtitleOnlyNotice : pack.AudioNotice(node);

    /// <summary>带标记且确实没有可播放的音频。</summary>
    public static bool Applies(Pack pack, Node? node)
    {
        if (!IsFlagged(node)) return false;
        string? path = null;
        try { path = pack.ResolveAudio(node!); } catch { path = null; }
        return path == null || !File.Exists(path);
    }

    /// <summary>一句台词的播放方式：有音频照常播放；纯标点短停顿；待配音 / 仅字幕 / 无配音按字数停留后继续；not-spoken 不停留。</summary>
    public static AudioKind Classify(Pack pack, Node node)
    {
        string? path = null;
        try { path = pack.ResolveAudio(node); } catch { path = null; }
        if (path != null && File.Exists(path)) return AudioKind.Playable;
        if (node.Kind == "line" && string.IsNullOrWhiteSpace(node.Audio) && IsPunctuationOnly(node.Text)) return AudioKind.Punctuation;
        if (node.Kind == "line" && node.AudioStatus == "not-spoken") return AudioKind.NotSpoken;
        if (IsFlagged(node)) return IsSubtitleOnly(node) ? AudioKind.SubtitleOnly : IsDraftMissing(node) ? AudioKind.PendingVoice : AudioKind.NoVoice;
        return AudioKind.NotSpoken;
    }

    /// <summary>停留时长：约每字 0.25 秒（不计空白），至少 2 秒。</summary>
    public static int DwellMilliseconds(string? text)
    {
        int chars = 0;
        if (text != null)
        {
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) if (!string.IsNullOrWhiteSpace(e.GetTextElement())) chars++;
        }
        return Math.Max(MinimumMilliseconds, chars * MillisecondsPerCharacter);
    }

    static bool IsPunctuationOnly(string? text) => !string.IsNullOrWhiteSpace(text) && text.Any(char.IsPunctuation) &&
        text.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c));
}

/// <summary>SubtitleOnly：仅字幕（不需要配音），按字数停留后继续，不标签、不算缺句。</summary>
public enum AudioKind { Playable, Punctuation, PendingVoice, NoVoice, NotSpoken, SubtitleOnly }
