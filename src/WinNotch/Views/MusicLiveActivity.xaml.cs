using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Views;

public partial class MusicLiveActivity : UserControl
{
    private MediaService? _mediaService;
    private AudioCaptureService? _audioCaptureService;
    private bool _userEnabled = true;
    private string _title = string.Empty;
    private string _lyricLine = string.Empty;
    private bool _lyricsLoading;

    /// <summary>
    /// Current synced lyric line; empty shows the song title instead. While the lyrics of a
    /// new song are loading nothing is shown, so the song-change notice comes before the lyrics.
    /// </summary>
    public void SetLyricLine(string line, bool loading = false)
    {
        _lyricLine = line ?? string.Empty;
        _lyricsLoading = loading;
        ShowText();
    }

    // A lyric line gets a fixed width: the closed notch is sized to its content, so a
    // free-width line would overflow its edges and make the notch jump on every line.
    private const double LyricWidth = 240;

    private void ShowText()
    {
        bool lyric = _lyricLine.Length > 0 || _lyricsLoading;
        CompactTitle.Text = _lyricsLoading ? string.Empty : lyric ? _lyricLine : _title;
        CompactTitle.Width = lyric ? LyricWidth : double.NaN;
        CompactTitle.TextAlignment = lyric ? TextAlignment.Center : TextAlignment.Left;
        CompactArtBox.Visibility = lyric ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Reflects the ShowMusicControls setting; gates self-show on media changes.</summary>
    public bool UserEnabled
    {
        get => _userEnabled;
        set
        {
            _userEnabled = value;
            if (_mediaService != null)
                UpdateCompact(_mediaService.MediaInfo, null);
        }
    }

    public MusicLiveActivity()
    {
        InitializeComponent();
    }

    public void Bind(MediaService mediaService, AudioCaptureService audioCaptureService)
    {
        _mediaService = mediaService;
        _audioCaptureService = audioCaptureService;

        var info = mediaService.MediaInfo;
        info.PropertyChanged += (_, e) =>
        {
            Dispatcher.Invoke(() => UpdateCompact(info, e.PropertyName));
        };

        audioCaptureService.SpectrumUpdated += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (CompactVisualizer.IsVisible && audioCaptureService.SpectrumData != null)
                    CompactVisualizer.UpdateSpectrum(audioCaptureService.SpectrumData);
            });
        };

        UpdateCompact(info, null);
    }

    private void UpdateCompact(MediaInfo info, string? propertyName)
    {
        // Gate visibility, but keep content in sync even while hidden —
        // otherwise a title change during pause shows stale text on resume.
        Visibility = _userEnabled && info.HasMedia && info.IsPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (propertyName == null || propertyName == nameof(info.AlbumArt))
            CompactAlbumArt.Source = info.AlbumArt;

        if (propertyName == null || propertyName == nameof(info.Title))
        {
            _title = info.Title;
            ShowText();
        }

        if (propertyName == null || propertyName == nameof(info.DominantColor))
        {
            var color = info.DominantColor;
            // Ensure the visualizer bar color is always visible against the dark notch
            // If dominant color is too dark, use white instead
            double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            CompactVisualizer.BarColor = brightness < 0.3 ? Colors.White : color;
        }
    }
}
