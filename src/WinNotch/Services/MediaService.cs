using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinNotch.Models;

namespace WinNotch.Services;

public class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly DispatcherTimer _positionTimer;
    private DateTime _lastTimelineUpdate = DateTime.MinValue;
    private TimeSpan _basePosition = TimeSpan.Zero;

    public MediaInfo MediaInfo { get; } = new();

    public event Action? SessionChanged;

    public MediaService()
    {
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200) // Update more frequently for smoother progress
        };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MediaService init failed: {ex.Message}");
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AttachSession(sender.GetCurrentSession());
            SessionChanged?.Invoke();
        });
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        // Detach old
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        _requestedRepeat = null;

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;

            MediaInfo.HasMedia = true;
            _ = UpdateMediaPropertiesAsync();
            UpdatePlaybackInfo();
            UpdateTimeline();
            _positionTimer.Start();
        }
        else
        {
            MediaInfo.HasMedia = false;
            MediaInfo.IsPlaying = false;
            // Clear stale metadata so nothing from the old session lingers
            MediaInfo.Title = string.Empty;
            MediaInfo.Artist = string.Empty;
            MediaInfo.AlbumTitle = string.Empty;
            MediaInfo.AlbumArt = null;
            MediaInfo.Position = TimeSpan.Zero;
            MediaInfo.Duration = TimeSpan.Zero;
            _positionTimer.Stop();
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Application.Current?.Dispatcher.Invoke(() => _ = UpdateMediaPropertiesAsync());
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Application.Current?.Dispatcher.Invoke(UpdatePlaybackInfo);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Application.Current?.Dispatcher.Invoke(UpdateTimeline);
    }

    private async Task UpdateMediaPropertiesAsync()
    {
        if (_session == null) return;
        var session = _session;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || session != _session) return;

            MediaInfo.Title = props.Title ?? string.Empty;
            MediaInfo.Artist = props.Artist ?? string.Empty;
            MediaInfo.AlbumTitle = props.AlbumTitle ?? string.Empty;

            // Load album art
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    if (session != _session) return;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream.AsStreamForRead();
                    bitmap.DecodePixelWidth = 300;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    MediaInfo.AlbumArt = bitmap;

                    // Extract dominant color
                    MediaInfo.DominantColor = ColorExtractionService.GetDominantColor(bitmap);
                }
                catch
                {
                    MediaInfo.AlbumArt = null;
                }
            }
            else
            {
                MediaInfo.AlbumArt = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateMediaProperties failed: {ex.Message}");
        }
    }

    private void UpdatePlaybackInfo()
    {
        if (_session == null) return;

        try
        {
            var info = _session.GetPlaybackInfo();
            if (info == null) return;

            bool wasPlaying = MediaInfo.IsPlaying;
            MediaInfo.IsPlaying = info.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // Reset timeline timestamp when playback state changes
            if (wasPlaying != MediaInfo.IsPlaying)
            {
                _lastTimelineUpdate = DateTime.MinValue;
                UpdateTimeline();
            }

            MediaInfo.IsShuffle = info.IsShuffleActive ?? false;

            int reported = info.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.List => 1,
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => 2,
                _ => 0
            };
            MediaInfo.RepeatMode = ResolveRepeatMode(reported);
        }
        catch { }
    }

    // Repeat as requested from WinNotch. Players report changes late (Spotify: 1-2 s), and
    // Spotify reports "repeat one" as "repeat all": trusting the report alone kept the
    // button cycling between all and one, never reaching off.
    private int? _requestedRepeat;
    private DateTime _requestedRepeatAt;
    private static readonly TimeSpan RepeatReportDelay = TimeSpan.FromSeconds(3);

    private int ResolveRepeatMode(int reported)
    {
        if (_requestedRepeat is not int requested) return reported;
        if (reported == requested) return requested;

        // Still waiting for the player to catch up with the request.
        if (DateTime.UtcNow - _requestedRepeatAt < RepeatReportDelay) return requested;

        // "Repeat one" that the player reports as "repeat all".
        if (requested == 2 && reported == 1) return requested;

        // Changed from the player itself: trust the report again.
        _requestedRepeat = null;
        return reported;
    }

    private void UpdateTimeline()
    {
        if (_session == null) return;

        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline == null) return;

            // Players report Position as of LastUpdatedTime, which can be several seconds old.
            // Advance it to "now" while playing, or every resync snaps the bar backwards.
            var position = timeline.Position;
            if (MediaInfo.IsPlaying)
            {
                var age = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (age > TimeSpan.Zero && age < TimeSpan.FromHours(1))
                    position += age;
            }
            if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                position = timeline.EndTime;

            _basePosition = position;
            MediaInfo.Position = _basePosition;
            MediaInfo.Duration = timeline.EndTime;
            _lastTimelineUpdate = DateTime.UtcNow;
        }
        catch { }
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_session == null || !MediaInfo.IsPlaying) return;

        // Interpolate position locally for smooth updates between timeline queries
        if (_lastTimelineUpdate != DateTime.MinValue && MediaInfo.Duration.TotalSeconds > 0)
        {
            double elapsed = (DateTime.UtcNow - _lastTimelineUpdate).TotalSeconds;
            var interpolatedPosition = _basePosition + TimeSpan.FromSeconds(elapsed);

            // Clamp to duration to prevent overflow
            MediaInfo.Position = interpolatedPosition <= MediaInfo.Duration
                ? interpolatedPosition
                : MediaInfo.Duration;
        }

        // Query actual timeline every second to resync and prevent drift
        if ((DateTime.UtcNow - _lastTimelineUpdate).TotalSeconds >= 1.0)
        {
            UpdateTimeline();
        }
    }

    // Transport controls
    public async Task PlayPauseAsync()
    {
        if (_session == null) return;
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async Task ToggleShuffleAsync()
    {
        if (_session == null) return;
        try { await _session.TryChangeShuffleActiveAsync(!MediaInfo.IsShuffle); } catch { }
    }

    public async Task CycleRepeatAsync()
    {
        if (_session == null) return;
        try
        {
            int nextMode = (MediaInfo.RepeatMode + 1) % 3;
            var next = nextMode switch
            {
                1 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
            };

            // Show the new mode right away; late reports from the player must not undo it.
            _requestedRepeat = nextMode;
            _requestedRepeatAt = DateTime.UtcNow;
            MediaInfo.RepeatMode = nextMode;

            if (!await _session.TryChangeAutoRepeatModeAsync(next))
            {
                _requestedRepeat = null;
                UpdatePlaybackInfo();
            }
        }
        catch { }
    }

    public async Task SeekToAsync(double percent)
    {
        if (_session == null || MediaInfo.Duration.TotalSeconds <= 0) return;
        try
        {
            var targetTicks = (long)(percent * MediaInfo.Duration.Ticks);
            await _session.TryChangePlaybackPositionAsync(targetTicks);

            // Reset timeline timestamp to force immediate update after seek
            _lastTimelineUpdate = DateTime.MinValue;
        }
        catch { }
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
    }
}
