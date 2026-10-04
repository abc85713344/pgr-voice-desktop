using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    GamepadInput? gamepadInput;
    GamepadUiNavigation? gamepadNavigation;
    GamepadReading? gamepadLatest;
    Task gamepadStopping=Task.CompletedTask;
    bool gamepadRestartPending;
    readonly GamepadActionGate gamepadGate = new();
    readonly ConcurrentQueue<(GamepadInput Source, GamepadReading Reading, IntPtr Foreground)> gamepadQueue = new();
    int gamepadDispatching, gamepadOverflow;
    GamepadDevice[] gamepadDevices = Array.Empty<GamepadDevice>();
    DispatcherTimer? gamepadTimer;
    TabItem gamepadTab = null!;
    readonly CheckBox gamepadEnabled = new() { Content="启用手柄操作" }, gamepadFollow = new() { Content="用手柄跟随游戏对白" }, gamepadTest = new() { Content="测试按钮（不播放、不推进）" };
    readonly ComboBox gamepadDeviceBox = new(), gamepadGlyphBox = new(), gamepadAdvanceBox = new(), gamepadModifierBox = new();
    readonly TextBlock gamepadStatus = new() { TextWrapping=TextWrapping.Wrap }, gamepadInputStatus = new() { TextWrapping=TextWrapping.Wrap }, gamepadHelp = new() { TextWrapping=TextWrapping.Wrap };
    readonly TextBlock gamepadPanelHint = new() { TextWrapping=TextWrapping.Wrap, FontSize=10, Margin=new Thickness(0,4,0,3) };
    readonly Dictionary<string,ComboBox> gamepadBindingBoxes = new();
    bool gamepadRefreshing, gamepadReady, gamepadWasConnected, gamepadRequireNeutral=true;
    uint? gamepadChosenId;
    bool gamepadAmbiguousSelection;
    string gamepadDeviceNotice="";
    string gamepadContext = "", gamepadBackendError = "";
    long gamepadLastReading;
    GamepadButtons gamepadHeld;
    sealed record GamepadDeviceOption(uint? Id,string Name,string Label) { public override string ToString()=>Label; }
    sealed record GamepadButtonOption(GamepadButtons Button,string Label) { public override string ToString()=>Label; }
    bool GamepadSony => preferences.GamepadGlyphStyle=="playstation" || preferences.GamepadGlyphStyle!="xbox" && gamepadLatest?.Device?.Family==GamepadFamily.PlayStation;
    bool GamepadConnected => preferences.GamepadEnabled && gamepadLatest?.Connected==true;
    bool GamepadFollowAvailable => GamepadConnected && preferences.GamepadFollowEnabled;
    // 自动点击检查直接读当前监听实例的后台快照，不等待 UI 输入队列，也不接受旧实例快照。
    bool GamepadActiveInput => preferences.GamepadEnabled && (gamepadInput?.LatestReading ?? gamepadLatest) is {Connected:true} r &&
        (r.Buttons!=GamepadButtons.None || Math.Abs(r.LeftX)>.45 || Math.Abs(r.LeftY)>.45 || Math.Abs(r.RightX)>.45 || Math.Abs(r.RightY)>.45);
    string PadLabel(GamepadButtons button)=>GamepadLayout.Label(button,GamepadSony);
    string PadChord(string action)
    {
        var button=GamepadLayout.Parse(preferences.GamepadBindings.GetValueOrDefault(action));
        return button==GamepadButtons.None?"未绑定":PadLabel(GamepadLayout.Parse(preferences.GamepadModifier))+" + "+PadLabel(button);
    }
    void InitializeGamepad()
    {
        preferences.GamepadBindings ??= GamepadLayout.Defaults();
        foreach(var entry in GamepadLayout.Defaults()) preferences.GamepadBindings.TryAdd(entry.Key,entry.Value);
        if(GamepadLayout.Parse(preferences.GamepadModifier)==GamepadButtons.None) preferences.GamepadModifier="Back";
        if(GamepadLayout.Parse(preferences.GamepadAdvanceButton)==GamepadButtons.None) preferences.GamepadAdvanceButton="South";
        gamepadNavigation=new(this);
        gamepadTab=new(){Header="手柄"};
        var panel=new StackPanel {Margin=new Thickness(0,8,6,10)};
        panel.Children.Add(new TextBlock {Text="Xbox / PS4 / PS5 · 可用 USB 或无线连接",FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(gamepadEnabled);panel.Children.Add(gamepadStatus);
        AddHeading(panel,"当前手柄");panel.Children.Add(gamepadDeviceBox);
        panel.Children.Add(new TextBlock {Text="一次只使用一只手柄。若同时出现实体和 Steam / DS4Windows 虚拟手柄，请选其中一只。",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,4,0,6)});
        AddHeading(panel,"按钮提示");gamepadGlyphBox.ItemsSource=new[]{"自动识别","Xbox：A / B / X / Y","索尼：× / ○ / □ / △"};panel.Children.Add(gamepadGlyphBox);
        AddHeading(panel,"游戏对白跟随");panel.Children.Add(gamepadFollow);panel.Children.Add(gamepadAdvanceBox);
        panel.Children.Add(new TextBlock {Text="选成游戏里实际的“对白继续”键，先确认当前台词再跟随。开启后仍可直接换用键盘或鼠标热区，无需反复切开关。不同来源近乎同时到达的推进只计一次，避免常见映射重复；切换前先松开上一设备。",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,5,0,6)});
        AddHeading(panel,"配音快捷操作 · 按住组合前缀键");panel.Children.Add(gamepadModifierBox);
        foreach(var item in new Dictionary<string,string>{{"panel","展开 / 收起配音面板"},{"pause","暂停 / 恢复"},{"replay","重播本句"},{"ocr","定位游戏当前句"},{"previous","配音上一句"},{"manualNext","配音下一句"},{"interactions","手动选择分支"},{"history","历史"},{"automatic","开始 / 停止自动播放"},{"original","游戏原声"}})
        {
            panel.Children.Add(new TextBlock {Text=item.Value,Margin=new Thickness(0,5,0,2)});
            var box=new ComboBox();gamepadBindingBoxes[item.Key]=box;panel.Children.Add(box);
            string action=item.Key;
            box.SelectionChanged+=(_,_)=>
            {
                if(gamepadRefreshing || !gamepadReady || box.SelectedItem is not GamepadButtonOption selected)return;
                bool duplicate=selected.Button!=GamepadButtons.None && (selected.Button==GamepadLayout.Parse(preferences.GamepadModifier) ||
                    preferences.GamepadBindings.Any(x=>x.Key!=action && GamepadLayout.Parse(x.Value)==selected.Button));
                if(duplicate){gamepadInputStatus.Text="这个组合已被使用，请选择其他按钮。";RefreshGamepadControls();return;}
                if(action=="panel" && selected.Button==GamepadButtons.None){gamepadInputStatus.Text="展开面板需要保留一个组合键。";RefreshGamepadControls();return;}
                preferences.GamepadBindings[action]=selected.Button.ToString();GamepadSettingsChanged();
            };
        }
        AddButton(panel,"恢复默认手柄布局",()=>{preferences.GamepadBindings=GamepadLayout.Defaults();preferences.GamepadModifier="Back";preferences.GamepadAdvanceButton="South";GamepadSettingsChanged();RefreshGamepadControls();});
        AddHeading(panel,"手柄测试");panel.Children.Add(gamepadTest);panel.Children.Add(gamepadInputStatus);panel.Children.Add(gamepadHelp);
        gamepadTab.Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Tabs.Items.Insert(Math.Max(0,Tabs.Items.Count-1),gamepadTab);
        ((StackPanel)PlaybackBar.Child).Children.Add(gamepadPanelHint);
        gamepadEnabled.Checked+=(_,_)=>ChangeGamepadEnabled(true);gamepadEnabled.Unchecked+=(_,_)=>ChangeGamepadEnabled(false);
        gamepadFollow.Checked+=(_,_)=>ChangeGamepadFollow(true);gamepadFollow.Unchecked+=(_,_)=>ChangeGamepadFollow(false);
        gamepadDeviceBox.SelectionChanged+=(_,_)=>
        {
            if(gamepadRefreshing || !gamepadReady || gamepadDeviceBox.SelectedItem is not GamepadDeviceOption device)return;
            preferences.GamepadDeviceName=device.Name;gamepadChosenId=device.Id;gamepadDeviceNotice="";
            gamepadAmbiguousSelection=device.Name.Length>0 && gamepadDevices.Count(d=>d.Name==device.Name)>1;
            if(gamepadInput!=null)gamepadInput.SelectedDeviceId=device.Id;
            PauseForGamepadChange("已更换手柄，核对当前句后再继续。");GamepadSettingsChanged();
        };
        gamepadGlyphBox.SelectionChanged+=(_,_)=>
        {if(gamepadRefreshing || !gamepadReady)return;preferences.GamepadGlyphStyle=gamepadGlyphBox.SelectedIndex switch{1=>"xbox",2=>"playstation",_=>"auto"};GamepadSettingsChanged();RefreshGamepadControls();};
        gamepadAdvanceBox.SelectionChanged+=(_,_)=>
        {
            if(gamepadRefreshing || !gamepadReady || gamepadAdvanceBox.SelectedItem is not GamepadButtonOption x)return;
            if(x.Button==GamepadLayout.Parse(preferences.GamepadModifier)){gamepadInputStatus.Text="对白继续键不能与组合前缀键相同。";RefreshGamepadControls();return;}
            preferences.GamepadAdvanceButton=x.Button.ToString();GamepadSettingsChanged();
        };
        gamepadModifierBox.SelectionChanged+=(_,_)=>
        {
            if(gamepadRefreshing || !gamepadReady || gamepadModifierBox.SelectedItem is not GamepadButtonOption x)return;
            if(x.Button==GamepadLayout.Parse(preferences.GamepadAdvanceButton) || preferences.GamepadBindings.Values.Any(v=>GamepadLayout.Parse(v)==x.Button))
            {gamepadInputStatus.Text="前缀键不能同时作为某个组合的第二个按钮，请先调整该组合。";RefreshGamepadControls();return;}
            preferences.GamepadModifier=x.Button.ToString();GamepadSettingsChanged();
        };
        gamepadTest.Checked+=(_,_)=>{if(gamepadReady){PauseForGamepadChange("手柄测试中，不推进剧情。");gamepadGate.Reset();}};
        gamepadTest.Unchecked+=(_,_)=>{ResetGamepadContext();gamepadInputStatus.Text="测试结束，确认位置后再继续。";};
        keyTestBox.Unchecked+=(_,_)=>{if(gamepadReady)ResetGamepadContext();};
        PreviewMouseDown+=(_,_)=>gamepadNavigation.ReleaseHighlight();
        gamepadReady=true;RefreshGamepadControls();
        gamepadTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
        gamepadTimer.Tick+=(_,_)=>TickGamepad();
        Loaded+=(_,_)=>{if(!testUi){gamepadTimer.Start();if(preferences.GamepadEnabled)StartGamepadInput();}};
    }
    void ChangeGamepadEnabled(bool value)
    {
        if(gamepadRefreshing || !gamepadReady)return;
        preferences.GamepadEnabled=value;gamepadGate.Reset();
        if(value){if(!testUi)StartGamepadInput();}else{PauseForGamepadChange("手柄操作已关闭。");StopGamepadInput();}
        Save();RefreshGamepadHints();
    }
    void ChangeGamepadFollow(bool value)
    {
        if(gamepadRefreshing || !gamepadReady)return;
        preferences.GamepadFollowEnabled=value;PauseForGamepadChange(value?"手柄对白跟随已启用，键盘和鼠标仍可热切换。":"手柄对白跟随已关闭，键盘和鼠标可继续使用。");
        GamepadSettingsChanged();
    }
    void GamepadSettingsChanged(){ResetGamepadContext();Save();RefreshGamepadHints();}
    void PauseForGamepadChange(string message)
    {
        // 闲置手柄故障不能打断当前键鼠或独立听书；活动手柄中断才暂停跟随。
        if (ActiveGamepadFollow)
        {
            StopAutomatic(message); CancelPendingMouseFollow(); engine?.PauseForBrowse();
            Tell(message + " 请核对当前句后继续。");
        }
        gamepadFollowPress=false;gamepadGate.Reset();gamepadRequireNeutral=true;gamepadNavigation?.ReleaseHighlight();
    }
    void StartGamepadInput()
    {
        if(gamepadInput!=null || closing)return;
        if(!gamepadStopping.IsCompleted)
        {
            if(!gamepadRestartPending){gamepadRestartPending=true;gamepadStatus.Text="正在结束上次监听…";_ = RestartGamepadAfterStop();}
            return;
        }
        var source=new GamepadInput();gamepadInput=source;gamepadBackendError="";
        if(preferences.GamepadDeviceName.Length>0)source.SelectedDeviceId=0;
        source.DevicesChanged+=devices=>Dispatcher.BeginInvoke(()=>
        {
            if(closing || source!=gamepadInput)return;
            gamepadDevices=devices;
            SelectRememberedGamepad(source,devices);
            RefreshGamepadControls();
        });
        source.Reading+=reading=>
        {
            if(closing || source!=gamepadInput)return;
            gamepadQueue.Enqueue((source,reading,Native.GetForegroundWindow()));
            while(gamepadQueue.Count>64 && gamepadQueue.TryDequeue(out _))Interlocked.Exchange(ref gamepadOverflow,1);
            if(Interlocked.Exchange(ref gamepadDispatching,1)==0)Dispatcher.BeginInvoke(DispatcherPriority.Input,DrainGamepadInput);
        };
        source.Start();
    }
    async Task RestartGamepadAfterStop()
    {await gamepadStopping;gamepadRestartPending=false;if(!closing && preferences.GamepadEnabled)StartGamepadInput();}
    void SelectRememberedGamepad(GamepadInput source,GamepadDevice[] devices)
    {
        if(preferences.GamepadDeviceName.Length==0){source.SelectedDeviceId=null;gamepadChosenId=null;gamepadDeviceNotice="";return;}
        var matches=devices.Where(d=>d.Name==preferences.GamepadDeviceName).ToList();
        if(matches.Count>1)gamepadAmbiguousSelection=true;
        if(gamepadChosenId.HasValue && matches.Any(d=>d.Id==gamepadChosenId.Value)){source.SelectedDeviceId=gamepadChosenId;return;}
        if(gamepadAmbiguousSelection || matches.Count>1)
        {source.SelectedDeviceId=0;gamepadChosenId=0;gamepadAmbiguousSelection=true;gamepadDeviceNotice="检测到同名手柄，请在列表中重新选定一只。";return;}
        source.SelectedDeviceId=gamepadChosenId=matches.FirstOrDefault()?.Id??0;gamepadDeviceNotice="";
    }
    void DrainGamepadInput()
    {
        if(Interlocked.Exchange(ref gamepadOverflow,0)!=0)gamepadGate.Reset();
        int count=0;
        while(count++<32 && gamepadQueue.TryDequeue(out var item))
        {
            if(closing || item.Source!=gamepadInput)continue;
            gamepadLatest=item.Reading;
            HandleGamepadReading(item.Reading,item.Foreground);
        }
        Interlocked.Exchange(ref gamepadDispatching,0);
        if(!gamepadQueue.IsEmpty && Interlocked.Exchange(ref gamepadDispatching,1)==0)Dispatcher.BeginInvoke(DispatcherPriority.Input,DrainGamepadInput);
    }
    void StopGamepadInput()
    {
        var input=gamepadInput;gamepadInput=null;if(input!=null){input.Dispose();gamepadStopping=input.Completion;}gamepadLatest=null;gamepadWasConnected=false;
        while(gamepadQueue.TryDequeue(out _)){}gamepadGate.Reset();gamepadDevices=Array.Empty<GamepadDevice>();
        RefreshGamepadControls();
    }
    void ShutdownGamepad(){gamepadTimer?.Stop();StopGamepadInput();gamepadNavigation?.Dispose();}
    void RefreshGamepadControls()
    {
        if(!gamepadReady)return;
        gamepadRefreshing=true;
        try
        {
            gamepadEnabled.IsChecked=preferences.GamepadEnabled;gamepadFollow.IsChecked=preferences.GamepadFollowEnabled;
            var options=new List<GamepadDeviceOption>{new(null,"","自动选择一只手柄")};
            options.AddRange(gamepadDevices.Select(d=>new GamepadDeviceOption(d.Id,d.Name,d.Name+" · #"+d.Id)));
            if(preferences.GamepadDeviceName.Length>0 && (!options.Any(d=>d.Name==preferences.GamepadDeviceName) || gamepadDeviceNotice.Length>0))options.Add(new(0,preferences.GamepadDeviceName,preferences.GamepadDeviceName+(gamepadDeviceNotice.Length>0?"（请重新选择）":"（未连接）")));
            gamepadDeviceBox.ItemsSource=options;
            gamepadDeviceBox.SelectedItem=preferences.GamepadDeviceName.Length==0?options[0]:
                options.FirstOrDefault(d=>d.Name==preferences.GamepadDeviceName && d.Id==gamepadChosenId)??options.FirstOrDefault(d=>d.Name==preferences.GamepadDeviceName)??options[0];
            gamepadGlyphBox.SelectedIndex=preferences.GamepadGlyphStyle switch{"xbox"=>1,"playstation"=>2,_=>0};
            void Fill(ComboBox box,IEnumerable<GamepadButtons> values,string selected)
            {var rows=values.Select(v=>new GamepadButtonOption(v,PadLabel(v))).ToList();box.ItemsSource=rows;box.SelectedItem=rows.FirstOrDefault(v=>v.Button==GamepadLayout.Parse(selected))??rows.FirstOrDefault();}
            Fill(gamepadAdvanceBox,new[]{GamepadButtons.South,GamepadButtons.East,GamepadButtons.West,GamepadButtons.North,GamepadButtons.LeftShoulder,GamepadButtons.RightShoulder,GamepadButtons.LeftTrigger,GamepadButtons.RightTrigger},preferences.GamepadAdvanceButton);
            Fill(gamepadModifierBox,new[]{GamepadButtons.Back,GamepadButtons.LeftShoulder,GamepadButtons.LeftTrigger,GamepadButtons.LeftStick},preferences.GamepadModifier);
            foreach(var pair in gamepadBindingBoxes)Fill(pair.Value,GamepadLayout.Choices,preferences.GamepadBindings.GetValueOrDefault(pair.Key,"None"));
        }
        finally{gamepadRefreshing=false;}
        RefreshGamepadHints();
    }
    void RefreshGamepadHints()
    {
        if(!gamepadReady)return;
        gamepadStatus.Text=!preferences.GamepadEnabled?"手柄操作已关闭":gamepadBackendError.Length>0?gamepadBackendError:gamepadDeviceNotice.Length>0?gamepadDeviceNotice:gamepadLatest is {Connected:true,Device:not null} r?"已连接："+r.Device.Name:"未连接手柄；连接后会自动识别。";
        gamepadHelp.Text="面板：方向键 / 左摇杆移动，"+PadLabel(GamepadButtons.South)+" 确认，"+PadLabel(GamepadButtons.East)+" 返回，LB / RB（L1 / R1）切页。\n"+
            "听书：Menu / Options 暂停续听，"+PadLabel(GamepadButtons.West)+" 重播，"+PadLabel(GamepadButtons.North)+" 记书签。\n"+
            "游戏前台用 "+PadChord("panel")+" 打开配音面板。组合键也会传给游戏，如冲突可改绑。请先在游戏选择分支，再打开配音菜单确认相同路线。\n"+
            "确认和快捷操作在按钮松开后执行；重连或切回窗口后，先松开按钮并让摇杆归中。三种输入可自动切换，短时间重叠按一次处理。时间关联不能识别所有 Steam 映射；如果自定义映射仍重复，保留一种推进映射。";
        gamepadPanelHint.Visibility=GamepadConnected?Visibility.Visible:Visibility.Collapsed;
        gamepadPanelHint.Text="手柄：方向移动 · "+PadLabel(GamepadButtons.South)+" 确认 · "+PadLabel(GamepadButtons.East)+" 返回 · LB / RB 切页 · "+PadChord("panel")+" 展开/收起";
        branchMenu.SetGamepadHint(GamepadConnected?(branchMenu.GamepadNavigation?"手柄：↑↓选择 · "+PadLabel(GamepadButtons.South)+" 松开确认 · "+PadLabel(GamepadButtons.East)+" 返回游戏":"先在游戏选择路线，再按 "+PadChord("panel")+" 操作配音菜单。") : "");
    }
}
