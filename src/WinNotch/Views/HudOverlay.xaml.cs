using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinNotch.Services;

namespace WinNotch.Views;

public partial class HudOverlay : UserControl
{
    private VolumeService? _volumeService;
    private BrightnessService? _brightnessService;
    private DispatcherTimer? _dismissTimer;
    private HudType _currentType;
    private float _lastFraction;

    /// <summary>When false, Show() is a no-op (prevents dual-instance conflicts).</summary>
    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; if (!value) Dismiss(); }
    }
    public bool IsShowing => IsActive && Visibility == Visibility.Visible;

    /// <summary>Reflects the ShowVolumeHud setting.</summary>
    private bool _volumeHudEnabled = true;
    public bool VolumeHudEnabled
    {
        get => _volumeHudEnabled;
        set { _volumeHudEnabled = value; if (!value && _currentType == HudType.Volume) Dismiss(); }
    }

    /// <summary>Reflects the ShowBrightnessHud setting.</summary>
    private bool _brightnessHudEnabled = true;
    public bool BrightnessHudEnabled
    {
        get => _brightnessHudEnabled;
        set { _brightnessHudEnabled = value; if (!value && _currentType == HudType.Brightness) Dismiss(); }
    }

    private enum HudType { None, Volume, Brightness, CapsLock, Music }
    private bool _capsLockFeedbackEnabled = true;
    public bool CapsLockFeedbackEnabled
    {
        get => _capsLockFeedbackEnabled;
        set { _capsLockFeedbackEnabled = value; if (!value && _currentType == HudType.CapsLock) Dismiss(); }
    }
    private bool _musicFeedbackEnabled = true;
    public bool MusicFeedbackEnabled
    {
        get => _musicFeedbackEnabled;
        set { _musicFeedbackEnabled = value; if (!value && _currentType == HudType.Music) Dismiss(); }
    }

    public event Action? HudShown;
    public event Action? HudDismissed;

    public HudOverlay()
    {
        InitializeComponent();

        // The fill is proportional to the track width, which is 0 while the HUD
        // is collapsed — re-apply once layout produces a real width.
        if (SliderFill.Parent is Border track)
            track.SizeChanged += (_, _) => UpdateSliderFill(_lastFraction);
    }

    public void Bind(VolumeService volumeService, BrightnessService brightnessService)
    {
        _volumeService = volumeService;
        _brightnessService = brightnessService;

        // Callbacks arrive on non-UI threads (CoreAudio/WMI) — never block them.
        volumeService.VolumeChanged += (volume, muted) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (VolumeHudEnabled)
                    ShowVolume(volume, muted);
            });
        };

        if (brightnessService.IsSupported)
        {
            brightnessService.BrightnessChanged += brightness =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (BrightnessHudEnabled)
                        ShowBrightness(brightness);
                });
            };
        }
    }

    private void ShowVolume(float volume, bool muted)
    {
        if (!IsActive || !VolumeHudEnabled) return;
        LevelContent.Visibility = Visibility.Visible;
        FeedbackContent.Visibility = Visibility.Collapsed;
        _currentType = HudType.Volume;

        string icon = muted ? "\uE74F" : volume switch
        {
            < 0.01f => "\uE74F",
            < 0.33f => "\uE992",
            < 0.66f => "\uE993",
            _ => "\uE767"
        };

        HudIcon.Text = icon;
        int percent = (int)(volume * 100);
        HudPercent.Text = muted ? "Mute" : $"{percent}%";
        UpdateSliderFill(muted ? 0 : volume);

        Show();
    }

    private void ShowBrightness(int brightness)
    {
        if (!IsActive || !BrightnessHudEnabled) return;
        LevelContent.Visibility = Visibility.Visible;
        FeedbackContent.Visibility = Visibility.Collapsed;
        _currentType = HudType.Brightness;

        HudIcon.Text = brightness switch
        {
            < 33 => "\uE706",
            _ => "\uE706"
        };

        HudPercent.Text = $"{brightness}%";
        UpdateSliderFill(brightness / 100f);

        Show();
    }

    private void UpdateSliderFill(float fraction)
    {
        _lastFraction = fraction;
        if (SliderFill.Parent is Border parent && parent.ActualWidth > 0)
        {
            SliderFill.Width = Math.Max(0, fraction * parent.ActualWidth);
        }
    }

    public void BindFeedback(CapsLockService capsLock, MediaFeedbackService music)
    {
        capsLock.Changed += enabled => Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive || !CapsLockFeedbackEnabled) return;
            _currentType = HudType.CapsLock;
            ShowFeedback("⇪", enabled ? "Mayúsculas activadas" : "Mayúsculas desactivadas", "Caps Lock");
        });
        music.Changed += (icon, title, detail) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive || !MusicFeedbackEnabled) return;
            _currentType = HudType.Music;
            ShowFeedback(icon, title, detail);
        });
    }

    private void ShowFeedback(string icon, string title, string detail)
    {
        LevelContent.Visibility = Visibility.Collapsed;
        FeedbackContent.Visibility = Visibility.Visible;
        FeedbackIcon.Text = icon switch { "⇪" => "\uE8F8", "♫" => "\uE8D6", "▶" => "\uE768", "⏸" => "\uE769", _ => icon };
        FeedbackTitle.Text = title;
        FeedbackDetail.Text = detail;
        Show();
    }

    private void Show()
    {
        if (!IsActive) return;

        Visibility = Visibility.Visible;
        HudShown?.Invoke();

        // Auto-dismiss after 2 seconds
        if (_dismissTimer == null)
        {
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _dismissTimer.Tick += (_, _) =>
            {
                _dismissTimer!.Stop();
                Dismiss();
            };
        }
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    public void Dismiss()
    {
        _dismissTimer?.Stop();
        if (Visibility != Visibility.Visible) return;
        Visibility = Visibility.Collapsed;
        _currentType = HudType.None;
        HudDismissed?.Invoke();
    }

    private void OnSliderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border bar || bar.ActualWidth <= 0) return;
        var pos = e.GetPosition(bar);
        float fraction = (float)Math.Clamp(pos.X / bar.ActualWidth, 0, 1);

        switch (_currentType)
        {
            case HudType.Volume:
                _volumeService?.SetVolume(fraction);
                break;
            case HudType.Brightness:
                _brightnessService?.SetBrightness((int)(fraction * 100));
                break;
        }

        // Reset dismiss timer
        Show();
    }
}


