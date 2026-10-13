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
using PgrVoice.Listening;

namespace PgrVoice;
// 不激活窗口；键盘从低层钩子定向送入，不改变游戏焦点。
public sealed partial class BranchMenuWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int n);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int n,int value);
    public ListBox Options { get; } = new() { Background=Brushes.Transparent, BorderThickness=new Thickness(0), MaxHeight=300, Foreground=Theme.Brush("Fg"), FontSize=14, Focusable=false };
    readonly TextBlock title=new(){FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Theme.Brush("Fg"),TextWrapping=TextWrapping.Wrap};
    readonly TextBlock badge=new(){Text="◆ 分支选择 · 等待确认",FontSize=11,Foreground=LineRow.BranchAccent,FontWeight=FontWeights.SemiBold,Background=LineRow.BranchFill,Padding=new Thickness(8,3,8,3),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,7,0,9),Foreground=Theme.Brush("MutedText")};
    readonly TextBlock continuationFeedback=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,0,0,7),Foreground=LineRow.BranchAccent,Visibility=Visibility.Collapsed};
    readonly TextBlock gamepadHint=new(){TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,0,0,7),Foreground=Theme.Brush("NormalAccent"),Visibility=Visibility.Collapsed};
    public bool GamepadNavigation { get; private set; }
    public bool CanConfirm => IsAnchorView ? SelectedAnchor is {IsAmbiguous:false} : confirmButton?.IsEnabled == true;
    Button? confirmButton;
    ListBoxItem? pendingMouseConfirm;
    int pendingMouseEpoch;
    readonly Button scopeButton = ActionButton.Apply(new Button { Content="查看本节全部选择点", Focusable=false, Margin=new Thickness(0,0,0,7), MinHeight=28, HorizontalAlignment=HorizontalAlignment.Left, HorizontalContentAlignment=HorizontalAlignment.Left, Visibility=Visibility.Collapsed }, "SCOPE · ALL", ActionIcons.Scope);
    readonly Button peopleButton = ActionButton.Apply(new Button { Content="换人 / 话题", Focusable=false, Margin=new Thickness(0,6,3,0) }, "MANUAL BRANCH", ActionIcons.Fork);
    readonly Button topicButton = ActionButton.Apply(new Button { Content="返回话题", Focusable=false, Margin=new Thickness(3,6,0,0) }, "RETURN", ActionIcons.Return);
    // 10-A3 指令按钮（标签与点击逻辑不变，只换样式与排列）
    Button switchButton=null!, locateButton=null!, manualButton=null!, cancelButton=null!;
    readonly StackPanel footer=new();
    readonly System.Windows.Controls.Primitives.UniformGrid navigationRow=new(){Columns=2}, compactRow=new(){Columns=2};
    bool? footerCompact;
    // 10-A2 装饰与页眉
    readonly BranchDecor decor=new();
    readonly Border badgeHost=new(){BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Left};
    readonly System.Windows.Shapes.Rectangle badgeDash=new(){Width=2,StrokeThickness=2,StrokeDashArray=new DoubleCollection{1.5,1.5},HorizontalAlignment=HorizontalAlignment.Left,Visibility=Visibility.Collapsed};
    readonly FrameworkElement badgeGrip=GripDots();
    readonly ContentControl headerTag=new(){Focusable=false,IsTabStop=false,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock summary=new(){FontSize=11,Foreground=Theme.Brush("MutedText"),VerticalAlignment=VerticalAlignment.Center};
    readonly DockPanel summaryBar=new(){LastChildFill=false,Margin=new Thickness(0,0,0,6),Visibility=Visibility.Collapsed};
    Grid panel=null!;
    readonly OrbitArc routeOrbit=new(){Stroke=Hud.Solid("#596F8FB0"),Dashed=false,Tilt=-70,StartAngle=200,Sweep=140,Margin=new Thickness(-10,0,0,0),Visibility=Visibility.Collapsed};
    public event Action? Confirm, Cancel, Locate, Manual, Interactions, ReturnTopic, NavigationScope;
    /// <summary>0.4.8：用户拖动窗口后触发（含双击把手复位为 null）；参数为新的用户位置，null 表示恢复默认跟随主窗口。</summary>
    public event Action<Point?>? UserMoved;
    /// <summary>用户拖动后的记忆位置；为 null 时按调用方给的默认位置摆放。</summary>
    public Point? UserPosition { get; set; }
    bool dragging;
    public bool IsNavigation { get; private set; }
    public bool IsAllMenus { get; private set; }
    public string? SelectedNavigationId => (Options.SelectedItem as ListBoxItem)?.Tag as string;
    public string? MenuId { get; private set; }
    public int Epoch { get; private set; }
    internal long LastPointerInputTimestamp { get; private set; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public BranchMenuWindow()
    {
        Icon=Theme.AppIcon;
        Title="剧情配音 · 选择路线";Width=430;SizeToContent=SizeToContent.Height;MaxHeight=600;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Background=Theme.Brush("Inset");Foreground=Theme.Brush("Fg");FontFamily=Theme.Font("UiFont");UseLayoutRounding=true;
        panel=new Grid{Margin=new Thickness(16,16,16+BranchDecor.RailWidth,14)};
        // 行：页眉 / 完整句读数条（12-B）/ 列表 / 按钮或翻页；列：左栏 / 右侧指令条（12-B）
        panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition());panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        panel.ColumnDefinitions.Add(new ColumnDefinition());panel.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        // 切角黄铜外框：窗口本身保持矩形（不改分层窗口与激活方式），切角处露出更深的底色。
        // 10-A2：页眉档案底纹 / 节点号水印 / F1b 出血 / 轨道 / 右侧刻度轨 / 潮汐带 / 角括号，全部画在 decor 层，不压在选项与按钮上。
        var layers=new Grid();layers.Children.Add(decor);layers.Children.Add(panel);
        Content=new CutFrame{Background=Theme.Brush("Bg"),BorderBrush=Theme.Brush("BranchStroke"),NotchBrush=Theme.Brush("Brass"),NotchLength=40,Cut=14,Corners=CutCorners.Diagonal,BracketBrush=Theme.Brush("Brass"),BracketLength=8,ClipContent=true,Child=layers};
        Options.AlternationCount=99;
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
        var header=new StackPanel();Grid.SetColumnSpan(header,2);
        var badgeRow=new DockPanel{Margin=new Thickness(0,0,0,9),LastChildFill=false};
        var badgeInner=new StackPanel{Orientation=Orientation.Horizontal};badgeInner.Children.Add(badgeGrip);badgeInner.Children.Add(badge);
        var badgeGridHost=new Grid();badgeGridHost.Children.Add(badgeInner);badgeGridHost.Children.Add(badgeDash);badgeHost.Child=badgeGridHost;
        badgeRow.Children.Add(badgeHost);
        headerTag.Content=Archive.StripedTag("ROUTE // BRANCH");badgeRow.Children.Add(headerTag);
        var emblem=new Emblem{Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(emblem,Dock.Right);badgeRow.Children.Add(emblem);header.Children.Add(badgeRow);header.Children.Add(title);header.Children.Add(notice);header.Children.Add(continuationFeedback);header.Children.Add(gamepadHint);header.Children.Add(scopeButton);
        var mapLabel=new TextBlock{Text="SECTION ROUTE MAP",FontFamily=Theme.Font("LabelFont"),FontSize=8.5,Foreground=Theme.Brush("NormalAccent"),VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(mapLabel,Dock.Right);
        summaryBar.Children.Add(summary);summaryBar.Children.Add(mapLabel);header.Children.Add(summaryBar);
        panel.Children.Add(header);
        InitializeExplore(header,badgeRow);
        header.SizeChanged+=(_,_)=>decor.HeaderBottom=header.ActualHeight+panel.Margin.Top+4;
        // 0.4.8：页眉（把手 / 标签 / 标题 / 说明）可拖动整个窗口；选项列表与按钮不参与，避免抢走单击/双击确认。
        // 双击把手或标签恢复默认位置（跟随主窗口）。拖动不激活窗口（WS_EX_NOACTIVATE 保持），不向游戏发送点击。
        header.Cursor=Cursors.SizeAll;
        header.MouseLeftButtonDown+=(_,e)=>
        {
            if(IsAnchorView || e.ButtonState!=MouseButtonState.Pressed) return;   // 首句确认视图保留原有把手拖动
            if(e.OriginalSource is DependencyObject source && (FindAncestor<Button>(source,header)!=null || FindAncestor<ListBox>(source,header)!=null)) return;
            if(e.ClickCount==2){UserPosition=null;UserMoved?.Invoke(null);e.Handled=true;return;}
            dragging=true;
            try{DragMove();}catch(InvalidOperationException){}
            finally{dragging=false;}
            UserPosition=new Point(Left,Top);UserMoved?.Invoke(UserPosition);e.Handled=true;
        };
        SetBadgeTone(false);
        scopeButton.Click+=(_,_)=>NavigationScope?.Invoke();
        Grid.SetRow(routeOrbit,2);panel.Children.Add(routeOrbit);
        Grid.SetRow(Options,2);panel.Children.Add(Options);
        Grid.SetRow(footer,3);panel.Children.Add(footer);
        Button Button(string text,string sub,Geometry icon,Action click,string kind="") {var b=ActionButton.Apply(new Button{Content=text,Focusable=false},sub,icon,kind);b.Click+=(_,_)=>click();return b;}
        confirmButton=Button("确认并播放  Enter","CONFIRM",ActionIcons.Confirm,()=>Confirm?.Invoke(),"go");
        switchButton=Button("按下一句正文确认","NEXT LINE",ActionIcons.Line,()=>{if(presentedEngine!=null && !TryPresentAnchors(presentedEngine))notice.Text="没有可直接确认的已核首句；请保留原选项，或定位游戏当前正文。";});
        peopleButton.Click+=(_,_)=>Interactions?.Invoke();topicButton.Click+=(_,_)=>ReturnTopic?.Invoke();
        locateButton=Button("定位游戏当前台词","LOCATE",ActionIcons.Locate,()=>Locate?.Invoke());
        manualButton=Button("手动选续接句","RESUME LINK",ActionIcons.Link,()=>Manual?.Invoke());
        cancelButton=Button("收起，保持待选  Esc","STANDBY",ActionIcons.Collapse,()=>Cancel?.Invoke(),"ghost");
        LayoutFooter(false);
        InitializeAnchorView(header);
        SourceInitialized+=(_,_)=> {SetWindowLong(Handle,-20,GetWindowLong(Handle,-20)|0x08000000|0x80);HwndSource.FromHwnd(Handle)?.AddHook(Hook);};
    }
    // 10-A3 按钮排列：常规为单列（「手动选分支 | 返回」一行两个）；11-B 紧凑为主按钮 + 两列（半宽按钮隐藏英文副标）。
    void LayoutFooter(bool compact)
    {
        if(footerCompact==compact)return;
        footerCompact=compact;
        footer.Children.Clear();navigationRow.Children.Clear();compactRow.Children.Clear();
        footer.Children.Add(confirmButton!);
        var pairs=compact?new[]{switchButton,locateButton,peopleButton,topicButton,manualButton,cancelButton}:new[]{peopleButton,topicButton};
        for(int i=0;i<pairs.Length;i++)
        {
            pairs[i].Margin=i%2==0?new Thickness(0,6,3,0):new Thickness(3,6,0,0);
            ActionButton.SetCompact(pairs[i],compact);
            (compact?compactRow:navigationRow).Children.Add(pairs[i]);
        }
        foreach(var b in new[]{switchButton,locateButton,manualButton,cancelButton})if(!compact){b.Margin=new Thickness(0,6,0,0);ActionButton.SetCompact(b,false);}
        if(compact)footer.Children.Add(compactRow);
        else{footer.Children.Add(switchButton);footer.Children.Add(navigationRow);footer.Children.Add(locateButton);footer.Children.Add(manualButton);footer.Children.Add(cancelButton);}
    }
    // 状态徽标：分支 = 黄铜；「连接尚未确认」= 雾蓝字 + 雾蓝浅底 + 2px 虚线左边（文字不变）。
    void SetBadgeTone(bool gap)
    {
        badge.Foreground=gap?Theme.Brush("NormalAccent"):LineRow.BranchAccent;
        badge.Background=gap?Theme.Brush("SlateFill"):LineRow.BranchFill;
        badgeDash.Stroke=Theme.Brush("Slate");badgeDash.Visibility=gap?Visibility.Visible:Visibility.Collapsed;
        badge.Padding=new Thickness(gap?10:8,3,8,3);
    }
    static FrameworkElement GripDots()
    {
        var grid=new System.Windows.Controls.Primitives.UniformGrid{Columns=2,Rows=3,Width=6,Height=10,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed,IsHitTestVisible=false};
        for(int i=0;i<6;i++)grid.Children.Add(new System.Windows.Shapes.Rectangle{Width=2,Height=2,Fill=Theme.Brush("Brass")});
        return grid;
    }
    // 页眉描边水印：选择点编号（节点 id 末尾数字）或小节编号（如「32-6」）。
    static string NodeNumber(string? id)
    {
        var m=System.Text.RegularExpressions.Regex.Match(id??"",@"(\d+)(?!.*\d)");
        return m.Success?(m.Value.Length>4?m.Value[^4..]:m.Value):"";
    }
    static string SectionNumber(string title)
    {
        var m=System.Text.RegularExpressions.Regex.Match(title,@"(?<![\w-])([A-Za-z]{0,3}-?\d+(?:-\d+)+|[A-Za-z]{1,3}\d+|\d+)");
        return m.Success?m.Groups[1].Value:"";
    }
    static string[] RailNumbers(string number)
    {
        if(!int.TryParse(number,out int n) || n<3)return Array.Empty<string>();
        return new[]{(n-2).ToString(),(n-1).ToString(),n.ToString()};
    }
    void SetDecor(string watermark,bool anchor)
    {
        decor.Watermark=watermark;decor.WatermarkSize=anchor?96:128;decor.EmblemSize=anchor?104:118;
        decor.ShowRail=!anchor;decor.Ruler=anchor;decor.TideHeight=anchor?22:44;decor.RailMarks=anchor?null:RailNumbers(watermark);
        panel.Margin=new Thickness(16,16,anchor?16:16+BranchDecor.RailWidth,anchor?26:14);
        headerTag.Content=Archive.StripedTag(anchor?"FIRST LINE // CONFIRM":"ROUTE // BRANCH");
        badgeGrip.Visibility=anchor?Visibility.Visible:Visibility.Collapsed;
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
        SetBadgeTone(boundary);SetDecor(NodeNumber(engine.CurrentId),false);LayoutFooter(false);
        summaryBar.Visibility=routeOrbit.Visibility=Visibility.Collapsed;Options.ItemContainerStyle=(Style)Application.Current.FindResource("BranchOptionStyle");
        title.Text=engine.Current?.Text;
        notice.Text=engine.Notice+(boundary?"\n单击选中，双击打开所选菜单（不播放）。":"\n单击选中，双击确认进入；也可点击确认按钮。")+"\n↑↓选择 · Enter确认 · Esc收起";
        Options.Items.Clear();ResetExplore();
        badge.Text=boundary?"◆ 连接尚未确认 · 等待续接":"◆ 分支选择 · 等待确认";
        confirmButton!.Content=boundary?"打开选中的菜单  Enter":"确认进入  Enter";
        confirmButton.IsEnabled=!boundary || engine.ResumeMenus.Count>0;
        if(boundary)
            foreach(var menu in engine.ResumeMenus)Options.Items.Add(new ListBoxItem{Content="手动返回："+menu.Text,Focusable=false});
        // 0.4.7 同回应隐藏（如 19-14 答错回来）：与刚选项回应相同的选项在游戏里已隐藏，这里同样不显示（保留下标，确认仍按原序号）。
        var hiddenOptions=boundary?Array.Empty<int>():DesktopExploration.HiddenIndexes(engine);
        int optionIndex=-1;
        // 0.4.7 探索菜单行样式（设计稿 14 / 15 桌面浮动菜单行）：回环项带 ↻ 图标，出口项带「EXIT」标签与「随时可选」，已读项右侧「✓ 已听」。不显示主线 / 闲聊标识。
        MenuShapeInfo? shape=null;
        if(!boundary && engine.Current is {Kind:"choice"} shapeMenu){try{shape=MenuShape.Of(engine.Pack,shapeMenu);}catch{shape=null;}}
        bool exploring=shape?.Exploration==true;
        var exploredHeard=exploring && engine.CurrentId!=null?DesktopExploration.Heard(engine,engine.CurrentId):new HashSet<string>();
        // 0.4.7 设计稿 14 / 15：探索菜单换成自动跟随卡（卡头、跳转框、键位字行、换人 / 重新识别、兜底卡）。
        if(exploring)PresentExploreHeader(engine,engine.Current!,shape!,x=>engine.Heard.Contains(x.PathId)||exploredHeard.Contains(x.Id));
        int exploreKey=0;
        foreach(var o in engine.AvailableOptions)
        {
            optionIndex++;
            if(exploring)
            {
                string st=engine.Pack.SchemaVersion==3 ? !o.BodyVerified?"  · 仅单句核对":!o.ExitVerified?"  · 段尾需选择":"" : !o.Verified?"  · 待核对":"";
                bool h=engine.Heard.Contains(o.PathId) || exploredHeard.Contains(o.Id);
                bool loop=shape!.Loops.Any(x=>x.Id==o.Id), exitRow=!loop && shape.Exits.Any(x=>x.Id==o.Id);
                bool hidden=hiddenOptions.Contains(optionIndex);
                bool returnLoop=loop && shape.EmptyLoops.Any(x=>x.Id==o.Id);
                var row=ExploreOptionRow(engine,engine.Current!,o,exitRow?0:exploreKey,loop,exitRow,h,st,returnLoop);
                if(!exitRow && !returnLoop && !hidden)exploreKey++;
                Options.Items.Add(new ListBoxItem{Content=row,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,Visibility=hidden?Visibility.Collapsed:Visibility.Visible});
                continue;
            }
            var p=new StackPanel();
            string state=engine.Pack.SchemaVersion==3 ? !o.BodyVerified?"  · 仅单句核对":!o.ExitVerified?"  · 段尾需选择":"" : !o.Verified?"  · 待核对":"";
            bool heard=engine.Heard.Contains(o.PathId) || exploredHeard.Contains(o.Id);
            bool isLoop=exploring && shape!.Loops.Any(x=>x.Id==o.Id), isExit=exploring && !isLoop && shape!.Exits.Any(x=>x.Id==o.Id);
            var head=new DockPanel();
            if(isExit)head.Children.Add(Docked(new TextBlock{Text="随时可选",FontSize=11,Foreground=Theme.Brush("BrassText"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)},Dock.Right));
            else if(heard)head.Children.Add(Docked(new TextBlock{Text="✓ 已听",FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Theme.Brush("TealText"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)},Dock.Right));
            if(isExit)head.Children.Add(Docked(new Border{BorderBrush=Theme.Brush("Brass"),BorderThickness=new Thickness(1),Background=Theme.Brush("BranchFill"),Padding=new Thickness(4,0,5,1),Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center,ToolTip="出口：随时可选，选后离开本菜单",
                Child=new TextBlock{Text="[→ EXIT",FontSize=9.5,FontWeight=FontWeights.SemiBold,Foreground=Theme.Brush("BrassText")}},Dock.Left));
            else if(isLoop)head.Children.Add(Docked(new TextBlock{Text="↻",FontSize=13,Foreground=Theme.Brush("MutedText"),Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center,ToolTip="回环：读完回到本菜单"},Dock.Left));
            head.Children.Add(new TextBlock{Text=o.Label+state,TextWrapping=TextWrapping.Wrap,Foreground=heard&&!isExit?Theme.Brush("MutedText"):Theme.Brush("Fg"),FontWeight=isExit?FontWeights.SemiBold:FontWeights.Normal});
            p.Children.Add(head);
            p.Children.Add(new TextBlock{Text=o.Preview,ToolTip=o.Preview,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Theme.Brush("MutedText"),MaxHeight=44,Margin=new Thickness(0,4,0,0)});
            if(o.ConditionNote.Length>0)p.Children.Add(new TextBlock{Text=o.ConditionNote,TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=LineRow.BranchAccent});
            if(!engine.ConditionsKnown(o))p.Children.Add(new TextBlock{Text="前置进度未同步 · 游戏里有此选项即可确认",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=LineRow.BranchAccent});
            Options.Items.Add(new ListBoxItem{Content=p,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,
                Visibility=hiddenOptions.Contains(optionIndex)?Visibility.Collapsed:Visibility.Visible});
        }
        if(hiddenOptions.Count>0)notice.Text="与刚才回应相同的选项已隐藏（游戏里同样不再显示）。\n"+notice.Text;
        Options.SelectedIndex=Math.Clamp(selected,0,Math.Max(0,Options.Items.Count-1));
        if(Collapsed(Options.SelectedIndex)){int first=Enumerable.Range(0,Options.Items.Count).FirstOrDefault(i=>!Collapsed(i));Options.SelectedIndex=first;}
        Place(left,top,600);
        if(!IsVisible){Epoch++;Show();}
        Options.ScrollIntoView(Options.SelectedItem);
    }
    /// <summary>按默认位置或用户拖动过的位置摆放，始终收在工作区内；拖动进行中不改位置。</summary>
    void Place(double left,double top,double maxHeight)
    {
        if(dragging)return;
        var area=SystemParameters.WorkArea;MaxHeight=Math.Min(maxHeight,area.Height-24);
        if(UserPosition is {} user){left=user.X;top=user.Y;}
        Left=Math.Clamp(left,area.Left,Math.Max(area.Left,area.Right-Width));Top=Math.Clamp(top,area.Top,Math.Max(area.Top,area.Bottom-MaxHeight));
    }
    static T? FindAncestor<T>(DependencyObject? node,DependencyObject stop) where T:DependencyObject
    {
        while(node!=null && node!=stop){if(node is T hit)return hit;node=System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);}
        return null;
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
        IsNavigation=true;IsAllMenus=allMenus;title.Text="换人 / 选择话题";ResetExplore();
        SetBadgeTone(false);LayoutFooter(allMenus);summaryBar.Visibility=routeOrbit.Visibility=allMenus?Visibility.Visible:Visibility.Collapsed;
        Options.ItemContainerStyle=(Style)Application.Current.FindResource(allMenus?"RouteRowStyle":"BranchOptionStyle");
        badge.Text="◆ 自由互动 · 由你选择顺序";
        notice.Text="游戏正和谁交谈，就打开谁的话题。\n单击选中，双击打开菜单（不播放）。\n也可 ↑↓选择，点击下方按钮或按 Enter 确认。";
        scopeButton.Visibility=allMenus && !hasInteractions?Visibility.Collapsed:Visibility.Visible;
        scopeButton.Content=allMenus?"只看已核实的人物 / 话题":"查看本节全部选择点";
        scopeButton.IsEnabled=!allMenus || hasInteractions;
        ActionButton.SetSublabel(scopeButton,allMenus?"SCOPE · VERIFIED":"SCOPE · ALL");
        confirmButton!.Content="游戏已到这里 · 打开菜单（不播放） Enter";
        confirmButton.IsEnabled=allMenus?storyMenus.Count>0:hasInteractions;
        peopleButton.IsEnabled=false;topicButton.IsEnabled=section==engine.Current?.SectionId && (engine.ReturnInteractionTarget!=null || engine.ParentStoryMenu!=null);
        topicButton.Content=engine.ReturnInteractionTarget!=null?"返回话题":"返回选择点";
        topicButton.ToolTip=engine.ReturnInteractionTarget?.Label ?? engine.ParentStoryMenu?.Label;
        Options.Items.Clear();
        if(allMenus)
        {
            title.Text=engine.Pack.Chapters.SelectMany(c=>c.Sections).First(s=>s.Id==section).Title;
            SetDecor(SectionNumber(title.Text),false);
            badge.Text="◆ 手动选分支 · 按游戏画面同步";
            notice.Text="可直接选择人物、话题或剧情菜单，无需重读前置。\n单击选中，双击打开菜单（不播放）。\n也可 ↑↓选择，点击下方按钮或按 Enter 确认。";
            var current=storyMenus.FirstOrDefault(t=>t.IsCurrent);
            summary.Text=$"{storyMenus.Count} 个选择点 · 曾选择 {storyMenus.Count(t=>t.IsVisited)} · 当前 "+(current==null?"—":current.Index.ToString("00"));
            for(int i=0;i<storyMenus.Count;i++)
                Options.Items.Add(new ListBoxItem {Tag=storyMenus[i].MenuId,Content=StoryMenuRow(storyMenus[i],i==0,i==storyMenus.Count-1),Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip="按游戏当前画面打开此菜单，保持静音；确认具体选项后才进入对应内容。"});
        }
        else SetDecor(SectionNumber(engine.Pack.Chapters.SelectMany(c=>c.Sections).FirstOrDefault(s=>s.Id==section)?.Title??""),false);
        if(!allMenus)foreach(var target in engine.InteractionMenus)
        {
            var content=new StackPanel();
            content.Children.Add(new TextBlock {Text=target.Label+(target.IsCurrent?" · 当前":""),TextWrapping=TextWrapping.Wrap});
            if(engine.Pack.ById.TryGetValue(target.MenuId,out var menu))
                content.Children.Add(new TextBlock {Text=string.Join(" / ",menu.Options.Take(3).Select(o=>o.Label)),FontSize=11,Foreground=Theme.Brush("MutedText"),TextWrapping=TextWrapping.Wrap,MaxHeight=42,Margin=new Thickness(0,4,0,0)});
            Options.Items.Add(new ListBoxItem{Tag=target.MenuId,Content=content,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        }
        Options.SelectedIndex=Math.Clamp(selected,0,Math.Max(0,Options.Items.Count-1));
        Place(left,top,650);
        if(!IsVisible){Epoch++;Show();}Options.ScrollIntoView(Options.SelectedItem);
    }
    public void Dismiss(){pendingMouseConfirm=null;Epoch++;SetGamepadNavigation(false);Hide();}
    public void Move(int delta)
    {
        if(IsAnchorView && (Options.SelectedIndex+delta<0 || Options.SelectedIndex+delta>=Options.Items.Count))
        {ChangeAnchorPage(delta<0?-1:1);return;}
        int next=Math.Clamp(Options.SelectedIndex+delta,0,Math.Max(0,Options.Items.Count-1));
        // 跳过同回应隐藏的选项。
        while(next>0 && next<Options.Items.Count-1 && Collapsed(next))next+=Math.Sign(delta);
        if(Collapsed(next))next=Options.SelectedIndex;
        Options.SelectedIndex=next;Options.ScrollIntoView(Options.SelectedItem);
    }
    bool Collapsed(int index)=>index>=0 && index<Options.Items.Count && Options.Items[index] is ListBoxItem{Visibility:Visibility.Collapsed};
    static UIElement Docked(UIElement e,Dock dock){DockPanel.SetDock(e,dock);return e;}
}

