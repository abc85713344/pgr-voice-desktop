using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    async Task RunManualContinuationChecks(Action<bool,string> check)
    {
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"--listening-rechoice-pack");
        if(at<0 || at+1>=args.Length)return;
        string oldGame=preferences.PackFile;double oldScale=preferences.ReadingScale,oldWidth=preferences.PanelWidth,oldHeight=preferences.PanelHeight;bool customized=preferences.PanelSizeCustomized;
        T? Find<T>(DependencyObject root) where T:DependencyObject
        {
            if(root is T item)return item;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is T found)return found;
            return null;
        }
        Button? FindButton(DependencyObject root,string text)
        {
            if(root is Button b && b.Content?.ToString()==text)return b;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindButton(VisualTreeHelper.GetChild(root,i),text) is Button found)return found;
            return null;
        }
        async Task Drain()=>await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            StopListeningForGame();LoadPack(args[at+1]);
            const string section="ch32-71fcf055b68c2cfb1350";
            var menu=engine!.Pack.Nodes.Single(n=>!n.Archived && n.Kind=="choice" && n.SectionId==section && n.Options.Any(o=>Matcher.Normalize(o.Label)=="是这里吗"));
            var previous=engine.Pack.Nodes.FirstOrDefault(n=>!n.Archived && n.Kind=="line" && n.NextId==menu.Id);
            if(previous!=null)engine.ConfirmGameLine(previous.Id);
            engine.OpenGameMenu(menu.Id,section);ShowBranchMenu();branchMenu.UpdateLayout();
            string before=JsonSerializer.Serialize(engine.ExportNavigation());int plays=playCalls;
            var manual=FindButton(branchMenu,"手动选续接句") ?? throw new InvalidOperationException("真实菜单缺少手动续接入口");
            ((IInvokeProvider)new ButtonAutomationPeer(manual).GetPattern(PatternInterface.Invoke)).Invoke();await Drain();
            check(manualContinuationBrowsing && Tabs.SelectedItem==StoryTab && BranchBox.Visibility==Visibility.Collapsed && showAllSectionLines.IsChecked==true,
                "真实32-6是这里吗菜单的手动续接按钮打开完整阅读区，并临时收起重复选项框");
            check(JsonSerializer.Serialize(engine.ExportNavigation())==before && playCalls==plays && engine.Mode==RunMode.Choice,
                "手动续接视图、全台词展示不改变真实待选状态或发声");
            var expected=engine.Pack.Nodes.Where(n=>!n.Archived && n.Kind=="line" && n.SectionId==section).Select(n=>n.Id).OrderBy(x=>x).ToArray();
            check(rows.Where(r=>r.Node.Kind=="line").Select(r=>r.Node.Id).OrderBy(x=>x).SequenceEqual(expected),
                "真实32-6手动续接目录逐ID保留本节全部正文，不把缺空间误当缺台词");
            foreach(var size in new[]{(580d,620d),(760d,760d)})
            {
                preferences.ReadingScale=2;ApplyReadingScale();SetExpandedPanelSize(size.Item1,size.Item2);ApplyCompactLayout();UpdateLayout();await Drain();
                var scroll=Find<ScrollViewer>(LinesList) ?? throw new InvalidOperationException("正文列表没有滚动容器");
                check(LinesList.ActualHeight>=180 && scroll.ViewportHeight>0 && scroll.ScrollableHeight>0,
                    $"{size.Item1}×{size.Item2}、200%正文下仍有至少180DIP有效滚动视口");
                scroll.ScrollToTop();await Drain();
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-360){RoutedEvent=Mouse.MouseWheelEvent});await Drain();
                check(scroll.VerticalOffset>0,$"{size.Item2}高度实际鼠标滚轮事件能向后文滚动");
                var provider=(IScrollProvider)new ListBoxAutomationPeer(LinesList).GetPattern(PatternInterface.Scroll);
                provider.SetScrollPercent(-1,100);await Drain();
                check(provider.VerticalScrollPercent>=99,$"{size.Item2}高度UIA滚动条能到正文列表底部");
                LinesList.SelectedIndex=0;LinesList.ScrollIntoView(LinesList.SelectedItem);UpdateLayout();await Drain();
                var firstItem=LinesList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                    ?? throw new InvalidOperationException("首行尚未进入有效可视区，不能模拟其方向键");
                firstItem.Focus();await Drain();
                firstItem.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(this),Environment.TickCount,Key.Down){RoutedEvent=Keyboard.KeyDownEvent});await Drain();
                check(LinesList.SelectedIndex==1 && playCalls==plays,$"{size.Item2}高度键盘向下能选择下一行，浏览不播放（选中索引{LinesList.SelectedIndex}）");
                Screenshot("手动续接-真实32-6-"+(int)size.Item2+"-200percent.png");
            }
            double font=(double)Resources["DialogueSize"],width=Width,height=Height;
            PanelResizeCorner.RaiseEvent(new DragDeltaEventArgs(130,45){RoutedEvent=Thumb.DragDeltaEvent});
            PanelResizeCorner.RaiseEvent(new DragCompletedEventArgs(130,45,false){RoutedEvent=Thumb.DragCompletedEvent});await Drain();
            var area=SystemParameters.WorkArea;
            check(Width>=width && Height>=height && Width<=area.Width && Height<=area.Height && (double)Resources["DialogueSize"]==font,
                "实际右下角拖拽接线向外扩宽高且不放大文字、不溢出工作区");
            double savedWidth=Width,savedHeight=Height;Collapse(false);Expand(StoryTab);await Drain();
            check(Width==savedWidth && Height==savedHeight && preferences.PanelSizeCustomized,
                "收起成悬浮球再展开恢复玩家调整的面板大小，球尺寸不覆盖阅读尺寸");
            OpenManualContinuation();await Drain();var expand=FindButton(this,"展开台词窗口")!;
            ((IInvokeProvider)new ButtonAutomationPeer(expand).GetPattern(PatternInterface.Invoke)).Invoke();await Drain();
            check(Width>=Math.Min(960,area.Width-20) && LinesList.ActualHeight>=180 && JsonSerializer.Serialize(engine.ExportNavigation())==before && playCalls==plays,
                "展开台词窗口按钮实际扩大阅读空间，同时保留待选和全部原进度");
            Screenshot("手动续接-真实32-6-可扩展阅读.png");
        }
        finally
        {
            manualContinuationBrowsing=false;preferences.ReadingScale=oldScale;preferences.PanelWidth=oldWidth;preferences.PanelHeight=oldHeight;preferences.PanelSizeCustomized=customized;
            ApplyReadingScale();SetExpandedPanelSize(oldWidth,oldHeight);ApplyCompactLayout();showAllSectionLines.IsChecked=false;
            if(File.Exists(oldGame))LoadPack(oldGame);
        }
    }
}
