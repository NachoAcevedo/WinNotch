using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using WinNotch.Helpers;
using WinNotch.Models;
using WinNotch.Plugins;
using WinNotch.Views;

namespace PersonalCards;

/// <summary>
/// Hace que el notch cerrado se adapte al contenido visible (reloj, música, batería,
/// plugins) en lugar de usar el ancho fijo de 200 px (220 px al asomarse).
/// Escucha el resorte de ancho de cada NotchWindow (evento ValueChanged, que se
/// dispara en cada cuadro de la animación) y, si el destino es uno de esos anchos
/// fijos, lo reemplaza por el ancho medido del contenido.
/// No aplica al modo "Notch mínimo" ni al notch acoplado a los costados.
/// </summary>
[WinNotchPlugin("personal.notch.autowidth", "Notch adaptable", "1.0.0", "Personal")]
public sealed class NotchAutoWidthPlugin : PluginBase
{
    private const double FixedClosedWidth = 200.0;
    private const double FixedPeekWidth = 220.0;
    private const double PeekExtra = 20.0;
    // WinNotch ubica el contenido dentro del notch descontando las curvas superior (6) e
    // inferior (14) de cada lado: 40 px en total. Más 6 px de aire parejo a cada lado.
    private const double SidePadding = 40.0 + 12.0;
    private static readonly Thickness ContentMargin = new Thickness(4, 0, 4, 0);
    private const double MinWidth = 100.0;
    private static readonly Thickness ClosedMargin = new Thickness(0, -2, 0, -10);

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly FieldInfo? WidthSpringField = typeof(NotchWindow).GetField("_widthSpring", Flags);
    private static readonly FieldInfo? VmField = typeof(NotchWindow).GetField("_vm", Flags);
    private static readonly FieldInfo? DockField = typeof(NotchWindow).GetField("_dock", Flags);
    private static readonly FieldInfo? SettingsField = typeof(NotchWindow).GetField("_settings", Flags);
    private static readonly FieldInfo? ClosedContentField = typeof(NotchWindow).GetField("ClosedContent", Flags);
    private static readonly FieldInfo? HudField = typeof(NotchWindow).GetField("HudOverlay", Flags);
    private static readonly FieldInfo? VerticalHudField = typeof(NotchWindow).GetField("VerticalHudOverlay", Flags);
    private static readonly FieldInfo? ContentGridField = typeof(NotchWindow).GetField("ContentGrid", Flags);
    private static readonly MethodInfo? RefreshMethod = typeof(NotchWindow).GetMethod("RefreshOverlayPresentation", Flags);
    private static readonly FieldInfo? TargetField = typeof(SpringAnimator).GetField("_targetValue", Flags);

    private readonly Dictionary<NotchWindow, Action<double>> _handlers = new();
    private readonly HashSet<NotchWindow> _contentHooked = new();
    private readonly Dictionary<NotchWindow, double> _lastWidth = new();
    private DispatcherTimer? _contentTimer;
    private bool _active;

    public override string Id => "personal.notch.autowidth";
    public override string Name => "Notch adaptable";
    public override string Version => "1.0.0";
    public override string Author => "Personal";
    public override string Description => "El notch cerrado se ajusta al contenido visible en lugar de reservar un ancho fijo.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();

        if (WidthSpringField == null || VmField == null || DockField == null || SettingsField == null
            || ClosedContentField == null || RefreshMethod == null || TargetField == null)
        {
            Context?.Log("Notch adaptable: la versión de WinNotch no es compatible", PluginLogLevel.Error);
            return;
        }

        _active = true;
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var window in Application.Current.Windows.OfType<NotchWindow>())
            {
                Hook(window);
                Reflow(window);
            }
        });
    }

    public override Task OnDisableAsync()
    {
        _active = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var (window, handler) in _handlers)
            {
                if (WidthSpringField!.GetValue(window) is SpringAnimator spring)
                {
                    spring.ValueChanged -= handler;
                }
                Reflow(window);
            }
            _handlers.Clear();
        });
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        _active = false;
        _contentTimer?.Stop();
        return base.ShutdownAsync();
    }

    private void Hook(NotchWindow window)
    {
        if (!_handlers.ContainsKey(window) && WidthSpringField!.GetValue(window) is SpringAnimator spring)
        {
            Action<double> handler = _ => AdjustTarget(window, spring);
            spring.ValueChanged += handler;
            _handlers[window] = handler;
            HookHudWhileOpen(window);
        }

        // Cuando cambia lo que se muestra (ej. empieza o se pausa la música), re-anima.
        // Se revisa periódicamente y solo con el notch cerrado y quieto: engancharse a los
        // cambios de tamaño falla si el cambio coincide con otra animación, y re-animar
        // por el recorte durante la animación reinicia el resorte en bucle (el notch tiembla).
        _contentHooked.Add(window);
        BalanceMargins(window);
        if (_contentTimer == null)
        {
            _contentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500.0) };
            _contentTimer.Tick += (_, _) => CheckContentWidth();
            _contentTimer.Start();
        }
    }

    /// <summary>
    /// Con el notch abierto no se muestran avisos (música, volumen, Caps Lock): WinNotch los
    /// muestra igual e intenta achicar el notch abierto al tamaño del aviso, y queda un bloque
    /// gigante con el aviso adentro. Se apagan al abrir y al cerrar vuelven a quedar como los
    /// deja WinNotch (el horizontal activo con el notch arriba, el vertical con el notch al costado).
    /// </summary>
    private void HookHudWhileOpen(NotchWindow window)
    {
        if (VmField!.GetValue(window) is not { } vm
            || HudField?.GetValue(window) is not HudOverlay hud
            || VerticalHudField?.GetValue(window) is not HudOverlay verticalHud)
        {
            return;
        }

        ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (!_active || e.PropertyName != nameof(NotchViewModel.NotchState))
            {
                return;
            }
            bool open = vm is NotchViewModel { NotchState: NotchState.Open };
            bool top = DockField!.GetValue(window) is NotchDock.Top;
            hud.IsActive = !open && top;
            verticalHud.IsActive = !open && !top;
        };
    }

    private void CheckContentWidth()
    {
        if (!_active)
        {
            return;
        }

        foreach (var window in _contentHooked)
        {
            if (VmField!.GetValue(window) is not NotchViewModel { NotchState: NotchState.Closed }
                || WidthSpringField!.GetValue(window) is not SpringAnimator { IsAnimating: false }
                || DockField!.GetValue(window) is not NotchDock.Top
                || SettingsField!.GetValue(window) is AppSettings { AlwaysShowMinimum: true })
            {
                continue;
            }

            BalanceMargins(window);
            double needed = MeasureContent(window);
            if (!_lastWidth.TryGetValue(window, out double last) || Math.Abs(needed - last) > 1.0)
            {
                Reflow(window);
            }
        }
    }

    /// <summary>
    /// Centra el contenido del notch cerrado: WinNotch usa márgenes asimétricos (música 6/2,
    /// reloj 8/2) y deja 6 px a la izquierda para el panel de plugins aunque esté vacío, así
    /// que todo queda corrido a la derecha y el visualizador pegado al borde.
    /// </summary>
    private static void BalanceMargins(NotchWindow window)
    {
        if (ClosedContentField!.GetValue(window) is not Grid closed)
        {
            return;
        }

        // El sobrante de ancho se reparte a ambos lados en lugar de quedar a la derecha.
        if (closed.HorizontalAlignment != HorizontalAlignment.Center)
        {
            closed.HorizontalAlignment = HorizontalAlignment.Center;
        }

        // WinNotch ubica el contenido entre la curva superior (6) y la inferior (14): le quedan
        // 10 px de alto, 4 por encima del centro, y la carátula sale recortada. Se agranda el
        // área a 4 px de cada borde del notch (márgenes negativos) y el área de contenido deja
        // de recortar mientras el notch está cerrado (ver AdjustTarget).
        if (ContentGridField?.GetValue(window) is Grid contentGrid && contentGrid.ClipToBounds)
        {
            contentGrid.ClipToBounds = false;
        }
        if (closed.Margin != ClosedMargin)
        {
            closed.Margin = ClosedMargin;
        }
        if (closed.RenderTransform is TranslateTransform)
        {
            closed.RenderTransform = Transform.Identity;
        }

        foreach (var child in closed.Children.OfType<FrameworkElement>())
        {
            switch (child.Name)
            {
                case "PluginClosedPanel" when child is Panel panel:
                    var margin = panel.Children.Count == 0 ? new Thickness(0) : new Thickness(6, 0, 0, 0);
                    if (panel.Margin != margin)
                    {
                        panel.Margin = margin;
                    }
                    break;
                case "LiveActivity":
                case "ClockWidget":
                    if (child.Margin != ContentMargin)
                    {
                        child.Margin = ContentMargin;
                    }
                    break;
            }
        }
    }

    /// <summary>Pide a la ventana recalcular su tamaño; el destino lo corrige AdjustTarget.</summary>
    private void Reflow(NotchWindow window)
    {
        try
        {
            RefreshMethod!.Invoke(window, null);
        }
        catch (Exception ex)
        {
            Context?.Log("Notch adaptable: " + ex.Message, PluginLogLevel.Warning);
        }
    }

    private void AdjustTarget(NotchWindow window, SpringAnimator spring)
    {
        if (!_active || TargetField!.GetValue(spring) is not double target)
        {
            return;
        }

        // Al abrir, el contenido vuelve a recortarse al área del notch.
        if (VmField!.GetValue(window) is NotchViewModel { NotchState: not NotchState.Closed }
            && ContentGridField?.GetValue(window) is Grid { ClipToBounds: false } contentGrid)
        {
            contentGrid.ClipToBounds = true;
        }

        bool closed = Math.Abs(target - FixedClosedWidth) < 0.01;
        bool peek = Math.Abs(target - FixedPeekWidth) < 0.01;
        if (!closed && !peek)
        {
            return;
        }

        if (DockField!.GetValue(window) is not NotchDock dock || dock != NotchDock.Top
            || SettingsField!.GetValue(window) is not AppSettings settings || settings.AlwaysShowMinimum
            || VmField!.GetValue(window) is not NotchViewModel vm)
        {
            return;
        }

        if (closed && vm.NotchState == NotchState.Closed)
        {
            double width = MeasureContent(window);
            _lastWidth[window] = width;
            TargetField.SetValue(spring, width);
        }
        else if (peek && vm.NotchState == NotchState.Peeking)
        {
            TargetField.SetValue(spring, MeasureContent(window) + PeekExtra);
        }
    }

    private static double MeasureContent(NotchWindow window)
    {
        if (ClosedContentField!.GetValue(window) is not Grid closed)
        {
            return FixedClosedWidth;
        }

        // Ancho natural del contenido (columnas Auto) sin el límite de la ventana actual.
        double height = closed.ActualHeight > 0 ? closed.ActualHeight : double.PositiveInfinity;
        closed.Measure(new Size(double.PositiveInfinity, height));
        double width = closed.DesiredSize.Width + SidePadding;
        closed.InvalidateMeasure();

        // Nunca exactamente 200/220, para no volver a "corregir" el destino ya corregido.
        width = Math.Clamp(width, MinWidth, FixedClosedWidth + 200.0);
        if (Math.Abs(width - FixedClosedWidth) < 0.01 || Math.Abs(width - FixedPeekWidth) < 0.01)
        {
            width += 0.5;
        }
        return width;
    }
}
