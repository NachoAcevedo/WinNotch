using System.Windows;
using System.Collections.Specialized;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinNotch.Views;
using Binding = System.Windows.Data.Binding;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using TabControl = System.Windows.Controls.TabControl;
using RadioButton = System.Windows.Controls.RadioButton;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace WinNotch.Helpers;

public sealed class CardSwipeController : IDisposable
{
    private readonly TabControl _tabs;
    private readonly Func<bool> _allowed;
    private readonly Action<bool> _dragStateChanged;
    private readonly TranslateTransform _offset = new();
    private Point? _start;
    private TouchDevice? _touch;
    public bool IsDragging { get; private set; }

    public CardSwipeController(TabControl tabs, Func<bool> allowed, Action<bool> dragStateChanged)
    {
        _tabs = tabs;
        _allowed = allowed;
        _dragStateChanged = dragStateChanged;
        tabs.PreviewMouseLeftButtonDown += MouseDown;
        tabs.PreviewMouseMove += MouseMove;
        tabs.PreviewMouseLeftButtonUp += MouseUp;
        tabs.LostMouseCapture += LostMouse;
        tabs.PreviewTouchDown += TouchDown;
        tabs.PreviewTouchMove += TouchMove;
        tabs.PreviewTouchUp += TouchUp;
        tabs.LostTouchCapture += LostTouch;
        tabs.PreviewMouseWheel += Wheel;
        ((INotifyCollectionChanged)tabs.Items).CollectionChanged += ItemsChanged;
        tabs.ApplyTemplate();
        BuildDots();
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildDots();
    private void BuildDots()
    {
        if (_tabs.Template.FindName("CardDots", _tabs) is not StackPanel dots) return;
        dots.Children.Clear();
        foreach (var tab in _tabs.Items.OfType<TabItem>())
        {
            var dot = new RadioButton { Style = (Style)_tabs.FindResource("CardDot") };
            dot.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(TabItem.IsSelected)) { Source = tab, Mode = BindingMode.TwoWay });
            dot.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(TabItem.Header)) { Source = tab });
            dot.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(TabItem.Visibility)) { Source = tab });
            dots.Children.Add(dot);
        }
    }
    private bool CanStart(DependencyObject? target)
    {
        if (!_allowed()) return false;
        while (target != null && target != _tabs)
        {
            if (target is ButtonBase or TextBoxBase or RangeBase or ScrollViewer or ShelfView or TabPanel) return false;
            target = target is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return true;
    }
    private FrameworkElement? Body => _tabs.Template.FindName("CardBody", _tabs) as FrameworkElement;
    private void Move(Point point)
    {
        if (_start == null) return;
        if (!_allowed()) { Finish(0); return; }
        var delta = point - _start.Value;
        if (!IsDragging)
        {
            if (Math.Abs(delta.Y) > 14 && Math.Abs(delta.Y) > Math.Abs(delta.X)) { _start = null; _touch = null; return; }
            if (Math.Abs(delta.X) < 14 || Math.Abs(delta.X) < Math.Abs(delta.Y) * 1.4) return;
            IsDragging = true;
            _dragStateChanged(true);
            if (_touch != null) _touch.Capture(_tabs, CaptureMode.SubTree);
            else Mouse.Capture(_tabs, CaptureMode.SubTree);
        }
        if (Body is { } body && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            body.RenderTransform = _offset;
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _offset.X = Math.Clamp(delta.X * 0.45, -55, 55);
        }
    }
    private void Finish(double distance)
    {
        bool wasDragging = IsDragging;
        IsDragging = false;
        _start = null;
        var touch = _touch;
        _touch = null;
        if (Mouse.Captured == _tabs) Mouse.Capture(null);
        if (touch?.Captured == _tabs) touch.Capture(null);
        bool changed = wasDragging && Math.Abs(distance) >= 48 && Step(distance < 0 ? 1 : -1);
        Animate(changed ? (distance < 0 ? 28 : -28) : _offset.X);
        if (wasDragging) _dragStateChanged(false);
    }
    private bool Step(int direction)
    {
        if (!_allowed()) return false;
        var visible = _tabs.Items.OfType<TabItem>().Where(tab => tab.Visibility == Visibility.Visible && tab.IsEnabled).ToList();
        int index = _tabs.SelectedItem is TabItem selected ? visible.IndexOf(selected) : -1;
        if (index < 0 || index + direction < 0 || index + direction >= visible.Count) return false;
        _tabs.SelectedItem = visible[index + direction];
        return true;
    }
    private void Animate(double from)
    {
        if (Body is not { } body) return;
        body.RenderTransform = _offset;
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            _offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice == null && CanStart(e.OriginalSource as DependencyObject)) _start = e.GetPosition(_tabs);
    }
    private void MouseMove(object sender, MouseEventArgs e)
    {
        if (_touch != null || e.StylusDevice != null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { if (_start != null) Finish(0); return; }
        Move(e.GetPosition(_tabs));
        if (IsDragging) e.Handled = true;
    }
    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_touch != null || e.StylusDevice != null || _start == null) return;
        double distance = e.GetPosition(_tabs).X - _start.Value.X;
        bool handled = IsDragging;
        Finish(distance);
        if (handled) e.Handled = true;
    }
    private void LostMouse(object sender, MouseEventArgs e) { if (IsDragging && _touch == null) Finish(0); }
    private void TouchDown(object? sender, TouchEventArgs e)
    {
        if (_touch == null && CanStart(e.OriginalSource as DependencyObject)) { _touch = e.TouchDevice; _start = e.GetTouchPoint(_tabs).Position; }
    }
    private void TouchMove(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice != _touch) return;
        Move(e.GetTouchPoint(_tabs).Position);
        if (IsDragging) e.Handled = true;
    }
    private void TouchUp(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice != _touch) return;
        double distance = _start == null ? 0 : e.GetTouchPoint(_tabs).Position.X - _start.Value.X;
        bool handled = IsDragging;
        Finish(distance);
        if (handled) e.Handled = true;
    }
    private void LostTouch(object? sender, TouchEventArgs e) { if (IsDragging && e.TouchDevice == _touch) Finish(0); }
    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || !CanStart(e.OriginalSource as DependencyObject)) return;
        if (Step(e.Delta < 0 ? 1 : -1)) Animate(e.Delta < 0 ? 28 : -28);
        e.Handled = true;
    }
    public void Dispose()
    {
        Finish(0);
        _tabs.PreviewMouseLeftButtonDown -= MouseDown;
        _tabs.PreviewMouseMove -= MouseMove;
        _tabs.PreviewMouseLeftButtonUp -= MouseUp;
        _tabs.LostMouseCapture -= LostMouse;
        _tabs.PreviewTouchDown -= TouchDown;
        _tabs.PreviewTouchMove -= TouchMove;
        _tabs.PreviewTouchUp -= TouchUp;
        _tabs.LostTouchCapture -= LostTouch;
        _tabs.PreviewMouseWheel -= Wheel;
        ((INotifyCollectionChanged)_tabs.Items).CollectionChanged -= ItemsChanged;
    }
}

