using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNotch.Models;

/// <summary>
/// Hover interaction mode for the notch.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HoverMode
{
    /// <summary>Hover slightly expands, click fully opens.</summary>
    HoverPeekClickOpen,
    /// <summary>Long hover fully opens, click also opens.</summary>
    LongHoverOpen
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    Dark,
    Light,
    Auto
}

/// <summary>
/// User-configurable settings, persisted to %AppData%/WinNotch/settings.json.
/// </summary>
public class AppSettings
{
    // General
    public bool StartOnBoot { get; set; }
    public HoverMode HoverMode { get; set; } = HoverMode.HoverPeekClickOpen;
    public int HoverOpenDelayMs { get; set; } = 100;
    public int HoverCloseDelayMs { get; set; } = 150;
    public int LongHoverDelayMs { get; set; } = 600;

    // Updates
    public bool AutoCheckForUpdates { get; set; } = true;

    // Screens & position
    /// <summary>Where the main island was last docked (persisted across restarts).</summary>
    public NotchDock NotchDockPosition { get; set; } = NotchDock.Top;

    /// <summary>Device name of the screen hosting the main island (null = primary).</summary>
    public string? NotchScreenDevice { get; set; }

    /// <summary>Show an island on every connected screen.</summary>
    public bool ShowOnAllScreens { get; set; }

    /// <summary>When multiple islands are shown, open/close them together.</summary>
    public bool SyncIslandsOpenState { get; set; } = true;

    /// <summary>
    /// Reserve a dedicated strip at the top of the screen (shell AppBar, like the
    /// taskbar) so maximized windows start below the notch. Opt-in.
    /// </summary>
    public bool ReserveSpaceAtTop { get; set; }

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.Auto;
    public bool AdaptToWindowsTheme { get; set; } = true;
    public double CornerRadiusScale { get; set; } = 1.0;
    public bool ShowShadow { get; set; } = true;

    // Music
    public bool ShowMusicControls { get; set; } = true;
    public bool ShowVisualizer { get; set; } = true;

    // System notices (media, volume, brightness, Caps Lock) are part of the notch and always on.
    [JsonIgnore] public bool ShowMusicFeedback => true;
    [JsonIgnore] public bool ShowCapsLockFeedback => true;
    [JsonIgnore] public bool ShowVolumeHud => true;
    [JsonIgnore] public bool ShowBrightnessHud => true;

    // Calendar
    public bool ShowCalendar { get; set; } = true;

    // Cards
    public bool ShowWeather { get; set; } = false;
    public bool ShowTasks { get; set; } = true;
    public bool ShowAgent { get; set; } = true;
    public bool ShowAudioCard { get; set; } = true;
    public bool ShowBatteryCard { get; set; } = true;
    public bool ShowFiles { get; set; } = true;
    public bool AlwaysShowMinimum { get; set; } = false;

    // Battery
    public bool ShowBattery { get; set; } = true;

    // Webcam
    public bool ShowWebcam { get; set; } = false;
    public int WebcamFps { get; set; } = 15;

    // Beta features
    public bool UseLiquidGlassTheme { get; set; } = false;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    // Write every property: skipping CLR-default values (0/false) would make
    // settings like "HoverCloseDelayMs = 0" silently revert on next load.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

                using var document = JsonDocument.Parse(json);
                bool hasAdaptSetting = document.RootElement.TryGetProperty(nameof(AdaptToWindowsTheme), out _);

                if (!hasAdaptSetting)
                    settings.AdaptToWindowsTheme = settings.Theme == AppTheme.Auto;

                if (settings.Theme == AppTheme.Auto)
                    settings.Theme = AppTheme.Dark;

                return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Atomic write: a crash mid-write must never corrupt settings.json
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch { }
    }

    public AppTheme GetManualTheme()
    {
        return Theme == AppTheme.Light ? AppTheme.Light : AppTheme.Dark;
    }

    public AppTheme GetRequestedTheme()
    {
        return AdaptToWindowsTheme ? AppTheme.Auto : GetManualTheme();
    }

    /// <summary>Register or unregister the app from Windows startup.</summary>
    public void ApplyStartOnBoot()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "WinNotch";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true);
            if (key == null) return;

            if (StartOnBoot)
            {
                var exePath = Environment.ProcessPath;
                if (exePath != null)
                    key.SetValue(valueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(valueName, false);
            }
        }
        catch { }
    }
}
