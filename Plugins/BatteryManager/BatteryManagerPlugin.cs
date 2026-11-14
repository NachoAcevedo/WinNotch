using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNotch.Plugins;
using WinNotch.Views;
using WinNotch.Services;

namespace PersonalCards.BatteryManager;

[WinNotchPlugin("personal.card.batterymanager", "BatteryManager", "1.0.16", "Nacho_114")]
public sealed class BatteryManagerPlugin : CardPluginBase, IConfigurablePlugin
{
    public override string Id => "personal.card.batterymanager";
    public override string Name => "BatteryManager";
    public override string Version => "1.0.16";
    public override string Author => "Nacho_114";
    public override string Description => "Batería de periféricos Bluetooth y USB compatibles.";
    public override string CardKey => "batterymanager";
    public override string CardTitle => "Baterías";
    public override int CardOrder => 66;
    public override double CardHeight => 205;
    private const string SettingsFile = "batterymanager-settings.json";
    private static readonly int[] Thresholds = { 0, 5, 10, 15, 20, 25, 30 };
    private static readonly int[] Intervals = { 15, 30, 60, 120, 300 };
    private BatteryPreferences _preferences = LocalCards.Read<BatteryPreferences>(SettingsFile) ?? new();
    private BatteryMonitor? _monitor;

    public PluginConfigurationDefinition GetConfigurationDefinition() => Describe(_preferences, general: true);

    private static PluginConfigurationDefinition Describe(BatteryPreferences preferences, bool general = false) => new()
    {
        Title = "BatteryManager",
        IsConfigured = true,
        Sections = new[] { new PluginConfigurationSection
        {
            Title = "Avisos y dispositivos",
            Fields = new[]
            {
                Choice("threshold", "Aviso de batería baja", preferences.LowThreshold.ToString(),
                    Thresholds.Select(n => Option(n.ToString(), n == 0 ? "Desactivado" : $"{n}%")).ToArray()),
                Toggle("full", "Avisar al completar la carga", preferences.NotifyFull),
                Toggle("offline", "Conservar desconectados hasta reiniciar", preferences.KeepOffline),
                Toggle("remaining", "Mostrar tiempo restante", preferences.ShowRemaining)
            }.Concat(general ? new[]
            {
                Choice("interval", "Intervalo de lectura", preferences.IntervalSeconds.ToString(),
                    Intervals.Select(n => Option(n.ToString(), n < 60 ? $"{n} s" : $"{n / 60} min")).ToArray()),
                Toggle("playstation", "Modo completo de PlayStation por Bluetooth", preferences.PlayStationFullMode)
            } : Array.Empty<PluginConfigurationField>()).ToArray()
        }}
    };

    public Task<PluginConfigurationResult> ApplyConfigurationAsync(IReadOnlyDictionary<string, string?> values) =>
        SaveAsync(null, values);

    private async Task<PluginConfigurationResult> SaveAsync(string? deviceId, IReadOnlyDictionary<string, string?> values)
    {
        if (!int.TryParse(values.GetValueOrDefault("threshold"), out int threshold) || !Thresholds.Contains(threshold) ||
            !bool.TryParse(values.GetValueOrDefault("full"), out bool full) ||
            !bool.TryParse(values.GetValueOrDefault("offline"), out bool offline) ||
            !bool.TryParse(values.GetValueOrDefault("remaining"), out bool remaining))
            return PluginConfigurationResult.Fail("Revisá las opciones de BatteryManager.");

        var selected = new BatteryPreferences(threshold, full, offline, ShowRemaining: remaining);
        if (deviceId == null)
        {
            if (!int.TryParse(values.GetValueOrDefault("interval"), out int interval) || !Intervals.Contains(interval) ||
                !bool.TryParse(values.GetValueOrDefault("playstation"), out bool playStation))
                return PluginConfigurationResult.Fail("Revisá el intervalo y el modo PlayStation.");
            selected = selected with { IntervalSeconds = interval, PlayStationFullMode = playStation };
        }
        var devices = _preferences.Devices == null
            ? new Dictionary<string, BatteryPreferences>()
            : new Dictionary<string, BatteryPreferences>(_preferences.Devices);
        if (deviceId != null) devices[deviceId] = selected;
        var preferences = (deviceId == null ? selected : _preferences) with { Devices = devices };
        try { LocalCards.Write(SettingsFile, preferences); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            return PluginConfigurationResult.Fail("No se pudieron guardar los ajustes.");
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _preferences = preferences;
            _monitor?.SetPreferences(preferences);
        });
        return PluginConfigurationResult.Ok("Ajustes guardados.");
    }

    private static PluginConfigurationField Toggle(string key, string label, bool enabled) =>
        Choice(key, label, enabled.ToString(), Option("True", "Activado"), Option("False", "Desactivado"));

    private static PluginConfigurationOption Option(string value, string label) => new() { Value = value, Label = label };

    private static PluginConfigurationField Choice(string key, string label, string value, params PluginConfigurationOption[] options) =>
        new() { Key = key, Label = label, Value = value, FieldType = PluginConfigurationFieldType.Choice, Options = options };

    protected override UIElement CreateCard() => new BatteryCard(GetMonitor(), device => device == null ? GetConfigurationDefinition() : Describe(_preferences.For(device.Identity)), SaveAsync);

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();
        await Application.Current.Dispatcher.InvokeAsync(() => GetMonitor().Start());
    }

    public override Task OnDisableAsync()
    {
        Application.Current.Dispatcher.Invoke(() => _monitor?.Stop());
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        Application.Current.Dispatcher.Invoke(() => _monitor?.Stop());
        return base.ShutdownAsync();
    }

    private BatteryMonitor GetMonitor() => _monitor ??= new BatteryMonitor(
        _preferences,
        (device, kind) => Context?.MainWindow is NotchWindow window &&
            window.TryShowDeviceBattery(device.Name, device.Level!.Value,
                kind == BatteryAlert.Low && device.Band == BatteryBand.Critical,
                kind switch { BatteryAlert.Low => "Batería baja", BatteryAlert.Full => "Carga completa", _ => "Conectado" }),
        message => Context?.Log("BatteryManager: " + message, PluginLogLevel.Debug));
}

internal sealed record BatteryPreferences(int LowThreshold = 30, bool NotifyFull = true, bool KeepOffline = false,
    Dictionary<string, BatteryPreferences>? Devices = null, bool ShowRemaining = true,
    int IntervalSeconds = 120, bool PlayStationFullMode = false)
{
    public BatteryPreferences For(string deviceId) => Devices?.GetValueOrDefault(deviceId) ?? this;
}
internal enum BatteryAlert { Connection, Full, Low }

internal enum BatteryBand { Normal, Low, Critical }

internal sealed record DeviceBattery(string Name, int? Level, bool Charging, bool Online, string Kind, bool Approximate, string? Key, long? SecondsLeft = null, bool ShowEstimate = true)
{
    public string Identity => string.IsNullOrEmpty(Key) ? $"{Kind}:{Name}" : Key;
    public BatteryBand Band => Level switch
    {
        >= 0 and <= 15 => BatteryBand.Critical,
        > 15 and <= 30 => BatteryBand.Low,
        _ => BatteryBand.Normal
    };
}
internal sealed record BatterySnapshot(DeviceBattery[] Devices, int Failures);

internal sealed class BatteryCard : StackPanel
{
    private readonly CenteredDevicePanel _devices = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };

    public BatteryCard(BatteryMonitor monitor, Func<DeviceBattery?, PluginConfigurationDefinition> configuration,
        Func<string?, IReadOnlyDictionary<string, string?>, Task<PluginConfigurationResult>> save)
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(new ScrollViewer
        {
            Content = _devices, MaxHeight = 155, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var options = new StackPanel();
        options.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WinNotch;component/Views/Components/ToggleSwitchStyle.xaml", UriKind.Relative)
        });
        var settings = new Border
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0),
            Child = options
        };
        settings.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Children.Add(settings);
        Children.Add(_status);

        var generalOptions = new StackPanel();
        generalOptions.Resources.MergedDictionaries.Add(options.Resources.MergedDictionaries[0]);
        string? selectedDevice = null;
        var generalSettings = new Border
        {
            Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = generalOptions,
            Margin = new Thickness(0, 10, 0, 0)
        };
        generalSettings.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Children.Insert(Children.IndexOf(_status), generalSettings);
        bool saving = false;

        void BuildOptions(StackPanel panel, DeviceBattery? device)
        {
            panel.Children.Clear();
            var editors = new Dictionary<string, Func<string?>>();
            var fields = configuration(device).Sections.SelectMany(section => section.Fields).ToArray();
            var values = fields.ToDictionary(field => field.Key, field => field.Value);

            async Task SaveChange(string key, string? value, Action restore)
            {
                if (saving || value == null || value == values[key]) return;
                saving = true;
                IsEnabled = false;
                try
                {
                    var updated = device == null
                        ? configuration(null).Sections.SelectMany(section => section.Fields)
                            .ToDictionary(field => field.Key, field => field.Value)
                        : editors.ToDictionary(entry => entry.Key, entry => entry.Value());
                    updated[key] = value;
                    var result = await save(device?.Identity, updated);
                    if (result.Success)
                    {
                        values[key] = value;
                        _status.Text = monitor.Status;
                    }
                    else
                    {
                        restore();
                        _status.Text = result.Message;
                    }
                }
                finally
                {
                    IsEnabled = true;
                    saving = false;
                }
            }

            foreach (var field in fields.Where(field => device != null || field.Key is "interval" or "playstation"))
            {
                var label = new TextBlock { Text = field.Label, Margin = new Thickness(0, 8, 0, 3) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                if (field.Key is "full" or "offline" or "remaining" or "playstation")
                {
                    var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), MinHeight = 26 };
                    label.Margin = new Thickness(0, 0, 8, 0);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    label.TextWrapping = TextWrapping.Wrap;
                    var toggle = new CheckBox
                    {
                        Style = (Style)panel.FindResource("ToggleSwitch"),
                        IsChecked = bool.Parse(field.Value!),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    System.Windows.Automation.AutomationProperties.SetLabeledBy(toggle, label);
                    DockPanel.SetDock(toggle, Dock.Right);
                    row.Children.Add(toggle);
                    row.Children.Add(label);
                    panel.Children.Add(row);
                    editors.Add(field.Key, () => (toggle.IsChecked == true).ToString());
                    toggle.Click += async (_, _) => await SaveChange(field.Key, editors[field.Key](),
                        () => toggle.IsChecked = bool.Parse(values[field.Key]!));
                    continue;
                }
                panel.Children.Add(label);
                var picker = new ComboBox
                {
                    ItemsSource = field.Options, DisplayMemberPath = nameof(PluginConfigurationOption.Label),
                    SelectedValuePath = nameof(PluginConfigurationOption.Value), SelectedValue = field.Value
                };
                System.Windows.Automation.AutomationProperties.SetLabeledBy(picker, label);
                panel.Children.Add(picker);
                editors.Add(field.Key, () => picker.SelectedValue as string);
                picker.SelectionChanged += async (_, _) => await SaveChange(field.Key, editors[field.Key](),
                    () => picker.SelectedValue = values[field.Key]);
            }
        }

        void OpenOptions(DeviceBattery device)
        {
            if (saving) return;
            if (selectedDevice == device.Identity)
            {
                selectedDevice = null;
                generalSettings.Visibility = Visibility.Collapsed;
                settings.Visibility = Visibility.Collapsed;
                return;
            }

            selectedDevice = device.Identity;
            generalSettings.Visibility = Visibility.Visible;
            BuildOptions(options, device);
            var title = new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            options.Children.Insert(0, title);
            settings.Visibility = Visibility.Visible;
        }

        BuildOptions(generalOptions, null);
        Loaded += (_, _) =>
        {
            monitor.Changed += Show;
            Show();
        };
        Unloaded += (_, _) => monitor.Changed -= Show;

        void Show()
        {
            _status.Text = monitor.Status;
            if (ReferenceEquals(_devices.Tag, monitor.Snapshot)) return;
            _devices.Tag = monitor.Snapshot;
            _devices.Children.Clear();
            if (monitor.Snapshot is { } snapshot)
            {
                foreach (var device in snapshot.Devices.OrderByDescending(item => item.Online).ThenBy(item => item.Name))
                    {
                    var button = new Button { Content = CreateTile(device), Padding = new Thickness(0), Background = Brushes.Transparent };
                    System.Windows.Automation.AutomationProperties.SetName(button, $"Opciones de {device.Name}");
                    button.Click += (_, _) => OpenOptions(device);
                    button.MouseRightButtonUp += (_, args) => { args.Handled = true; OpenOptions(device); };
                    _devices.Children.Add(button);
                }
            }
        }
    }


    private static FrameworkElement CreateTile(DeviceBattery device)
    {
        var tile = new StackPanel { Width = 80, Margin = new Thickness(2, 3, 2, 5) };
        var face = new Grid { Width = 66, Height = 66 };
        face.Children.Add(new BatteryRing(device));

        var icon = new TextBlock
        {
            Text = DeviceIcon(device.Kind, device.Name), FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
        };
        var number = new TextBlock
        {
            Text = device.Level is int level and >= 0 and <= 100 ? level.ToString() : "–",
            FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0)
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        face.Children.Add(icon);
        face.Children.Add(number);
        tile.Children.Add(face);
        var name = new TextBlock
        {
            Text = device.Name, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center, FontSize = 11
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        tile.Children.Add(name);
        tile.ToolTip = device.Name + (device.Charging ? " · Cargando" : !device.Online ? " · Desconectado" :
            device.Approximate ? " · Nivel aproximado" : "");
        if (device.ShowEstimate && device.Online)
        {
            double hours = device.SecondsLeft.GetValueOrDefault() / 3600.0;
            string text = device.Charging ? "Cargando"
                : device.Approximate || device.Level is null ? "Sin estimación"
                : hours <= 0 ? "Calculando…"
                : hours < 1 ? $"≈ {Math.Max(1, Math.Round(hours * 60))} min" : $"≈ {hours:0.#} h";
            var estimate = new TextBlock { Text = text, FontSize = 10, TextAlignment = TextAlignment.Center };
            if (text == "Calculando…")
                estimate.ToolTip = "La estimación necesita al menos 30 min de uso y una caída de 3 puntos de batería.";
            estimate.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            tile.Children.Add(estimate);
        }
        if (!device.Online) tile.Opacity = 0.55;
        return tile;
    }

    private static string DeviceIcon(string kind, string name)
    {
        string label = (kind + " " + name).ToLowerInvariant();
        if (label.Contains("headset") || label.Contains("headphone") || label.Contains("buds")) return "🎧";
        if (label.Contains("mouse")) return "🖱";
        if (label.Contains("keyboard")) return "⌨";
        if (label.Contains("gamepad") || label.Contains("controller") || label.Contains("xbox")) return "🎮";
        if (label.Contains("phone")) return "📱";
        return "🔋";
    }
}

internal sealed class BatteryRing(DeviceBattery device) : FrameworkElement
{
    private const double ArcLength = 296;
    private const double StartAngle = 122;

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var track = TryFindResource("ProgressTrackBrush") as Brush ?? Brushes.DimGray;
        DrawArc(context, track, ArcLength);
        if (device.Level is not int level || level <= 0 || level > 100) return;

        Brush color = device.Charging ? Brushes.MediumSeaGreen
            : device.Band == BatteryBand.Critical ? Brushes.IndianRed
            : device.Band == BatteryBand.Low ? Brushes.Goldenrod
            : TryFindResource("AccentBrush") as Brush ?? Brushes.LightGray;
        DrawArc(context, color, ArcLength * level / 100);
    }

    private static void DrawArc(DrawingContext context, Brush brush, double angle)
    {
        const double radius = 27;
        Point At(double degrees)
        {
            double radians = degrees * Math.PI / 180;
            return new Point(33 + radius * Math.Cos(radians), 33 + radius * Math.Sin(radians));
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(StartAngle), false, false);
            path.ArcTo(At(StartAngle + angle), new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.DrawGeometry(null, pen, geometry);
    }
}
