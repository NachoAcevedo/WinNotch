using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WinNotch.Services;

/// <summary>Monitors level and mute on a default audio endpoint.</summary>
public class VolumeService : IDisposable
{
    private readonly object _sync = new();
    private readonly DataFlow _flow;
    private bool _lastMuted;

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
                System.Diagnostics.Debug.WriteLine($"VolumeService init failed: {ex.Message}");
            }
        }
    }

    // Windows callbacks only queue work: never release COM objects from inside
    // an IMMNotificationClient callback or block the Windows notification thread.
    private void QueueEndpointRefresh()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_sync)
            {
                if (!_disposed) RefreshEndpoint();
            }
        });
    }

    // Called under _sync at startup, on device events, or before changing volume.
    private void RefreshEndpoint()
    {
        MMDevice? next = null;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            next = _enumerator.GetDefaultAudioEndpoint(_flow, _role);
            if (_device?.ID == next.ID)
                return;

            ReleaseEndpoint();
            var endpoint = next.AudioEndpointVolume;
            _lastMuted = endpoint.Mute;
            endpoint.OnVolumeNotification += OnVolumeNotification;
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
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        float volume;
        bool muted;
        bool muteChanged;
        lock (_sync)
        {
            if (_disposed || _endpointVolume == null) return;
            try
            {
                // A previous output's callback may already be queued during a switch.
                volume = _endpointVolume.MasterVolumeLevelScalar;
                muted = _endpointVolume.Mute;
                muteChanged = muted != _lastMuted;
                _lastMuted = muted;
            }
            catch (Exception)
            {
                return; // Device-change notifications will reconnect the output.
            }
        }
        VolumeChanged?.Invoke(volume, muted);
        if (muteChanged) MuteChanged?.Invoke(muted);
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
        if (_endpointVolume != null)
            _endpointVolume.OnVolumeNotification -= OnVolumeNotification;
        _endpointVolume = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_deviceNotifications != null && _enumerator != null)
                _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications);
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
