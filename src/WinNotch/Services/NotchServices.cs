using System;
using System.Threading.Tasks;
using WinNotch.Models;

namespace WinNotch.Services;

/// <summary>
/// Container for the system-level services shared by every notch window.
/// Exactly one instance exists per app run: with "island on all screens"
/// enabled, all windows bind to the same media session, audio capture,
/// volume endpoint, etc. instead of duplicating them.
/// Owned and disposed by <see cref="WinNotch.App"/>.
/// </summary>
public sealed class NotchServices : IDisposable
{
    public MediaService Media { get; } = new();
    public AudioCaptureService AudioCapture { get; } = new(bandCount: 12);
    public VolumeService Volume { get; } = new();
    public BrightnessService Brightness { get; } = new();
    public BatteryService Battery { get; } = new();
    public CalendarService Calendar { get; } = new();
    public ShelfService Shelf { get; } = new();
    public FullscreenService Fullscreen { get; } = new();
    public CapsLockService CapsLock { get; } = new();
    public MediaFeedbackService MediaFeedback { get; } = new();
    public WebcamService Webcam { get; } = new();
    public LyricsService Lyrics { get; } = new();

    private Task? _initTask;

    /// <summary>
    /// One-time global initialization; safe to await from every window
    /// (the first caller runs it, later callers await the same task).
    /// </summary>
    public Task EnsureInitializedAsync(AppSettings settings)
    {
        return _initTask ??= InitializeAsync(settings);
    }

    private async Task InitializeAsync(AppSettings settings)
    {
        await Media.InitializeAsync();
        MediaFeedback.Initialize(Media.MediaInfo);
        Lyrics.Bind(Media.MediaInfo);
        ApplyFeedbackSettings(settings);
        AudioCapture.Start();
        Volume.Initialize();
        Brightness.Initialize();
        Battery.Initialize();
        Calendar.Initialize();
        Webcam.SetTargetFps(settings.WebcamFps);
        Fullscreen.Start();
    }

    public void Dispose()
    {
        CapsLock.Dispose();
        MediaFeedback.Dispose();
        AudioCapture.Dispose();
        Media.Dispose();
        Volume.Dispose();
        Brightness.Dispose();
        Battery.Dispose();
        Calendar.Dispose();
        Fullscreen.Dispose();
        Webcam.Dispose();
        Lyrics.Dispose();
    }

    public void ApplyFeedbackSettings(AppSettings settings)
    {
        if (settings.ShowCapsLockFeedback) CapsLock.Start(); else CapsLock.Stop();
        MediaFeedback.SetEnabled(settings.ShowMusicFeedback);
        Lyrics.SetEnabled(settings.ShowMusicControls && settings.ShowLyrics);
    }

}
