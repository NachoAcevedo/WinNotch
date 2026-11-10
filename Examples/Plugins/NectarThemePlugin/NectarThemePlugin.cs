using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WinNotch.Models;
using WinNotch.Plugins;

namespace PersonalCards;

/// <summary>
/// Tema Nectar (Bloom, Material 3 expresivo) para toda la interfaz de WinNotch, sin
/// configuración: viene activo.
/// - Paleta Material You generada desde el color de acento de Windows, oscura o clara
///   según el tema de WinNotch; reemplaza los pinceles que usa toda la app.
/// - Notch negro cerrado y con el fondo Nectar al abrirse.
/// - Fuente Figtree (incluida en la carpeta del plugin).
/// - Controles con forma Nectar (NectarStyles.xaml) y esquinas de tarjetas/paneles.
/// </summary>
[WinNotchPlugin("personal.theme.nectar", "Tema Nectar", "1.0.0", "Personal")]
public sealed class NectarThemePlugin : PluginBase
{
    private const string FontKey = "NectarFont";
    private const double TabGap = 4.0;
    private static readonly Color NotchClosed = Colors.Black;

    private ResourceDictionary? _styles;
    private FontFamily? _font;
    private NectarScheme? _scheme;
    private INotifyPropertyChanged? _vm;
    private DispatcherTimer? _restyleDebounce;
    private bool _active;

    public override string Id => "personal.theme.nectar";
    public override string Name => "Tema Nectar";
    public override string Version => "1.0.0";
    public override string Author => "Personal";
    public override string Description => "Interfaz con el estilo de Nectar Link (Material You desde el acento de Windows).";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();
        Application.Current.Dispatcher.Invoke(Start);
    }

    public override Task OnDisableAsync()
    {
        Application.Current.Dispatcher.Invoke(Stop);
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        Application.Current.Dispatcher.Invoke(Stop);
        return base.ShutdownAsync();
    }

    private void Start()
    {
        try
        {
            _font = LoadFont();
            _styles = LoadStyles();
            Application.Current.Resources[FontKey] = _font;

            // Estilos implícitos que WinNotch no define (no hay nada que reemplazar): se
            // registran en la app para que alcancen a todos los controles de ese tipo.
            foreach (var type in new[] { typeof(System.Windows.Controls.Primitives.ScrollBar) })
            {
                Application.Current.Resources[type] = _styles[type];
            }
        }
        catch (Exception ex)
        {
            Context?.Log("Tema Nectar: no se pudieron cargar los estilos: " + ex.Message, PluginLogLevel.Error);
            return;
        }

        _active = true;
        ApplyPalette();

        if (Context?.ThemeService is { } theme)
        {
            theme.ThemeChanged += OnThemeChanged;
        }
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _vm = (object?)Context?.NotchViewModel as INotifyPropertyChanged;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnNotchPropertyChanged;
        }

        // Ventanas que se abran después (ej. administrador de plugins).
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (_active && s is Window w) Restyle(w); }));

        _restyleDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _restyleDebounce.Tick += (_, _) =>
        {
            _restyleDebounce.Stop();
            RestyleAll();
        };

        // Las tarjetas de plugins se insertan después de habilitar los plugins.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, RestyleAll);
    }

    private void Stop()
    {
        if (!_active)
        {
            return;
        }
        _active = false;
        _restyleDebounce?.Stop();
        if (Context?.ThemeService is { } theme)
        {
            theme.ThemeChanged -= OnThemeChanged;
        }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnNotchPropertyChanged;
        }
    }

    // ---------------------------------------------------------------- recursos

    private FontFamily LoadFont()
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(NectarThemePlugin).Assembly.Location)!, "Fonts");
        return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Figtree, Segoe UI Variable Text, Segoe UI");
    }

    private static ResourceDictionary LoadStyles()
    {
        using var stream = typeof(NectarThemePlugin).Assembly.GetManifestResourceStream("NectarStyles.xaml")
            ?? throw new InvalidOperationException("Falta NectarStyles.xaml");
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    // ---------------------------------------------------------------- paleta

    private void OnThemeChanged() => Application.Current.Dispatcher.Invoke(ApplyPalette);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Application.Current.Dispatcher.BeginInvoke(ApplyPalette);
        }
    }

    private void ApplyPalette()
    {
        if (!_active)
        {
            return;
        }

        bool dark = !(Context?.ThemeService?.IsLight ?? false);
        var s = _scheme = NectarScheme.FromSeed(ReadWindowsAccent(), dark);

        Color Faded(double t) => NectarScheme.Mix(s.OnSurfaceVariant, s.OutlineVariant, t);
        Color Over(double a) => NectarScheme.WithAlpha(s.OnSurface, a);

        var map = new Dictionary<string, Color>
        {
            ["BackgroundBrush"] = s.Surface,
            ["SurfaceBrush"] = s.SurfaceContainer,
            ["SurfaceRaisedBrush"] = s.SurfaceContainerHigh,
            ["TextPrimaryBrush"] = s.OnSurface,
            ["TextSecondaryBrush"] = s.OnSurfaceVariant,
            ["TextTertiaryBrush"] = Faded(0.2),
            ["TextMutedBrush"] = Faded(0.3),
            ["TextDimBrush"] = Faded(0.4),
            ["TextSubtleBrush"] = Faded(0.5),
            ["TextFaintBrush"] = Faded(0.6),
            ["TextFaintestBrush"] = Faded(0.7),
            ["BorderBrush"] = s.OutlineVariant,
            ["SeparatorBrush"] = s.OutlineVariant,
            ["AccentBrush"] = s.Primary,
            ["AccentPressedBrush"] = s.PrimaryPressed,
            ["AccentSoftBrush"] = s.PrimaryContainer,
            ["AccentTextBrush"] = s.OnPrimary,
            ["SuccessBrush"] = s.Primary,
            ["WarningBrush"] = s.Warning,
            ["DangerBrush"] = s.Error,
            ["FocusRingBrush"] = s.Primary,
            ["DisabledBackgroundBrush"] = Over(0.12),
            ["DisabledTextBrush"] = Over(0.38),
            ["NotchShadowBrush"] = NectarScheme.WithAlpha(Colors.Black, 0.4),
            ["SettingLabelBrush"] = s.OnSurface,
            ["BatteryTextBrush"] = s.OnSurfaceVariant,
            ["ProgressTrackBrush"] = s.SurfaceContainerHighest,
            ["ProgressFillBrush"] = s.Primary,
            ["SegmentedBgBrush"] = s.SurfaceContainerHigh,
            ["SegmentedCheckedBgBrush"] = s.SecondaryContainer,
            ["SegmentedTextBrush"] = s.OnSurfaceVariant,
            ["SegmentedHoverBgBrush"] = Over(0.08),
            ["SegmentedHoverTextBrush"] = s.OnSurface,
            ["SegmentedCheckedTextBrush"] = s.OnSecondaryContainer,
            ["ToggleTrackOffBrush"] = s.SurfaceContainerHighest,
            ["ToggleThumbBrush"] = s.Outline,
            ["TransportHoverBrush"] = Over(0.08),
            ["TransportPressedBrush"] = Over(0.12),
            ["HoverOverlayBrush"] = Over(0.08),
            ["PressedOverlayBrush"] = Over(0.12),
            ["ScrollThumbBrush"] = s.OutlineVariant,
            ["ScrollThumbHoverBrush"] = s.Outline,
            ["ScrollThumbDragBrush"] = s.OnSurfaceVariant,
            ["BatteryBorderBrush"] = s.Outline,
            ["BatteryNormalFillBrush"] = s.OnSurface,
            ["HudIconBrush"] = s.OnSurface,
            ["HudSliderFillBrush"] = s.Primary,
            ["PluginAccentBrush"] = s.Primary,
            ["PluginAccentPressedBrush"] = s.PrimaryPressed,
            ["PluginAccentSoftBrush"] = s.PrimaryContainer,
            ["PluginPositiveBrush"] = s.Primary,
            ["PluginNegativeBrush"] = s.Error,
            ["DropZoneBrush"] = NectarScheme.WithAlpha(s.Primary, 0.14),
        };

        foreach (var (key, color) in map)
        {
            SetBrush(key, color);
        }
        Application.Current.Resources["VisualizerBarColor"] = s.Primary;
        SetNotchFill(animate: false);
    }

    /// <summary>Mismo criterio que WinNotch: cambia el color del pincel existente o lo crea.</summary>
    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.TryFindResource(key) is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
        }
        else
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    private static Color ReadWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                uint v = unchecked((uint)abgr);
                return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
        }
        catch
        {
        }
        return Color.FromRgb(0xB7, 0x79, 0x1F);   // semilla por defecto de Nectar (honey)
    }

    // ---------------------------------------------------------------- notch

    private void OnNotchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotchViewModel.NotchState))
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                SetNotchFill(animate: true);
                if (Context?.NotchViewModel is { NotchState: NotchState.Open })
                {
                    _restyleDebounce?.Stop();
                    _restyleDebounce?.Start();
                }
            });
        }
    }

    /// <summary>Negro con el notch cerrado (se funde con el borde de la pantalla); fondo Nectar abierto.</summary>
    private void SetNotchFill(bool animate)
    {
        if (_scheme == null)
        {
            return;
        }

        bool open = Context?.NotchViewModel is { NotchState: NotchState.Open };
        Color target = open ? _scheme.Surface : NotchClosed;

        if (Application.Current.TryFindResource("NotchFillBrush") is not SolidColorBrush brush || brush.IsFrozen)
        {
            Application.Current.Resources["NotchFillBrush"] = new SolidColorBrush(target);
            return;
        }

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }

        var animation = new ColorAnimation(target, TimeSpan.FromMilliseconds(open ? 200 : 140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    // ---------------------------------------------------------------- estilos, fuente y formas

    private void RestyleAll()
    {
        if (!_active)
        {
            return;
        }
        ReplaceInDictionary(Application.Current.Resources, new HashSet<ResourceDictionary>());
        foreach (Window window in Application.Current.Windows)
        {
            Restyle(window);
        }
    }

    private void Restyle(Window window)
    {
        if (_styles == null || _font == null)
        {
            return;
        }

        try
        {
            window.FontFamily = _font;
            var swaps = new Dictionary<Style, Style>();
            var seenDicts = new HashSet<ResourceDictionary>();
            var visited = new HashSet<DependencyObject>();
            CollectAndReplace(window.Resources, swaps, seenDicts);
            Walk(window, swaps, seenDicts, visited);
        }
        catch (Exception ex)
        {
            Context?.Log("Tema Nectar: " + ex.Message, PluginLogLevel.Warning);
        }
    }

    /// <summary>Reemplaza en el diccionario (y sus fusionados) cada clave que define Nectar.</summary>
    private void ReplaceInDictionary(ResourceDictionary dict, HashSet<ResourceDictionary> seen)
    {
        CollectAndReplace(dict, new Dictionary<Style, Style>(), seen);
    }

    private void CollectAndReplace(ResourceDictionary dict, Dictionary<Style, Style> swaps, HashSet<ResourceDictionary> seen)
    {
        if (_styles == null || ReferenceEquals(dict, _styles) || !seen.Add(dict))
        {
            return;
        }

        foreach (var key in _styles.Keys)
        {
            if (!dict.Contains(key) || _styles[key] is not Style mine)
            {
                continue;
            }
            if (dict[key] is Style old && !ReferenceEquals(old, mine))
            {
                swaps[old] = mine;
                dict[key] = mine;
            }
        }

        foreach (var merged in dict.MergedDictionaries)
        {
            CollectAndReplace(merged, swaps, seen);
        }
    }

    private void Walk(DependencyObject node, Dictionary<Style, Style> swaps, HashSet<ResourceDictionary> seenDicts, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
        {
            return;
        }

        if (node is FrameworkElement fe)
        {
            if (fe.Resources.Count > 0 || fe.Resources.MergedDictionaries.Count > 0)
            {
                CollectAndReplace(fe.Resources, swaps, seenDicts);
            }
            if (fe.Style != null && swaps.TryGetValue(fe.Style, out var replacement))
            {
                fe.Style = replacement;
            }
            ApplyFont(fe);
            ApplyShape(fe);
            ApplyEmptyStateWrap(fe);
        }

        int count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (int i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), swaps, seenDicts, visited);
        }

        // Contenido que no está en el árbol visual (pestañas no seleccionadas, etc.).
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            Walk(child, swaps, seenDicts, visited);
        }
    }

    /// <summary>Texto con Segoe UI fijada a mano pasa a Figtree; los íconos (Segoe Fluent/MDL2) no.</summary>
    private void ApplyFont(FrameworkElement fe)
    {
        FontFamily? current = fe switch
        {
            TextBlock tb => tb.FontFamily,
            Control c => c.FontFamily,
            _ => null
        };
        if (current == null || ReferenceEquals(current, _font))
        {
            return;
        }

        string source = current.Source ?? "";
        if (!source.Contains("Segoe UI", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
            || source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Emoji", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (fe is TextBlock text)
        {
            text.FontFamily = _font;
        }
        else if (fe is Control control)
        {
            control.FontFamily = _font;
        }
    }

    /// <summary>
    /// Los mensajes de "vacío" de WinNotch (texto en cursiva, ej. el de Archivos) son una sola
    /// línea que se corta si es más ancha que la tarjeta: pasan a varias líneas.
    /// </summary>
    private static void ApplyEmptyStateWrap(FrameworkElement fe)
    {
        if (fe is not TextBlock text || text.FontStyle != FontStyles.Italic || text.TextWrapping != TextWrapping.NoWrap)
        {
            return;
        }

        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        if (text.HorizontalAlignment == HorizontalAlignment.Center)
        {
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    /// <summary>
    /// Reparte el ancho libre de la barra de pestañas en partes iguales entre las pestañas
    /// visibles, para que ocupen toda la barra sin dejar un hueco a la derecha. Se recalcula
    /// cuando cambia el ancho de la barra o se muestra/oculta/agrega una pestaña.
    /// </summary>
    private static void HookTabDistribution(Border strip)
    {
        if (strip.TemplatedParent is not TabControl tabs)
        {
            return;
        }

        void Distribute()
        {
            var visible = new List<TabItem>();
            foreach (var item in tabs.Items)
            {
                if ((item as TabItem ?? tabs.ItemContainerGenerator.ContainerFromItem(item) as TabItem) is { } tab)
                {
                    tab.IsVisibleChanged -= OnTabVisibility;
                    tab.IsVisibleChanged += OnTabVisibility;
                    tab.SizeChanged -= OnTabSize;
                    tab.SizeChanged += OnTabSize;
                    if (tab.Visibility == Visibility.Visible)
                    {
                        visible.Add(tab);
                    }
                }
            }

            double available = strip.ActualWidth - strip.Padding.Left - strip.Padding.Right
                - strip.BorderThickness.Left - strip.BorderThickness.Right;
            if (visible.Count == 0 || available <= 0)
            {
                return;
            }

            // Ancho natural de cada pestaña (sin el mínimo de una pasada anterior).
            var natural = new double[visible.Count];
            for (int i = 0; i < visible.Count; i++)
            {
                // El TabPanel de WinNotch ignora el margen de las pestañas (pone cada una pegada a
                // la anterior y la recorta), así que la separación va dentro: margen del fondo.
                visible[i].MinWidth = 0;
                visible[i].Margin = new Thickness(0);
                if (visible[i].Template?.FindName("Bg", visible[i]) is Border bg)
                {
                    bg.Margin = new Thickness(0, 0, i == visible.Count - 1 ? 0 : TabGap, 0);
                }
                visible[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                natural[i] = visible[i].DesiredSize.Width;
            }

            double slack = available - natural.Sum();
            if (slack <= 0)
            {
                return;
            }

            // Ancho mínimo, no fijo: si el contenido crece (la fuente Figtree se aplica después
            // de este cálculo) la pestaña crece en vez de cortarse, y OnTabSize recalcula.
            // Enteros, reservando 1 px por pestaña para el redondeo hacia arriba.
            double extra = Math.Floor((slack - visible.Count) / visible.Count);
            for (int i = 0; i < visible.Count; i++)
            {
                visible[i].MinWidth = Math.Ceiling(natural[i]) + Math.Max(0, extra);
            }
        }

        void OnTabVisibility(object sender, DependencyPropertyChangedEventArgs e) =>
            strip.Dispatcher.BeginInvoke(Distribute, DispatcherPriority.Loaded);

        // Una pestaña más ancha que su mínimo es porque cambió su contenido: se reparte de nuevo.
        // Sin mínimo (no sobraba lugar) no se recalcula, para no entrar en bucle.
        void OnTabSize(object sender, SizeChangedEventArgs e)
        {
            if (sender is TabItem { MinWidth: > 0 } tab && e.NewSize.Width > tab.MinWidth + 0.5)
            {
                strip.Dispatcher.BeginInvoke(Distribute, DispatcherPriority.Loaded);
            }
        }

        strip.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            {
                Distribute();
            }
        };
        ((System.Collections.Specialized.INotifyCollectionChanged)tabs.Items).CollectionChanged +=
            (_, _) => strip.Dispatcher.BeginInvoke(Distribute, DispatcherPriority.Loaded);
        strip.Dispatcher.BeginInvoke(Distribute, DispatcherPriority.Loaded);
    }

    /// <summary>Esquinas cuadradas suaves: segmentados 8, superficies chicas 10, tarjetas y paneles 14.</summary>
    private static void ApplyShape(FrameworkElement fe)
    {
        // El engranaje de ajustes va centrado con la barra de pestañas: WinNotch lo pega arriba
        // de una fila que abarca también la tarjeta, así que se baja 3 px en lugar de centrarlo.
        if (fe is Button { Name: "SettingsGearButton" } gear && gear.Margin.Top < 3)
        {
            gear.Margin = new Thickness(gear.Margin.Left, 3, gear.Margin.Right, gear.Margin.Bottom);
            return;
        }

        // Barra de pestañas: 4 px de aire alrededor de las pestañas y esquina = la de la pestaña
        // (6) + ese aire, si no la curva de 16 de WinNotch muerde la primera y la última.
        if (fe is Border { TemplatedParent: TabControl } strip && strip.CornerRadius.TopLeft > 10)
        {
            strip.CornerRadius = new CornerRadius(10);
            strip.Padding = new Thickness(4);
            HookTabDistribution(strip);
            return;
        }

        if (fe is not Border border || border.Background is not SolidColorBrush bg || border.TemplatedParent != null)
        {
            return;
        }

        bool Is(string key) => Application.Current.TryFindResource(key) is SolidColorBrush b && b.Color == bg.Color;

        if (Is("SegmentedBgBrush") && border.ActualHeight > 0)
        {
            border.CornerRadius = new CornerRadius(8);
        }
        else if (Is("SurfaceBrush") || Is("SurfaceRaisedBrush"))
        {
            if (border.ActualHeight <= 0)
            {
                return;
            }
            border.CornerRadius = new CornerRadius(border.ActualHeight < 60 ? 10 : 14);
        }
    }
}
