using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    sealed class ListeningRechoiceMenuBinding(ContextMenu menu, ListeningSession owner, long version, long epoch)
    {
        public ContextMenu Menu { get; } = menu;
        public ListeningSession Owner { get; } = owner;
        public long Version { get; } = version;
        public long Epoch { get; } = epoch;
        public bool Cancelled { get; set; }
        public int Cursor { get; set; }
    }
    ListeningRechoiceMenuBinding? listeningRechoiceMenuBinding;
    long listeningRechoiceMenuEpoch;

    void PrepareListeningRechoiceGamepad(ContextMenu menu, ListeningSession owner, long version)
    {
        if (listeningRechoiceMenuBinding is { } previous) CloseListeningRechoiceMenu(previous, true);
        var binding = new ListeningRechoiceMenuBinding(menu, owner, version, ++listeningRechoiceMenuEpoch);
        listeningRechoiceMenuBinding = binding;
        menu.Opened += (_, _) =>
        {
            if (!IsCurrentListeningRechoiceMenu(menu, owner, version)) { CloseListeningRechoiceMenu(binding, true); return; }
            ResetGamepadContext();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (menu.IsOpen && IsCurrentListeningRechoiceMenu(menu, owner, version)) FocusListeningRechoiceItem(binding, 0);
            }));
        };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseListeningRechoiceMenu(binding, true);
        };
        menu.Closed += (_, _) =>
        {
            // WPF 原生菜单的关闭与 Click 在同一次分发内完成；等这次原生 Click 完成再使旧菜单失效。
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                binding.Cancelled = true;
                if (!ReferenceEquals(listeningRechoiceMenuBinding, binding)) return;
                listeningRechoiceMenuBinding = null;
                ResetGamepadContext();
                if (expanded && Tabs.SelectedItem == listeningTab) listeningRechoose.Focus();
            }));
        };
    }

    bool IsCurrentListeningRechoiceMenu(ContextMenu menu, ListeningSession owner, long version) =>
        listeningRechoiceMenuBinding is { Cancelled: false } binding && ReferenceEquals(binding.Menu, menu)
        && ReferenceEquals(binding.Owner, owner) && ReferenceEquals(owner, listeningSession)
        && binding.Version == version && version == listeningChoiceUiVersion
        && ListeningActive && expanded && Tabs.SelectedItem == listeningTab;

    string? ListeningRechoiceGamepadContext(IntPtr foreground)
    {
        var binding = listeningRechoiceMenuBinding;
        if (binding?.Menu.IsOpen != true) return null;
        var popupHandle = (PresentationSource.FromVisual(binding.Menu) as HwndSource)?.Handle ?? IntPtr.Zero;
        bool ownsForeground = foreground == new WindowInteropHelper(this).Handle
            || popupHandle != IntPtr.Zero && foreground == popupHandle;
        return ownsForeground ? "listening-rechoice:" + binding.Epoch : "";
    }

    void CloseListeningRechoiceMenu(ListeningRechoiceMenuBinding binding, bool cancelled)
    {
        if (cancelled) binding.Cancelled = true;
        binding.Menu.IsOpen = false;
        if (cancelled && ReferenceEquals(listeningRechoiceMenuBinding, binding))
        {
            listeningRechoiceMenuBinding = null;
            ResetGamepadContext();
            if (expanded && Tabs.SelectedItem == listeningTab) listeningRechoose.Focus();
        }
    }

    void FocusListeningRechoiceItem(ListeningRechoiceMenuBinding binding, int index)
    {
        var items = binding.Menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled && i.Visibility == Visibility.Visible).ToArray();
        if (items.Length == 0) return;
        binding.Cursor = Math.Clamp(index, 0, items.Length - 1);
        items[binding.Cursor].BringIntoView(); items[binding.Cursor].Focus();
    }

    void HandleListeningRechoiceGamepad(string action)
    {
        var binding = listeningRechoiceMenuBinding;
        if (binding?.Menu.IsOpen != true) return;
        if (!IsCurrentListeningRechoiceMenu(binding.Menu, binding.Owner, binding.Version))
        { CloseListeningRechoiceMenu(binding, true); return; }
        if (action is "back" or "panel") { CloseListeningRechoiceMenu(binding, true); return; }
        var items = binding.Menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled && i.Visibility == Visibility.Visible).ToArray();
        if (items.Length == 0) return;
        int focused = Array.FindIndex(items, i => i.IsKeyboardFocusWithin);
        if (focused >= 0) binding.Cursor = focused;
        if (action is "up" or "left" or "down" or "right")
        {
            FocusListeningRechoiceItem(binding, binding.Cursor + (action is "up" or "left" ? -1 : 1));
            return;
        }
        if (action != "confirm") return;
        // 与鼠标共用现有的稳定菜单键校验；只打开待选点，尚不选择路线或播放。
        items[Math.Clamp(binding.Cursor, 0, items.Length - 1)].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        CloseListeningRechoiceMenu(binding, true);
    }
}
