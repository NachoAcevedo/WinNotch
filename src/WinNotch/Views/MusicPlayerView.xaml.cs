using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Views;

public partial class MusicPlayerView : UserControl
{
    private MediaService? _mediaService;

    /// <summary>The user pressed the lyrics button; the window owns the setting.</summary>
    public event Action? LyricsToggleRequested;

    public MusicPlayerView()
    {
        InitializeComponent();
    }

    /// <summary>Highlights the lyrics button while lyrics are on.</summary>
    public void SetLyricsEnabled(bool enabled) =>
        System.Windows.Controls.Primitives.Selector.SetIsSelected(LyricsButton, enabled);

    private void OnLyricsClick(object sender, RoutedEventArgs e) => LyricsToggleRequested?.Invoke();

    public void Bind(MediaService mediaService)
    {
        _mediaService = mediaService;
        var info = mediaService.MediaInfo;

        info.PropertyChanged += (_, e) =>
        {
            Dispatcher.Invoke(() => UpdateUI(info, e.PropertyName));
        };

        mediaService.SessionChanged += () =>
        {
            Dispatcher.Invoke(() => UpdateAll(info));
        };

        UpdateAll(info);
    }

    private void UpdateAll(MediaInfo info)
    {
        UpdateUI(info, null);
    }

    private void UpdateUI(MediaInfo info, string? propertyName)
    {
        // Visibility
        if (!info.HasMedia)
        {
            NoMediaText.Visibility = Visibility.Visible;
            AlbumArtGrid.Visibility = Visibility.Collapsed;
            InfoControlsGrid.Visibility = Visibility.Collapsed;
            return;
        }
        NoMediaText.Visibility = Visibility.Collapsed;
        AlbumArtGrid.Visibility = Visibility.Visible;
        InfoControlsGrid.Visibility = Visibility.Visible;

        if (propertyName == null || propertyName == nameof(info.Title))
            TitleText.Text = info.Title;

        if (propertyName == null || propertyName == nameof(info.Artist))
            ArtistText.Text = info.Artist;

        if (propertyName == null || propertyName == nameof(info.AlbumArt))
        {
            AlbumArtImage.Source = info.AlbumArt;
        }

        if (propertyName == null || propertyName == nameof(info.IsPlaying))
            PlayPauseIcon.Data = (Geometry)FindResource(info.IsPlaying ? "TransportPause" : "TransportPlay");

        if (propertyName == null || propertyName == nameof(info.Position) || propertyName == nameof(info.ProgressPercent))
        {
            ElapsedText.Text = FormatTime(info.Position);
            UpdateProgressBar(info.ProgressPercent);
        }

        if (propertyName == null || propertyName == nameof(info.Duration))
            DurationText.Text = FormatTime(info.Duration);

        if (propertyName == null || propertyName == nameof(info.IsShuffle))
            System.Windows.Controls.Primitives.Selector.SetIsSelected(ShuffleButton, info.IsShuffle);

        if (propertyName == null || propertyName == nameof(info.RepeatMode))
        {
            RepeatIcon.Data = (Geometry)FindResource(info.RepeatMode == 2 ? "TransportRepeatOne" : "TransportRepeat");
            System.Windows.Controls.Primitives.Selector.SetIsSelected(RepeatButton, info.RepeatMode > 0);
        }

        if (propertyName == null || propertyName == nameof(info.DominantColor))
        {
            var color = info.DominantColor;
            var accentBrush = new SolidColorBrush(color);
            accentBrush.Freeze();
            ProgressFill.Background = accentBrush;
        }
    }

    private void UpdateProgressBar(double percent)
    {
        if (ProgressFill.Parent is Border parent)
        {
            double parentWidth = parent.ActualWidth;
            // Only update if parent has valid width to prevent visual glitches
            if (parentWidth > 0 && !double.IsNaN(parentWidth) && !double.IsInfinity(parentWidth))
            {
                double targetWidth = Math.Max(0, Math.Min(percent * parentWidth, parentWidth));
                ProgressFill.Width = targetWidth;
            }
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // Transport button handlers
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService != null) await _mediaService.PlayPauseAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService != null) await _mediaService.NextAsync();
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService != null) await _mediaService.PreviousAsync();
    }

    private async void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService != null) await _mediaService.ToggleShuffleAsync();
    }

    private async void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService != null) await _mediaService.CycleRepeatAsync();
    }

    private async void OnProgressBarClick(object sender, MouseButtonEventArgs e)
    {
        if (_mediaService == null || sender is not Border bar) return;

        double barWidth = bar.ActualWidth;
        // Validate bar width before calculating seek position
        if (barWidth <= 0 || double.IsNaN(barWidth) || double.IsInfinity(barWidth)) return;

        var pos = e.GetPosition(bar);
        double percent = Math.Clamp(pos.X / barWidth, 0, 1);
        await _mediaService.SeekToAsync(percent);
    }
}

