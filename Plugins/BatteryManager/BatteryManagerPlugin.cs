using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNotch.Plugins;
using WinNotch.Views;

namespace PersonalCards.BatteryManager;

[WinNotchPlugin("personal.card.batterymanager", "BatteryManager", "1.0.4", "Nacho_114")]
public sealed class BatteryManagerPlugin : CardPluginBase
{
    public override string Id => "personal.card.batterymanager";
    public override string Name => "BatteryManager";
    public override string Version => "1.0.4";
    public override string Author => "Nacho_114";
    public override string Description => "Batería de periféricos Bluetooth y USB compatibles.";
    public override string CardKey => "batterymanager";
    public override string CardTitle => "Baterías";
    public override int CardOrder => 66;
    public override double CardHeight => 205;
    private BatteryMonitor? _monitor;

    protected override UIElement CreateCard() => new BatteryCard(GetMonitor());

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
        device => Context?.MainWindow is NotchWindow window &&
            window.TryShowDeviceBattery(device.Name, device.Level!.Value, device.Band == BatteryBand.Critical),
        message => Context?.Log("BatteryManager: " + message, PluginLogLevel.Debug));
}

internal enum BatteryBand { Normal, Low, Critical }

internal sealed record DeviceBattery(string Name, int? Level, bool Charging, bool Online, string Kind, bool Approximate, string? Key)
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

    public BatteryCard(BatteryMonitor monitor)
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(new ScrollViewer
        {
            Content = _devices, MaxHeight = 155, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Children.Add(_status);
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
                    _devices.Children.Add(CreateTile(device));
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
