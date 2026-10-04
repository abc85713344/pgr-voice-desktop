namespace PgrVoice;

// 核心导航测试不加载 WPF 的 Services.cs，保留其只读日志接口。
public static class Log
{
    public static void Write(string category, string message) { }
}
