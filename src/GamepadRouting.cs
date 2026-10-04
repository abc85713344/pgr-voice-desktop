using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PgrVoice;

public partial class MainWindow
{
    uint? gamepadHandledDevice;
    string GetGamepadContext()
    {
        if(closing || !preferences.GamepadEnabled || recordingAction!=null || keyEditor?.IsVisible==true)return "";
        var foreground=Native.GetForegroundWindow();
        if(branchMenu.IsVisible && branchMenu.GamepadNavigation && foreground==branchMenu.Handle)return "branch:"+branchMenu.Epoch;
        if(expanded && foreground==new WindowInteropHelper(this).Handle)return "panel:"+Tabs.SelectedIndex+":"+locating;
        if(game!=null && foreground==game.Handle && Native.IsWindow(game.Handle))return "game";
        return "";
    }
    void ResetGamepadContext()
    {
        gamepadGate.Reset();gamepadHeld=GamepadButtons.None;gamepadRequireNeutral=true;
        if(gamepadLatest is {Connected:true,IsNeutral:true} reading)
        {
            gamepadRequireNeutral=false;
            var held=gamepadGate.WithStick(reading.Buttons,reading.LeftX,reading.LeftY);
            gamepadGate.Read(held,reading.Timestamp,false,GamepadLayout.Parse(preferences.GamepadModifier),preferences.GamepadBindings,GamepadButtons.None);
        }
    }
    void HandleGamepadReading(GamepadReading reading,IntPtr observedForeground)
    {
        if(closing || !preferences.GamepadEnabled)return;
        bool deviceChanged=gamepadHandledDevice!=reading.Device?.Id || gamepadWasConnected!=reading.Connected;
        if(deviceChanged)
        {
            if(gamepadWasConnected && (!reading.Connected || gamepadHandledDevice!=reading.Device?.Id))
                PauseForGamepadChange("手柄已断开或切换，配音已暂停；重新连接后确认位置再继续。");
            gamepadHandledDevice=reading.Connected?reading.Device?.Id:null;gamepadWasConnected=reading.Connected;
            gamepadGate.Reset();gamepadRequireNeutral=true;gamepadLastReading=0;
            RefreshGamepadControls();
            Log.Write("gamepad",reading.Connected?"连接："+reading.Device?.Name:"手柄已断开");
        }
        if(!reading.Connected){gamepadHeld=GamepadButtons.None;return;}
        if(reading.Timestamp<gamepadLastReading)return;
        gamepadLastReading=reading.Timestamp;
        ObserveFollowGamepadRelease(reading);
        var buttons=gamepadGate.WithStick(reading.Buttons,reading.LeftX,reading.LeftY);
        gamepadHeld=buttons;
        if(buttons!=GamepadButtons.None || Math.Abs(reading.RightX)>.45 || Math.Abs(reading.RightY)>.45)
            ObserveTextAutoInput(game != null && observedForeground == game.Handle && GameIsForeground(), "检测到游戏内手柄操作，本句自动点击已取消。");
        gamepadInputStatus.Text="收到："+GamepadLayout.Describe(buttons,GamepadSony)+(gamepadTest.IsChecked==true?"\n测试中，没有执行剧情操作。":"");
        if(gamepadTest.IsChecked==true || KeyTesting)
        {gamepadGate.Reset();gamepadRequireNeutral=true;if(KeyTesting)keyTestStatus.Text=gamepadInputStatus.Text+"\n手柄测试中，没有执行剧情操作。";return;}
        string context=GetGamepadContext();
        if(context!=gamepadContext){gamepadContext=context;gamepadGate.Reset();gamepadRequireNeutral=true;}
        if(context.Length==0 || Native.GetForegroundWindow()!=observedForeground)
        {gamepadGate.Reset();gamepadRequireNeutral=true;gamepadNavigation?.ReleaseHighlight();return;}
        if(Stopwatch.GetTimestamp()-reading.Timestamp>Stopwatch.Frequency/2)
        {
            // 已就绪的跟随按压仍须经过迟到保护；空闲或菜单快照只丢弃，不打断键鼠。
            if(!gamepadRequireNeutral && context=="game" && GamepadFollowAvailable)
            {
                var delayedAction=gamepadGate.Read(buttons,reading.Timestamp,false,GamepadLayout.Parse(preferences.GamepadModifier),
                    preferences.GamepadBindings,GamepadLayout.Parse(preferences.GamepadAdvanceButton));
                if(delayedAction=="next")RequestFollowNext(FollowInputSource.Gamepad,reading.Timestamp);
            }
            gamepadGate.Reset();gamepadRequireNeutral=true;gamepadNavigation?.ReleaseHighlight();return;
        }
        // 不等按钮松开才停止自动点击。后台快照也被 AutomaticEnvironment 检查。
        if(automaticRunning && (buttons!=GamepadButtons.None || Math.Abs(reading.RightX)>.45 || Math.Abs(reading.RightY)>.45))
        {StopAutomatic("检测到手柄操作，自动播放已停止，请核对当前句。");gamepadGate.Reset();return;}
        if(gamepadRequireNeutral)
        {
            if(!reading.IsNeutral)return;
            gamepadRequireNeutral=false;
        }
        string? action=gamepadGate.Read(buttons,reading.Timestamp,context!="game",GamepadLayout.Parse(preferences.GamepadModifier),
            preferences.GamepadBindings,GamepadFollowAvailable?GamepadLayout.Parse(preferences.GamepadAdvanceButton):GamepadButtons.None);
        if(action!=null)HandleGamepadAction(action,reading.Timestamp);
    }
    void TickGamepad()
    {
        if(closing || !gamepadReady)return;
        string error=gamepadInput?.Error??"";
        if(error!=gamepadBackendError){gamepadBackendError=error;if(error.Length>0){PauseForGamepadChange("手柄监听出现问题，已暂停配音。");Log.Write("gamepad-error",error);}RefreshGamepadHints();}
        string context=GetGamepadContext();
        if(context!=gamepadContext){gamepadContext=context;ResetGamepadContext();gamepadNavigation?.ReleaseHighlight();RefreshGamepadHints();}
        if(!GamepadConnected || context.Length==0 || gamepadTest.IsChecked==true || KeyTesting)return;
        if(context!="game" && gamepadGate.Repeat(Stopwatch.GetTimestamp()) is string action)HandleGamepadAction(action,Stopwatch.GetTimestamp());
    }
    void OpenGamepadPanel()
    {
        if(branchMenu.IsVisible)
        {
            if(branchMenu.GamepadNavigation && Native.GetForegroundWindow()==branchMenu.Handle){DismissSmallMenu();RestoreGamepadGameFocus();}
            else{StopAutomatic("已打开手柄分支菜单，自动播放暂停。");branchMenu.SetGamepadNavigation(true);RefreshGamepadHints();}
            return;
        }
        if(expanded && Native.GetForegroundWindow()==new WindowInteropHelper(this).Handle){Collapse();gamepadNavigation?.ReleaseHighlight();return;}
        if(ListeningActive)Expand(listeningTab);else{Expand(StoryTab);BrowseCurrent();}
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);gamepadNavigation?.FocusDefault();
    }
    void RestoreGamepadGameFocus()
    {if(game!=null && Native.IsWindow(game.Handle))Native.SetForegroundWindow(game.Handle);}
    void CompleteGamepadPanelAction(int previousMenuEpoch,uint? deviceId)
    {
        if(closing || !GamepadConnected || gamepadLatest?.Device?.Id!=deviceId || gamepadTest.IsChecked==true || KeyTesting ||
            !branchMenu.IsVisible || branchMenu.Epoch==previousMenuEpoch)return;
        var foreground=Native.GetForegroundWindow();
        if(foreground!=new WindowInteropHelper(this).Handle && foreground!=branchMenu.Handle && foreground!=game?.Handle)return;
        branchMenu.SetGamepadNavigation(true);
        gamepadContext=GetGamepadContext();ResetGamepadContext();RefreshGamepadHints();
    }
    void HandleGamepadAction(string action,long timestamp)
    {
        string context=GetGamepadContext();if(context.Length==0)return;
        bool panel=context.StartsWith("panel:"),branch=context.StartsWith("branch:");
        if(action=="panel"){OpenGamepadPanel();return;}
        if(action=="next")
        {
            if(GamepadFollowAvailable && context=="game" && !expanded && !locating && !branchMenu.IsVisible && engine?.MenuWaiting!=true)
                RequestFollowNext(FollowInputSource.Gamepad,timestamp);
            return;
        }
        if(action is "up" or "down" or "left" or "right")
        {
            if(branch){if(action is "up" or "down")branchMenu.Move(action=="up"?-1:1);return;}
            if(panel)gamepadNavigation?.Move(action switch{"up"=>FocusNavigationDirection.Up,"down"=>FocusNavigationDirection.Down,"left"=>FocusNavigationDirection.Left,_=>FocusNavigationDirection.Right});
            return;
        }
        if(action=="back")
        {
            if(branch){DismissSmallMenu();RestoreGamepadGameFocus();}
            else if(panel && gamepadNavigation?.BackFromControl()!=true){CancelOcr();Collapse();gamepadNavigation?.ReleaseHighlight();}
            return;
        }
        if(action=="confirm")
        {
            if(branch)
            {
                if(!branchMenu.CanConfirm)return;
                ConfirmSmallBranch();
                if(branchMenu.IsVisible)branchMenu.SetGamepadNavigation(true);else RestoreGamepadGameFocus();
            }
            else if(panel)
            {
                int previousMenuEpoch=branchMenu.Epoch;
                uint? deviceId=gamepadLatest?.Device?.Id;
                if(gamepadNavigation?.ActivateFocused()!=true)
                {
                    if(BranchList.IsKeyboardFocusWithin)ConfirmBranch();
                    else if(CandidatesList.IsKeyboardFocusWithin)ConfirmCandidate();
                    else if(historyList.IsKeyboardFocusWithin)PreviewHistory();
                    else if(LinesList.IsKeyboardFocusWithin)ConfirmLine();
                    else gamepadNavigation?.FocusDefault();
                }
                // WPF 的 UIA Invoke 在 Input 优先级异步执行 Click；等它完成再衔接新菜单焦点。
                Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>CompleteGamepadPanelAction(previousMenuEpoch,deviceId));
            }
            RefreshGamepadHints();return;
        }
        if(action is "tabPrevious" or "tabNext")
        {
            if(!panel)return;
            var visible=Tabs.Items.OfType<TabItem>().Where(t=>t.IsEnabled && t.Visibility==Visibility.Visible).ToList();
            int index=visible.IndexOf((TabItem)Tabs.SelectedItem),next=(index+(action=="tabNext"?1:-1)+visible.Count)%visible.Count;
            Tabs.SelectedItem=visible[next];gamepadNavigation?.FocusDefault();return;
        }
        if(action=="bookmark")
        {if(panel){if(ListeningActive && Tabs.SelectedItem==listeningTab)AddListeningBookmark();else SaveBookmark();}return;}
        if(action=="automatic")
        {
            if(automaticRunning || automaticPreparingOwner!=null || automaticPendingOwner!=null)StopAutomatic("手柄已停止自动播放。");
            else if(!ListeningActive)_=BeginAutomatic();
            return;
        }
        if(ListeningActive)
        {
            if(action is "pause" or "replay" or "previous" or "manualNext")HandleListeningShortcut(action);
            return;
        }
        // 复用键盘/按钮的停止和过期定位处理，不建立第二套播放状态。
        HandleGameShortcut(action,true,timestamp);
        if(branchMenu.IsVisible && (panel || branch || action=="interactions"))branchMenu.SetGamepadNavigation(true);
        RefreshGamepadHints();
    }
}
