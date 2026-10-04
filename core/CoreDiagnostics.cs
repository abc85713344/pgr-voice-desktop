namespace PgrVoice;

/// <summary>由各平台接入日志；核心库不访问桌面目录或安卓服务。</summary>
public static class CoreDiagnostics
{
    public static event Action<string, string>? Message;
    public static void Write(string category, string text)
    {
        try { Message?.Invoke(category, text); }
        catch { /* 日志失败不能中断剧情导航。 */ }
    }
}
