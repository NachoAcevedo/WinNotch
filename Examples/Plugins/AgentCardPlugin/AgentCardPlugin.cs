using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinNotch.Plugins;
using WinNotch.Services;

namespace PersonalCards;

/// <summary>
/// Tarjeta de IA: muestra la última respuesta terminada de Codex o Claude Code en VS Code
/// (reply-*.json en la carpeta Cards). Reconstruida a partir del plugin original. Sin tamaño
/// mínimo y con máximo: se ajusta al largo de la respuesta (el notch se ensancha hasta
/// <see cref="MaxContentWidth"/>) y las respuestas largas hacen scroll adentro de la tarjeta.
/// </summary>
public sealed class AgentCardView : UserControl
{
    // Ancho del contenido del notch abierto de WinNotch (460 - 84) y el máximo de esta tarjeta.
    public const double MinContentWidth = 376.0;
    public const double MaxContentWidth = 556.0;
    private const double ReplyMaxHeight = 160.0;
    // Relleno del campo de texto (10 por lado), su borde (2 por lado), el margen interno del
    // texto, la barra de desplazamiento y aire.
    private const double ReplyChrome = 20.0 + 4.0 + 6.0 + 8.0 + 12.0;
    private const int MaxReplyChars = 20000;

    /// <summary>Ancho de contenido que pide la respuesta actual; lo usa el plugin para el notch.</summary>
    public static double PreferredContentWidth { get; private set; } = MinContentWidth;

    private readonly ComboBox _provider = new ComboBox
    {
        Width = 125.0,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0.0, 0.0, 0.0, 7.0)
    };

    private readonly TextBox _reply = new TextBox
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        BorderThickness = new Thickness(0.0),
        Background = Brushes.Transparent,
        MaxHeight = ReplyMaxHeight,
        FontSize = 12.0
    };

    private readonly TextBlock _status = new TextBlock
    {
        FontSize = 10.0,
        Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private FileSystemWatcher? _watcher;
    private string? _currentReplyText;

    private readonly DispatcherTimer _debounce = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(200.0)
    };

    public AgentCardView()
    {
        ClipToBounds = true;

        var panel = new StackPanel();
        _provider.ItemsSource = new[] { "Automático", "Codex", "Claude" };
        _provider.SelectedIndex = 0;
        panel.Children.Add(_provider);
        _reply.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_reply);
        panel.Children.Add(new AgentComposer(() => _currentReplyText, status => _status.Text = status));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(_status);
        Content = panel;

        _provider.SelectionChanged += (_, _) => Refresh();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
        Loaded += (_, _) => StartWatching();
        Unloaded += (_, _) =>
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce.Stop();
        };
        Refresh();
    }

    private void StartWatching()
    {
        Directory.CreateDirectory(LocalCards.DirectoryPath);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(LocalCards.DirectoryPath, "reply-*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        void Changed(object? s, FileSystemEventArgs e) =>
            Dispatcher.BeginInvoke(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += Changed;
        Refresh();
    }

    private void Refresh()
    {
        string selected = _provider.SelectedItem as string ?? "Automático";
        var reply = new[] { "Codex", "Claude" }
            .Where(provider => selected == "Automático" || selected == provider)
            .Select(provider => LocalCards.Read<AgentReply>("reply-" + provider + ".json"))
            .Where(r => r != null)
            .OrderByDescending(r => r!.UpdatedUtc)
            .FirstOrDefault();

        _currentReplyText = reply?.Text;
        if (reply == null)
        {
            PreferredContentWidth = MinContentWidth;
            _reply.Text = "Acá aparecerá la última respuesta terminada de Codex o Claude Code en VS Code.\n\nSolo lectura: esta tarjeta no envía mensajes ni ejecuta las instrucciones de las respuestas.";
            _status.Text = "Esperando una respuesta del proveedor seleccionado.";
            return;
        }

        // Las respuestas muy largas se recortan para que la tarjeta siga fluida.
        string text = reply.Text.Length > MaxReplyChars
            ? reply.Text.Substring(0, MaxReplyChars) + "\n\n… (respuesta recortada, seguí en VS Code)"
            : reply.Text;
        if (_reply.Text != text)
        {
            _reply.Text = text;
            _reply.ScrollToHome();
            UpdatePreferredWidth();
        }
        _status.Text = $"{reply.Provider} · {reply.UpdatedUtc.ToLocalTime():dd/MM HH:mm} · guardado localmente";
    }

    /// <summary>Ancho que necesita la línea más larga de la respuesta, entre el mínimo y el máximo.</summary>
    private void UpdatePreferredWidth()
    {
        var probe = new TextBlock { Text = _reply.Text, FontSize = _reply.FontSize, FontFamily = _reply.FontFamily };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PreferredContentWidth = Math.Clamp(Math.Ceiling(probe.DesiredSize.Width + ReplyChrome), MinContentWidth, MaxContentWidth);
    }

    // Ancho más angosto que le dio WinNotch a la tarjeta: el del notch estándar, ya descontado
    // el relleno de la tarjeta.
    private double _narrowestWidth = double.PositiveInfinity;

    // WinNotch mide la altura del notch con el ancho estándar; se mide con el ancho que va a
    // tener en esta pestaña (estándar + lo que se ensancha el notch), así el alto no queda de más.
    protected override Size MeasureOverride(Size constraint)
    {
        double width = constraint.Width;
        if (width > 0 && !double.IsInfinity(width))
        {
            _narrowestWidth = Math.Min(_narrowestWidth, width);
            width = Math.Max(width, _narrowestWidth + PreferredContentWidth - MinContentWidth);
        }
        var size = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(Math.Min(size.Width, constraint.Width), size.Height);
    }
}

[WinNotchPlugin("personal.card.agent", "IA", "1.2.0", "Nacho_114")]
public sealed class AgentCardPlugin : CardPluginBase
{
    // Cuánto queda abierto el notch al llegar una respuesta, si no se pasa el mouse por encima.
    private static readonly TimeSpan AutoCloseAfter = TimeSpan.FromSeconds(10.0);

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _autoClose;
    private DateTime _lastSeenUtc;
    private DispatcherTimer? _composeIdle;

    // Sin teclear ni tocar el campo durante este tiempo, el notch vuelve a cerrarse solo.
    private static readonly TimeSpan ComposeIdle = TimeSpan.FromSeconds(20.0);

    public override string Id => "personal.card.agent";

    public override string Name => "IA";

    public override string Version => "1.2.0";

    public override string Description => "Tarjeta independiente: IA (tamaño máximo con scroll; abre el notch al llegar una respuesta)";

    public override string CardKey => "agent";

    public override string CardTitle => "IA";

    public override int CardOrder => 50;

    public override double CardHeight => 0.0;

    protected override UIElement CreateCard()
    {
        return new AgentCardView();
    }

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();

        // La vista solo vigila mientras está a la vista; esto vigila siempre, para abrir el
        // notch en la pestaña IA cuando Claude o Codex terminan una respuesta.
        AgentComposer.Log = msg => Context?.Log("IA: " + msg, PluginLogLevel.Info);
        AgentComposer.Activity += OnComposeActivity;
        AgentComposer.Finished += OnComposeFinished;
        Application.Current.Dispatcher.Invoke(() =>
        {
            HookOpenWidth();
            _lastSeenUtc = LatestReplyUtc();
            Directory.CreateDirectory(LocalCards.DirectoryPath);
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300.0) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                OnReplyChanged();
            };
            _watcher = new FileSystemWatcher(LocalCards.DirectoryPath, "reply-*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            void Changed(object? s, FileSystemEventArgs e) =>
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _debounce.Stop();
                    _debounce.Start();
                });
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Renamed += Changed;
        });
    }

    public override Task OnDisableAsync()
    {
        AgentComposer.Activity -= OnComposeActivity;
        AgentComposer.Finished -= OnComposeFinished;
        _composeIdle?.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Stop();
        _autoClose?.Stop();
        return base.OnDisableAsync();
    }

    /// <summary>
    /// Con la pestaña IA elegida, el notch abierto se ensancha lo que pida la respuesta (hasta
    /// el máximo): se corrige el destino del resorte de ancho cuando WinNotch anima al ancho
    /// estándar, igual que el plugin de notch adaptable.
    /// </summary>
    private void HookOpenWidth()
    {
        var window = NotchWindowWithTabs();
        if (window?.GetType().GetField("_widthSpring", WindowFlags)?.GetValue(window) is not { } spring)
        {
            return;
        }
        var targetField = spring.GetType().GetField("_targetValue", WindowFlags);
        var openWidth = window.GetType().GetMethod("GetOpenWidth", WindowFlags, Type.EmptyTypes);
        var valueChanged = spring.GetType().GetEvent("ValueChanged");
        if (targetField == null || openWidth == null || valueChanged == null)
        {
            Context?.Log("IA: no se pudo enganchar el ancho del notch", PluginLogLevel.Warning);
            return;
        }

        Action<double> handler = _ =>
        {
            if (targetField.GetValue(spring) is not double target
                || openWidth.Invoke(window, null) is not double standard
                || Math.Abs(target - standard) > 0.01
                || !IsCardTabSelected(window)
                || (object?)Context?.NotchViewModel is not WinNotch.Models.NotchViewModel { NotchState: WinNotch.Models.NotchState.Open })
            {
                return;
            }
            double wide = standard + AgentCardView.PreferredContentWidth - AgentCardView.MinContentWidth;
            if (wide > standard + 0.5)
            {
                targetField.SetValue(spring, wide);
            }
        };
        valueChanged.AddEventHandler(spring, handler);
    }

    private bool IsCardTabSelected(Window window) =>
        window.GetType().GetField("CardTabs", WindowFlags)?.GetValue(window) is TabControl { SelectedItem: TabItem { Header: string header } }
        && header == CardTitle;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static bool IsVsCodeInFront()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith("Code", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mientras se escribe en la tarjeta el notch no se cierra al sacar el mouse: se usa la
    /// misma marca con la que WinNotch lo mantiene abierto con un menú contextual abierto.
    /// </summary>
    private void OnComposeActivity()
    {
        var window = NotchWindowWithTabs();
        if (window == null)
        {
            return;
        }
        _autoClose?.Stop();
        window.GetType().GetField("_isContextMenuOpen", WindowFlags)?.SetValue(window, true);
        if (_composeIdle == null)
        {
            _composeIdle = new DispatcherTimer { Interval = ComposeIdle };
            _composeIdle.Tick += (_, _) => ReleaseCompose(closeIfMouseAway: true);
        }
        _composeIdle.Stop();
        _composeIdle.Start();
    }

    // Enter (envío) o Esc: se suelta la marca y el notch se cierra.
    private void OnComposeFinished()
    {
        ReleaseCompose(closeIfMouseAway: false);
        if (NotchWindowWithTabs() is { } window)
        {
            CallWindow(window, "TransitionToClose");
        }
    }

    private void ReleaseCompose(bool closeIfMouseAway)
    {
        _composeIdle?.Stop();
        if (NotchWindowWithTabs() is not { } window)
        {
            return;
        }
        window.GetType().GetField("_isContextMenuOpen", WindowFlags)?.SetValue(window, false);
        bool mouseOver = window.GetType().GetField("_isMouseOverNotch", WindowFlags)?.GetValue(window) is true;
        if (closeIfMouseAway && !mouseOver)
        {
            CallWindow(window, "TransitionToClose");
        }
    }

    private static DateTime LatestReplyUtc() =>
        new[] { "Codex", "Claude" }
            .Select(provider => LocalCards.Read<AgentReply>("reply-" + provider + ".json"))
            .Where(r => r != null)
            .Select(r => r!.UpdatedUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

    private void OnReplyChanged()
    {
        DateTime latest = LatestReplyUtc();
        if (latest <= _lastSeenUtc)
        {
            return;
        }
        _lastSeenUtc = latest;

        // Con VS Code al frente la respuesta ya está a la vista: no se abre el notch.
        if (IsVsCodeInFront())
        {
            return;
        }

        var window = NotchWindowWithTabs();
        if (window == null)
        {
            return;
        }

        SelectCardTab(window);
        bool wasClosed = (object?)Context?.NotchViewModel is WinNotch.Models.NotchViewModel { NotchState: not WinNotch.Models.NotchState.Open };

        // Como el atajo de teclado de WinNotch: la transición completa (animación y contenido),
        // no solo el estado del view model.
        CallWindow(window, "TransitionToOpen");

        // Si lo abrimos nosotros, se cierra solo, salvo que el mouse esté encima.
        if (wasClosed)
        {
            _autoClose?.Stop();
            _autoClose = new DispatcherTimer { Interval = AutoCloseAfter };
            _autoClose.Tick += (_, _) =>
            {
                bool mouseOver = window.GetType().GetField("_isMouseOverNotch", WindowFlags)?.GetValue(window) is true
                    || window.IsMouseOver;
                if (mouseOver)
                {
                    return;
                }
                _autoClose.Stop();
                if ((object?)Context?.NotchViewModel is WinNotch.Models.NotchViewModel { NotchState: WinNotch.Models.NotchState.Open })
                {
                    CallWindow(window, "TransitionToClose");
                }
            };
            _autoClose.Start();
        }
    }

    private const System.Reflection.BindingFlags WindowFlags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

    private static void CallWindow(Window window, string method) =>
        window.GetType().GetMethod(method, WindowFlags, Type.EmptyTypes)?.Invoke(window, null);

    private static Window? NotchWindowWithTabs() =>
        Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.GetType().GetField("CardTabs", WindowFlags)?.GetValue(w) is TabControl);

    /// <summary>Selecciona la pestaña de esta tarjeta en la barra de pestañas del notch.</summary>
    private void SelectCardTab(Window window)
    {
        if (window.GetType().GetField("CardTabs", WindowFlags)?.GetValue(window) is not TabControl tabs)
        {
            return;
        }
        foreach (var item in tabs.Items)
        {
            if (item is TabItem { Header: string header } tab && header == CardTitle)
            {
                tabs.SelectedItem = tab;
                return;
            }
        }
    }
}
