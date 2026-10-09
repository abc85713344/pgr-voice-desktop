using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public partial class MainWindow
{
    // 返回 true 只停止窗口快捷键路由；除关闭下拉外，按键仍交给原控件处理。
    bool RouteSettingsControlKey(KeyEventArgs e, Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return false;
        var control = SettingsKeyControl(e.OriginalSource as DependencyObject);
        if (control is ComboBox combo)
        {
            if (key == Key.Escape && combo.IsDropDownOpen)
            {
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
                combo.Focus();
                e.Handled = true;
                return true;
            }
            // 非编辑下拉也有原生文字检索；改绑字母不能抢走这个输入。
            return IsSettingsNavigationKey(key) || key is Key.Back or Key.Delete ||
                key is >= Key.A and <= Key.Z || key is >= Key.D0 and <= Key.D9 ||
                key is >= Key.NumPad0 and <= Key.Divide || key is >= Key.Oem1 and <= Key.OemBackslash;
        }
        return control is Slider && IsSettingsNavigationKey(key);
    }

    static bool IsSettingsNavigationKey(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or
        Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space or Key.Enter or Key.Tab;

    static Control? SettingsKeyControl(DependencyObject? node)
    {
        var visited = new HashSet<DependencyObject>();
        while (node != null && visited.Add(node))
        {
            if (node is ComboBox or Slider) return (Control)node;
            if (node is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner)
                return owner;
            var visualParent = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null;
            node = visualParent ?? (node as FrameworkContentElement)?.Parent ??
                (node as FrameworkElement)?.Parent ?? (node as FrameworkElement)?.TemplatedParent ?? LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
