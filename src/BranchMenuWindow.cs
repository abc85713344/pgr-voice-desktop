using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PgrVoice;
// 不激活窗口；键盘从低层钩子定向送入，不改变游戏焦点。
public sealed partial class BranchMenuWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int n);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int n,int value);
    public ListBox Options { get; } = new() { Background=Brushes.Transparent, BorderThickness=new Thickness(0), MaxHeight=300, Foreground=Theme.Brush("Fg"), FontSize=14, Focusable=false };
    readonly TextBlock title=new(){FontSize=17,Foreground=Theme.Brush("Fg"),TextWrapping=TextWrapping.Wrap};
    readonly TextBlock badge=new(){Text="◆ 分支选择 · 等待确认",FontSize=11,Foreground=LineRow.BranchAccent,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)};
    readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,7,0,9),Foreground=Theme.Brush("MutedText")};
    readonly TextBlock continuationFeedback=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,0,0,7),Foreground=LineRow.BranchAccent,Visibility=Visibility.Collapsed};
    readonly TextBlock gamepadHint=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,0,0,7),Foreground=Theme.Brush("NormalAccent"),Visibility=Visibility.Collapsed};
    public bool GamepadNavigation { get; private set; }
    public bool CanConfirm => IsAnchorView ? SelectedAnchor is {IsAmbiguous:false} : confirmButton?.IsEnabled == true;
    Button? confirmButton;
    ListBoxItem? pendingMouseConfirm;
    int pendingMouseEpoch;
    readonly Button scopeButton = new() { Content="查看本节全部选择点", Focusable=false, Padding=new Thickness(8,5,8,5), Margin=new Thickness(0,0,0,7), Visibility=Visibility.Collapsed };
    readonly Button peopleButton = new() { Content="换人 / 话题", Focusable=false, Padding=new Thickness(8,6,8,6), Margin=new Thickness(0,6,4,0) };
    readonly Button topicButton = new() { Content="返回话题", Focusable=false, Padding=new Thickness(8,6,8,6), Margin=new Thickness(4,6,0,0) };
    public event Action? Confirm, Cancel, Locate, Manual, Interactions, ReturnTopic, NavigationScope;
    public bool IsNavigation { get; private set; }
    public bool IsAllMenus { get; private set; }
    public string? SelectedNavigationId => (Options.SelectedItem as ListBoxItem)?.Tag as string;
    public string? MenuId { get; private set; }
    public int Epoch { get; private set; }
    internal long LastPointerInputTimestamp { get; private set; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public BranchMenuWindow()
    {
        Icon=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/PgrVoice.ico"));
        Title="剧情配音 · 选择路线";Width=430;SizeToContent=SizeToContent.Height;MaxHeight=600;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Background=Theme.Brush("Bg");Foreground=Theme.Brush("Fg");FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
        var panel=new Grid{Margin=new Thickness(16)};
        panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition());panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Content=new Border{BorderBrush=LineRow.BranchAccent,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Child=panel};
        Options.ItemContainerStyle=(Style)Application.Current.FindResource("BranchOptionStyle");
        // 条目不获取键盘焦点，因此主动处理选择；双击在第二次松开时确认，避免松开落到游戏。
        Options.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pendingMouseConfirm = null;
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(Options, source) is ListBoxItem item && item.IsEnabled)
            {
                Options.SelectedItem = item;
                if (IsAnchorView || e.ClickCount == 2) { pendingMouseConfirm = item; pendingMouseEpoch = Epoch; }
                e.Handled = true;
            }
        };
        Options.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var pending = pendingMouseConfirm;
            pendingMouseConfirm = null;
            if (pending != null && pendingMouseEpoch == Epoch && IsVisible && CanConfirm &&
                ReferenceEquals(Options.SelectedItem, pending) && e.OriginalSource is DependencyObject source &&
                ReferenceEquals(ItemsControl.ContainerFromElement(Options, source), pending))
            {
                e.Handled = true;
                Confirm?.Invoke();
            }
        };
        // Raw Input 可能稍后才到达；即使确认按钮已经收起菜单，也不能把同一次点击算作下一句。
        PreviewMouseDown += (_, _) => LastPointerInputTimestamp = Stopwatch.GetTimestamp();
        PreviewMouseUp += (_, _) => LastPointerInputTimestamp = Stopwatch.GetTimestamp();
        var header=new StackPanel();header.Children.Add(badge);header.Children.Add(title);header.Children.Add(notice);header.Children.Add(continuationFeedback);header.Children.Add(gamepadHint);header.Children.Add(scopeButton);panel.Children.Add(header);
        scopeButton.Click+=(_,_)=>NavigationScope?.Invoke();
        Grid.SetRow(Options,1);panel.Children.Add(Options);
        var footer=new StackPanel();Grid.SetRow(footer,2);panel.Children.Add(footer);
        void Button(string text,Action click) {var b=new Button{Content=text,Padding=new Thickness(8,6,8,6),Margin=new Thickness(0,6,0,0),Focusable=false};b.Click+=(_,_)=>click();footer.Children.Add(b);confirmButton??=b;}
        Button("确认并播放  Enter",()=>Confirm?.Invoke());
        confirmButton!.Background=LineRow.BranchFill;confirmButton.BorderBrush=LineRow.BranchAccent;
        var navigation=new System.Windows.Controls.Primitives.UniformGrid {Columns=2};
        navigation.Children.Add(peopleButton);navigation.Children.Add(topicButton);footer.Children.Add(navigation);
        peopleButton.Click+=(_,_)=>Interactions?.Invoke();topicButton.Click+=(_,_)=>ReturnTopic?.Invoke();
        Button("定位游戏当前台词",()=>Locate?.Invoke());Button("手动选续接句",()=>Manual?.Invoke());Button("收起，保持待选  Esc",()=>Cancel?.Invoke());
        InitializeAnchorView(panel,footer,header);
        SourceInitialized+=(_,_)=> {SetWindowLong(Handle,-20,GetWindowLong(Handle,-20)|0x08000000|0x80);HwndSource.FromHwnd(Handle)?.AddHook(Hook);};
    }
    IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled) {if(msg==0x21 && !GamepadNavigation){handled=true;return new IntPtr(3);}return IntPtr.Zero;}
    public void SetGamepadNavigation(bool enabled)
    {
        GamepadNavigation=enabled;
        if(Handle!=IntPtr.Zero) {int style=GetWindowLong(Handle,-20);SetWindowLong(Handle,-20,enabled?style&~0x08000000:style|0x08000000);}
        if(enabled && IsVisible) {Activate();Native.SetForegroundWindow(Handle);}
    }
    public void SetGamepadHint(string text)
    {gamepadHint.Text=text;gamepadHint.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;}
    public void Present(PlaybackEngine engine,double left,double top,int selected,bool anchors=false)
    {
        continuationFeedback.Text="";continuationFeedback.Visibility=Visibility.Collapsed;
        presentedEngine=engine;normalLeft=left;normalTop=top;
        if(anchors && TryPresentAnchors(engine))return;
        RestoreOptionView();
        if(MenuId!=engine.CurrentId || IsNavigation){MenuId=engine.CurrentId;Epoch++;}
        IsNavigation=false;
        scopeButton.Visibility=Visibility.Collapsed;
        peopleButton.IsEnabled=engine.Current!=null;
        peopleButton.Content="手动选分支";
        topicButton.IsEnabled=engine.ReturnInteractionTarget!=null || engine.ParentStoryMenu!=null;
        topicButton.Content=engine.ReturnInteractionTarget!=null?"返回话题":"返回选择点";
        topicButton.ToolTip=engine.ReturnInteractionTarget?.Label ?? engine.ParentStoryMenu?.Label;
        bool boundary=engine.Mode==RunMode.Gap;
        title.Text=engine.Current?.Text;
        notice.Text=engine.Notice+(boundary?"\n单击选中，双击打开所选菜单（不播放）。":"\n单击选中，双击确认进入；也可点击确认按钮。")+"\n↑↓选择 · Enter确认 · Esc收起";
        Options.Items.Clear();
        badge.Text=boundary?"◆ 连接尚未确认 · 等待续接":"◆ 分支选择 · 等待确认";
        confirmButton!.Content=boundary?"打开选中的菜单  Enter":"确认进入  Enter";
        confirmButton.IsEnabled=!boundary || engine.ResumeMenus.Count>0;
        if(boundary)
            foreach(var menu in engine.ResumeMenus)Options.Items.Add(new ListBoxItem{Content="手动返回："+menu.Text,Focusable=false});
        foreach(var o in engine.AvailableOptions)
        {
            var p=new StackPanel();
            string state=engine.Pack.SchemaVersion==3 ? !o.BodyVerified?"  · 仅单句核对":!o.ExitVerified?"  · 段尾需选择":"" : !o.Verified?"  · 待核对":"";
            p.Children.Add(new TextBlock{Text=o.Label+(engine.Heard.Contains(o.PathId)?"  · 已听":"")+state,TextWrapping=TextWrapping.Wrap,Foreground=Theme.Brush("Fg")});
            p.Children.Add(new TextBlock{Text=o.Preview,ToolTip=o.Preview,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Theme.Brush("MutedText"),MaxHeight=44,Margin=new Thickness(0,4,0,0)});
            if(o.ConditionNote.Length>0)p.Children.Add(new TextBlock{Text=o.ConditionNote,TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=LineRow.BranchAccent});
            if(!engine.ConditionsKnown(o))p.Children.Add(new TextBlock{Text="前置进度未同步 · 游戏里有此选项即可确认",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=LineRow.BranchAccent});
            Options.Items.Add(new ListBoxItem{Content=p,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        }
        Options.SelectedIndex=Math.Clamp(selected,0,Math.Max(0,Options.Items.Count-1));
        var area=SystemParameters.WorkArea;MaxHeight=Math.Min(600,area.Height-24);Left=Math.Clamp(left,area.Left,Math.Max(area.Left,area.Right-Width));Top=Math.Clamp(top,area.Top,Math.Max(area.Top,area.Bottom-MaxHeight));
        if(!IsVisible){Epoch++;Show();}
        Options.ScrollIntoView(Options.SelectedItem);
    }
    public void ShowNavigationNotice(string text) => notice.Text=text;
    public void SetContinuationFeedback(PlaybackEngine? engine,string reason)
    {
        bool current=IsVisible && !IsNavigation && presentedEngine==engine && MenuId==engine?.CurrentId;
        continuationFeedback.Text=current?reason:"";
        continuationFeedback.Visibility=current && reason.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
    public void PresentNavigation(PlaybackEngine engine,double left,double top,int selected,bool allMenus=false,string? sectionId=null)
    {
        continuationFeedback.Text="";continuationFeedback.Visibility=Visibility.Collapsed;
        RestoreOptionView();presentedEngine=engine;
        string? section=sectionId ?? engine.Current?.SectionId;
        bool hasInteractions=section==engine.Current?.SectionId && engine.InteractionMenus.Count>0;
        var storyMenus=engine.GetStoryMenus(section);
        string id=(allMenus?"story-navigation:":"navigation:")+section;
        if(MenuId!=id || !IsNavigation){MenuId=id;Epoch++;}
        IsNavigation=true;IsAllMenus=allMenus;title.Text="换人 / 选择话题";
        badge.Text="◆ 自由互动 · 由你选择顺序";
        notice.Text="游戏正和谁交谈，就打开谁的话题。\n单击选中，双击打开菜单（不播放）。\n也可 ↑↓选择，点击下方按钮或按 Enter 确认。";
        scopeButton.Visibility=allMenus && !hasInteractions?Visibility.Collapsed:Visibility.Visible;
        scopeButton.Content=allMenus?"只看已核实的人物 / 话题":"查看本节全部选择点";
        scopeButton.IsEnabled=!allMenus || hasInteractions;
        confirmButton!.Content="游戏已到这里 · 打开菜单（不播放） Enter";
        confirmButton.IsEnabled=allMenus?storyMenus.Count>0:hasInteractions;
        peopleButton.IsEnabled=false;topicButton.IsEnabled=section==engine.Current?.SectionId && (engine.ReturnInteractionTarget!=null || engine.ParentStoryMenu!=null);
        topicButton.Content=engine.ReturnInteractionTarget!=null?"返回话题":"返回选择点";
        topicButton.ToolTip=engine.ReturnInteractionTarget?.Label ?? engine.ParentStoryMenu?.Label;
        Options.Items.Clear();
        if(allMenus)
        {
            title.Text=engine.Pack.Chapters.SelectMany(c=>c.Sections).First(s=>s.Id==section).Title;
            badge.Text="◆ 手动选分支 · 按游戏画面同步";
            notice.Text="可直接选择人物、话题或剧情菜单，无需重读前置。\n单击选中，双击打开菜单（不播放）。\n也可 ↑↓选择，点击下方按钮或按 Enter 确认。";
            foreach(var target in storyMenus)
            {
                var content=new StackPanel();
                content.Children.Add(new TextBlock {Text=target.Label+(target.IsCurrent?" · 当前":""),TextWrapping=TextWrapping.Wrap});
                content.Children.Add(new TextBlock {Text=target.Preview,FontSize=12,Foreground=Theme.Brush("Fg"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
                content.Children.Add(new TextBlock {Text=target.IsVisited?"曾选择 · 可重新定位":"按游戏当前画面确认",FontSize=11,Foreground=LineRow.BranchAccent,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
                Options.Items.Add(new ListBoxItem {Tag=target.MenuId,Content=content,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip="按游戏当前画面打开此菜单，保持静音；确认具体选项后才进入对应内容。"});
            }
        }
        else foreach(var target in engine.InteractionMenus)
        {
            var content=new StackPanel();
            content.Children.Add(new TextBlock {Text=target.Label+(target.IsCurrent?" · 当前":""),TextWrapping=TextWrapping.Wrap});
            if(engine.Pack.ById.TryGetValue(target.MenuId,out var menu))
                content.Children.Add(new TextBlock {Text=string.Join(" / ",menu.Options.Take(3).Select(o=>o.Label)),FontSize=11,Foreground=Theme.Brush("MutedText"),TextWrapping=TextWrapping.Wrap,MaxHeight=42,Margin=new Thickness(0,4,0,0)});
            Options.Items.Add(new ListBoxItem{Tag=target.MenuId,Content=content,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        }
        Options.SelectedIndex=Math.Clamp(selected,0,Math.Max(0,Options.Items.Count-1));
        var area=SystemParameters.WorkArea;MaxHeight=Math.Min(650,area.Height-24);
        Left=Math.Clamp(left,area.Left,Math.Max(area.Left,area.Right-Width));Top=Math.Clamp(top,area.Top,Math.Max(area.Top,area.Bottom-MaxHeight));
        if(!IsVisible){Epoch++;Show();}Options.ScrollIntoView(Options.SelectedItem);
    }
    public void Dismiss(){pendingMouseConfirm=null;Epoch++;SetGamepadNavigation(false);Hide();}
    public void Move(int delta)
    {
        if(IsAnchorView && (Options.SelectedIndex+delta<0 || Options.SelectedIndex+delta>=Options.Items.Count))
        {ChangeAnchorPage(delta<0?-1:1);return;}
        Options.SelectedIndex=Math.Clamp(Options.SelectedIndex+delta,0,Math.Max(0,Options.Items.Count-1));Options.ScrollIntoView(Options.SelectedItem);
    }
}
