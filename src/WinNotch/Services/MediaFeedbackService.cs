using System.ComponentModel;
using System.Windows.Threading;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class MediaFeedbackService : IDisposable
{
    private MediaInfo? _media;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private string _title = "";
    private string _artist = "";
    private bool _playing;
    private bool _hadMedia;
    private DateTime _quietUntil;
    private bool _enabled = true;
    public event Action<string, string, string>? Changed;

    public MediaFeedbackService() => _timer.Tick += (_, _) => Publish();
    public void Initialize(MediaInfo media)
    {
        _media = media;
        Snapshot();
        _quietUntil = DateTime.UtcNow.AddSeconds(2);
        media.PropertyChanged += OnChanged;
    }
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _timer.Stop();
        Snapshot();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MediaInfo.Title) or nameof(MediaInfo.Artist) or nameof(MediaInfo.IsPlaying) or nameof(MediaInfo.HasMedia))) return;
        if (!_enabled) { Snapshot(); return; }
        _timer.Stop();
        _timer.Start();
    }
    private void Snapshot()
    {
        if (_media == null) return;
        _title = _media.Title;
        _artist = _media.Artist;
        _playing = _media.IsPlaying;
        _hadMedia = _media.HasMedia;
    }
    private void Publish()
    {
        _timer.Stop();
        if (_media == null) return;
        bool trackChanged = _media.Title != _title || _media.Artist != _artist;
        bool playbackChanged = _media.IsPlaying != _playing;
        if (_enabled && DateTime.UtcNow >= _quietUntil && _media.HasMedia && !string.IsNullOrWhiteSpace(_media.Title)
            && (trackChanged || playbackChanged || !_hadMedia))
        {
            string action = trackChanged && _hadMedia ? "Siguiente canción" : _media.IsPlaying ? "Reproduciendo" : "Pausado";
            string icon = trackChanged ? "♫" : _media.IsPlaying ? "▶" : "⏸";
            string artist = string.IsNullOrWhiteSpace(_media.Artist) ? string.Empty : _media.Artist;
            string detail = string.IsNullOrEmpty(artist) ? action : $"{artist} · {action}";
            Changed?.Invoke(icon, _media.Title, detail);
        }
        Snapshot();
    }
    public void Dispose()
    {
        _timer.Stop();
        if (_media != null) _media.PropertyChanged -= OnChanged;
    }
}
