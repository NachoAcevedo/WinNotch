using System.Windows;
using System.Windows.Media;

namespace WinNotch.Views;

// Small vector drawing: no downloads, animation, blur, or additional weather requests.
public sealed class WeatherIcon : FrameworkElement
{
    public int Code { get; set; } = -1;
    public bool IsDay { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        Brush accent = TryFindResource("AccentBrush") as Brush ?? Brushes.Plum;
        Brush foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.WhiteSmoke;
        var pen = new Pen(accent, 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 68, ActualHeight / 68));
        bool clear = Code is 0 or 1;
        bool partly = Code == 2;
        if (clear || partly)
        {
            if (IsDay)
            {
                Point center = partly ? new Point(25, 24) : new Point(34, 32);
                dc.DrawEllipse(null, pen, center, 12, 12);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    dc.DrawLine(pen, new Point(center.X + 18 * Math.Cos(a), center.Y + 18 * Math.Sin(a)), new Point(center.X + 23 * Math.Cos(a), center.Y + 23 * Math.Sin(a)));
                }
            }
            else
            {
                // Crescent formed with a geometry subtraction, without painting over the card.
                var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(32, 31), 22, 22), new EllipseGeometry(new Point(42, 22), 21, 21));
                dc.DrawGeometry(accent, null, moon);
                dc.DrawEllipse(foreground, null, new Point(53, 15), 1.8, 1.8);
                dc.DrawLine(pen, new Point(47, 7), new Point(47, 13));
                dc.DrawLine(pen, new Point(44, 10), new Point(50, 10));
            }
        }
        if (!clear)
        {
            var cloud = Geometry.Parse("M 16,46 C 3,46 4,29 17,29 C 18,13 41,12 46,28 C 63,24 68,46 52,46 Z");
            dc.DrawGeometry(TryFindResource("SurfaceBrush") as Brush ?? Brushes.Transparent, new Pen(foreground, 2.8) { LineJoin = PenLineJoin.Round }, cloud);
            if (Code is 45 or 48)
            {
                dc.DrawLine(pen, new Point(13, 53), new Point(56, 53));
                dc.DrawLine(pen, new Point(19, 60), new Point(50, 60));
            }
            else if (Code is 95 or 96 or 99)
                dc.DrawGeometry(accent, null, Geometry.Parse("M 35,44 L 25,57 L 34,57 L 29,67 L 47,50 L 37,50 L 43,44 Z"));
            else if (Code is 71 or 73 or 75 or 77 or 85 or 86)
            {
                foreach (double x in new[] { 20.0, 34.0, 48.0 })
                {
                    dc.DrawLine(pen, new Point(x - 3, 56), new Point(x + 3, 56));
                    dc.DrawLine(pen, new Point(x, 53), new Point(x, 59));
                }
            }
            else if (Code is >= 51 and <= 82)
                foreach (double x in new[] { 20.0, 34.0, 48.0 }) dc.DrawLine(pen, new Point(x, 53), new Point(x - 3, 60));
        }
        dc.Pop();
    }
}
