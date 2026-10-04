using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    void InitializeModeLayout()
    {
        // 常用的三个入口并列，辅助操作放后；只重排视图，不切换播放模式。
        Tabs.Items.Remove(listeningTab); Tabs.Items.Insert(2, listeningTab);
        HeroTitle.Text = "战双配音";
        LocateTab.ToolTip = "备用截图定位；游戏文字跟随不需要开启它";
        gamepadTab.ToolTip = "设置手柄按钮和快捷操作";
        StoryTab.ToolTip = "浏览、搜索和手动校正台词";
        gameTextTab.ToolTip = "选择文字、按键、点按跟随或顺序播放";
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return;
            dialogueRecovery.Visibility = !TextFollowing && Tabs.SelectedItem == StoryTab && preferences.DialogueGuardEnabled && dialogueHeld ? Visibility.Visible : Visibility.Collapsed;
            gamepadPanelHint.Visibility = Tabs.SelectedItem == gamepadTab || Tabs.SelectedItem == StoryTab ? Visibility.Visible : Visibility.Collapsed;
        };
    }
    static void ExpandContainingSettings(FrameworkElement element)
    {
        for (DependencyObject? current = element; current != null; current = LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null))
            if (current is Expander expander) expander.IsExpanded = true;
        element.BringIntoView();
    }
}
