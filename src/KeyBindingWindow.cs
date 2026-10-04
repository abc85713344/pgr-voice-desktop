using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PgrVoice;

public sealed class KeyBindingWindow : Window
{
    public sealed record Choice(Key Key, string Label) { public override string ToString() => Label; }
    readonly Dictionary<string, string> bindings;
    readonly Dictionary<string, string> labels;
    readonly string action;
    readonly TextBlock status;
    readonly ComboBox choices;
    public TextBox CaptureField { get; }
    public Button SaveButton { get; }
    public Key? SelectedKey { get; private set; }

    public KeyBindingWindow(Window owner, string action, Dictionary<string,string> bindings, Dictionary<string,string> labels)
    {
        Owner = owner; this.action = action; this.bindings = bindings; this.labels = labels;
        Title = "自定义按键 · " + labels[action]; Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = Theme.Brush("Bg");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = labels[action], FontSize = 20, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "点击下方输入框，按下要使用的键；也可以从列表选择。点击保存才生效。", Margin = new Thickness(0,12,0,8) });
        CaptureField = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, TextAlignment = TextAlignment.Center, FontSize = 22, MinHeight = 50, ToolTip = "点击这里后按一个键" };
        // 此处录入的是按键，不是文字；避免中文输入法拦截字母和 Esc。
        InputMethod.SetIsInputMethodEnabled(CaptureField, false);
        AutomationProperties.SetAutomationId(CaptureField, "KeyCapture"); panel.Children.Add(CaptureField);
        CaptureField.PreviewKeyDown += CaptureKey;
        panel.Children.Add(new TextBlock { Text = "或直接选择按键", Margin = new Thickness(0,14,0,5) });
        choices = new ComboBox { ItemsSource = Enum.GetValues<Key>().Distinct().Where(IsAllowed).Select(k=>new Choice(k,Display(k))).ToList() };
        AutomationProperties.SetAutomationId(choices, "KeyChoices"); panel.Children.Add(choices);
        choices.SelectionChanged += (_,_) => { if (choices.SelectedItem is Choice choice) Pick(choice.Key); };
        status = new TextBlock { MinHeight = 44, Margin = new Thickness(0,12,0,8), Foreground = Theme.Brush("BranchAccent") }; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = new Button { Content = "取消", MinWidth = 82, IsCancel = true }; buttons.Children.Add(cancel);
        SaveButton = new Button { Content = "保存", MinWidth = 82, Style=(Style)FindResource("PrimaryButton"), Margin=new Thickness(8,0,0,0) };
        AutomationProperties.SetAutomationId(SaveButton, "SaveKey"); buttons.Children.Add(SaveButton);
        SaveButton.Click += (_,_) => { if (SelectedKey.HasValue && SaveButton.IsEnabled) DialogResult = true; };
        PreviewKeyDown += (_,e) => { if (EventKey(e) == Key.Escape) { e.Handled = true; DialogResult = false; } };
        if (Enum.TryParse<Key>(bindings[action],out var initial) && IsAllowed(initial)) Pick(initial);
        Loaded += (_,_) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { Activate(); CaptureField.Focus(); Keyboard.Focus(CaptureField); });
    }
    public static string Display(Key key) => key switch
    {
        Key.None => "未设置", Key.Space => "空格", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Back => "Backspace",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "小键盘 " + ((int)key - (int)Key.NumPad0),
        _ => key.ToString()
    };
    static bool IsAllowed(Key key) => key is not (Key.None or Key.Cancel or Key.Escape or Key.Enter or Key.Up or Key.Down or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed);
    static Key EventKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
    void CaptureKey(object sender, KeyEventArgs e)
    {
        if (EventKey(e) == Key.Escape) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        Key key = EventKey(e);
        if (!IsAllowed(key) || Keyboard.Modifiers != ModifierKeys.None)
        {
            SelectedKey = null; SaveButton.IsEnabled = false;
            status.Text = "请选择一个单键；↑↓、回车和 Esc 保留给菜单操作。"; return;
        }
        Pick(key);
    }
    public static bool Matches(string? binding, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        modifiers == ModifierKeys.None && key != Key.None && Enum.TryParse<Key>(binding, out var assigned) && assigned == key;
    void Pick(Key key)
    {
        SelectedKey = key; CaptureField.Text = Display(key);
        var conflict = bindings.FirstOrDefault(b => b.Key != action && Matches(b.Value, key));
        SaveButton.IsEnabled = conflict.Key == null;
        status.Text = conflict.Key == null ? "点击“保存”后生效。取消会保留原按键。" : "这个键已用于“" + labels.GetValueOrDefault(conflict.Key,conflict.Key) + "”，请换一个。";
    }
}
