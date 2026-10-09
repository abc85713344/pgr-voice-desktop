namespace PgrVoice;

public partial class MainWindow
{
    // 保留旧回归入口；可见UI已改为分类浏览窗口。
    System.Threading.Tasks.Task RunChapterSwitchUiTest() => RunChapterPickerUiTest();
}
