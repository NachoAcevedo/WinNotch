using System.Windows;
using System.Windows.Controls;
using WinNotch.Plugins;
using WinNotch.Views;

namespace PersonalCards;

[WinNotchPlugin("personal.ui.cardcontrols", "BetterUI", "1.0.0", "Nacho_114")]
public sealed class CardControlsPlugin : PluginBase
{
    private static CardControlsPlugin? _active;
    private readonly EscapeKeyService _escapeKey = new();
    private readonly Dictionary<TabControl, ResourceDictionary> _applied = new();

    static CardControlsPlugin()
    {
        EventManager.RegisterClassHandler(typeof(NotchWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                    _active?.Apply((NotchWindow)sender);
            }));
    }

    public override string Id => "personal.ui.cardcontrols";
    public override string Name => "BetterUI";
    public override string Version => "1.0.0";
    public override string Author => "Nacho_114";
    public override string Description => "Controles coherentes para las tarjetas del notch.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();
        Application.Current.Dispatcher.Invoke(() =>
        {
            _active = this;
            _escapeKey.Pressed -= RevealForGaming;
            _escapeKey.Pressed += RevealForGaming;
            if (RevealWindow != null) _escapeKey.Start();
            foreach (var window in Application.Current.Windows.OfType<NotchWindow>())
                Apply(window);
        });
    }

    public override Task OnDisableAsync()
    {
        Application.Current.Dispatcher.Invoke(RemoveStyles);
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        Application.Current.Dispatcher.Invoke(RemoveStyles);
        return base.ShutdownAsync();
    }

    // The same plugin can load on upstream builds without the fork's reveal action.
    private static readonly Action<NotchWindow>? RevealWindow =
        typeof(NotchWindow).GetMethod("RevealForGaming", Type.EmptyTypes)?.CreateDelegate<Action<NotchWindow>>();

    private static void RevealForGaming()
    {
        if (RevealWindow == null) return;
        foreach (var window in Application.Current.Windows.OfType<NotchWindow>())
            RevealWindow(window);
    }

    private void Apply(NotchWindow window)
    {
        if (window.FindName("CardTabs") is not TabControl tabs || _applied.ContainsKey(tabs)) return;

        var styles = new ResourceDictionary
        {
            Source = new Uri("/BetterUI.CardControls;component/CardStyles.xaml", UriKind.Relative)
        };
        tabs.Resources.MergedDictionaries.Add(styles);
        _applied.Add(tabs, styles);
        window.Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is not NotchWindow window) return;
        window.Closed -= OnWindowClosed;
        if (window.FindName("CardTabs") is TabControl tabs && _applied.Remove(tabs, out var styles))
            tabs.Resources.MergedDictionaries.Remove(styles);
    }

    private void RemoveStyles()
    {
        _escapeKey.Pressed -= RevealForGaming;
        _escapeKey.Dispose();
        if (ReferenceEquals(_active, this)) _active = null;
        foreach (var (tabs, styles) in _applied)
        {
            tabs.Resources.MergedDictionaries.Remove(styles);
            if (Window.GetWindow(tabs) is { } window)
                window.Closed -= OnWindowClosed;
        }
        _applied.Clear();
    }
}
