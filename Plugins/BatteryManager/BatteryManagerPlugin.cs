using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinNotch.Models;
using WinNotch.Plugins;

namespace PersonalCards.BatteryManager;

[WinNotchPlugin("personal.card.batterymanager", "BatteryManager", "1.0.2", "Nacho_114")]
public sealed class BatteryManagerPlugin : CardPluginBase
{
    public override string Id => "personal.card.batterymanager";
    public override string Name => "BatteryManager";
    public override string Version => "1.0.2";
    public override string Author => "Nacho_114";
    public override string Description => "Batería de periféricos Bluetooth y USB compatibles.";
    public override string CardKey => "batterymanager";
    public override string CardTitle => "Baterías";
    public override int CardOrder => 66;
    public override double CardHeight => 205;
    private BatteryCard? _card;

    protected override UIElement CreateCard() =>
        _card = new BatteryCard(Context?.NotchViewModel.NotchState == NotchState.Open);

    public override void OnNotchStateChanged(NotchState state) =>
        _card?.SetNotchOpen(state == NotchState.Open);
}

internal sealed record DeviceBattery(string Name, int? Level, bool Charging, bool Online, string Kind, bool Approximate);
internal sealed record BatterySnapshot(DeviceBattery[] Devices, int Failures);

internal sealed class BatteryCard : StackPanel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly WrapPanel _devices = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? _scan;
    private DateTime _lastScanUtc;

    private bool _notchOpen;
    private bool CanScan => _notchOpen && IsLoaded && IsVisible;

    public BatteryCard(bool notchOpen)
    {
        _notchOpen = notchOpen;
        var refresh = new Button { Content = "Actualizar", HorizontalAlignment = HorizontalAlignment.Right };
        refresh.Click += async (_, _) => await RefreshAsync(force: true);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        refresh.VerticalAlignment = VerticalAlignment.Top;
        refresh.Margin = new Thickness(4, 2, 0, 0);
        var content = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        content.Children.Add(refresh);
        content.Children.Add(new ScrollViewer { Content = _devices, MaxHeight = 155, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Children.Add(content);
        Children.Add(_status);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await StartAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) await StartAsync();
            else Stop();
        };
        Unloaded += (_, _) => Stop();
    }

    public async void SetNotchOpen(bool open)
    {
        _notchOpen = open;
        if (open) await StartAsync();
        else Stop();
    }

    private async Task StartAsync()
    {
        if (!CanScan) return;
        _timer.Start();
        await RefreshAsync();
    }

    private void Stop()
    {
        _timer.Stop();
        _scan?.Cancel();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (!CanScan || _scan != null || (!force && DateTime.UtcNow - _lastScanUtc < _timer.Interval))
            return;

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "BatteryManager");
        string reader = Path.Combine(folder, "Reader", "BatteryReader.exe");
        if (!File.Exists(reader))
        {
            _status.Text = "Falta el lector de BatteryManager en la carpeta Reader.";
            return;
        }

        using var scan = new CancellationTokenSource();
        _scan = scan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scan.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(reader)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(reader)!
            }
        };

        try
        {
            _status.Text = "Buscando dispositivos…";
            process.Start();
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
                if (process.ExitCode != 0) throw new IOException("El lector no terminó correctamente.");
                var snapshot = JsonSerializer.Deserialize<BatterySnapshot>(await output, JsonOptions)
                    ?? throw new JsonException("Respuesta vacía.");
                timeout.Token.ThrowIfCancellationRequested();
                Show(snapshot);
                _lastScanUtc = DateTime.UtcNow;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!scan.IsCancellationRequested) _status.Text = "La lectura tardó demasiado.";
            else _status.Text = "";
        }
        catch (Exception error) when (error is IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "No se pudieron leer las baterías.";
        }
        finally { _scan = null; }
    }

    private void Show(BatterySnapshot snapshot)
    {
        _devices.Children.Clear();
        foreach (var device in snapshot.Devices.OrderByDescending(item => item.Online).ThenBy(item => item.Name))
            _devices.Children.Add(CreateTile(device));

        _status.Text = snapshot.Failures > 0 ? "Algunos lectores no respondieron."
            : snapshot.Devices.Length == 0 ? "No se detectaron dispositivos compatibles." : "";
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
            : level <= 15 ? Brushes.IndianRed
            : level <= 30 ? Brushes.Goldenrod
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
