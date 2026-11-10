using System;
using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace WinNotch.Services;

public sealed class ThemePalette
{
    private const double MinimumTextContrast = 4.5;
    private const double MinimumAccentContrast = 3.0;

    public bool IsLight { get; init; }
    public bool IsHighContrast { get; init; }
    public bool UseReducedMotion { get; init; }
    public bool UseLiquidGlass { get; init; }

    public Color Background { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceRaised { get; init; }
    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextTertiary { get; init; }
    public Color TextMuted { get; init; }
    public Color TextDim { get; init; }
    public Color TextSubtle { get; init; }
    public Color TextFaint { get; init; }
    public Color TextFaintest { get; init; }
    public Color Border { get; init; }
    public Color Accent { get; init; }
    public Color AccentPressed { get; init; }
    public Color AccentSoft { get; init; }
    public Color AccentText { get; init; }
    public Color Success { get; init; }
    public Color Warning { get; init; }
    public Color Danger { get; init; }
    public Color FocusRing { get; init; }
    public Color DisabledBackground { get; init; }
    public Color DisabledText { get; init; }
    public Color HoverOverlay { get; init; }
    public Color PressedOverlay { get; init; }
    public Color ScrollThumb { get; init; }
    public Color ScrollThumbHover { get; init; }
    public Color ScrollThumbDrag { get; init; }
    public Color Shadow { get; init; }
    public Color VisualizerBar { get; init; }

    public static ThemePalette Create(bool isLight, bool useLiquidGlass)
    {
        var isHighContrast = SystemParameters.HighContrast;
        var reduceMotion = isHighContrast || !SystemParameters.ClientAreaAnimation;
        var accent = DetectSystemAccentColor();

        return isHighContrast
            ? CreateHighContrast(isLight, useLiquidGlass, accent, reduceMotion)
            : CreateStandard(isLight, useLiquidGlass, accent, reduceMotion);
    }

    private static ThemePalette CreateStandard(bool isLight, bool useLiquidGlass, Color accentSeed, bool reduceMotion)
    {
        var readabilityBackground = isLight
            ? Color.FromRgb(0xFA, 0xF8, 0xFC)
            : Color.FromRgb(0x16, 0x14, 0x1B);

        // Liquid glass: a thin tint (~35-40%) over the blurred backdrop — iOS-style
        // glass is mostly the refracted background, not a solid fill. Non-glass
        // light is deliberately off-white so the island still reads on white
        // wallpapers (paired with the always-on soft shadow).
        var background = useLiquidGlass
            ? (isLight
                ? Mix(Color.FromArgb(0x59, 0xEC, 0xEE, 0xF2), accentSeed, 0.03)
                : Mix(Color.FromArgb(0x66, 0x12, 0x12, 0x15), accentSeed, 0.03))
            : (isLight
                ? Mix(Color.FromRgb(0xFA, 0xF8, 0xFC), accentSeed, 0.035)
                : Mix(readabilityBackground, accentSeed, 0.035));

        var surface = useLiquidGlass
            ? (isLight
                ? Mix(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), accentSeed, 0.05)
                : Mix(Color.FromArgb(0xB8, 0x16, 0x16, 0x19), accentSeed, 0.03))
            : (isLight
                ? Mix(Color.FromRgb(0xFF, 0xFF, 0xFF), accentSeed, 0.045)
                : Mix(readabilityBackground, Colors.White, 0.065));

        var surfaceRaised = useLiquidGlass
            ? (isLight
                ? Mix(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), accentSeed, 0.02)
                : Mix(Color.FromArgb(0xE0, 0x1B, 0x1B, 0x1F), Colors.White, 0.05))
            : (isLight
                ? Mix(Color.FromRgb(0xFF, 0xFF, 0xFF), accentSeed, 0.015)
                : Mix(surface, Colors.White, 0.055));

        var textPrimary = EnsureContrast(isLight ? Color.FromRgb(0x12, 0x18, 0x22) : Color.FromRgb(0xF5, 0xF7, 0xFB), readabilityBackground, 7.0);
        var textSecondary = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.62 : 0.38), readabilityBackground, MinimumTextContrast);
        var textTertiary = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.70 : 0.48), readabilityBackground, MinimumTextContrast);
        var textMuted = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.78 : 0.58), readabilityBackground, 3.7);
        var textDim = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.82 : 0.64), readabilityBackground, 3.2);
        var textSubtle = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.86 : 0.71), readabilityBackground, 3.0);
        var textFaint = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.90 : 0.78), readabilityBackground, 2.6);
        var textFaintest = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.94 : 0.84), readabilityBackground, 2.2);
        var border = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.90 : 0.80), readabilityBackground, 1.7);

        var accent = EnsureContrast(isLight ? Mix(accentSeed, Colors.Black, 0.10) : Mix(accentSeed, Colors.White, 0.40), readabilityBackground, MinimumAccentContrast);
        var accentPressed = Mix(accent, isLight ? Colors.Black : Colors.White, isLight ? 0.16 : 0.12);
        var accentSoft = WithAlpha(accent, (byte)(isLight ? 0x26 : 0x36));
        var accentText = PickReadableForeground(accent, MinimumTextContrast);

        var success = EnsureContrast(Color.FromRgb(0x1E, 0xB8, 0x4B), readabilityBackground, MinimumAccentContrast);
        var warning = EnsureContrast(Color.FromRgb(0xC9, 0x7A, 0x00), readabilityBackground, MinimumAccentContrast);
        var danger = EnsureContrast(Color.FromRgb(0xD8, 0x3B, 0x32), readabilityBackground, MinimumAccentContrast);
        var focusRing = EnsureContrast(accent, readabilityBackground, MinimumAccentContrast);

        var disabledBackground = Mix(surfaceRaised, textPrimary, isLight ? 0.06 : 0.12);
        var disabledText = EnsureContrast(Mix(textPrimary, readabilityBackground, isLight ? 0.88 : 0.76), readabilityBackground, 2.5);
        var hoverOverlay = WithAlpha(textPrimary, (byte)(isLight ? 0x12 : 0x1F));
        var pressedOverlay = WithAlpha(textPrimary, (byte)(isLight ? 0x20 : 0x32));
        var scrollThumb = WithAlpha(textPrimary, (byte)(isLight ? 0x45 : 0x55));
        var scrollThumbHover = WithAlpha(textPrimary, (byte)(isLight ? 0x66 : 0x72));
        var scrollThumbDrag = WithAlpha(textPrimary, (byte)(isLight ? 0x88 : 0x92));
        var shadow = WithAlpha(Colors.Black, (byte)(useLiquidGlass ? 0x50 : (isLight ? 0x5A : 0x62)));

        return new ThemePalette
        {
            IsLight = isLight,
            IsHighContrast = false,
            UseReducedMotion = reduceMotion,
            UseLiquidGlass = useLiquidGlass,
            Background = background,
            Surface = surface,
            SurfaceRaised = surfaceRaised,
            TextPrimary = textPrimary,
            TextSecondary = textSecondary,
            TextTertiary = textTertiary,
            TextMuted = textMuted,
            TextDim = textDim,
            TextSubtle = textSubtle,
            TextFaint = textFaint,
            TextFaintest = textFaintest,
            Border = border,
            Accent = accent,
            AccentPressed = accentPressed,
            AccentSoft = accentSoft,
            AccentText = accentText,
            Success = success,
            Warning = warning,
            Danger = danger,
            FocusRing = focusRing,
            DisabledBackground = disabledBackground,
            DisabledText = disabledText,
            HoverOverlay = hoverOverlay,
            PressedOverlay = pressedOverlay,
            ScrollThumb = scrollThumb,
            ScrollThumbHover = scrollThumbHover,
            ScrollThumbDrag = scrollThumbDrag,
            Shadow = shadow,
            VisualizerBar = accent
        };
    }

    private static ThemePalette CreateHighContrast(bool isLight, bool useLiquidGlass, Color accentSeed, bool reduceMotion)
    {
        var background = System.Windows.SystemColors.WindowColor;
        var textPrimary = EnsureContrast(System.Windows.SystemColors.WindowTextColor, background, 7.0);
        var textSecondary = EnsureContrast(Mix(textPrimary, background, isLight ? 0.45 : 0.35), background, MinimumTextContrast);
        var border = EnsureContrast(Mix(textPrimary, background, 0.80), background, 3.0);
        var surface = Mix(background, textPrimary, isLight ? 0.03 : 0.08);
        var surfaceRaised = Mix(background, textPrimary, isLight ? 0.06 : 0.12);
        var accent = EnsureContrast(System.Windows.SystemColors.HighlightColor == background ? accentSeed : System.Windows.SystemColors.HighlightColor, background, MinimumAccentContrast);
        var accentText = EnsureContrast(System.Windows.SystemColors.HighlightTextColor, accent, MinimumTextContrast);
        var muted = EnsureContrast(System.Windows.SystemColors.GrayTextColor, background, 3.0);

        return new ThemePalette
        {
            IsLight = RelativeLuminance(background) > 0.5,
            IsHighContrast = true,
            UseReducedMotion = reduceMotion,
            UseLiquidGlass = false,
            Background = background,
            Surface = surface,
            SurfaceRaised = surfaceRaised,
            TextPrimary = textPrimary,
            TextSecondary = textSecondary,
            TextTertiary = textSecondary,
            TextMuted = muted,
            TextDim = muted,
            TextSubtle = muted,
            TextFaint = muted,
            TextFaintest = muted,
            Border = border,
            Accent = accent,
            AccentPressed = Mix(accent, textPrimary, 0.16),
            AccentSoft = WithAlpha(accent, 0x30),
            AccentText = accentText,
            Success = EnsureContrast(Color.FromRgb(0x00, 0x88, 0x00), background, MinimumAccentContrast),
            Warning = EnsureContrast(Color.FromRgb(0x9A, 0x57, 0x00), background, MinimumAccentContrast),
            Danger = EnsureContrast(Color.FromRgb(0xAA, 0x00, 0x00), background, MinimumAccentContrast),
            FocusRing = accent,
            DisabledBackground = Mix(background, textPrimary, 0.08),
            DisabledText = muted,
            HoverOverlay = WithAlpha(textPrimary, 0x18),
            PressedOverlay = WithAlpha(textPrimary, 0x2A),
            ScrollThumb = WithAlpha(textPrimary, 0x55),
            ScrollThumbHover = WithAlpha(textPrimary, 0x72),
            ScrollThumbDrag = WithAlpha(textPrimary, 0x88),
            Shadow = WithAlpha(Colors.Black, 0x18),
            VisualizerBar = accent
        };
    }

    private static Color DetectSystemAccentColor()
    {
        try
        {
            var settings = new UISettings();
            var color = settings.GetColorValue(UIColorType.Accent);
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch
        {
            return SystemParameters.WindowGlassColor;
        }
    }

    private static Color PickReadableForeground(Color background, double minContrast)
    {
        var whiteContrast = ContrastRatio(Colors.White, background);
        var blackContrast = ContrastRatio(Colors.Black, background);
        if (whiteContrast >= minContrast || whiteContrast >= blackContrast)
            return Colors.White;

        return Colors.Black;
    }

    private static Color EnsureContrast(Color foreground, Color background, double minContrast)
    {
        if (ContrastRatio(foreground, background) >= minContrast)
            return foreground;

        var lighter = Colors.White;
        var darker = Colors.Black;
        var lighten = ContrastRatio(lighter, background) >= ContrastRatio(darker, background);
        var candidate = foreground;

        for (int i = 0; i < 24; i++)
        {
            candidate = Mix(candidate, lighten ? lighter : darker, 0.12);
            if (ContrastRatio(candidate, background) >= minContrast)
                return candidate;
        }

        return lighten ? lighter : darker;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLum = RelativeLuminance(first);
        var secondLum = RelativeLuminance(second);
        var lighter = Math.Max(firstLum, secondLum);
        var darker = Math.Min(firstLum, secondLum);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        var r = ToLinear(color.R / 255.0);
        var g = ToLinear(color.G / 255.0);
        var b = ToLinear(color.B / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double ToLinear(double channel)
    {
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static Color Mix(Color baseColor, Color mixColor, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(baseColor.A + ((mixColor.A - baseColor.A) * amount)),
            (byte)Math.Round(baseColor.R + ((mixColor.R - baseColor.R) * amount)),
            (byte)Math.Round(baseColor.G + ((mixColor.G - baseColor.G) * amount)),
            (byte)Math.Round(baseColor.B + ((mixColor.B - baseColor.B) * amount)));
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
