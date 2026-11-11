using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.Views;
using Forms = System.Windows.Forms;
using DrawColor = System.Drawing.Color;

namespace WinNotch;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;
    private NotchWindow? _notchWindow;
    private readonly Dictionary<string, NotchWindow> _secondaryWindows = new();
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private AppSettings _settings = null!;
    private ThemeService _themeService = null!;
    private NotchServices _services = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handler for diagnostics
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            System.Windows.MessageBox.Show(args.Exception.ToString(), "WinNotch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Single instance enforcement
        const string mutexName = "WinNotch_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            // The named mutex already exists. Normally that means another instance is
            // running — but it can also be a stale mutex left behind by a previous
            // instance that was force-killed during a self-update. Briefly try to
            // acquire it: if we succeed (including via an abandoned mutex, which
            // happens when the old owner died without releasing), we are the rightful
            // owner and continue. Otherwise a live instance holds it, so we exit.
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                // Previous owner terminated without releasing — ownership is now ours.
                acquired = true;
            }

            if (acquired)
            {
                _ownsMutex = true;
            }
            else
            {
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }
        }

        // Load settings
        _settings = AppSettings.Load();

        // Initialize theme
        _themeService = new ThemeService();
        _themeService.Apply(_settings);
        _themeService.StartWatchingSystemTheme();
        _themeService.ThemeChanged += ApplyTrayTheme;

        // System tray icon
        SetupTrayIcon();

        // Shared system services — one set for every island window
        _services = new NotchServices();

        // Launch the primary island (on its remembered screen)
        _notchWindow = new NotchWindow(_settings, _themeService, _services,
            isPrimary: true, screenDevice: _settings.NotchScreenDevice);
        WireIslandEvents(_notchWindow);
        _notchWindow.Show();

        // Optional islands on every other screen
        RebuildSecondaryWindows();

        // React to monitors being plugged/unplugged or rearranged
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Silent update check shortly after startup (network failures are ignored)
        if (_settings.AutoCheckForUpdates)
            _ = CheckForUpdatesSilentlyAsync();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _notchWindow?.HandleDisplayChange();
            RebuildSecondaryWindows();
        });
    }

    private void WireIslandEvents(NotchWindow window)
    {
        // Settings changed on one island → apply everywhere + re-evaluate islands
        window.SettingsUpdatedByUser += settings =>
        {
            _settings = settings;
            foreach (var other in AllIslands())
            {
                if (!ReferenceEquals(other, window))
                    other.ApplySettings(settings);
            }
            RebuildSecondaryWindows();
        };

        // Open/close sync across islands (when enabled)
        window.UserStateChanged += state =>
        {
            if (!_settings.SyncIslandsOpenState) return;
            if (state != NotchState.Open && state != NotchState.Closed) return;

            foreach (var other in AllIslands())
            {
                if (!ReferenceEquals(other, window))
                    other.ApplySyncedState(state);
            }
        };
    }

    private IEnumerable<NotchWindow> AllIslands()
    {
        if (_notchWindow != null)
            yield return _notchWindow;
        foreach (var window in _secondaryWindows.Values)
            yield return window;
    }

    /// <summary>
    /// Creates/removes secondary islands so that — when ShowOnAllScreens is on —
    /// every screen except the primary island's has exactly one island.
    /// </summary>
    private void RebuildSecondaryWindows()
    {
        var wanted = new HashSet<string>();
        if (_settings.ShowOnAllScreens && _notchWindow != null)
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                if (screen.DeviceName != _notchWindow.ScreenDevice)
                    wanted.Add(screen.DeviceName);
            }
        }

        // Close islands whose screen disappeared, moved under the primary,
        // or that the setting no longer wants
        foreach (var device in _secondaryWindows.Keys.ToList())
        {
            if (!wanted.Contains(device))
            {
                _secondaryWindows[device].Close();
                _secondaryWindows.Remove(device);
            }
        }

        // Spawn islands for newly wanted screens
        foreach (var device in wanted)
        {
            if (_secondaryWindows.ContainsKey(device))
                continue;

            var island = new NotchWindow(_settings, _themeService, _services,
                isPrimary: false, screenDevice: device);
            WireIslandEvents(island);
            _secondaryWindows[device] = island;
            island.Show();
        }
    }

    private async System.Threading.Tasks.Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15));

            var updateService = new UpdateService();
            var result = await updateService.CheckForUpdateAsync();

            if (result == UpdateCheckResult.UpdateAvailable && _trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(
                    5000,
                    "WinNotch update available",
                    $"Version {updateService.LatestVersion} is ready. Open Settings → Check for Updates to install.",
                    Forms.ToolTipIcon.Info);
            }
        }
        catch
        {
            // Never let a background check surface an error.
        }
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinNotch", "Logs");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(logDir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never crash the handler.
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Text = "WinNotch",
            Visible = true
        };

        _trayMenu = new Forms.ContextMenuStrip
        {
            Renderer = new ThemedMenuRenderer(() => _themeService.CurrentPalette),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(4, 6, 4, 6),
            Font = new Font("Segoe UI", 9.5f)
        };

        var settingsItem = new Forms.ToolStripMenuItem("Settings")
        {
            Padding = new Forms.Padding(12, 6, 20, 6)
        };
        settingsItem.Click += (_, _) => OpenSettings();

        var separator = new Forms.ToolStripSeparator();

        var quitItem = new Forms.ToolStripMenuItem("Quit")
        {
            Padding = new Forms.Padding(12, 6, 20, 6)
        };
        quitItem.Click += (_, _) => QuitApp();

        _trayMenu.Items.Add(settingsItem);
        _trayMenu.Items.Add(separator);
        _trayMenu.Items.Add(quitItem);

        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.DoubleClick += (_, _) => OpenSettings();
        ApplyTrayTheme();
    }

    private static Icon CreateDefaultIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var capsule = new GraphicsPath();
        capsule.AddArc(0.8f, 4.4f, 7.2f, 7.2f, 90, 180);
        capsule.AddArc(8f, 4.4f, 7.2f, 7.2f, 270, 180);
        capsule.CloseFigure();

        using var outline = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 43, 51), 0.85f);
        g.FillPath(System.Drawing.Brushes.White, capsule);
        g.DrawPath(outline, capsule);
        g.FillEllipse(System.Drawing.Brushes.DarkSlateGray, 10.25f, 6.45f, 3.1f, 3.1f);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void OpenSettings()
    {
        _notchWindow?.OpenSettings();
    }

    private void QuitApp()
    {
        _themeService.ThemeChanged -= ApplyTrayTheme;
        _themeService?.StopWatchingSystemTheme();
        _trayIcon?.Dispose();
        foreach (var window in _secondaryWindows.Values.ToList())
            window.Close();
        _secondaryWindows.Clear();
        _notchWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_themeService != null)
            _themeService.ThemeChanged -= ApplyTrayTheme;
        _trayIcon?.Dispose();
        _services?.Dispose();
        if (_ownsMutex && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Another shutdown path may already have released the mutex.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
        base.OnExit(e);
    }

    private void ApplyTrayTheme()
    {
        if (_trayMenu == null) return;

        var palette = _themeService.CurrentPalette;
        var backColor = ToDrawingColor(palette.SurfaceRaised);
        var foreground = ToDrawingColor(palette.TextPrimary);

        _trayMenu.BackColor = backColor;
        _trayMenu.ForeColor = foreground;
        foreach (Forms.ToolStripItem item in _trayMenu.Items)
        {
            item.BackColor = backColor;
            item.ForeColor = foreground;
        }
    }

    private static DrawColor ToDrawingColor(System.Windows.Media.Color color)
    {
        return DrawColor.FromArgb(color.A, color.R, color.G, color.B);
    }
}

/// <summary>
/// Custom renderer for dark-themed ContextMenuStrip.
/// </summary>
internal class ThemedMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly Func<ThemePalette> _getPalette;

    public ThemedMenuRenderer(Func<ThemePalette> getPalette) : base(new ThemedColorTable(getPalette))
    {
        _getPalette = getPalette;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(ToDrawingColor(_getPalette().SurfaceRaised));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(ToDrawingColor(_getPalette().Border));
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        var rc = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        if (e.Item.Selected)
        {
            using var brush = new SolidBrush(ToDrawingColor(_getPalette().HoverOverlay));
            using var gp = RoundedRect(rc, 4);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, gp);
        }
        else
        {
            using var brush = new SolidBrush(ToDrawingColor(_getPalette().SurfaceRaised));
            e.Graphics.FillRectangle(brush, rc);
        }
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(ToDrawingColor(_getPalette().Border));
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        var palette = _getPalette();
        e.TextColor = e.Item.Selected
            ? ToDrawingColor(palette.TextPrimary)
            : ToDrawingColor(palette.TextSecondary);
        base.OnRenderItemText(e);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static DrawColor ToDrawingColor(System.Windows.Media.Color color)
    {
        return DrawColor.FromArgb(color.A, color.R, color.G, color.B);
    }
}

internal class ThemedColorTable : Forms.ProfessionalColorTable
{
    private readonly Func<ThemePalette> _getPalette;

    public ThemedColorTable(Func<ThemePalette> getPalette)
    {
        _getPalette = getPalette;
    }

    public override DrawColor MenuBorder => ToDrawingColor(_getPalette().Border);
    public override DrawColor MenuItemBorder => DrawColor.Transparent;
    public override DrawColor MenuItemSelected => ToDrawingColor(_getPalette().HoverOverlay);
    public override DrawColor MenuStripGradientBegin => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor MenuStripGradientEnd => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor MenuItemSelectedGradientBegin => ToDrawingColor(_getPalette().HoverOverlay);
    public override DrawColor MenuItemSelectedGradientEnd => ToDrawingColor(_getPalette().HoverOverlay);
    public override DrawColor MenuItemPressedGradientBegin => ToDrawingColor(_getPalette().PressedOverlay);
    public override DrawColor MenuItemPressedGradientEnd => ToDrawingColor(_getPalette().PressedOverlay);
    public override DrawColor ImageMarginGradientBegin => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor ImageMarginGradientMiddle => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor ImageMarginGradientEnd => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor ToolStripDropDownBackground => ToDrawingColor(_getPalette().SurfaceRaised);
    public override DrawColor SeparatorDark => ToDrawingColor(_getPalette().Border);
    public override DrawColor SeparatorLight => DrawColor.Transparent;

    private static DrawColor ToDrawingColor(System.Windows.Media.Color color)
    {
        return DrawColor.FromArgb(color.A, color.R, color.G, color.B);
    }
}
