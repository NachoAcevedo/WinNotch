using System.Windows;
using System.Text.Json.Serialization;

namespace WinNotch.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotchDock
{
    Top,
    Left,
    Right
}

public static class NotchConstants
{
    // Closed dimensions (before DPI scaling)
    public const double ClosedWidth = 200;
    public const double ClosedHeight = 32;

    // Peek dimensions (slightly expanded on hover)
    public const double PeekWidth = 220;
    public const double PeekHeight = 34;

    // HUD dimensions (wider for volume/brightness slider)
    public const double HudWidth = 360;

    // Open dimensions (sized to fit music player content)
    public const double OpenWidth = 460;
    public const double OpenWidthWithCalendar = 640;
    public const double OpenWidthWithWebcam = 620;
    public const double OpenWidthWithCalendarAndWebcam = 800;
    public const double OpenHeight = 160;
    public const double OpenSettingsHeight = 420;

    // Corner radii
    public const double ClosedTopRadius = 6;
    public const double ClosedBottomRadius = 14;
    public const double OpenTopRadius = 10;
    public const double OpenBottomRadius = 20;

    // Window padding around notch for shadow space
    public const double WindowPadding = 20;

    // Animation
    public const double SpringResponse = 0.40;
    public const double SpringDamping = 0.82;

    // Hover
    public const int HoverOpenDelayMs = 200;
    public const int HoverCloseDelayMs = 150;

    // File Shelf
    public const double ShelfRowHeight = 80;
    public const double OpenHeightWithShelf = 250;
    public const double OpenSettingsHeightWithShelf = 500;

    // Shadow
    public static readonly System.Windows.Media.Color ShadowColor = System.Windows.Media.Color.FromArgb(180, 0, 0, 0);
    public const double ShadowBlurRadius = 12;
    public const double ShadowDepth = 2;

    // Side dock vertical open dimensions
    public const double SideOpenWidth = 160;    // how far the notch protrudes from edge
    public const double SideOpenHeight = 460;   // tall axis along screen edge

    // Drag & Dock
    public const double DragThreshold = 3;        // pixels before drag starts
    public const double EdgeSnapDistance = 80;     // px from screen edge to snap
    public const double ThrowVelocityThreshold = 800; // px/s to register as throw
}
