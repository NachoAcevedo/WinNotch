using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinNotch.Helpers;
using WinNotch.Models;
using WinNotch.Plugins;
using WinNotch.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace WinNotch.Views;

public partial class NotchWindow : Window
{
    private readonly NotchViewModel _vm;
    private bool HasActiveHud => HudOverlay.IsShowing || VerticalHudOverlay.IsShowing;
    private bool _incomingFileDrag;

    public bool TryShowDeviceBattery(string name, int level, bool critical)
    {
        if (!IsVisible || FullscreenOnThisMonitor || HasActiveHud || _vm.NotchState != NotchState.Closed)
            return false;

        var hud = _dock == NotchDock.Top ? HudOverlay : VerticalHudOverlay;
        hud.ShowDeviceBattery(name, level, critical);
        return true;
    }

    private void OnHudPresentationChanged() => Dispatcher.Invoke(() =>
    {
        UpdateClosedContentVisibility();
        RefreshOverlayPresentation();
    });

    private void OnHudDismissed() => Dispatcher.Invoke(() =>
    {
        UpdateClosedContentVisibility();
        RefreshOverlayPresentation();
    });

    private void RefreshOverlayPresentation()
    {
        double width;
        double height;
        if (HasActiveHud)
        {
            width = _dock == NotchDock.Top ? NotchConstants.HudWidth : 52;
            height = _dock == NotchDock.Top ? 52 : NotchConstants.HudWidth;
        }
        else if (_vm.NotchState == NotchState.Open)
        {
            width = _dock == NotchDock.Top ? GetOpenWidth() : NotchConstants.SideOpenWidth + (_isSettingsExpanded ? 280 + NotchConstants.OpenTopRadius : 0);
            height = _dock == NotchDock.Top ? (_isSettingsExpanded ? GetTopSettingsHeight() : GetTopOpenHeight()) : GetSideOpenHeight();
        }
        else if (_vm.NotchState == NotchState.Peeking && !_settings.AlwaysShowMinimum)
        {
            width = _dock == NotchDock.Top ? NotchConstants.PeekWidth : NotchConstants.PeekHeight;
            height = _dock == NotchDock.Top ? NotchConstants.PeekHeight : NotchConstants.PeekWidth;
        }
        else
        {
            width = _dock == NotchDock.Top ? ClosedWidth : ClosedHeight;
            height = _dock == NotchDock.Top ? ClosedHeight : ClosedWidth;
        }
        _widthSpring.AnimateTo(width, _currentWidth);
        _heightSpring.AnimateTo(height, _currentHeight);
        UpdateNotchVisuals();
    }
    private double ClosedWidth => _lyricsStripShown ? LyricsStripWidth : _settings.AlwaysShowMinimum ? 96 : NotchConstants.ClosedWidth;
    private double ClosedHeight => _lyricsStripShown ? LyricsStripHeight : _settings.AlwaysShowMinimum ? 10 : NotchConstants.ClosedHeight;

    // Lyrics with the minimum notch: while music plays, the 96x10 bar grows to fit one line.
    // Not in fullscreen, so games and videos keep only the bar.
    private const double LyricsStripWidth = 360;
    private const double LyricsStripHeight = 30;
    private bool _lyricsStripShown;
    private bool LyricsStripActive =>
        _settings.AlwaysShowMinimum && _settings.ShowLyrics && _settings.ShowMusicControls &&
        _dock == NotchDock.Top && !FullscreenOnThisMonitor &&
        _mediaService.MediaInfo is { HasMedia: true, IsPlaying: true };

    /// <summary>Re-sizes the closed notch when the lyrics strip turns on or off.</summary>
    private void RefreshLyricsStrip()
    {
        bool active = LyricsStripActive;
        if (active == _lyricsStripShown) return;
        _lyricsStripShown = active;
        if (_vm.NotchState == NotchState.Closed && !HasActiveHud)
        {
            _widthSpring.AnimateTo(ClosedWidth, _currentWidth);
            _heightSpring.AnimateTo(ClosedHeight, _currentHeight);
        }
        UpdateNotchVisuals();
    }

    private void ToggleLyrics()
    {
        _settings.ShowLyrics = !_settings.ShowLyrics;
        _settings.Save();
        ApplySettings(_settings);
        SettingsUpdatedByUser?.Invoke(_settings);
    }

    private void ShowLyricLine()
    {
        bool on = _settings.ShowLyrics && _settings.ShowMusicControls;
        LiveActivity.SetLyricLine(on ? _services.Lyrics.CurrentLine : string.Empty, on && _services.Lyrics.IsLoading);
    }
    private readonly Dictionary<string, TabItem> _pluginCardTabs = new();
    private ShelfView? _shelfCardView;
    private bool CanReceiveShelfDrop => _dock == NotchDock.Top && IsCardEnabled("files") && _shelfCardView != null;
    private bool CardIsSelected(string key) => _pluginCardTabs.TryGetValue(key, out var tab) && CardTabs.SelectedItem == tab;
    private bool IsCardEnabled(string key) => key switch
    {
        "weather" => _settings.ShowWeather,
        "tasks" => _settings.ShowTasks,
        "agent" => _settings.ShowAgent,
        "files" => _settings.ShowFiles,
        "eartrumpet" => _settings.ShowAudioCard,
        "batterymanager" => _settings.ShowBatteryCard,
        _ => true
    };
    private bool ShouldShowCard(string key) => IsCardEnabled(key) &&
        (key != "files" || _shelfService.HasItems || _incomingFileDrag);
    private bool _shelfDragInProgress;
    public bool ShelfDragInProgress => _shelfDragInProgress;
    public void SetShelfDragInProgress(bool active)
    {
        _shelfDragInProgress = active;
        if (!active && !_isMouseOverNotch) TransitionToClose();
    }
    private bool FullscreenOnThisMonitor => _fullscreenService.IsFullscreen &&
        _fullscreenService.FullscreenMonitor == ScreenHelper.GetMonitorHandle(this);

    // Gaming mode: with a fullscreen app on this monitor the notch is hidden and
    // hover can't invoke it. Esc pops it up; from there only a click opens it,
    // otherwise it hides again after GamingRevealTimeout.
    private static readonly TimeSpan GamingRevealTimeout = TimeSpan.FromMinutes(1);
    private bool _gamingRevealed;
    private DispatcherTimer? _gamingHideTimer;
    private bool _notchShown = true;

    private bool GamingActive => _settings.GamingMode && FullscreenOnThisMonitor;

    private void ApplyFullscreenVisibility(bool enteringFullscreen = false)
    {
        bool fullscreen = FullscreenOnThisMonitor;
        bool gaming = GamingActive;
        if (!gaming && _gamingRevealed)
        {
            _gamingRevealed = false;
            _gamingHideTimer?.Stop();
        }
        bool hidden = gaming ? !_gamingRevealed : fullscreen && !_settings.AlwaysShowMinimum;
        SetNotchShown(!hidden, animate: false);
        if (gaming)
        {
            if (enteringFullscreen && !_isMouseOverNotch && !_gamingRevealed) TransitionToClose();
        }
        else if (_settings.AlwaysShowMinimum && enteringFullscreen && !_isMouseOverNotch)
        {
            TransitionToClose();
        }
        if (_settings.AlwaysShowMinimum || _gamingRevealed)
        {
            Topmost = true;
            WindowHelper.KeepAbove(this);
        }
        RefreshLyricsStrip();
    }

    private void OnGamingEscape()
    {
        if (!GamingActive || _vm.NotchState != NotchState.Closed) return;
        _gamingRevealed = true;
        if (_gamingHideTimer == null)
        {
            _gamingHideTimer = new DispatcherTimer { Interval = GamingRevealTimeout };
            _gamingHideTimer.Tick += (_, _) =>
            {
                _gamingHideTimer.Stop();
                if (_vm.NotchState != NotchState.Open) EndGamingReveal();
            };
        }
        _gamingHideTimer.Stop();
        _gamingHideTimer.Start();
        Topmost = true;
        WindowHelper.KeepAbove(this);
        SetNotchShown(true, animate: true);
    }

    private void EndGamingReveal()
    {
        _gamingHideTimer?.Stop();
        if (!_gamingRevealed) return;
        _gamingRevealed = false;
        if (GamingActive) SetNotchShown(false, animate: true);
    }

    /// <summary>Shows or hides the whole notch; animated = popup in / shrink out.</summary>
    private void SetNotchShown(bool shown, bool animate)
    {
        if (shown == _notchShown) return;
        _notchShown = shown;
        IsHitTestVisible = shown;

        if (NotchCanvas.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            NotchCanvas.RenderTransform = scale;
        }
        NotchCanvas.RenderTransformOrigin = _dock switch
        {
            NotchDock.Left => new Point(0, 0.5),
            NotchDock.Right => new Point(1, 0.5),
            _ => new Point(0.5, 0)
        };

        if (!animate)
        {
            NotchCanvas.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchCanvas.Opacity = shown ? 1 : 0;
            scale.ScaleX = scale.ScaleY = 1;
            return;
        }

        var scaleTime = TimeSpan.FromMilliseconds(shown ? 460 : 220);
        IEasingFunction ease = shown
            ? new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };
        var scaleAnim = new DoubleAnimation(shown ? 0.3 : 1, shown ? 1 : 0.5, scaleTime) { EasingFunction = ease };
        var fadeAnim = new DoubleAnimation(shown ? 0 : 1, shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 160 : 220));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        NotchCanvas.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void OnCardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != CardTabs) return;
        RefreshPluginHostLayout();
    }

    // Spring animators for each animated property
    private readonly SpringAnimator _widthSpring;
    private readonly SpringAnimator _heightSpring;
    private readonly SpringAnimator _topRadiusSpring;
    private readonly SpringAnimator _bottomRadiusSpring;
    private readonly SpringAnimator _shadowOpacitySpring;
    private readonly SpringAnimator _contentOpacitySpring;

    // Current animated values
    private double _currentWidth;
    private double _currentHeight;
    private double _currentTopRadius;
    private double _currentBottomRadius;
    private double _currentShadowOpacity;
    private double _currentContentOpacity;

    // Fixed window dimensions (large enough for open state + shadow padding)
    private double _fixedWindowWidth;
    private double _fixedWindowHeight;

    // Hover debounce
    private DispatcherTimer? _hoverOpenTimer;
    private DispatcherTimer? _hoverCloseTimer;
    private bool _isMouseOverNotch;
    private bool _isSettingsExpanded;
    private bool _isContextMenuOpen;

    // Drag & Dock
    private NotchDock _dock = NotchDock.Top;
    private bool _isDragging;
    private bool _isMouseDownOnNotch;
    private Point _dragStartScreen;
    private Point _dragLastScreen;
    private DateTime _dragLastTime;
    private double _dragVelocityX;
    private double _dragVelocityY;

    // Multi-screen: which monitor this island lives on (WinForms device name).
    // All window placement is done in PHYSICAL pixels (Per-Monitor V2) — DIPs
    // are only used inside the window, scaled by the screen's own DPI.
    private string? _screenDevice;
    // The screen the user chose (drag/drop or settings). Never overwritten by the
    // primary-screen fallback, so the island returns home once that monitor reappears.
    private string? _preferredScreenDevice;
    private System.Drawing.Rectangle _screenPhysical;
    private double _screenScale = 1.0;

    // Opt-in reserved screen strip (shell AppBar, like the taskbar)
    private readonly AppBarHelper _appBar = new();

    // Island role & cross-island sync
    private readonly bool _isPrimary;
    private bool _applyingSyncedState;

    /// <summary>Raised when the user opens/closes this island (not for synced changes).</summary>
    public event Action<NotchState>? UserStateChanged;

    /// <summary>Raised after the user changed settings from this island's inline panel.</summary>
    public event Action<AppSettings>? SettingsUpdatedByUser;

    /// <summary>Device name of the screen currently hosting this island.</summary>
    public string? ScreenDevice => _screenDevice;

    // Liquid Glass backdrop
    private bool _isLiquidGlassActive;
    private ImageBrush? _backdropBrush;
    private System.Windows.Media.Imaging.WriteableBitmap? _backdropWb;
    private int _wbW, _wbH;
    private double _cachedDpiX = 1, _cachedDpiY = 1;
    private bool _dpiCached;

    // Services (shared across all islands; owned by App via NotchServices)
    private readonly NotchServices _services;
    private readonly MediaService _mediaService;
    private readonly AudioCaptureService _audioCaptureService;
    private readonly VolumeService _volumeService;
    private readonly BrightnessService _brightnessService;
    private readonly BatteryService _batteryService;
    private readonly CalendarService _calendarService;
    private readonly ThemeService _themeService;
    private readonly ShelfService _shelfService;
    private readonly FullscreenService _fullscreenService;
    private readonly WebcamService _webcamService;
    private readonly GlobalHotkey? _globalHotkey;

    // Settings
    private AppSettings _settings;

    // Plugin system
    private PluginManager? _pluginManager;
    private PluginLibraryService? _pluginLibraryService;
    private PluginContext? _pluginContext;

    /// <summary>
    /// The primary island hosts the plugin system, the global hotkey, and persists
    /// its dock position. Secondary islands (one per extra screen) share every
    /// service but skip those responsibilities. <paramref name="screenDevice"/> is
    /// the WinForms device name of the screen to appear on (null = primary screen).
    /// </summary>
    public NotchWindow(AppSettings settings, ThemeService themeService, NotchServices services,
        bool isPrimary = true, string? screenDevice = null)
    {
        InitializeComponent();

        _settings = settings;
        _isPrimary = isPrimary;
        _screenDevice = screenDevice;
        _preferredScreenDevice = screenDevice;
        _vm = new NotchViewModel();
        DataContext = _vm;

        // Theme
        _themeService = themeService;

        // Initialize springs
        _widthSpring = new SpringAnimator(0.42, 0.82);
        _heightSpring = new SpringAnimator(0.42, 0.82);
        _topRadiusSpring = new SpringAnimator(0.38, 0.85);
        _bottomRadiusSpring = new SpringAnimator(0.38, 0.85);
        _shadowOpacitySpring = new SpringAnimator(0.30, 0.90);
        _contentOpacitySpring = new SpringAnimator(0.35, 0.90);

        // Set initial values
        _currentWidth = ClosedWidth;
        _currentHeight = ClosedHeight;
        _currentTopRadius = NotchConstants.ClosedTopRadius;
        _currentBottomRadius = NotchConstants.ClosedBottomRadius;
        _currentShadowOpacity = 0.0;
        _currentContentOpacity = 0.0;

        // Shared services
        _services = services;
        _mediaService = services.Media;
        _audioCaptureService = services.AudioCapture;
        _volumeService = services.Volume;
        _brightnessService = services.Brightness;
        _batteryService = services.Battery;
        _calendarService = services.Calendar;
        _shelfService = services.Shelf;
        _fullscreenService = services.Fullscreen;
        _webcamService = services.Webcam;
        _globalHotkey = isPrimary ? new GlobalHotkey() : null;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowHelper.MakeOverlayWindow(this);
        _appBar.Attach(this);

        // Register global hotkey (Ctrl+Alt+N) — primary island only
        if (_globalHotkey != null)
        {
            _globalHotkey.Register(this);
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
        }
    }

    /// <summary>
    /// Claims or releases the dedicated top strip for this island's screen.
    /// Only meaningful while top-docked; side docks and drags release it.
    /// </summary>
    private void UpdateSpaceReservation()
    {
        if (_settings.ReserveSpaceAtTop && _dock == NotchDock.Top && !_isDragging)
        {
            var screen = ScreenHelper.FindScreenByDevice(_screenDevice);
            double scale = ScreenHelper.GetScaleForScreen(screen);
            int strip = (int)Math.Ceiling((ClosedHeight + 2) * scale);
            _appBar.ReserveTop(screen.Bounds, strip);
        }
        else
        {
            _appBar.Release();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PositionFixedWindow();
        UpdateNotchVisuals();

        // Apply liquid glass if enabled at startup
        ApplyLiquidGlassEffect(_settings.UseLiquidGlassTheme);

        // Register for per-frame rendering
        CompositionTarget.Rendering += OnRendering;

        // Mouse events on the notch path
        NotchPath.MouseEnter += OnNotchMouseEnter;
        NotchPath.MouseLeave += OnNotchMouseLeave;
        NotchPath.MouseLeftButtonDown += OnNotchMouseDown;
        NotchPath.MouseLeftButtonUp += OnNotchMouseUp;
        NotchPath.MouseMove += OnNotchMouseMove;

        // Also track mouse on content grid (so hovering content keeps it open)
        ContentGrid.MouseEnter += OnNotchMouseEnter;
        ContentGrid.MouseLeave += OnNotchMouseLeave;

        // Drag + click from content area (so vertical time label etc. are draggable)
        ContentGrid.PreviewMouseLeftButtonDown += OnNotchMouseDown;
        ContentGrid.PreviewMouseMove += OnContentGridMouseMove;
        ContentGrid.PreviewMouseLeftButtonUp += OnContentGridMouseUp;

        // Handle mouse leaving window during drag
        RootGrid.MouseLeave += OnRootGridMouseLeave;

        // Scroll on notch changes volume (Preview to catch before children)
        RootGrid.PreviewMouseWheel += OnNotchMouseWheel;

        // Right-click context menu
        NotchPath.MouseRightButtonDown += OnNotchRightClick;

        // Drag-drop for file shelf
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        // Initialize/bind shared services
        _ = InitializeServicesAsync();

        // Initialize plugin system (primary island only — a plugin's UIElement
        // can live in exactly one visual tree)
        if (_isPrimary)
            _ = InitializePluginsAsync();

        // Restore the persisted dock position (primary island only)
        if (_isPrimary && _settings.NotchDockPosition != NotchDock.Top)
            DockTo(_settings.NotchDockPosition);
    }

    private async System.Threading.Tasks.Task InitializeServicesAsync()
    {
        // Global one-time init happens once app-wide; every island awaits it
        await _services.EnsureInitializedAsync(_settings);

        // Bind views to services
        MusicPlayer.Bind(_mediaService);
        LiveActivity.Bind(_mediaService, _audioCaptureService);
        MusicPlayer.LyricsToggleRequested += ToggleLyrics;
        MusicPlayer.SetLyricsEnabled(_settings.ShowLyrics);
        _services.Lyrics.Changed += ShowLyricLine;
        ShowLyricLine();

        // Inline settings
        InlineSettings.SettingsChanged += s =>
        {
            ApplySettings(s);
            SettingsUpdatedByUser?.Invoke(s);
        };
        InlineSettings.BackRequested += CollapseSettings;
        InlineSettings.ThemeChangeRequested += async themeSettings =>
        {
            _settings = themeSettings;
            await _themeService.ApplyWithTransition(themeSettings, NotchCanvas);
            SettingsUpdatedByUser?.Invoke(themeSettings);
        };

        // Vertical inline settings (separate instance for side dock)
        VerticalInlineSettings.SettingsChanged += s =>
        {
            ApplySettings(s);
            SettingsUpdatedByUser?.Invoke(s);
        };
        VerticalInlineSettings.BackRequested += CollapseSettings;
        VerticalInlineSettings.ThemeChangeRequested += async themeSettings =>
        {
            _settings = themeSettings;
            await _themeService.ApplyWithTransition(themeSettings, NotchCanvas);
            SettingsUpdatedByUser?.Invoke(themeSettings);
        };

        // Toggle clock/live-activity visibility based on music state
        _mediaService.MediaInfo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaInfo.HasMedia) or nameof(MediaInfo.IsPlaying))
                Dispatcher.Invoke(UpdateClosedContentVisibility);
        };
        _mediaService.SessionChanged += () => Dispatcher.Invoke(UpdateClosedContentVisibility);
        UpdateClosedContentVisibility();

        // Volume & Brightness
        HudOverlay.Bind(_volumeService, _brightnessService);
        HudOverlay.BindFeedback(_services.CapsLock, _services.MediaFeedback);
        VerticalHudOverlay.IsActive = false;
        VerticalHudOverlay.Bind(_volumeService, _brightnessService);
        VerticalHudOverlay.BindFeedback(_services.CapsLock, _services.MediaFeedback);

        // Battery
        BatteryIndicator.Bind(_batteryService);

        // Calendar
        CalendarPanel_View.Bind(_calendarService);
        CalendarPanel.Visibility = _settings.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;

        // File Shelf
        _shelfService.ShelfChanged += () => Dispatcher.Invoke(UpdateShelfVisibility);

        // Webcam mirror
        if (_settings.ShowWebcam)
        {
            WebcamPanel.Visibility = Visibility.Visible;
            WebcamMirror.Bind(_webcamService);
        }

        // Vertical side panel: wire media + clock
        _mediaService.MediaInfo.PropertyChanged += (_, e) =>
        {
            Dispatcher.Invoke(() => UpdateSideMediaContent(_mediaService.MediaInfo, e.PropertyName));
        };
        _mediaService.SessionChanged += () =>
        {
            Dispatcher.Invoke(() => UpdateSideMediaContent(_mediaService.MediaInfo, null));
        };

        // Side visualizer (skip work while it isn't on screen, e.g. top dock)
        _audioCaptureService.SpectrumUpdated += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (SideVisualizer.IsVisible && _audioCaptureService.SpectrumData != null)
                    SideVisualizer.UpdateSpectrum(_audioCaptureService.SpectrumData);
            });
        };

        var sideClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        sideClockTimer.Tick += (_, _) => UpdateSideClock();
        sideClockTimer.Start();
        UpdateSideClock();
        UpdateSideMediaContent(_mediaService.MediaInfo, null);

        // Fullscreen detection — hide this island only when the fullscreen app
        // is on the SAME monitor (a game on screen 1 shouldn't hide screen 2's island)
        _fullscreenService.FullscreenChanged += isFs =>
        {
            Dispatcher.Invoke(() =>
            {
                ApplyFullscreenVisibility(enteringFullscreen: isFs);
            });
        };
        _fullscreenService.ForegroundChanged += () => Dispatcher.Invoke(() => ApplyFullscreenVisibility(enteringFullscreen: true));
        _services.EscapeKey.Pressed += OnGamingEscape;

        HudOverlay.HudShown += OnHudPresentationChanged;
        VerticalHudOverlay.HudShown += OnHudPresentationChanged;
        HudOverlay.HudDismissed += OnHudDismissed;
        VerticalHudOverlay.HudDismissed += OnHudDismissed;
        // Apply all persisted settings now that every component is bound —
        // without this, toggles like ShowMusicControls or ShowBattery only
        // take effect after the user touches a setting.
        ApplySettings(_settings);
    }

    private async System.Threading.Tasks.Task InitializePluginsAsync()
    {
        try
        {
            _pluginContext = new PluginContext(
                this, _vm,
                _mediaService, _themeService, _volumeService,
                _brightnessService, _batteryService, _calendarService,
                _audioCaptureService, _shelfService, _webcamService,
                _fullscreenService, _settings);
            _pluginContext.RegisterService(new PluginManagerNavigationService(OpenPluginManager));

            _pluginManager = new PluginManager(_pluginContext);
            _pluginLibraryService = new PluginLibraryService();

            await _pluginManager.LoadAllPluginsAsync();

            // Inject UI plugin elements into the notch
            InjectPluginUI();

            // Enable/disable now applies live — re-inject without a restart
            _pluginManager.PluginStateChanged += () => Dispatcher.Invoke(InjectPluginUI);

            // Wire settings "Manage Plugins" button
            InlineSettings.ManagePluginsRequested += OpenPluginManager;
            VerticalInlineSettings.ManagePluginsRequested += OpenPluginManager;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plugin system initialization failed: {ex.Message}");
        }
    }

    private void InjectPluginUI()
    {
        if (_pluginManager == null) return;

        PluginClosedPanel.Children.Clear();
        PluginOpenPanel.Children.Clear();
        PluginAccessoryPanel.Children.Clear();
        PluginAccessoryPanel.Visibility = Visibility.Collapsed;
        VerticalPluginClosedPanel.Children.Clear();
        VerticalPluginOpenPanel.Children.Clear();
        string? selectedKey = (CardTabs.SelectedItem as TabItem)?.Tag is ICardPlugin selectedPlugin ? selectedPlugin.CardKey : null;
        foreach (var tab in _pluginCardTabs.Values)
        {
            tab.Content = null;
            CardTabs.Items.Remove(tab);
        }
        _pluginCardTabs.Clear();
        _shelfCardView = null;

        foreach (var uiPlugin in _pluginManager.GetUIPlugins())
        {
            try
            {
                if (uiPlugin is ICardPlugin cardPlugin)
                {
                    var view = cardPlugin.GetUIElement(UIPluginLocation.CustomTab);
                    if (view != null)
                    {
                        var tab = new TabItem
                        {
                            Header = cardPlugin.CardKey == "files" ? ShelfCardHeader : cardPlugin.CardTitle,
                            Content = view, Tag = cardPlugin,
                            Style = (Style)FindResource("CardTab"),
                            Visibility = ShouldShowCard(cardPlugin.CardKey) ? Visibility.Visible : Visibility.Collapsed
                        };
                        _pluginCardTabs[cardPlugin.CardKey] = tab;
                        if (cardPlugin.CardKey == "files") _shelfCardView = view as ShelfView;
                    }
                    continue;
                }
                var closedElement = uiPlugin.GetUIElement(UIPluginLocation.ClosedContent);
                if (closedElement != null)
                {
                    if (_dock == NotchDock.Top)
                        PluginClosedPanel.Children.Add(closedElement);
                    else
                        VerticalPluginClosedPanel.Children.Add(closedElement);
                }

               if (_dock == NotchDock.Top)
                {
                    var accessoryElement = uiPlugin.GetUIElement(UIPluginLocation.OpenAccessory);
                    if (accessoryElement != null)
                    {
                        PluginAccessoryPanel.Children.Add(WrapAccessoryElement(accessoryElement));
                        PluginAccessoryPanel.Visibility = Visibility.Visible;
                    }

                   var openElement = uiPlugin.GetUIElement(UIPluginLocation.OpenContent);
                    if (openElement != null)
                        PluginOpenPanel.Children.Add(openElement);
                }
                else
                {
                    var verticalOpenElement = uiPlugin.GetUIElement(UIPluginLocation.VerticalOpenContent)
                        ?? uiPlugin.GetUIElement(UIPluginLocation.OpenContent);
                    if (verticalOpenElement != null)
                        VerticalPluginOpenPanel.Children.Add(verticalOpenElement);
                }

               // Notify initial state
                uiPlugin.OnNotchStateChanged(_vm.NotchState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to inject UI for plugin {uiPlugin.Name}: {ex.Message}");
            }
        }

        foreach (var tab in _pluginCardTabs.Values.OrderBy(tab => ((ICardPlugin)tab.Tag).CardOrder))
            CardTabs.Items.Add(tab);
        if (selectedKey != null && _pluginCardTabs.TryGetValue(selectedKey, out var selectedTab) && selectedTab.Visibility == Visibility.Visible)
            CardTabs.SelectedItem = selectedTab;
        EnsureVisibleCardSelected();
        UpdateShelfVisibility();
        RefreshPluginHostLayout();
    }

    private void EnsureVisibleCardSelected()
    {
        if (CardTabs.SelectedItem is not TabItem selected || selected.Visibility != Visibility.Visible)
            CardTabs.SelectedItem = CardTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Visibility == Visibility.Visible);
    }

    private static UIElement WrapAccessoryElement(UIElement element)
    {
        var host = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        host.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = (System.Windows.Media.Brush?)Application.Current?.TryFindResource("SeparatorBrush")
                ?? new SolidColorBrush(Color.FromRgb(72, 72, 76))
        });
        host.Children.Add(element);
        return host;
    }

    private void NotifyPluginsStateChanged(NotchState state)
    {
        if (_pluginManager == null) return;
        foreach (var uiPlugin in _pluginManager.GetUIPlugins())
        {
            try { uiPlugin.OnNotchStateChanged(state); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Plugin state notification error ({uiPlugin.Name}): {ex.Message}");
            }
        }
    }

    private void OpenPluginManager()
    {
        OpenPluginManager(null);
    }

    private void OpenPluginManager(string? focusPluginId)
    {
        if (_pluginManager == null || _pluginLibraryService == null) return;

        var pluginView = new PluginManagerView(_pluginManager, _pluginLibraryService, focusPluginId);
        var window = new Window
        {
            Title = "WinNotch Plugin Manager",
            Width = 980,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.Transparent,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = pluginView,
            Topmost = true
        };
        window.Show();
        window.Activate();
        window.Topmost = false;
    }

    // A resting shadow keeps the island visible on same-color wallpapers
    // (e.g. a white notch on a white background). ShowShadow disables all of it.
    private double ClosedShadowOpacity => _settings.ShowShadow ? 0.6 : 0.0;
    private double OpenShadowOpacity => _settings.ShowShadow ? 0.95 : 0.0;
    private double DragShadowOpacity => _settings.ShowShadow ? 0.7 : 0.0;

    private void UpdateClosedContentVisibility()
    {
        var info = _mediaService.MediaInfo;
        bool hasMusic = _settings.ShowMusicControls && info.HasMedia && info.IsPlaying;
        LiveActivity.Visibility = hasMusic && !HasActiveHud ? Visibility.Visible : Visibility.Collapsed;
        ClockWidget.Visibility = hasMusic || HasActiveHud ? Visibility.Collapsed : Visibility.Visible;
        BatteryIndicator.Suppressed = HasActiveHud;
        RefreshLyricsStrip();
    }

    #region Vertical Side Content

    private void UpdateSideClock()
    {
        var now = DateTime.Now;
        SideTimeText.Text = now.ToString("HH:mm");
        SideDateText.Text = now.ToString("ddd d", System.Globalization.CultureInfo.CurrentCulture);
        SideTimeSmall.Text = now.ToString("HH:mm");
    }

    private void UpdateSideMediaContent(MediaInfo info, string? prop)
    {
        if (prop == null || prop == nameof(info.Title))
            SideSongTitle.Text = info.Title;
        if (prop == null || prop == nameof(info.Artist))
            SideArtistName.Text = info.Artist;
        if (prop == null || prop == nameof(info.AlbumArt))
            SideAlbumArt.Source = info.AlbumArt;
        if (prop == null || prop == nameof(info.IsPlaying))
            SidePlayIcon.Text = info.IsPlaying ? "\uE769" : "\uE768";
        if (prop == null || prop == nameof(info.HasMedia))
            SideMusicPanel.Visibility = info.HasMedia ? Visibility.Visible : Visibility.Collapsed;

        // Progress bar + time
        if (prop == null || prop == nameof(info.Position) || prop == nameof(info.ProgressPercent))
        {
            SideElapsedText.Text = FormatTimeSpan(info.Position);
            UpdateSideProgressBar(info.ProgressPercent);
        }
        if (prop == null || prop == nameof(info.Duration))
            SideDurationText.Text = FormatTimeSpan(info.Duration);

        // Visualizer bar color from album dominant color
        if (prop == null || prop == nameof(info.DominantColor))
        {
            var color = info.DominantColor;
            double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            SideVisualizer.BarColor = brightness < 0.3 ? System.Windows.Media.Colors.White : color;
        }
    }

    private void UpdateSideProgressBar(double percent)
    {
        if (SideProgressFill.Parent is System.Windows.Controls.Border parent)
        {
            double parentWidth = parent.ActualWidth;
            // Only update if parent has valid width to prevent visual glitches
            if (parentWidth > 0 && !double.IsNaN(parentWidth) && !double.IsInfinity(parentWidth))
            {
                double targetWidth = Math.Max(0, Math.Min(percent * parentWidth, parentWidth));
                SideProgressFill.Width = targetWidth;
            }
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private async void OnSidePlayClick(object sender, RoutedEventArgs e) =>
        await _mediaService.PlayPauseAsync();

    private async void OnSidePrevClick(object sender, RoutedEventArgs e) =>
        await _mediaService.PreviousAsync();

    private async void OnSideNextClick(object sender, RoutedEventArgs e) =>
        await _mediaService.NextAsync();

    private async void OnSideProgressBarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border bar) return;

        double barWidth = bar.ActualWidth;
        // Validate bar width before calculating seek position
        if (barWidth <= 0 || double.IsNaN(barWidth) || double.IsInfinity(barWidth)) return;

        var pos = e.GetPosition(bar);
        double percent = Math.Clamp(pos.X / barWidth, 0, 1);
        await _mediaService.SeekToAsync(percent);
    }

    #endregion

    public void OpenSettings()
    {
        // First ensure the notch is open
        if (_vm.NotchState != NotchState.Open)
            TransitionToOpen();

        ExpandSettings();
    }

    private void ExpandSettings()
    {
        _isSettingsExpanded = true;

        if (_dock == NotchDock.Top)
        {
            // Top dock: Load settings and grow the notch taller (slide down)
            InlineSettings.LoadSettings(_settings);
            InlineSettings.Visibility = Visibility.Visible;

            double targetH = GetTopSettingsHeight();
            _heightSpring.Response = 0.42;
            _heightSpring.DampingFraction = 0.80;
            _heightSpring.AnimateTo(targetH, _currentHeight);
        }
        else
        {
            // Side dock: Load settings in vertical panel and expand width
            VerticalInlineSettings.LoadSettings(_settings);
            VerticalSettingsPanel.Visibility = Visibility.Visible;

            double targetW = NotchConstants.SideOpenWidth + 280 + NotchConstants.OpenTopRadius;
            _widthSpring.Response = 0.42;
            _widthSpring.DampingFraction = 0.80;
            _widthSpring.AnimateTo(targetW, _currentWidth);
        }
    }

    private void CollapseSettings()
    {
        _isSettingsExpanded = false;

        if (_dock == NotchDock.Top)
        {
            // Top dock: Hide settings and shrink back to normal open height
            InlineSettings.Visibility = Visibility.Collapsed;

            double targetH = GetTopOpenHeight();
            _heightSpring.Response = 0.42;
            _heightSpring.DampingFraction = 0.80;
            _heightSpring.AnimateTo(targetH, _currentHeight);
        }
        else
        {
            // Side dock: Hide settings panel and shrink width back
            VerticalSettingsPanel.Visibility = Visibility.Collapsed;

            _widthSpring.Response = 0.42;
            _widthSpring.DampingFraction = 0.80;
            _widthSpring.AnimateTo(NotchConstants.SideOpenWidth, _currentWidth);
        }
    }

    // Removed OpenSettingsWindow - now using integrated settings panel

    /// <summary>
    /// Position the window once: centered at top of primary screen,
    /// sized to fit the largest notch state (open) + shadow padding.
    /// The window never moves or resizes after this.
    /// </summary>
    private void PositionFixedWindow()
    {
        UpdateFixedWindowBounds();
        RefreshScreenBounds();

        int w = (int)Math.Round(_fixedWindowWidth * _screenScale);
        int h = (int)Math.Round(_fixedWindowHeight * _screenScale);
        int x = _screenPhysical.Left + (_screenPhysical.Width - w) / 2;
        int y = _screenPhysical.Top - (int)Math.Round(2 * _screenScale);
        WindowHelper.SetWindowRectPhysical(this, x, y, w, h);
    }

    /// <summary>
    /// Resolves this island's screen device to physical bounds + DPI scale.
    /// Falls back to the primary screen when the device is missing.
    /// </summary>
    private void RefreshScreenBounds()
    {
        var screen = ScreenHelper.FindScreenByDevice(_preferredScreenDevice);
        _screenDevice = screen.DeviceName;
        _screenPhysical = screen.Bounds;
        _screenScale = ScreenHelper.GetScaleForScreen(screen);
    }

    /// <summary>
    /// Re-validates the screen and repositions after a display-topology change
    /// (monitor plugged/unplugged, resolution change).
    /// </summary>
    public void HandleDisplayChange()
    {
        if (_isDragging) return;
        RefreshScreenBounds();
        RepositionForDock();
        UpdateSpaceReservation();
    }

    private void UpdateFixedWindowBounds()
    {
        double padding = NotchConstants.WindowPadding;
        double maxBaseTopOpenWidth = new[]
        {
            NotchConstants.OpenWidth,
            NotchConstants.OpenWidthWithCalendar,
            NotchConstants.OpenWidthWithWebcam,
            NotchConstants.OpenWidthWithCalendarAndWebcam
        }.Max();
        double maxTopOpenWidth = maxBaseTopOpenWidth
            + GetMeasuredPanelWidth(PluginAccessoryPanel, NotchConstants.OpenHeight);

        double maxTopHeight = Math.Max(GetTopOpenHeight(), GetTopSettingsHeight());
        double sideExpandedWidth = NotchConstants.SideOpenWidth + 280 + NotchConstants.OpenTopRadius;
        double maxShortAxis = Math.Max(maxTopHeight, sideExpandedWidth);
        double maxTallAxis = Math.Max(maxTopOpenWidth, GetSideOpenHeight());

        _fixedWindowWidth = maxTallAxis + padding * 2;
        _fixedWindowHeight = maxShortAxis + padding + 10;
    }

    private void RefreshPluginHostLayout()
    {
        UpdateFixedWindowBounds();

        if (!IsLoaded || _isDragging)
            return;

        RepositionForDock();

        if (_vm.NotchState != NotchState.Open)
            return;

        if (_dock == NotchDock.Top)
        {
            _widthSpring.AnimateTo(GetOpenWidth(), _currentWidth);

            var targetHeight = _isSettingsExpanded
                ? GetTopSettingsHeight()
                : GetTopOpenHeight();

            _heightSpring.AnimateTo(targetHeight, _currentHeight);
            return;
        }

        _heightSpring.AnimateTo(GetSideOpenHeight(), _currentHeight);
    }

    private const double SettingsViewportHeight = 320;

    private double GetTopOpenHeight()
    {
        // Use the same width as OpenContent: shape insets (60) and margins (24).
        // Measuring the complete row includes the tabs, padding, gear and webcam.
        double width = Math.Max(120, GetOpenWidth() - 84);
        MainOpenContent.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Ceiling(MainOpenContent.DesiredSize.Height);
        if (!_incomingFileDrag && PluginOpenPanel.Children.Count > 0)
            height += GetMeasuredPanelHeight(PluginOpenPanel, width);
        // Only the shape's vertical insets (30) and outer content margins (16).
        return height + 46;
    }

    private bool _cardLayoutRefreshPending;
    private void OnCardContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || !IsLoaded || _dock != NotchDock.Top || _vm.NotchState != NotchState.Open || _cardLayoutRefreshPending)
            return;
        _cardLayoutRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _cardLayoutRefreshPending = false;
            if (IsLoaded && _dock == NotchDock.Top && _vm.NotchState == NotchState.Open)
                RefreshPluginHostLayout();
        }));
    }
    private double GetTopSettingsHeight()
    {
        // Keep the settings viewport readable regardless of the selected card.
        return GetTopOpenHeight() + SettingsViewportHeight + 6;
    }
    private double GetSideOpenHeight()
    {
        return NotchConstants.SideOpenHeight + GetMeasuredPanelHeight(VerticalPluginOpenPanel, 136);
    }

    private static double GetMeasuredPanelHeight(System.Windows.Controls.Panel panel, double availableWidth)
    {
        if (panel.Children.Count == 0)
            return 0;

        panel.Measure(new Size(availableWidth, double.PositiveInfinity));
        return Math.Ceiling(panel.DesiredSize.Height);
    }

    private static double GetMeasuredPanelWidth(System.Windows.Controls.Panel panel, double availableHeight)
    {
        if (panel.Children.Count == 0)
            return 0;

        panel.Measure(new Size(double.PositiveInfinity, availableHeight));
        return Math.Ceiling(panel.DesiredSize.Width);
    }

    #region Animation Loop

    private void OnRendering(object? sender, EventArgs e)
    {
        bool anyAnimating = false;

        if (_widthSpring.IsAnimating)
        {
            _currentWidth = _widthSpring.Tick();
            anyAnimating = true;
        }
        if (_heightSpring.IsAnimating)
        {
            _currentHeight = _heightSpring.Tick();
            anyAnimating = true;
        }
        if (_topRadiusSpring.IsAnimating)
        {
            _currentTopRadius = _topRadiusSpring.Tick();
            anyAnimating = true;
        }
        if (_bottomRadiusSpring.IsAnimating)
        {
            _currentBottomRadius = _bottomRadiusSpring.Tick();
            anyAnimating = true;
        }
        if (_shadowOpacitySpring.IsAnimating)
        {
            _currentShadowOpacity = _shadowOpacitySpring.Tick();
            anyAnimating = true;
        }
        if (_contentOpacitySpring.IsAnimating)
        {
            _currentContentOpacity = _contentOpacitySpring.Tick();
            anyAnimating = true;
        }

        if (anyAnimating)
        {
            UpdateNotchVisuals();
        }
    }

    private void UpdateNotchVisuals()
    {
        double w = _currentWidth;
        double h = _currentHeight;
        double topR = _currentTopRadius;
        double bottomR = _currentBottomRadius;

        // Generate notch geometry
        var rect = new Rect(0, 0, w, h);
        Geometry geometry;

        if (_isDragging)
            geometry = NotchShape.CreateNotchGeometry(rect, topR, bottomR);
        else if (_dock == NotchDock.Left)
            geometry = NotchShape.CreateSideNotchGeometry(rect, topR, bottomR, isLeft: true);
        else if (_dock == NotchDock.Right)
            geometry = NotchShape.CreateSideNotchGeometry(rect, topR, bottomR, isLeft: false);
        else
            geometry = NotchShape.CreateNotchGeometry(rect, topR, bottomR);

        NotchPath.Data = geometry;
        ShadowPath.Data = geometry;
        BackdropBlurPath.Data = geometry;
        GlassSpecularPath.Data = geometry;
        GlassBorderPath.Data = geometry;
        GlassInnerShadowPath.Data = geometry;

        double windowW = ActualWidth > 0 ? ActualWidth : Width;
        double windowH = ActualHeight > 0 ? ActualHeight : Height;

        double offsetX, offsetY;

        if (_isDragging)
        {
            // During drag the mouse handler sizes the window (physical px);
            // here we only lay content out against the DIP pill size
            double dragW = ClosedWidth * 0.8;
            double dragH = ClosedHeight * 0.8;
            windowW = dragW;
            windowH = dragH;
            offsetX = 0;
            offsetY = 0;

            // Position is already updated in OnNotchMouseMove to follow cursor smoothly
            // Don't recalculate here to avoid visual jitter
        }
        else if (_dock == NotchDock.Left)
        {
            offsetX = 0; // Anchored to left edge
            offsetY = (windowH - h) / 2;
        }
        else if (_dock == NotchDock.Right)
        {
            offsetX = windowW - w; // Anchored to right edge
            offsetY = (windowH - h) / 2;
        }
        else
        {
            // Top dock: centered horizontally at top
            offsetX = (windowW - w) / 2;
            offsetY = 0;
        }

        System.Windows.Controls.Canvas.SetLeft(NotchPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(NotchPath, offsetY);
        System.Windows.Controls.Canvas.SetLeft(ShadowPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(ShadowPath, offsetY);
        System.Windows.Controls.Canvas.SetLeft(BackdropBlurPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(BackdropBlurPath, offsetY);
        System.Windows.Controls.Canvas.SetLeft(GlassSpecularPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(GlassSpecularPath, offsetY);
        System.Windows.Controls.Canvas.SetLeft(GlassBorderPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(GlassBorderPath, offsetY);
        System.Windows.Controls.Canvas.SetLeft(GlassInnerShadowPath, offsetX);
        System.Windows.Controls.Canvas.SetTop(GlassInnerShadowPath, offsetY);

        // Shadow opacity
        ShadowPath.Opacity = _currentShadowOpacity;

        // Content grid: positioned inside the notch shape (inset from corners)
        double contentLeft, contentTop, contentWidth, contentHeight;

        if (_dock == NotchDock.Left)
        {
            contentLeft = offsetX + topR;
            contentTop = offsetY + topR + bottomR;
            contentWidth = Math.Max(0, w - topR);
            contentHeight = Math.Max(0, h - 2 * (topR + bottomR));
        }
        else if (_dock == NotchDock.Right)
        {
            contentLeft = offsetX;
            contentTop = offsetY + topR + bottomR;
            contentWidth = Math.Max(0, w - topR);
            contentHeight = Math.Max(0, h - 2 * (topR + bottomR));
        }
        else
        {
            contentLeft = offsetX + topR + bottomR;
            contentTop = offsetY + topR;
            contentWidth = Math.Max(0, w - (topR + bottomR) * 2);
            contentHeight = Math.Max(0, h - topR - bottomR);
        }

        System.Windows.Controls.Canvas.SetLeft(ContentGrid, contentLeft);
        System.Windows.Controls.Canvas.SetTop(ContentGrid, contentTop);
        ContentGrid.Width = contentWidth;
        ContentGrid.Height = contentHeight;

        // The lyrics strip is a single line taller than the space between the notch curves:
        // clipping it there cut the text in half. Opening the notch clips again.
        if (_lyricsStripShown && _vm.NotchState == NotchState.Closed)
            ContentGrid.ClipToBounds = false;
        else if (_vm.NotchState == NotchState.Open)
            ContentGrid.ClipToBounds = true;

        // Content opacity — switch between horizontal and vertical panels
        if (_dock != NotchDock.Top)
        {
            VerticalOpenContent.Opacity = _currentContentOpacity;
            VerticalClosedContent.Opacity = 1.0 - _currentContentOpacity;
            OpenContent.Opacity = 0;
            ClosedContent.Opacity = 0;
        }
        else
        {
            OpenContent.Opacity = _currentContentOpacity;
            ClosedContent.Opacity = 1.0 - _currentContentOpacity;
            VerticalOpenContent.Opacity = 0;
            VerticalClosedContent.Opacity = 0;
        }

        // Invisible layers must not swallow clicks (WPF hit-tests opacity-0 elements)
        if (_settings.AlwaysShowMinimum && !_lyricsStripShown)
        {
            ClosedContent.Opacity = 0;
            VerticalClosedContent.Opacity = 0;
        }
        if (HasActiveHud)
        {
            ClosedContent.Opacity = 0;
            VerticalClosedContent.Opacity = 0;
            OpenContent.Opacity = 0;
            VerticalOpenContent.Opacity = 0;
        }
        OpenContent.IsHitTestVisible = OpenContent.Opacity > 0.5;
        ClosedContent.IsHitTestVisible = ClosedContent.Opacity > 0.5;
        VerticalOpenContent.IsHitTestVisible = VerticalOpenContent.Opacity > 0.5;
        VerticalClosedContent.IsHitTestVisible = VerticalClosedContent.Opacity > 0.5;

        // Canvas covers the full window
        NotchCanvas.Width = windowW;
        NotchCanvas.Height = windowH;
    }

    #endregion

    #region State Transitions

    /// <summary>
    /// Applies an open/close originating from another island (sync mode)
    /// without re-broadcasting it.
    /// </summary>
    public void ApplySyncedState(NotchState state)
    {
        if (_isDragging || _vm.NotchState == state) return;

        _applyingSyncedState = true;
        try
        {
            if (state == NotchState.Open)
                TransitionToOpen();
            else if (state == NotchState.Closed)
                TransitionToClose();
        }
        finally
        {
            _applyingSyncedState = false;
        }
    }

    private void TransitionToOpen()
    {
        HudOverlay.Dismiss();
        VerticalHudOverlay.Dismiss();
        _gamingHideTimer?.Stop();
        if (_vm.NotchState == NotchState.Open) return;
        _vm.Open();
        NotifyPluginsStateChanged(NotchState.Open);
        if (!_applyingSyncedState)
            UserStateChanged?.Invoke(NotchState.Open);

        _widthSpring.Response = 0.42;
        _widthSpring.DampingFraction = 0.80;
        _heightSpring.Response = 0.42;
        _heightSpring.DampingFraction = 0.80;

        if (_dock == NotchDock.Top)
        {
            double openW = GetOpenWidth();
            double openH = GetTopOpenHeight();

            _widthSpring.AnimateTo(openW, _currentWidth);
            _heightSpring.AnimateTo(openH, _currentHeight);
        }
        else
        {
            // Side dock: width = protrusion from edge, height = tall axis
            double openW = NotchConstants.SideOpenWidth;
            double openH = GetSideOpenHeight();

            _widthSpring.AnimateTo(openW, _currentWidth);
            _heightSpring.AnimateTo(openH, _currentHeight);
        }

        _topRadiusSpring.AnimateTo(NotchConstants.OpenTopRadius, _currentTopRadius);
        _bottomRadiusSpring.AnimateTo(NotchConstants.OpenBottomRadius, _currentBottomRadius);
        _shadowOpacitySpring.AnimateTo(OpenShadowOpacity, _currentShadowOpacity);
        _contentOpacitySpring.AnimateTo(1.0, _currentContentOpacity);
    }

    private void TransitionToPeek()
    {
        if (_vm.NotchState == NotchState.Peeking || _vm.NotchState == NotchState.Open) return;
        _vm.Peek();
        NotifyPluginsStateChanged(NotchState.Peeking);

        _widthSpring.Response = 0.30;
        _widthSpring.DampingFraction = 0.90;
        _heightSpring.Response = 0.30;
        _heightSpring.DampingFraction = 0.90;

        if (_dock == NotchDock.Top)
        {
            _widthSpring.AnimateTo(NotchConstants.PeekWidth, _currentWidth);
            _heightSpring.AnimateTo(NotchConstants.PeekHeight, _currentHeight);
        }
        else
        {
            _widthSpring.AnimateTo(NotchConstants.PeekHeight, _currentWidth);
            _heightSpring.AnimateTo(NotchConstants.PeekWidth, _currentHeight);
        }
    }

    private void TransitionToClose()
    {
        if (_shelfDragInProgress || _incomingFileDrag) return;
        if (_vm.NotchState == NotchState.Closed) return;
        NotifyPluginsStateChanged(NotchState.Closed);
        if (!_applyingSyncedState)
            UserStateChanged?.Invoke(NotchState.Closed);

        // Collapse settings if open
        if (_isSettingsExpanded)
        {
            _isSettingsExpanded = false;
            InlineSettings.Visibility = Visibility.Collapsed;
            VerticalSettingsPanel.Visibility = Visibility.Collapsed;
        }

        _vm.Close();
        WindowHelper.MakeOverlayWindow(this);

        _widthSpring.Response = 0.45;
        _widthSpring.DampingFraction = 1.0;
        _heightSpring.Response = 0.45;
        _heightSpring.DampingFraction = 1.0;

        if (_dock == NotchDock.Top)
        {
            _widthSpring.AnimateTo(ClosedWidth, _currentWidth);
            _heightSpring.AnimateTo(ClosedHeight, _currentHeight);
        }
        else
        {
            // Side: narrow width, tall height
            _widthSpring.AnimateTo(ClosedHeight, _currentWidth);
            _heightSpring.AnimateTo(ClosedWidth, _currentHeight);
        }

        _topRadiusSpring.AnimateTo(NotchConstants.ClosedTopRadius, _currentTopRadius);
        _bottomRadiusSpring.AnimateTo(NotchConstants.ClosedBottomRadius, _currentBottomRadius);
        _shadowOpacitySpring.AnimateTo(ClosedShadowOpacity, _currentShadowOpacity);
        _contentOpacitySpring.AnimateTo(0.0, _currentContentOpacity);
        if (HasActiveHud) RefreshOverlayPresentation();
        // Gaming mode: once used, the notch goes back into hiding until the next Esc.
        EndGamingReveal();
    }

    #endregion

    #region Hover Logic

    private void OnNotchMouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDragging || _isMouseDownOnNotch) return;
        _isMouseOverNotch = true;
        _hoverCloseTimer?.Stop();

        if (_vm.NotchState == NotchState.Closed)
        {
            _hoverOpenTimer?.Stop();

            // Gaming mode: hovering never opens it, only a click does.
            if (GamingActive) return;

            if (_settings.HoverMode == HoverMode.HoverPeekClickOpen && !_settings.AlwaysShowMinimum)
            {
                // Peek mode: slight expand on hover, click to fully open
                _hoverOpenTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings.HoverOpenDelayMs))
                };
                _hoverOpenTimer.Tick += (_, _) =>
                {
                    _hoverOpenTimer!.Stop();
                    if (_isDragging || _isMouseDownOnNotch) return;
                    if (_isMouseOverNotch && _vm.NotchState == NotchState.Closed)
                        TransitionToPeek();
                };
                _hoverOpenTimer.Start();
            }
            else // LongHoverOpen
            {
                // Long hover: wait longer then fully open
                _hoverOpenTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(_settings.LongHoverDelayMs)
                };
                _hoverOpenTimer.Tick += (_, _) =>
                {
                    _hoverOpenTimer!.Stop();
                    if (_isDragging || _isMouseDownOnNotch) return;
                    if (_isMouseOverNotch)
                        TransitionToOpen();
                };
                _hoverOpenTimer.Start();
            }
        }
    }

    private void OnNotchMouseLeave(object sender, MouseEventArgs e)
    {
        if (_shelfDragInProgress) { _isMouseOverNotch = false; return; }
        if (_isDragging) return;
        _isMouseOverNotch = false;
        _hoverOpenTimer?.Stop();

        // Don't close while a context menu is open
        if (_isContextMenuOpen) return;

        if (_vm.NotchState == NotchState.Open || _vm.NotchState == NotchState.Peeking)
        {
            _hoverCloseTimer?.Stop();
            _hoverCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(
                    _vm.NotchState == NotchState.Peeking ? 80 : Math.Max(50, _settings.HoverCloseDelayMs))
            };
            _hoverCloseTimer.Tick += (_, _) =>
            {
                _hoverCloseTimer!.Stop();
                if (!_isMouseOverNotch && !_isContextMenuOpen)
                    TransitionToClose();
            };
            _hoverCloseTimer.Start();
        }
    }

    private void OnNotchMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Don't capture for drag when open — let buttons/controls work normally
        if (_vm.NotchState == NotchState.Open && sender != NotchPath) return;

        var cursorPos = System.Windows.Forms.Cursor.Position;
        _dragStartScreen = new Point(cursorPos.X, cursorPos.Y);
        _dragLastScreen = _dragStartScreen;
        _dragLastTime = DateTime.UtcNow;
        _dragVelocityX = 0;
        _dragVelocityY = 0;
        _isDragging = false;
        _isMouseDownOnNotch = true;

        // Suppress hover timers while mouse is down
        _hoverOpenTimer?.Stop();

        NotchPath.CaptureMouse();
    }

    private void OnNotchMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDownOnNotch && !_isDragging) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        // Use raw cursor position to avoid drift from window movement
        var cursorPos = System.Windows.Forms.Cursor.Position;
        var currentScreen = new Point(cursorPos.X, cursorPos.Y);

        // Check drag threshold
        if (_isMouseDownOnNotch && !_isDragging)
        {
            double dx = currentScreen.X - _dragStartScreen.X;
            double dy = currentScreen.Y - _dragStartScreen.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > NotchConstants.DragThreshold)
            {
                BeginDrag(currentScreen);
            }
            else return;
        }

        if (!_isDragging) return;

        // Track velocity (exponential smoothing with clamped time delta)
        var now = DateTime.UtcNow;
        double dt = (now - _dragLastTime).TotalSeconds;
        // Clamp dt to prevent huge velocity spikes from timer irregularities
        dt = Math.Clamp(dt, 0.001, 0.1);

        if (dt > 0.001)
        {
            double vx = (currentScreen.X - _dragLastScreen.X) / dt;
            double vy = (currentScreen.Y - _dragLastScreen.Y) / dt;
            _dragVelocityX = _dragVelocityX * 0.6 + vx * 0.4;
            _dragVelocityY = _dragVelocityY * 0.6 + vy * 0.4;
        }
        _dragLastScreen = currentScreen;
        _dragLastTime = now;

        // Move window in physical px, sized for the cursor's current screen DPI
        double dragScale = ScreenHelper.GetScaleForScreen(ScreenHelper.ScreenFromPhysicalPoint(
            new System.Drawing.Point((int)currentScreen.X, (int)currentScreen.Y)));
        int pillW = (int)Math.Round(ClosedWidth * 0.8 * dragScale);
        int pillH = (int)Math.Round(ClosedHeight * 0.8 * dragScale);
        WindowHelper.SetWindowRectPhysical(this,
            (int)currentScreen.X - pillW / 2,
            (int)currentScreen.Y - pillH / 2,
            pillW, pillH);
    }

    private void OnNotchMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isMouseDownOnNotch = false;

        // Always release mouse capture to prevent stuck states
        if (NotchPath.IsMouseCaptured)
            NotchPath.ReleaseMouseCapture();

        if (_isDragging)
        {
            EndDrag();
        }
        else
        {
            // Short click — normal behavior (no movement occurred)
            if (_vm.NotchState == NotchState.Closed || _vm.NotchState == NotchState.Peeking)
                TransitionToOpen();
        }
    }

    private void BeginDrag(Point currentScreenPos)
    {
        _isDragging = true;
        _isMouseDownOnNotch = false;

        // A floating island shouldn't hold reserved screen space
        _appBar.Release();

        // Target drag pill size
        double pillW = ClosedWidth * 0.8;
        double pillH = ClosedHeight * 0.8;

        // If coming from a side dock, immediately set dimensions to horizontal
        if (_dock == NotchDock.Left || _dock == NotchDock.Right)
        {
            _currentWidth = pillW;
            _currentHeight = pillH;
        }

        // Set window to drag pill size and center on cursor, in physical px
        double beginScale = ScreenHelper.GetScaleForScreen(ScreenHelper.ScreenFromPhysicalPoint(
            new System.Drawing.Point((int)currentScreenPos.X, (int)currentScreenPos.Y)));
        WindowHelper.SetWindowRectPhysical(this,
            (int)(currentScreenPos.X - pillW * beginScale / 2),
            (int)(currentScreenPos.Y - pillH * beginScale / 2),
            (int)Math.Round(pillW * beginScale),
            (int)Math.Round(pillH * beginScale));

        // Switch to horizontal content during drag
        ClosedContent.Visibility = Visibility.Visible;
        OpenContent.Visibility = Visibility.Visible;
        VerticalClosedContent.Visibility = Visibility.Collapsed;
        VerticalOpenContent.Visibility = Visibility.Collapsed;

        // Close the notch to a small dragging pill
        if (_vm.NotchState != NotchState.Closed)
        {
            _vm.Close();
            _isSettingsExpanded = false;
            InlineSettings.Visibility = Visibility.Collapsed;
        }

        // Animate to compact drag pill size
        _widthSpring.Response = 0.25;
        _widthSpring.DampingFraction = 0.90;
        _heightSpring.Response = 0.25;
        _heightSpring.DampingFraction = 0.90;
        _widthSpring.AnimateTo(pillW, _currentWidth);
        _heightSpring.AnimateTo(pillH, _currentHeight);
        _shadowOpacitySpring.AnimateTo(DragShadowOpacity, _currentShadowOpacity);
        _contentOpacitySpring.AnimateTo(0.0, _currentContentOpacity);
    }

    private void EndDrag()
    {
        _isDragging = false;

        // Dock on whichever screen the cursor was released on
        var dropScreen = ScreenHelper.ScreenFromPhysicalPoint(
            new System.Drawing.Point((int)_dragLastScreen.X, (int)_dragLastScreen.Y));
        _screenDevice = dropScreen.DeviceName;
        _preferredScreenDevice = dropScreen.DeviceName;
        var screenBounds = dropScreen.Bounds; // physical px
        double dropScale = ScreenHelper.GetScaleForScreen(dropScreen);

        double cursorX = _dragLastScreen.X;
        double cursorY = _dragLastScreen.Y;

        // Determine target dock based on position + velocity (thresholds are DIP
        // constants — scale them to this screen's physical pixels)
        NotchDock targetDock = NotchDock.Top;
        double edgeDist = NotchConstants.EdgeSnapDistance * dropScale;
        double throwV = NotchConstants.ThrowVelocityThreshold * dropScale;

        // Any edge of the cursor's screen is a valid dock target — including edges
        // facing another monitor (drag past the seam to dock on the far screen).
        bool nearLeft = cursorX - screenBounds.Left < edgeDist || _dragVelocityX < -throwV;
        bool nearRight = screenBounds.Right - cursorX < edgeDist || _dragVelocityX > throwV;
        bool nearTop = cursorY - screenBounds.Top < edgeDist * 1.5;

        if (nearLeft && !nearTop)
            targetDock = NotchDock.Left;
        else if (nearRight && !nearTop)
            targetDock = NotchDock.Right;
        else
            targetDock = NotchDock.Top;

        DockTo(targetDock);
    }

    private void DockTo(NotchDock dock)
    {
        _dock = dock;

        // Reset state so subsequent TransitionToOpen() isn't skipped
        _vm.Close();

        RepositionForDock();

        // Persist where the main island lives so it survives restarts
        if (_isPrimary)
        {
            _settings.NotchDockPosition = dock;
            _settings.NotchScreenDevice = _preferredScreenDevice;
            _settings.Save();
            SettingsUpdatedByUser?.Invoke(_settings);
        }

        // Move (or drop) the reserved strip to the new screen/edge
        UpdateSpaceReservation();

        // Update HUD overlay active state based on dock
        HudOverlay.IsActive = (dock == NotchDock.Top);
        VerticalHudOverlay.IsActive = (dock != NotchDock.Top);

        // Switch content panels based on dock orientation
        if (_dock == NotchDock.Top)
        {
            ClosedContent.Visibility = Visibility.Visible;
            OpenContent.Visibility = Visibility.Visible;
            VerticalClosedContent.Visibility = Visibility.Collapsed;
            VerticalOpenContent.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClosedContent.Visibility = Visibility.Collapsed;
            OpenContent.Visibility = Visibility.Collapsed;
            VerticalClosedContent.Visibility = Visibility.Visible;
            VerticalOpenContent.Visibility = Visibility.Visible;
        }

        InjectPluginUI();

        // Animate back to closed dimensions
        _widthSpring.Response = 0.40;
        _widthSpring.DampingFraction = 0.82;
        _heightSpring.Response = 0.40;
        _heightSpring.DampingFraction = 0.82;

        if (_dock == NotchDock.Top)
        {
            _widthSpring.AnimateTo(ClosedWidth, _currentWidth);
            _heightSpring.AnimateTo(ClosedHeight, _currentHeight);
        }
        else
        {
            // Side dock: swap width/height (tall & narrow)
            _widthSpring.AnimateTo(ClosedHeight, _currentWidth);
            _heightSpring.AnimateTo(ClosedWidth, _currentHeight);
        }

        _topRadiusSpring.AnimateTo(NotchConstants.ClosedTopRadius, _currentTopRadius);
        _bottomRadiusSpring.AnimateTo(NotchConstants.ClosedBottomRadius, _currentBottomRadius);
        _shadowOpacitySpring.AnimateTo(ClosedShadowOpacity, _currentShadowOpacity);
        _contentOpacitySpring.AnimateTo(0.0, _currentContentOpacity);
    }

    private void RepositionForDock()
    {
        RefreshScreenBounds();

        // Physical dimensions for THIS screen's DPI
        int longAxis = (int)Math.Round(_fixedWindowWidth * _screenScale);
        int shortAxis = (int)Math.Round(_fixedWindowHeight * _screenScale);
        int inset = (int)Math.Round(2 * _screenScale);

        switch (_dock)
        {
            case NotchDock.Top:
                WindowHelper.SetWindowRectPhysical(this,
                    _screenPhysical.Left + (_screenPhysical.Width - longAxis) / 2,
                    _screenPhysical.Top - inset,
                    longAxis, shortAxis);
                break;

            case NotchDock.Left:
                // Vertical window on the left edge
                WindowHelper.SetWindowRectPhysical(this,
                    _screenPhysical.Left - inset,
                    _screenPhysical.Top + (_screenPhysical.Height - longAxis) / 2,
                    shortAxis, longAxis);
                break;

            case NotchDock.Right:
                // Vertical window on the right edge
                WindowHelper.SetWindowRectPhysical(this,
                    _screenPhysical.Right - shortAxis + inset,
                    _screenPhysical.Top + (_screenPhysical.Height - longAxis) / 2,
                    shortAxis, longAxis);
                break;
        }
    }

    private void OnContentGridMouseMove(object sender, MouseEventArgs e)
    {
        // Forward to drag handler
        OnNotchMouseMove(sender, e);
    }

    private void OnContentGridMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Handle drag end or click-to-open
        OnNotchMouseUp(sender, e);
    }

    private void OnRootGridMouseLeave(object sender, MouseEventArgs e)
    {
        // If dragging and mouse leaves window (very fast movement), end drag safely
        if (_isDragging && e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _isMouseDownOnNotch = false;
            if (NotchPath.IsMouseCaptured)
                NotchPath.ReleaseMouseCapture();
            EndDrag();
        }
    }

    private void OnNotchMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Only adjust volume in closed/peeking state (open state scrolls content normally)
        if (_vm.NotchState == NotchState.Open) return;

        // ShowVolumeHud only gates the HUD display (inside HudOverlay), not the volume change itself
        if (_volumeService != null)
        {
            float current = _volumeService.Volume;
            // e.Delta is typically 120 per notch; scale to ~5% per scroll tick
            float delta = (e.Delta / 120f) * 0.05f;
            _volumeService.SetVolume(Math.Clamp(current + delta, 0f, 1f));
            e.Handled = true;
        }
    }

    private void OnNotchRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = CreateStyledContextMenu();

        // Settings open only from the tray icon.
        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quitItem);

        TrackContextMenu(menu);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnGlobalHotkeyPressed()
    {
        Dispatcher.Invoke(() =>
        {
            if (_vm.NotchState == NotchState.Open)
                TransitionToClose();
            else
                TransitionToOpen();
        });
    }

    /// <summary>
    /// Tracks a context menu so the notch doesn't close while it's open.
    /// </summary>
    public void TrackContextMenu(System.Windows.Controls.ContextMenu menu)
    {
        _isContextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            // Re-check if mouse has left while menu was open
            if (!_isMouseOverNotch && _vm.NotchState == NotchState.Open)
            {
                _hoverCloseTimer?.Stop();
                _hoverCloseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings.HoverCloseDelayMs))
                };
                _hoverCloseTimer.Tick += (_, _) =>
                {
                    _hoverCloseTimer!.Stop();
                    if (!_isMouseOverNotch && !_isContextMenuOpen)
                        TransitionToClose();
                };
                _hoverCloseTimer.Start();
            }
        };
    }

    /// <summary>
    /// Creates a styled ContextMenu matching the app's dark/light theme with rounded corners.
    /// </summary>
    public System.Windows.Controls.ContextMenu CreateStyledContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // Create a border template with rounded corners
        var template = new ControlTemplate(typeof(System.Windows.Controls.ContextMenu));
        var frameworkFactory = new FrameworkElementFactory(typeof(Border));
        frameworkFactory.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("NotchFillBrush"));
        frameworkFactory.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("SeparatorBrush"));
        frameworkFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frameworkFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        frameworkFactory.SetValue(Border.PaddingProperty, new Thickness(6, 8, 6, 8));

        // Add drop shadow effect
        var shadowEffect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = System.Windows.Media.Colors.Black,
            Direction = 270,
            ShadowDepth = 2,
            BlurRadius = 12,
            Opacity = 0.3
        };
        frameworkFactory.SetValue(Border.EffectProperty, shadowEffect);

        var scrollFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ScrollViewer));
        scrollFactory.SetValue(System.Windows.Controls.ScrollViewer.CanContentScrollProperty, true);
        scrollFactory.SetValue(System.Windows.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty,
            System.Windows.Controls.ScrollBarVisibility.Disabled);
        scrollFactory.SetValue(System.Windows.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty,
            System.Windows.Controls.ScrollBarVisibility.Auto);

        var stackFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
        stackFactory.SetValue(System.Windows.Controls.StackPanel.IsItemsHostProperty, true);

        scrollFactory.AppendChild(stackFactory);
        frameworkFactory.AppendChild(scrollFactory);
        template.VisualTree = frameworkFactory;
        menu.Template = template;

        // Style for MenuItems with rounded hover
        var menuItemStyle = new Style(typeof(System.Windows.Controls.MenuItem));
        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.ForegroundProperty,
            FindResource("TextSecondaryBrush")));
        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.FontSizeProperty, 12.0));
        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.PaddingProperty,
            new Thickness(12, 6, 24, 6)));
        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.MarginProperty,
            new Thickness(2, 1, 2, 1)));
        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.CursorProperty,
            System.Windows.Input.Cursors.Hand));

        // Create template for menu item with rounded background
        var itemTemplate = new ControlTemplate(typeof(System.Windows.Controls.MenuItem));
        var itemBorder = new FrameworkElementFactory(typeof(Border));
        itemBorder.Name = "ItemBorder";
        itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        itemBorder.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);

        var contentPresenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        contentPresenter.SetValue(System.Windows.Controls.ContentPresenter.ContentSourceProperty, "Header");
        contentPresenter.SetValue(System.Windows.Controls.ContentPresenter.MarginProperty, new Thickness(0));

        itemBorder.AppendChild(contentPresenter);
        itemTemplate.VisualTree = itemBorder;

        // Hover trigger with rounded background
        var hoverTrigger = new Trigger { Property = System.Windows.Controls.MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.MenuItem.BackgroundProperty,
            FindResource("HoverOverlayBrush"), "ItemBorder"));
        hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.MenuItem.ForegroundProperty,
            FindResource("TextPrimaryBrush")));
        itemTemplate.Triggers.Add(hoverTrigger);

        menuItemStyle.Setters.Add(new Setter(System.Windows.Controls.MenuItem.TemplateProperty, itemTemplate));
        menu.Resources.Add(typeof(System.Windows.Controls.MenuItem), menuItemStyle);

        // Style for Separator
        var separatorStyle = new Style(typeof(System.Windows.Controls.Separator));
        separatorStyle.Setters.Add(new Setter(System.Windows.Controls.Separator.BackgroundProperty,
            FindResource("SeparatorBrush")));
        separatorStyle.Setters.Add(new Setter(System.Windows.Controls.Separator.MarginProperty,
            new Thickness(8, 4, 8, 4)));
        separatorStyle.Setters.Add(new Setter(System.Windows.Controls.Separator.HeightProperty, 1.0));
        menu.Resources.Add(typeof(System.Windows.Controls.Separator), separatorStyle);

        return menu;
    }

    #endregion

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _cachedDpiX = newDpi.DpiScaleX;
        _cachedDpiY = newDpi.DpiScaleY;
        _dpiCached = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Shared services are owned and disposed by App — a secondary island
        // closing (e.g. multi-screen toggled off) must not tear them down.
        CompositionTarget.Rendering -= OnRenderBackdrop;
        CompositionTarget.Rendering -= OnRendering;
        _hoverOpenTimer?.Stop();
        _hoverCloseTimer?.Stop();
        _appBar.Dispose();
        _pluginManager?.Dispose();
        _globalHotkey?.Dispose();
        base.OnClosed(e);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _services.ApplyFeedbackSettings(settings);

        // Apply liquid glass theme if toggled
        _themeService.Apply(settings);
        ApplyLiquidGlassEffect(settings.UseLiquidGlassTheme);

        // Toggle visibility of components based on settings
        LiveActivity.UserEnabled = settings.ShowMusicControls;
        MusicPlayer.Visibility = settings.ShowMusicControls ? Visibility.Visible : Visibility.Collapsed;
        BatteryIndicator.UserEnabled = settings.ShowBattery;
        CalendarPanel.Visibility = settings.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;
        MusicCardTab.Visibility = settings.ShowMusicControls ? Visibility.Visible : Visibility.Collapsed;
        CalendarCardTab.Visibility = settings.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in _pluginCardTabs)
            entry.Value.Visibility = ShouldShowCard(entry.Key) ? Visibility.Visible : Visibility.Collapsed;
        EnsureVisibleCardSelected();
        CompactVisualizer_SetVisible(settings.ShowVisualizer);
        MusicPlayer.SetLyricsEnabled(settings.ShowLyrics);
        ShowLyricLine();
        UpdateClosedContentVisibility();

        // HUD gates (external volume/brightness changes must respect these too)
        HudOverlay.VolumeHudEnabled = settings.ShowVolumeHud;
        HudOverlay.BrightnessHudEnabled = settings.ShowBrightnessHud;
        VerticalHudOverlay.VolumeHudEnabled = settings.ShowVolumeHud;
        VerticalHudOverlay.BrightnessHudEnabled = settings.ShowBrightnessHud;
        HudOverlay.CapsLockFeedbackEnabled = settings.ShowCapsLockFeedback;
        VerticalHudOverlay.CapsLockFeedbackEnabled = settings.ShowCapsLockFeedback;
        HudOverlay.MusicFeedbackEnabled = settings.ShowMusicFeedback;
        VerticalHudOverlay.MusicFeedbackEnabled = settings.ShowMusicFeedback;

        // Webcam
        _webcamService.SetTargetFps(settings.WebcamFps);
        if (settings.ShowWebcam)
        {
            WebcamPanel.Visibility = Visibility.Visible;
            WebcamMirror.Bind(_webcamService);
            if (_webcamService.MaxCameraFps > 0)
            {
                InlineSettings.UpdateWebcamFpsMax(_webcamService.MaxCameraFps);
                VerticalInlineSettings.UpdateWebcamFpsMax(_webcamService.MaxCameraFps);
            }
        }
        else
        {
            WebcamPanel.Visibility = Visibility.Collapsed;
            WebcamMirror.StopCamera();
        }

        // Re-adjust notch size if currently open
        if (_vm.NotchState == NotchState.Open)
        {
            if (_dock == NotchDock.Top)
            {
                _widthSpring.AnimateTo(GetOpenWidth(), _currentWidth);

                if (!_isSettingsExpanded)
                {
                    double targetH = GetTopOpenHeight();
                    _heightSpring.AnimateTo(targetH, _currentHeight);
                }
            }
            // Side docks: don't re-animate width/height — settings panel has fixed size
        }

        UpdateSpaceReservation();

        ApplyFullscreenVisibility();
        if (_vm.NotchState == NotchState.Closed)
        {
            _widthSpring.AnimateTo(_dock == NotchDock.Top ? ClosedWidth : ClosedHeight, _currentWidth);
            _heightSpring.AnimateTo(_dock == NotchDock.Top ? ClosedHeight : ClosedWidth, _currentHeight);
        }
        if (HasActiveHud) RefreshOverlayPresentation();

        // Re-target the shadow for the current state (ShowShadow may have toggled)
        if (!_isDragging)
        {
            _shadowOpacitySpring.AnimateTo(
                _vm.NotchState == NotchState.Open ? OpenShadowOpacity : ClosedShadowOpacity,
                _currentShadowOpacity);
        }
    }

    private double GetOpenWidth()
    {
        double accessoryWidth = GetMeasuredPanelWidth(PluginAccessoryPanel, NotchConstants.OpenHeight);
        bool cal = false; // Calendar now occupies its own card instead of a second column.
        bool cam = _settings.ShowWebcam;

        double baseWidth = NotchConstants.OpenWidth;
        if (cal && cam)
            baseWidth = NotchConstants.OpenWidthWithCalendarAndWebcam;
        else if (cal)
            baseWidth = NotchConstants.OpenWidthWithCalendar;
        else if (cam)
            baseWidth = NotchConstants.OpenWidthWithWebcam;

        return baseWidth + accessoryWidth;
    }

    private void ApplyLiquidGlassEffect(bool enable)
    {
        _isLiquidGlassActive = enable;

        if (enable)
        {
            WindowHelper.SetExcludeFromCapture(this, true);

            // Cache DPI once
            if (!_dpiCached)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _cachedDpiX = dpi.DpiScaleX;
                _cachedDpiY = dpi.DpiScaleY;
                _dpiCached = true;
            }

           GlassSpecularPath.Opacity = 1.0;
            GlassBorderPath.Opacity = 1.0;
            GlassInnerShadowPath.Opacity = 1.0;
            BackdropBlurPath.Opacity = 1.0;

            // Idempotent: remove first so repeated enables never double-subscribe
            CompositionTarget.Rendering -= OnRenderBackdrop;
            CompositionTarget.Rendering += OnRenderBackdrop;
        }
        else
        {
            CompositionTarget.Rendering -= OnRenderBackdrop;
            WindowHelper.SetExcludeFromCapture(this, false);
            GlassSpecularPath.Opacity = 0;
            GlassBorderPath.Opacity = 0;
            GlassInnerShadowPath.Opacity = 0;
            BackdropBlurPath.Opacity = 0;
            BackdropBlurPath.Fill = null;
            _backdropBrush = null;
            _backdropWb = null;
        }
    }

    private long _lastBackdropCaptureTicks;

    private void OnRenderBackdrop(object? sender, EventArgs e)
    {
        if (!_isLiquidGlassActive || _isDragging) return;
        if (_currentWidth < 10 || _currentHeight < 10) return;

        // 30fps is indistinguishable under the blur and halves the capture cost
        long now = Environment.TickCount64;
        if (now - _lastBackdropCaptureTicks < 33) return;
        _lastBackdropCaptureTicks = now;

        try
        {
            double canvasLeft = System.Windows.Controls.Canvas.GetLeft(NotchPath);
            double canvasTop = System.Windows.Controls.Canvas.GetTop(NotchPath);
            if (double.IsNaN(canvasLeft)) canvasLeft = 0;
            if (double.IsNaN(canvasTop)) canvasTop = 0;

            var winRect = WindowHelper.GetWindowRectPhysical(this);
            int px = winRect.Left + (int)(canvasLeft * _cachedDpiX);
            int py = winRect.Top + (int)(canvasTop * _cachedDpiY);
            int pw = (int)(_currentWidth * _cachedDpiX);
            int ph = (int)(_currentHeight * _cachedDpiY);

            if (pw < 4 || ph < 4) return;

            // Ensure WriteableBitmap matches current size
            if (_backdropWb == null || _wbW != pw || _wbH != ph)
            {
                _backdropWb = new System.Windows.Media.Imaging.WriteableBitmap(
                    pw, ph, 96, 96, PixelFormats.Bgra32, null);
                _wbW = pw;
                _wbH = ph;

                _backdropBrush = new ImageBrush(_backdropWb)
                {
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.None
                };
                BackdropBlurPath.Fill = _backdropBrush;
            }

           // Capture directly into the WriteableBitmap (zero alloc)
            // Padding feeds the edge-lens: rim pixels sample from this outside band
            BackdropBlurHelper.CaptureInto(_backdropWb, px, py, pw, ph, padding: 8);
        }
        catch
        {
            // Silently ignore capture failures
        }
    }

    private void CompactVisualizer_SetVisible(bool visible)
    {
        // The visualizer is inside LiveActivity; toggle via its property
        if (LiveActivity.FindName("CompactVisualizer") is UIElement viz)
            viz.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    #region File Shelf / Drag-Drop

    private string? ShelfCardHeader => _shelfService.HasItems ? $"Archivos ({_shelfService.Items.Count})" : null;

    private void UpdateShelfVisibility()
    {
        if (_pluginCardTabs.TryGetValue("files", out var filesTab))
        {
            filesTab.Header = ShelfCardHeader;
            filesTab.Visibility = ShouldShowCard("files") ? Visibility.Visible : Visibility.Collapsed;
            EnsureVisibleCardSelected();
        }

        // Adjust open height if notch is open
        if (_vm.NotchState == NotchState.Open && !_isSettingsExpanded)
        {
            double targetHeight = GetTopOpenHeight();
            _heightSpring.AnimateTo(targetHeight, _currentHeight);
        }
    }

    private void SetIncomingFileDrag(bool active)
    {
        _incomingFileDrag = active;
        CardTabs.Tag = active ? "ShelfDrop" : null;
        PluginOpenPanel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (active)
            _shelfCardView?.ShowDropZone();
        else
            _shelfCardView?.HideDropZone();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        // The file shelf lives in the horizontal (top dock) layout only
        if (!CanReceiveShelfDrop || _shelfDragInProgress) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            if (_incomingFileDrag) return;
            SetIncomingFileDrag(true);
            _hoverCloseTimer?.Stop();
            var filesTab = _pluginCardTabs["files"];
            filesTab.Visibility = Visibility.Visible;
            CardTabs.SelectedItem = filesTab;

            // Open the notch if closed/peeking to show drop zone
            if (_vm.NotchState != NotchState.Open)
                TransitionToOpen();

            if (!_isSettingsExpanded)
            {
                _heightSpring.AnimateTo(GetTopOpenHeight(), _currentHeight);
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_shelfDragInProgress) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!CanReceiveShelfDrop) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!_incomingFileDrag) return;
        var position = e.GetPosition(ContentGrid);
        if (position.X >= 0 && position.Y >= 0 && position.X <= ContentGrid.ActualWidth && position.Y <= ContentGrid.ActualHeight) return;
        SetIncomingFileDrag(false);
        UpdateShelfVisibility();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_shelfDragInProgress) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!CanReceiveShelfDrop) return;
        SetIncomingFileDrag(false);

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _shelfService.AddFiles(files);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            UpdateShelfVisibility();
        }
    }

    #endregion
}





