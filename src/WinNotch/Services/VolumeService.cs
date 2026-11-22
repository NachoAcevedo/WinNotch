using System;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WinNotch.Services;

/// <summary>Monitors level and mute on a default audio endpoint.</summary>
public class VolumeService : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AudioEndpointVolumeNotificationDelegate? _volumeNotification;
    private readonly DataFlow _flow;
    private bool _lastMuted;
    private DispatcherTimer? _refreshTimer;
    internal event Action? EndpointChanged;

    public VolumeService() : this(DataFlow.Render) { }

    private readonly Role _role;

    internal VolumeService(DataFlow flow, Role role = Role.Multimedia)
    {
        _flow = flow;
        _role = role;
    }

    internal string? EndpointId
    {
        get { lock (_sync) return _device?.ID; }
    }
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpointVolume;
    private DeviceNotifications? _deviceNotifications;
    private bool _disposed;

    public float Volume
    {
        get { lock (_sync) return _endpointVolume?.MasterVolumeLevelScalar ?? 0f; }
    }

    public bool IsMuted
    {
        get { lock (_sync) return _endpointVolume?.Mute ?? false; }
    }

    public event Action<float, bool>? VolumeChanged;
    internal event Action<bool>? MuteChanged;

    public void Initialize()
    {
        lock (_sync)
        {
            if (_disposed || _deviceNotifications != null) return;
            try
            {
                _enumerator = new MMDeviceEnumerator();
                var notifications = new DeviceNotifications(this);
                _enumerator.RegisterEndpointNotificationCallback(notifications);
                _deviceNotifications = notifications;
                // Register before the initial lookup so a concurrent switch is not lost.
                RefreshEndpoint();
            }
            catch (Exception ex)
            {
                _enumerator?.Dispose();
                _enumerator = null;
                _deviceNotifications = null;
                System.Diagnostics.Debug.WriteLine($"VolumeService init failed: {ex.Message}");
            }
        }
    }

    internal void Reconnect()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _refreshTimer?.Stop();
            try
            {
                if (_deviceNotifications != null && _enumerator != null)
                    _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications);
            }
            catch (System.Runtime.InteropServices.COMException) { }
            finally
            {
                _deviceNotifications = null;
                ReleaseEndpoint();
                try { _enumerator?.Dispose(); }
                catch (System.Runtime.InteropServices.COMException) { }
                _enumerator = null;
            }
            Initialize();
        }
    }

    // Collapse device bursts on the UI dispatcher; never release COM inside a callback.
    private void QueueEndpointRefresh()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _refreshTimer.Tick += (_, _) =>
                {
                    _refreshTimer.Stop();
                    lock (_sync)
                        if (!_disposed) RefreshEndpoint(reconnect: true);
                };
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    // Called under _sync at startup, on device events, or before changing volume.
    private void RefreshEndpoint(bool reconnect = false)
    {
        MMDevice? next = null;
        bool changed = false;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            next = _enumerator.GetDefaultAudioEndpoint(_flow, _role);
            if (!reconnect && _device?.ID == next.ID)
                return;

            changed = true;
            ReleaseEndpoint();
            var endpoint = next.AudioEndpointVolume;
            _lastMuted = endpoint.Mute;
            _volumeNotification = data => _dispatcher.BeginInvoke(() => PublishVolumeChange(endpoint, data));
            endpoint.OnVolumeNotification += _volumeNotification;
            _device = next;
            _endpointVolume = endpoint;
            next = null;
        }
        catch (Exception ex)
        {
            ReleaseEndpoint();
            System.Diagnostics.Debug.WriteLine($"VolumeService reconnect failed: {ex.Message}");
        }
        finally
        {
            next?.Dispose();
            if (changed) EndpointChanged?.Invoke();
        }
    }

    private void PublishVolumeChange(AudioEndpointVolume endpoint, AudioVolumeNotificationData data)
    {
        bool muteChanged;
        lock (_sync)
        {
            // A queued event from the previous default device must not update this HUD.
            if (_disposed || !ReferenceEquals(endpoint, _endpointVolume)) return;
            muteChanged = data.Muted != _lastMuted;
            _lastMuted = data.Muted;
        }
        VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
        if (muteChanged) MuteChanged?.Invoke(data.Muted);
    }
    public void SetVolume(float level)
    {
        lock (_sync)
        {
            if (_disposed) return;
            RefreshEndpoint();
            if (_endpointVolume != null)
                _endpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
        }
    }

    public void ToggleMute()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RefreshEndpoint();
            if (_endpointVolume != null)
                _endpointVolume.Mute = !_endpointVolume.Mute;
        }
    }

    private void ReleaseEndpoint()
    {
        var endpoint = _endpointVolume;
        var notification = _volumeNotification;
        var device = _device;
        _volumeNotification = null;
        _endpointVolume = null;
        _device = null;
        try
        {
            if (endpoint != null && notification != null)
                endpoint.OnVolumeNotification -= notification;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        finally
        {
            try { device?.Dispose(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshTimer?.Stop();
            try
            {
                if (_deviceNotifications != null && _enumerator != null)
                    _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications);
            }
            catch (System.Runtime.InteropServices.COMException) { }
            _deviceNotifications = null;
            ReleaseEndpoint();
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private sealed class DeviceNotifications(VolumeService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
        {
            if (flow == owner._flow && role == owner._role)
                owner.QueueEndpointRefresh();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.QueueEndpointRefresh();
        public void OnDeviceAdded(string deviceId) => owner.QueueEndpointRefresh();
        public void OnDeviceRemoved(string deviceId) => owner.QueueEndpointRefresh();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
