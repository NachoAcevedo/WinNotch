using System.Windows.Media;

namespace PersonalCards;

/// <summary>
/// Esquema de colores Material You (roles de Material 3) generado desde un color semilla.
/// Aproxima HCT con CIELAB/LCh: el "tono" de Material es la luminosidad L*, así que cada rol
/// se obtiene fijando el matiz de la semilla, un croma por paleta y el tono del rol.
/// Los crómas son los del estilo "expresivo" de Nectar Bloom (neutros algo teñidos).
/// </summary>
public sealed class NectarScheme
{
    public bool IsDark { get; private init; }
    public Color Surface { get; private init; }
    public Color SurfaceContainerLow { get; private init; }
    public Color SurfaceContainer { get; private init; }
    public Color SurfaceContainerHigh { get; private init; }
    public Color SurfaceContainerHighest { get; private init; }
    public Color OnSurface { get; private init; }
    public Color OnSurfaceVariant { get; private init; }
    public Color Outline { get; private init; }
    public Color OutlineVariant { get; private init; }
    public Color Primary { get; private init; }
    public Color PrimaryPressed { get; private init; }
    public Color OnPrimary { get; private init; }
    public Color PrimaryContainer { get; private init; }
    public Color OnPrimaryContainer { get; private init; }
    public Color SecondaryContainer { get; private init; }
    public Color OnSecondaryContainer { get; private init; }
    public Color Error { get; private init; }
    public Color Warning { get; private init; }

    private const double PrimaryChroma = 40;
    private const double SecondaryChroma = 16;
    private const double NeutralChroma = 7;
    private const double NeutralVariantChroma = 11;
    private const double ErrorHue = 30;

    public static NectarScheme FromSeed(Color seed, bool dark)
    {
        var (_, _, hue) = ToLch(seed);
        Color P(double tone) => FromLch(tone, PrimaryChroma, hue);
        Color S(double tone) => FromLch(tone, SecondaryChroma, hue);
        Color N(double tone) => FromLch(tone, NeutralChroma, hue);
        Color NV(double tone) => FromLch(tone, NeutralVariantChroma, hue);

        return dark
            ? new NectarScheme
            {
                IsDark = true,
                Surface = N(6),
                SurfaceContainerLow = N(10),
                SurfaceContainer = N(13),
                SurfaceContainerHigh = N(18),
                SurfaceContainerHighest = N(23),
                OnSurface = N(91),
                OnSurfaceVariant = NV(80),
                Outline = NV(60),
                OutlineVariant = NV(30),
                Primary = P(80),
                PrimaryPressed = P(72),
                OnPrimary = P(20),
                PrimaryContainer = P(28),
                OnPrimaryContainer = P(90),
                SecondaryContainer = S(30),
                OnSecondaryContainer = S(90),
                Error = FromLch(80, 45, ErrorHue),
                Warning = Color.FromRgb(0xF2, 0xC1, 0x4E)
            }
            : new NectarScheme
            {
                IsDark = false,
                Surface = N(98),
                SurfaceContainerLow = N(96),
                SurfaceContainer = N(94),
                SurfaceContainerHigh = N(92),
                SurfaceContainerHighest = N(89),
                OnSurface = N(10),
                OnSurfaceVariant = NV(30),
                Outline = NV(50),
                OutlineVariant = NV(80),
                Primary = P(40),
                PrimaryPressed = P(34),
                OnPrimary = P(100),
                PrimaryContainer = P(90),
                OnPrimaryContainer = P(10),
                SecondaryContainer = S(90),
                OnSecondaryContainer = S(10),
                Error = FromLch(40, 60, ErrorHue),
                Warning = Color.FromRgb(0xB0, 0x7A, 0x00)
            };
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    // ---- Conversión sRGB <-> CIELAB (D65) <-> LCh ----

    private static (double L, double C, double H) ToLch(Color c)
    {
        double r = Linear(c.R / 255.0), g = Linear(c.G / 255.0), b = Linear(c.B / 255.0);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        double l = 116 * fy - 16, aa = 500 * (fx - fy), bb = 200 * (fy - fz);
        double h = Math.Atan2(bb, aa) * 180 / Math.PI;
        return (l, Math.Sqrt(aa * aa + bb * bb), h < 0 ? h + 360 : h);
    }

    /// <summary>Color con el tono (L*) y matiz pedidos; si el croma no entra en sRGB, lo reduce.</summary>
    private static Color FromLch(double tone, double chroma, double hue)
    {
        double lo = 0, hi = chroma;
        Color best = LabToRgb(tone, 0, 0, out _);
        for (int i = 0; i < 18; i++)
        {
            double c = i == 0 ? chroma : (lo + hi) / 2;
            double rad = hue * Math.PI / 180;
            var color = LabToRgb(tone, c * Math.Cos(rad), c * Math.Sin(rad), out bool inGamut);
            if (inGamut)
            {
                best = color;
                if (i == 0) break;
                lo = c;
            }
            else
            {
                hi = c;
            }
        }
        return best;
    }

    private static Color LabToRgb(double l, double a, double b, out bool inGamut)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        double x = FInv(fx) * 0.95047, y = FInv(fy), z = FInv(fz) * 1.08883;
        double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double bl = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
        const double eps = 0.0005;
        inGamut = r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && bl >= -eps && bl <= 1 + eps;
        return Color.FromRgb(ToByte(r), ToByte(g), ToByte(bl));
    }

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(Gamma(Math.Clamp(v, 0, 1)), 0, 1) * 255);
    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
    private static double FInv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
}
