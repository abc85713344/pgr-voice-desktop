using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

/// <summary>UI-thread-only, window-scoped navigation without synthesizing input.</summary>
internal sealed class GamepadUiNavigation : IDisposable
{
    readonly Window scope;
    readonly KeyboardFocusChangedEventHandler focusChanged;
    AdornerLayer? highlightLayer;
    FocusAdorner? highlight;
    bool highlighting;
    bool disposed;

    public GamepadUiNavigation(Window scope)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        scope.VerifyAccess();
        focusChanged = OnFocusChanged;
        scope.AddHandler(Keyboard.GotKeyboardFocusEvent, focusChanged, true);
        scope.AddHandler(Keyboard.LostKeyboardFocusEvent, focusChanged, true);
        scope.LayoutUpdated += OnLayoutUpdated;
    }

    public bool FocusDefault()
    {
        VerifyAccess();
        if (disposed || !scope.IsVisible || !scope.IsEnabled) return false;
        highlighting = true;
        var current = FocusedControl();
        if (current != null) { ShowHighlight(current); return true; }
        var candidates = Candidates().ToList();
        // Prefer the selected page over window chrome (collapse/quit buttons).
        var page = Descendants(scope).OfType<TabControl>().FirstOrDefault(x => x.IsVisible)?.SelectedContent as DependencyObject;
        var target = candidates.FirstOrDefault(x => page != null && IsDescendantOf(x, page)) ?? candidates.FirstOrDefault();
        return target != null && Focus(target);
    }

    public bool Move(FocusNavigationDirection direction)
    {
        VerifyAccess();
        if (disposed || !scope.IsVisible || !scope.IsEnabled) return false;
        highlighting = true;
        var current = FocusedControl();
        if (current == null) return FocusDefault();
        if (current is ComboBox combo && combo.IsDropDownOpen)
        {
            if (direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down)
                SelectNext(combo, direction == FocusNavigationDirection.Down ? 1 : -1);
            ShowHighlight(combo);
            return true;
        }
        if (current is ListBox list && direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down)
        {
            SelectNext(list, direction == FocusNavigationDirection.Down ? 1 : -1);
            ShowHighlight(list);
            return true;
        }
        if (current is Slider slider && direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
        {
            double step = slider.SmallChange > 0 ? slider.SmallChange : (slider.Maximum - slider.Minimum) / 100;
            double sign = direction == FocusNavigationDirection.Right ? 1 : -1;
            if (slider.IsDirectionReversed) sign = -sign;
            slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(slider.Value + sign * step, slider.Minimum, slider.Maximum));
            ShowHighlight(slider);
            return true;
        }
        return MoveBetweenControls(current, direction);
    }

    public bool ActivateFocused()
    {
        VerifyAccess();
        if (disposed) return false;
        var current = FocusedControl();
        if (current == null) return false;
        highlighting = true;
        ShowHighlight(current);
        // The caller owns selection confirmation. Text fields must consume A without typing.
        if (current is ListBox) return false;
        if (current is TextBoxBase or PasswordBox or Slider) return true;
        if (current is ComboBox combo)
        {
            combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, !combo.IsDropDownOpen);
            if (!combo.IsDropDownOpen) Focus(combo);
            return true;
        }
        var peer = UIElementAutomationPeer.CreatePeerForElement(current);
        if (peer == null) return false;
        if (current is RadioButton && peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection)
        { selection.Select(); return true; }
        if (current is ToggleButton && peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
        { toggle.Toggle(); return true; }
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        { invoke.Invoke(); return true; }
        if (current is TabItem && peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider tab)
        { tab.Select(); return true; }
        if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
        {
            if (expand.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Expanded) expand.Collapse();
            else expand.Expand();
            return true;
        }
        return false;
    }

    public bool BackFromControl()
    {
        VerifyAccess();
        if (disposed) return false;
        var current = FocusedControl();
        if (current is ComboBox combo && combo.IsDropDownOpen)
        {
            combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            Focus(combo);
            return true;
        }
        if (current is TextBoxBase or PasswordBox)
        {
            var candidates = Candidates().ToList();
            int index = candidates.IndexOf(current);
            var target = Enumerable.Range(1, candidates.Count)
                .Select(offset => candidates[(index + offset + candidates.Count) % candidates.Count])
                .FirstOrDefault(candidate => candidate is not TextBoxBase and not PasswordBox);
            if (target == null || !Focus(target))
            {
                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(scope, null);
                RemoveHighlight();
            }
            return true;
        }
        return false;
    }

    public void ReleaseHighlight()
    {
        VerifyAccess();
        highlighting = false;
        RemoveHighlight();
    }

    public void Dispose()
    {
        VerifyAccess();
        if (disposed) return;
        disposed = true;
        scope.RemoveHandler(Keyboard.GotKeyboardFocusEvent, focusChanged);
        scope.RemoveHandler(Keyboard.LostKeyboardFocusEvent, focusChanged);
        scope.LayoutUpdated -= OnLayoutUpdated;
        ReleaseHighlight();
    }

    void VerifyAccess() => scope.VerifyAccess();

    Control? FocusedControl() => ControlFor(Keyboard.FocusedElement as DependencyObject);

    Control? ControlFor(DependencyObject? focused)
    {
        if (focused == null) return null;
        var ancestors = Ancestors(focused).ToList();
        // ComboBox popups have their own presentation source, but their containers identify the owner.
        var combo = ancestors.OfType<ComboBox>().FirstOrDefault();
        var item = ancestors.OfType<ComboBoxItem>().FirstOrDefault();
        combo ??= item == null ? null : ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
        if (combo != null) return Eligible(combo) ? combo : null;
        var control = ancestors.OfType<Control>().FirstOrDefault(IsNavigationControl);
        return control != null && Eligible(control) ? control : null;
    }

    bool Eligible(Control control) => Window.GetWindow(control) == scope && control.IsVisible &&
        control.IsEnabled && control.IsHitTestVisible && control.Focusable;

    static bool IsNavigationControl(Control control) => control is ButtonBase or ComboBox or ListBox or
        TextBoxBase or PasswordBox or Slider or TabItem or Expander;

    IEnumerable<Control> Candidates() => Descendants(scope).OfType<Control>()
        .Where(x => IsNavigationControl(x) && Eligible(x) && !Ancestors(Parent(x)).OfType<Control>()
            .Any(parent => parent is ComboBox or ListBox or Slider || parent is ButtonBase))
        .OrderBy(KeyboardNavigation.GetTabIndex);

    bool Focus(Control control)
    {
        if (!Eligible(control)) return false;
        control.BringIntoView();
        bool focused = control.Focus();
        if (focused) { highlighting = true; ShowHighlight(control); }
        return focused;
    }

    bool MoveBetweenControls(Control current, FocusNavigationDirection direction)
    {
        var candidates = Candidates().ToList();
        if (candidates.Count == 0) return false;
        int index = candidates.IndexOf(current);
        if (direction is FocusNavigationDirection.First or FocusNavigationDirection.Last)
            return Focus(direction == FocusNavigationDirection.First ? candidates[0] : candidates[^1]);
        if (direction is FocusNavigationDirection.Next or FocusNavigationDirection.Previous)
        {
            int next = (index + (direction == FocusNavigationDirection.Next ? 1 : -1) + candidates.Count) % candidates.Count;
            return candidates[next] != current && Focus(candidates[next]);
        }
        Rect origin = Bounds(current);
        if (origin.IsEmpty) return false;
        bool horizontal = direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right;
        double sign = direction is FocusNavigationDirection.Right or FocusNavigationDirection.Down ? 1 : -1;
        Control? target = null;
        double best = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            if (candidate == current) continue;
            Rect bounds = Bounds(candidate);
            if (bounds.IsEmpty) continue;
            double dx = bounds.X + bounds.Width / 2 - origin.X - origin.Width / 2;
            double dy = bounds.Y + bounds.Height / 2 - origin.Y - origin.Height / 2;
            double forward = (horizontal ? dx : dy) * sign;
            if (forward <= .5) continue;
            double cross = horizontal ? Math.Max(0, Math.Abs(dy) - (origin.Height + bounds.Height) / 2) :
                Math.Max(0, Math.Abs(dx) - (origin.Width + bounds.Width) / 2);
            double score = forward + cross * 4;
            if (score < best) { best = score; target = candidate; }
        }
        return target != null && Focus(target);
    }

    Rect Bounds(Control control)
    {
        if (!control.IsArrangeValid || control.ActualWidth <= 0 || control.ActualHeight <= 0) return Rect.Empty;
        try { return control.TransformToAncestor(scope).TransformBounds(new Rect(control.RenderSize)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    static void SelectNext(Selector selector, int step)
    {
        int index = selector.SelectedIndex;
        for (int next = index < 0 ? (step > 0 ? 0 : selector.Items.Count - 1) : index + step;
             next >= 0 && next < selector.Items.Count; next += step)
        {
            var container = selector.ItemContainerGenerator.ContainerFromIndex(next) as UIElement;
            var own = selector.Items[next] as UIElement;
            if (container is { IsEnabled: false } || own is { IsEnabled: false } ||
                container is { Visibility: Visibility.Collapsed or Visibility.Hidden } ||
                own is { Visibility: Visibility.Collapsed or Visibility.Hidden }) continue;
            selector.SetCurrentValue(Selector.SelectedIndexProperty, next);
            if (selector is ListBox list) list.ScrollIntoView(list.SelectedItem);
            else (container as FrameworkElement)?.BringIntoView();
            return;
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static DependencyObject? Parent(DependencyObject node)
    {
        if (node is FrameworkContentElement content) return content.Parent;
        var visualParent = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null;
        return visualParent ?? (node as FrameworkElement)?.Parent ?? LogicalTreeHelper.GetParent(node);
    }

    static IEnumerable<DependencyObject> Ancestors(DependencyObject? node)
    {
        while (node != null) { yield return node; node = Parent(node); }
    }

    static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor) => Ancestors(node).Contains(ancestor);

    void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (highlighting) ShowHighlight(ControlFor(e.NewFocus as DependencyObject));
    }

    void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (highlighting) ShowHighlight(FocusedControl());
    }

    void ShowHighlight(Control? control)
    {
        if (control == null || !Eligible(control)) { RemoveHighlight(); return; }
        if (highlight?.AdornedElement == control) return;
        RemoveHighlight();
        highlightLayer = AdornerLayer.GetAdornerLayer(control);
        if (highlightLayer == null) return;
        highlight = new FocusAdorner(control);
        highlightLayer.Add(highlight);
    }

    void RemoveHighlight()
    {
        if (highlight != null) highlightLayer?.Remove(highlight);
        highlight = null;
        highlightLayer = null;
    }

    sealed class FocusAdorner : Adorner
    {
        static readonly Pen Outline = new(Brushes.Black, 4);
        static readonly Pen Accent = new(new SolidColorBrush(Color.FromRgb(103, 232, 249)), 2);
        public FocusAdorner(UIElement element) : base(element) { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width < 4 || size.Height < 4) return;
            var rect = new Rect(2, 2, size.Width - 4, size.Height - 4);
            drawingContext.DrawRoundedRectangle(null, Outline, rect, 3, 3);
            drawingContext.DrawRoundedRectangle(null, Accent, rect, 3, 3);
        }
    }
}
