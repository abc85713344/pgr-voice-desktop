namespace PgrVoice;

/// <summary>游戏、听书与引导共用的内存路线兼容入口；不写章节文件或音频。</summary>
public static class DesktopPackLoader
{
    public static Pack Load(string file) => AndroidApp.BundledRouteRepairs.Apply(Pack.Load(file));
}
