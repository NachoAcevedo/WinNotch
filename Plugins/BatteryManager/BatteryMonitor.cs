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
    // Al iniciar con Windows el Bluetooth puede no estar listo: si la primera lectura viene
    // vacía se reintenta seguido un rato antes de decir que no hay dispositivos.
    private readonly DispatcherTimer _startupRetry = new() { Interval = TimeSpan.FromSeconds(10) };
    private const int StartupRetries = 6;
    private int _emptyScans;
    private bool _foundAny;
    private bool _hasBaseline;
    private readonly HashSet<string> _online = new();
    private readonly Dictionary<string, DeviceBattery> _connections = new();
    private readonly Dictionary<string, DeviceBattery> _lastReadings = new();
    private readonly Dictionary<string, DeviceBattery> _full = new();
    private BatteryPreferences _preferences;
    private BatterySnapshot? _rawSnapshot;
    private readonly Dictionary<string, DeviceBattery> _pending = new();
    private readonly Func<DeviceBattery, BatteryAlert, bool> _showAlert;
    private readonly Action<string> _log;
    private CancellationTokenSource? _scan;

    public BatteryMonitor(BatteryPreferences preferences, Func<DeviceBattery, BatteryAlert, bool> showAlert, Action<string> log)
    {
        _preferences = preferences;
        _poll.Interval = TimeSpan.FromSeconds(preferences.IntervalSeconds is 15 or 30 or 60 or 120 or 300
            ? preferences.IntervalSeconds : 120);
        _showAlert = showAlert;
        _log = log;
        _poll.Tick += async (_, _) => await RefreshAsync();
        _nextAlert.Tick += (_, _) => ShowNextAlert();
        _startupRetry.Tick += async (_, _) =>
        {
            _startupRetry.Stop();
            await RefreshAsync();
        };
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
        _startupRetry.Stop();
        _emptyScans = 0;
        _foundAny = false;
        _hasBaseline = false;
        _online.Clear();
        _connections.Clear();
        _scan?.Cancel();
        _lastReadings.Clear();
        _full.Clear();
        _rawSnapshot = null;
        Snapshot = null;
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

        process.StartInfo.ArgumentList.Add("--history");
        process.StartInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(reader)!, "..", "history.json"));
        if (_preferences.PlayStationFullMode) process.StartInfo.ArgumentList.Add("--playstation-full-mode");

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
            _log("lectura cancelada o vencida");
        }
        catch (Exception error) when (error is IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus("No se pudieron leer las baterías.");
            _log("lectura fallida: " + error.Message);
        }
        finally { _scan = null; }
    }

    internal void Apply(BatterySnapshot snapshot)
    {
        _log($"lectura: {snapshot.Devices.Length} dispositivos, {snapshot.Failures} lectores sin respuesta");
        if (snapshot.Devices.Length > 0) _foundAny = true;
        else if (!_foundAny && ++_emptyScans < StartupRetries)
        {
            UpdateStatus("Buscando dispositivos…");
            _startupRetry.Start();
            return;
        }

        _rawSnapshot = snapshot;

        var online = new HashSet<string>();
        foreach (var device in snapshot.Devices)
        {
            if (!device.Online || device.Level is not (>= 0 and <= 100)) continue;
            string key = device.Identity;
            online.Add(key);
            if ((_hasBaseline && !_online.Contains(key)) || _connections.ContainsKey(key))
                _connections[key] = device;

            _lastReadings.TryGetValue(key, out var previous);
            _lastReadings[key] = device;
            if (!IsLow(device))
                _pending.Remove(key);
            else if ((previous != null && !IsLow(previous)) || _pending.ContainsKey(key))
                _pending[key] = device;

            if (!_preferences.For(key).NotifyFull || device.Level != 100)
                _full.Remove(key);
            else if (_online.Contains(key) && previous is { Charging: true, Level: < 100 })
                _full[key] = device;
        }

        if (snapshot.Failures == 0)
        {
            _online.IntersectWith(online);
            foreach (var queue in new[] { _pending, _full, _connections })
                foreach (var key in queue.Keys.Where(key => !online.Contains(key)).ToArray())
                    queue.Remove(key);
            _hasBaseline = true;
        }
        _online.UnionWith(online);
        UpdateDisplay();
        if (!_nextAlert.IsEnabled) ShowNextAlert();
    }

    public void SetPreferences(BatteryPreferences preferences)
    {
        _preferences = preferences;
        _poll.Interval = TimeSpan.FromSeconds(preferences.IntervalSeconds is 15 or 30 or 60 or 120 or 300
            ? preferences.IntervalSeconds : 120);
        foreach (var key in _pending.Keys.Where(key => !IsLow(_pending[key])).ToArray())
            _pending.Remove(key);
        foreach (var key in _full.Keys.Where(key => !preferences.For(key).NotifyFull).ToArray())
            _full.Remove(key);
        UpdateDisplay();
    }

    private bool IsLow(DeviceBattery device) =>
        _preferences.For(device.Identity).LowThreshold is > 0 and var threshold &&
        !device.Charging && device.Level <= threshold;

    private void UpdateDisplay()
    {
        if (_rawSnapshot is not { } raw) return;
        var devices = raw.Devices;
        {
            var current = devices.Select(device => device.Identity).ToHashSet();
            devices = devices
                .Select(device => !device.Online && _preferences.For(device.Identity).KeepOffline && _lastReadings.TryGetValue(device.Identity, out var last)
                    ? last with { Online = false, Charging = false } : device)
                .Concat(_lastReadings.Values.Where(device => !current.Contains(device.Identity) && _preferences.For(device.Identity).KeepOffline)
                    .Select(device => device with { Online = false, Charging = false }))
                .ToArray();
        }
        Snapshot = raw with
        {
            Devices = devices.Select(device => _preferences.For(device.Identity).ShowRemaining
                ? device : device with { SecondsLeft = null }).ToArray()
        };
        UpdateStatus(raw.Failures > 0 ? "Algunos lectores no respondieron."
            : devices.Length == 0 ? "No se detectaron dispositivos compatibles." : "");
    }

    private void ShowNextAlert()
    {
        _nextAlert.Stop();
        var alert = _pending.Values.OrderBy(device => device.Level).FirstOrDefault();
        var kind = BatteryAlert.Low;
        if (alert is null)
        {
            alert = _full.Values.FirstOrDefault();
            kind = BatteryAlert.Full;
        }
        if (alert is null)
        {
            alert = _connections.Values.FirstOrDefault();
            kind = BatteryAlert.Connection;
        }
        if (alert is null || !_showAlert(alert, kind)) return;
        _pending.Remove(alert.Identity);
        _full.Remove(alert.Identity);
        _connections.Remove(alert.Identity);
        if (_pending.Count > 0 || _full.Count > 0 || _connections.Count > 0) _nextAlert.Start();
    }

    private void UpdateStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
