using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinNotch.Models;
using WinNotch.Plugins;
using WinNotch.Services;
using WinNotch.Views;

namespace PersonalCards;

/// <summary>
/// Cuentas desactivadas para el calendario (calendar-accounts.json). La lista de cuentas
/// no se guarda: se lee siempre en vivo de Outlook.
/// </summary>
public sealed class CalendarAccountsSettings
{
    public List<string> Disabled { get; set; } = new();
}

/// <summary>
/// Extiende el calendario integrado de WinNotch para que lea el calendario de
/// todas las cuentas cargadas en Outlook clásico, no solo la cuenta por defecto.
/// Usa la sesión de Outlook ya abierta (COM), sin login ni autorizaciones nuevas.
/// Cada vez que el CalendarService integrado refresca, este plugin vuelve a leer
/// todas las cuentas y reemplaza la lista que muestran el panel y la pestaña.
/// En Ajustes, en la sección de opciones de Calendario, agrega un interruptor por cuenta.
/// </summary>
[WinNotchPlugin("personal.calendar.allaccounts", "Calendario multicuenta", "1.0.0", "Nacho_114")]
public sealed class CalendarAccountsPlugin : PluginBase
{
    private const int MaxEvents = 5;
    private const int OlFolderCalendar = 9;
    private const string SettingsFile = "calendar-accounts.json";
    private const string AccountsPanelTag = "personal.calendar.accounts";

    // Aviso al empezar un evento: se reutiliza el HUD de feedback integrado (el de Caps Lock / música).
    private const string CalendarGlyph = "";
    private static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(15.0);
    private static readonly TimeSpan AlertWindow = TimeSpan.FromMinutes(2.0);

    private readonly object _upcomingLock = new();
    private List<CalendarEvent> _upcoming = new();
    private readonly HashSet<string> _alerted = new();
    private DispatcherTimer? _alertTimer;

    private readonly object _settingsLock = new();
    private CalendarAccountsSettings _settings = LocalCards.Read<CalendarAccountsSettings>(SettingsFile) ?? new CalendarAccountsSettings();
    private List<string> _known = new();   // cuentas de Outlook leídas en el último refresco

    // Un color por cuenta, en el orden en que Outlook lista los stores: el acento del tema
    // y variantes con el matiz rotado (se recalculan en cada refresco, siguen al tema).
    private const double AccountHueStep = 55.0;
    private string[] _accountColors = { "#4A90D9" };

    private CalendarService? _calendar;
    private PropertyInfo? _todayEventsProp;
    private FieldInfo? _eventsUpdatedField;
    private int _refreshing;
    private volatile bool _publishing;

    public override string Id => "personal.calendar.allaccounts";
    public override string Name => "Calendario multicuenta";
    public override string Version => "1.0.0";
    public override string Author => "Nacho_114";
    public override string Description => "Muestra en el calendario los eventos de todas las cuentas de Outlook clásico.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();

        _calendar = Context?.CalendarService;
        if (_calendar == null)
        {
            Context?.Log("Calendario multicuenta: CalendarService no disponible", PluginLogLevel.Warning);
            return;
        }

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _todayEventsProp = typeof(CalendarService).GetProperty(nameof(CalendarService.TodayEvents), flags);
        _eventsUpdatedField = typeof(CalendarService).GetField(nameof(CalendarService.EventsUpdated), flags);
        if (_todayEventsProp?.SetMethod == null || _eventsUpdatedField == null)
        {
            Context?.Log("Calendario multicuenta: la versión de WinNotch no es compatible", PluginLogLevel.Error);
            _calendar = null;
            return;
        }

        _calendar.EventsUpdated += OnBuiltInEventsUpdated;
        Application.Current.Dispatcher.Invoke(() =>
        {
            InstallSettingsUi();
            _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15.0) };
            _alertTimer.Tick += (_, _) => CheckEventAlerts();
            _alertTimer.Start();
        });
        Refresh();
    }

    public override Task OnDisableAsync()
    {
        _alertTimer?.Stop();
        if (_calendar != null)
        {
            _calendar.EventsUpdated -= OnBuiltInEventsUpdated;
            _calendar.RefreshEvents();
            _calendar = null;
        }
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        _alertTimer?.Stop();
        OutlookKeeper.Stop();
        if (_calendar != null)
        {
            _calendar.EventsUpdated -= OnBuiltInEventsUpdated;
            _calendar = null;
        }
        return base.ShutdownAsync();
    }

    private void OnBuiltInEventsUpdated()
    {
        // Ignorar el aviso que dispara este mismo plugin al publicar su lista.
        if (_publishing)
        {
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                _accountColors = Application.Current.Dispatcher.Invoke(BuildAccountColors);
                var events = FetchAllAccounts();
                if (events == null)
                {
                    Context?.Log("Calendario multicuenta: Outlook no está abierto", PluginLogLevel.Warning);
                    Application.Current.Dispatcher.BeginInvoke(ShowOutlookClosed);
                    return;
                }
                Publish(events);
                Context?.Log("Calendario multicuenta: " + events.Count + " eventos publicados");
            }
            catch (Exception ex)
            {
                Context?.Log("Calendario multicuenta: " + ex.Message, PluginLogLevel.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
        thread.IsBackground = true;
        thread.Name = "WinNotch.CalendarAllAccounts";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Publish(List<CalendarEvent> events)
    {
        var calendar = _calendar;
        if (calendar == null)
        {
            return;
        }

        _todayEventsProp!.SetValue(calendar, events);
        _publishing = true;
        try
        {
            (_eventsUpdatedField!.GetValue(calendar) as Action)?.Invoke();
        }
        finally
        {
            _publishing = false;
        }
    }

    /// <summary>
    /// Mismo criterio que el CalendarService integrado (de ahora al fin del día,
    /// máximo 5), pero recorriendo el calendario de cada store de Outlook.
    /// Devuelve null si Outlook no está abierto, para no pisar la lista integrada.
    /// </summary>
    private List<CalendarEvent>? FetchAllAccounts()
    {
        var outlook = GetActiveOutlook();
        if (outlook == null)
        {
            return null;
        }

        dynamic? ns = null;
        dynamic? stores = null;
        var all = new List<CalendarEvent>();
        try
        {
            ns = ((dynamic)outlook).GetNamespace("MAPI");
            stores = ns.Stores;
            int count = stores.Count;

            DateTime now = DateTime.Now;
            DateTime endOfDay = now.Date.AddDays(1.0).AddSeconds(-1.0);
            string filter = $"[Start] >= '{now:g}' AND [Start] <= '{endOfDay:g}'";

            var known = new List<string>();
            List<string> disabled;
            lock (_settingsLock)
            {
                disabled = _settings.Disabled.ToList();
            }

            for (int i = 1; i <= count; i++)
            {
                dynamic? store = null;
                try
                {
                    store = stores.Item(i);
                    string account = store.DisplayName;
                    known.Add(account);
                    if (disabled.Contains(account, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string color = _accountColors[(i - 1) % _accountColors.Length];
                    all.AddRange(FetchStore(store, filter, color));
                }
                catch (Exception ex)
                {
                    // Stores sin calendario (archivos PST, carpetas públicas, etc.)
                    Context?.Log("Calendario multicuenta: store " + i + " omitido: " + ex.Message, PluginLogLevel.Debug);
                }
                finally
                {
                    ReleaseCom(store);
                }
            }

            UpdateKnownAccounts(known);
        }
        finally
        {
            ReleaseCom(stores);
            ReleaseCom(ns);
            ReleaseCom(outlook);
        }

        // La misma reunión puede estar en más de una cuenta.
        var ordered = all
            .GroupBy(e => (e.Title, e.Start))
            .Select(g => g.First())
            .OrderBy(e => e.Start)
            .ToList();

        lock (_upcomingLock)
        {
            // Outlook solo devuelve eventos que todavía no empezaron: se conservan los que
            // empezaron hace instantes para no perder su aviso si el refresco cae justo encima.
            DateTime current = DateTime.Now;
            var justStarted = _upcoming.Where(e => e.Start <= current && current - e.Start < AlertWindow);
            _upcoming = ordered.Concat(justStarted).ToList();
        }
        return ordered.Take(MaxEvents).ToList();
    }

    /// <summary>Muestra el aviso de los eventos que acaban de empezar (una sola vez por evento).</summary>
    private void CheckEventAlerts()
    {
        DateTime now = DateTime.Now;
        List<CalendarEvent> starting;
        lock (_upcomingLock)
        {
            starting = _upcoming
                .Where(e => !e.IsAllDay && e.Start <= now && now - e.Start < AlertWindow)
                .ToList();
        }

        foreach (var ev in starting)
        {
            if (_alerted.Add(ev.Title + "|" + ev.Start.ToString("O")))
            {
                ShowEventAlert(ev);
            }
        }
    }

    /// <summary>
    /// Usa el HUD de feedback integrado (el mismo de Caps Lock y música), que expande
    /// el notch con ícono, título y detalle, y lo deja visible <see cref="AlertDuration"/>.
    /// </summary>
    private void ShowEventAlert(CalendarEvent ev)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var showFeedback = typeof(HudOverlay).GetMethod("ShowFeedback", flags);
        var timerField = typeof(HudOverlay).GetField("_dismissTimer", flags);
        if (showFeedback == null || timerField == null)
        {
            Context?.Log("Calendario multicuenta: la versión de WinNotch no permite mostrar el aviso", PluginLogLevel.Warning);
            return;
        }

        string detail = $"Ahora · {ev.Start:HH:mm} – {ev.End:HH:mm}";
        foreach (Window window in Application.Current.Windows)
        {
            foreach (var field in window.GetType().GetFields(flags))
            {
                if (field.FieldType != typeof(HudOverlay) || field.GetValue(window) is not HudOverlay hud || !hud.IsActive)
                {
                    continue;
                }

                showFeedback.Invoke(hud, new object[] { CalendarGlyph, ev.Title, detail });
                if (timerField.GetValue(hud) is DispatcherTimer timer)
                {
                    var original = timer.Interval;
                    timer.Stop();
                    timer.Interval = AlertDuration;
                    timer.Start();

                    // El timer es compartido con los demás avisos: se restaura al cerrarse este.
                    void Restore()
                    {
                        timer.Interval = original;
                        hud.HudDismissed -= Restore;
                    }
                    hud.HudDismissed += Restore;
                }
            }
        }
        Context?.Log("Calendario multicuenta: aviso de evento '" + ev.Title + "' " + ev.Start.ToString("HH:mm"));
    }

    /// <summary>Colores por cuenta a partir del acento del tema actual (AccentBrush).</summary>
    private static string[] BuildAccountColors()
    {
        var accent = (Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush)?.Color
            ?? System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9);

        // RGB -> HSL, rotar matiz, HSL -> RGB.
        double r = accent.R / 255.0, g = accent.G / 255.0, b = accent.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);

        var colors = new string[6];
        for (int i = 0; i < colors.Length; i++)
        {
            double hue = ((h + i * AccountHueStep) % 360 + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = l - c / 2;
            var (rr, gg, bb) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };
            colors[i] = $"#{(int)Math.Round((rr + m) * 255):X2}{(int)Math.Round((gg + m) * 255):X2}{(int)Math.Round((bb + m) * 255):X2}";
        }
        return colors;
    }

    private void UpdateKnownAccounts(List<string> known)
    {
        lock (_settingsLock)
        {
            if (_known.SequenceEqual(known))
            {
                return;
            }
            _known = known;
        }
        Application.Current.Dispatcher.BeginInvoke(RebuildAccountToggles);
    }

    private void SaveSettings()
    {
        try
        {
            LocalCards.Write(SettingsFile, _settings);
        }
        catch (Exception ex)
        {
            Context?.Log("Calendario multicuenta: no se pudo guardar " + SettingsFile + ": " + ex.Message, PluginLogLevel.Warning);
        }
    }

    /// <summary>
    /// Agrega un contenedor con un interruptor por cuenta en la sección de opciones de
    /// Calendario de Ajustes (CalendarOptionsHost). WinNotch deshabilita esa sección
    /// cuando la tarjeta de calendario está apagada.
    /// </summary>
    private void InstallSettingsUi()
    {
        foreach (var view in SettingsDom.FindSettingsViews())
        {
            if (view.FindName("CalendarOptionsHost") is not Panel host)
            {
                Context?.Log("Calendario multicuenta: no se encontró la sección Calendario en Ajustes", PluginLogLevel.Warning);
                continue;
            }

            if (FindAccountsContainer(view) == null)
            {
                host.Children.Add(new StackPanel { Tag = AccountsPanelTag });
            }
        }
        RebuildAccountToggles();
    }

    private static StackPanel? FindAccountsContainer(WinNotch.Views.InlineSettingsView view) =>
        (view.FindName("CalendarOptionsHost") as Panel)?.Children.OfType<StackPanel>()
            .FirstOrDefault(e => Equals(e.Tag, AccountsPanelTag));

    private void RebuildAccountToggles()
    {
        List<string> known;
        List<string> disabled;
        lock (_settingsLock)
        {
            known = _known.ToList();
            disabled = _settings.Disabled.ToList();
        }

        foreach (var view in SettingsDom.FindSettingsViews())
        {
            var container = FindAccountsContainer(view);
            if (container == null)
            {
                continue;
            }

            container.Children.Clear();
            var rowMargin = new Thickness(0, 2, 0, 2);

            if (known.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = "Abrí Outlook para ver las cuentas del calendario.",
                    FontSize = 11.0,
                    Margin = new Thickness(0.0, rowMargin.Top, 0.0, rowMargin.Bottom),
                    TextWrapping = TextWrapping.Wrap
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
                container.Children.Add(hint);
                continue;
            }

            foreach (var account in known)
            {
                var grid = new Grid { Margin = rowMargin, Height = 32 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = account,
                    Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = account
                };
                if (view.TryFindResource("SettingLabel") is Style labelStyle)
                {
                    label.Style = labelStyle;
                }
                grid.Children.Add(label);

                var toggle = new CheckBox
                {
                    IsChecked = !disabled.Contains(account, StringComparer.OrdinalIgnoreCase),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Mostrar los eventos de " + account
                };
                if (view.TryFindResource("ToggleSwitch") is Style toggleStyle)
                {
                    toggle.Style = toggleStyle;
                }
                Grid.SetColumn(toggle, 1);
                toggle.Click += (_, _) => SetAccountEnabled(account, toggle.IsChecked == true);
                grid.Children.Add(toggle);

                container.Children.Add(grid);
            }
        }
    }

    private void SetAccountEnabled(string account, bool enabled)
    {
        lock (_settingsLock)
        {
            _settings.Disabled.RemoveAll(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                _settings.Disabled.Add(account);
            }
            SaveSettings();
        }
        // Sincroniza el interruptor en la otra vista de ajustes (horizontal/vertical).
        RebuildAccountToggles();
        Refresh();
    }

    private static List<CalendarEvent> FetchStore(dynamic store, string filter, string color)
    {
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? restricted = null;
        var list = new List<CalendarEvent>();
        try
        {
            folder = store.GetDefaultFolder(OlFolderCalendar);
            items = folder.Items;
            items.Sort("[Start]");
            items.IncludeRecurrences = true;
            restricted = items.Restrict(filter);

            foreach (dynamic item in restricted)
            {
                try
                {
                    list.Add(new CalendarEvent
                    {
                        Title = item.Subject ?? "Untitled",
                        Start = item.Start,
                        End = item.End,
                        IsAllDay = item.AllDayEvent,
                        CalendarColor = color
                    });
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(item);
                }
                if (list.Count >= MaxEvents)
                {
                    break;
                }
            }
        }
        finally
        {
            ReleaseCom(restricted);
            ReleaseCom(items);
            ReleaseCom(folder);
        }
        return list;
    }

    private static void ReleaseCom(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.ReleaseComObject(com);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Con Outlook cerrado, la tarjeta del calendario muestra un botón para abrirlo en la bandeja
    /// en lugar de "No upcoming events" (que haría pensar que no hay eventos).
    /// </summary>
    private void ShowOutlookClosed()
    {
        foreach (var window in Application.Current.Windows.OfType<NotchWindow>())
        {
            if (typeof(NotchWindow).GetField("CalendarPanel_View", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is not CalendarView view
                || typeof(CalendarView).GetField("EventsPanel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) is not StackPanel panel)
            {
                continue;
            }

            panel.Children.Clear();
            var message = new TextBlock
            {
                Text = "Outlook está cerrado: sin él no se ven tus eventos.",
                FontSize = 12.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 4.0, 0.0, 8.0)
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            var open = new Button { Content = "Abrir Outlook en la bandeja", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                open.IsEnabled = false;
                open.Content = "Abriendo Outlook…";
                OpenOutlookInTray();
            };
            panel.Children.Add(message);
            panel.Children.Add(open);
        }
    }

    /// <summary>Abre Outlook clásico en la bandeja (sin ventana) y actualiza el calendario cuando ya responde.</summary>
    private void OpenOutlookInTray()
    {
        OutlookKeeper.Start(msg => Context?.Log("Calendario multicuenta: " + msg));

        // Outlook tarda en arrancar: se reintenta hasta que responda (máximo ~1 minuto).
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8.0) };
        int attempts = 0;
        retry.Tick += (_, _) =>
        {
            attempts++;
            if (GetActiveOutlook() != null || attempts >= 8)
            {
                retry.Stop();
                _calendar?.RefreshEvents();
            }
        };
        retry.Start();
    }

    private static object? GetActiveOutlook()
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0)
            {
                return null;
            }
            if (GetActiveObject(ref clsid, IntPtr.Zero, out var unk) == 0)
            {
                return unk;
            }

            // Abierto por automatización (-Embedding, p. ej. por OutlookKeeper) Outlook no se
            // registra en la ROT: si el proceso existe, CreateInstance se engancha a esa misma
            // instancia (Outlook es de instancia única) sin abrir otra.
            if (System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length == 0)
            {
                return null;
            }
            var type = Type.GetTypeFromProgID("Outlook.Application");
            return type == null ? null : Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
