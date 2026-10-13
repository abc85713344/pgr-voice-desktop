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
    readonly StackPanel table = new();
    public Button SaveButton { get; }
    public Key? SelectedKey { get; private set; }

    public KeyBindingWindow(Window owner, string action, Dictionary<string,string> bindings, Dictionary<string,string> labels)
    {
        Owner = owner; this.action = action; this.bindings = bindings; this.labels = labels;
        Title = "自定义按键 · " + labels[action]; Width = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Theme.ApplyDialog(this); FontSize = 14;
        // 07-B：左侧录入（档案纸面 + 键帽 +「原按键 →」对照）/ 右侧只读「当前键位」表（正在改的行绯红边，被占用行绯红虚线）。
        var outer = new StackPanel { Margin = new Thickness(22, 18, 22, 18) }; Content = outer;
        outer.Children.Add(Theme.Title(labels[action], "ASSIGN KEY", new Thickness(0)));
        var columns = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(260) });
        outer.Children.Add(columns);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; columns.Children.Add(panel);
        panel.Children.Add(new TextBlock { Text = "点击下方输入框，按下要使用的键；也可以从列表选择。点击保存才生效。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) });
        CaptureField = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, TextAlignment = TextAlignment.Center, FontSize = 26, MinHeight = 54, MinWidth = 150, ToolTip = "点击这里后按一个键", FontFamily = Theme.Font("NumberFont"),
            Background = Hud.Solid("#2E363F"), Foreground = Brushes.White, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, CaretBrush = Brushes.Transparent };
        var keycap = new Border { BorderBrush = Theme.Brush("Accent"), BorderThickness = new Thickness(0, 3, 0, 0), Child = CaptureField };
        var keycapBase = new Border { BorderBrush = Hud.Solid("#1B2026"), BorderThickness = new Thickness(0, 0, 0, 5), Child = keycap, HorizontalAlignment = HorizontalAlignment.Center };
        var compare = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 18, 14, 18) };
        var original = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        original.Children.Add(new TextBlock { Text = "原按键", FontSize = 11, Foreground = Theme.Brush("MutedText"), HorizontalAlignment = HorizontalAlignment.Center });
        original.Children.Add(Archive.Keycap(Enum.TryParse<Key>(bindings[action], out var was) ? Display(was) : "未设置", true));
        compare.Children.Add(original);
        compare.Children.Add(new TextBlock { Text = "→", FontSize = 20, Foreground = Theme.Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 0) });
        compare.Children.Add(keycapBase);
        var paper = new ArchivePaper { Child = compare, ShowRail = false, EmblemSize = 96, EmblemRight = -10, EmblemBottom = -14, TideHeight = 26, ShowOrbits = false };
        panel.Children.Add(new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Child = paper });
        CaptureField.PreviewKeyDown += CaptureKey;
        panel.Children.Add(new TextBlock { Text = "或直接选择按键", Margin = new Thickness(0,14,0,5) });
        choices = new ComboBox { ItemsSource = Enum.GetValues<Key>().Distinct().Where(IsAllowed).Select(k=>new Choice(k,Display(k))).ToList() };
        AutomationProperties.SetAutomationId(choices, "KeyChoices"); panel.Children.Add(choices);
        choices.SelectionChanged += (_,_) => { if (choices.SelectedItem is Choice choice) Pick(choice.Key); };
        status = new TextBlock { MinHeight = 44, Margin = new Thickness(0,12,0,8), Foreground = Theme.Brush("BranchAccent"), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var tableArea = new StackPanel(); Grid.SetColumn(tableArea, 1); columns.Children.Add(tableArea);
        tableArea.Children.Add(Archive.Section("当前键位", "CURRENT KEYS"));
        tableArea.Children.Add(new Border { BorderBrush = Theme.Brush("Stroke"), BorderThickness = new Thickness(1), Background = Brushes.White, Child = table });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; outer.Children.Add(buttons);
        var cancel = new Button { Content = "取消", MinWidth = 82, IsCancel = true }; buttons.Children.Add(cancel);
        SaveButton = new Button { Content = "保存", MinWidth = 82, Style=(Style)FindResource("PrimaryButton"), Margin=new Thickness(8,0,0,0) };
        AutomationProperties.SetAutomationId(SaveButton, "SaveKey"); buttons.Children.Add(SaveButton);
        SaveButton.Click += (_,_) => { if (SelectedKey.HasValue && SaveButton.IsEnabled) DialogResult = true; };
        PreviewKeyDown += (_,e) => { if (EventKey(e) == Key.Escape) { e.Handled = true; DialogResult = false; } };
        if (Enum.TryParse<Key>(bindings[action],out var initial) && IsAllowed(initial)) Pick(initial); else RenderTable(null);
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
            status.Text = "请选择一个单键；↑↓、回车和 Esc 保留给菜单操作。"; RenderTable(null); return;
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
        RenderTable(conflict.Key);
    }
    // 只读键位表：行内无装饰；正在改的行 = 绯红实边 +「原键 → ?」，被占用行 = 绯红虚线 + 红键签 +「已占用」。
    void RenderTable(string? conflict)
    {
        table.Children.Clear();
        int index = 0;
        foreach (var (key, label) in labels.Where(l => bindings.ContainsKey(l.Key)).Select(l => (l.Key, l.Value)))
        {
            bool editing = key == action, taken = key == conflict;
            string current = Enum.TryParse<Key>(bindings[key], out var k) ? Display(k) : bindings[key];
            var row = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
            var number = new TextBlock { Text = (++index).ToString("00"), FontFamily = Theme.Font("NumberFont"), FontSize = 11, Foreground = Theme.Brush("MutedText"), Width = 22, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(number, Dock.Left); row.Children.Add(number);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (editing)
            {
                right.Children.Add(Archive.Keycap(current, true));
                right.Children.Add(new TextBlock { Text = " → " + (SelectedKey.HasValue && conflict == null ? Display(SelectedKey.Value) : "?"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("BranchAccent"), VerticalAlignment = VerticalAlignment.Center });
            }
            else if (taken)
            {
                var tag = Archive.Tag("已占用", "red"); tag.Margin = new Thickness(0, 0, 6, 0); right.Children.Add(tag);
                right.Children.Add(new Border { Background = Theme.Brush("BranchAccent"), Padding = new Thickness(6, 1, 6, 2), Child = new TextBlock { Text = current, Foreground = Brushes.White, FontSize = 11.5, FontFamily = Theme.Font("NumberFont") } });
            }
            else right.Children.Add(Archive.Keycap(current, true));
            DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = editing ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var cell = new Grid { Background = editing ? Hud.Solid("#FBEFF1") : index % 2 == 0 ? Hud.Solid("#F6F7F9") : Brushes.White };
            cell.Children.Add(row);
            if (editing) cell.Children.Add(new Border { BorderBrush = Theme.Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), IsHitTestVisible = false });
            if (taken) cell.Children.Add(new System.Windows.Shapes.Rectangle { Stroke = Theme.Brush("Accent"), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false });
            table.Children.Add(cell);
        }
    }
}
