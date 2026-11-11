using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace PersonalCards.BatteryManager;

internal sealed class BatteryMonitor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly DispatcherTimer _nextAlert = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, BatteryBand> _bands = new();
    private readonly Dictionary<string, DeviceBattery> _pending = new();
    private readonly Func<DeviceBattery, bool> _showAlert;
    private CancellationTokenSource? _scan;

    public BatteryMonitor(Func<DeviceBattery, bool> showAlert)
    {
        _showAlert = showAlert;
        _poll.Tick += async (_, _) => await RefreshAsync();
        _nextAlert.Tick += (_, _) => ShowNextAlert();
    }

    public BatterySnapshot? Snapshot { get; private set; }
    public string Status { get; private set; } = "Buscando dispositivos…";
    public event Action? Changed;

    public void Start()
    {
        if (_poll.IsEnabled) return;
        _poll.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _poll.Stop();
        _nextAlert.Stop();
        _scan?.Cancel();
        _bands.Clear();
        _pending.Clear();
    }

    private async Task RefreshAsync()
    {
        if (!_poll.IsEnabled || _scan != null) return;

        string reader = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinNotch", "BatteryManager", "Reader", "BatteryReader.exe");
        if (!File.Exists(reader))
        {
            UpdateStatus("Falta el lector de BatteryManager en la carpeta Reader.");
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
            if (Snapshot is null) UpdateStatus("Buscando dispositivos…");
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
                Apply(snapshot);
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
            if (!scan.IsCancellationRequested) UpdateStatus("La lectura tardó demasiado.");
        }
        catch (Exception error) when (error is IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus("No se pudieron leer las baterías.");
        }
        finally { _scan = null; }
    }

    internal void Apply(BatterySnapshot snapshot)
    {
        Snapshot = snapshot;
        UpdateStatus(snapshot.Failures > 0 ? "Algunos lectores no respondieron."
            : snapshot.Devices.Length == 0 ? "No se detectaron dispositivos compatibles." : "");

        var online = new HashSet<string>();
        foreach (var device in snapshot.Devices)
        {
            if (!device.Online || device.Level is not (>= 0 and <= 100)) continue;
            string key = device.Identity;
            online.Add(key);

            var band = device.Band;
            bool known = _bands.TryGetValue(key, out var previous);
            _bands[key] = band;
            if (device.Charging || band == BatteryBand.Normal || (known && band < previous))
                _pending.Remove(key);
            else if ((known && band > previous) || _pending.ContainsKey(key))
                _pending[key] = device;
        }
        foreach (var key in _pending.Keys.Where(key => !online.Contains(key)).ToArray())
            _pending.Remove(key);
        if (!_nextAlert.IsEnabled) ShowNextAlert();
    }

    private void ShowNextAlert()
    {
        _nextAlert.Stop();
        var alert = _pending.Values.OrderByDescending(device => device.Band).FirstOrDefault();
        if (alert is null || !_showAlert(alert)) return;
        _pending.Remove(alert.Identity);
        if (_pending.Count > 0) _nextAlert.Start();
    }

    private void UpdateStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
