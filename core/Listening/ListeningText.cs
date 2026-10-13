namespace PgrVoice.Listening;

/// <summary>
/// 0.4.7：现稿续接（策略 B）与默认分支相关的中文界面文字集中在此，改字只需改这里（安卓端 UiText 也引用这些常量）。
/// </summary>
public static class ListeningText
{
    /// <summary>短标签（状态行、通知、续接入口共用）。</summary>
    public const string DraftTag = "现稿续接";
    /// <summary>通知 / 菜单项前缀：“现稿续接：…”。</summary>
    public const string DraftPrefix = DraftTag + "：";
    /// <summary>收听状态行内的标签片段：“正在收听 · 现稿续接”。</summary>
    public const string DraftStatusPart = " · " + DraftTag;

    public const string DraftVerifiedPartEnds = "本路线已核对的部分到此，后续按配音包现有连接继续收听，接入顺序尚未逐句核实。";
    public const string DraftExitUnverifiedHere = "此处出口尚未核实，按配音包现有连接继续收听。";
    public const string DraftExitUnverified = "此段出口尚未逐句核实，按配音包现有连接继续收听。";
    public static string DraftOptionBodyUnverified(string optionLabel) => "所选分支“" + optionLabel + "”的正文尚未逐句核对，按配音包现有连接收听。";

    /// <summary>真正的缺口：包内这句之后没有写下一处。</summary>
    public const string DeadEnd = "配音包在这句之后没有写下一处连接（缺口），已暂停；请对照游戏在本节台词目录中选择续接位置。";

    /// <summary>待配音句（audioStatus "draft-missing"、无音频）的状态行前缀。</summary>
    public const string PendingVoiceTag = "待配音 · ";

    /// <summary>默认分支序号文字。</summary>
    public static string OptionOrdinal(int number) => number == 2 ? "第二个选项" : "第一个选项";
    /// <summary>0.4.7 结局分歧：选入的结局读完后不再串接其他结局。</summary>
    public const string EndingFinished = "所选结局已听完；如需收听其他结局，请回到结局选项前重新选择。";
    /// <summary>（I）未核出口倒计时结束或用户确认后，按本节配音包顺序继续。</summary>
    public const string OrderContinued = "此处分支续接未核实，已按顺序继续";
    /// <summary>（L）后续没有任何文本：提示并跳到下一小节开头。</summary>
    public const string DeadEndHint = "后续没有配音文本，如果游戏里还有内容，请点反馈";
    public const string DeadEndChapterEnd = "后续没有配音文本，本章已到最后一个小节；如果游戏里还有内容，请点反馈";
    public const string AutoSelected = "已自动选择：";
    /// <summary>0.4.7 空出口：探索菜单的出口没有台词（只有返回 / 汇合），记为已读，从汇合后第一句继续，不交给手动。</summary>
    public const string EmptyExitContinue = "探索菜单出口无台词，从汇合后第一句继续";
    public const int CountdownSeconds = 15;
}
