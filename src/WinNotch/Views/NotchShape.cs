using System.Windows;
using System.Windows.Media;

namespace WinNotch.Views;

/// <summary>
/// Custom WPF shape that draws the notch outline.
/// Matches boring.notch's NotchShape.swift — a rectangle with:
///   - Top edges: small inward quad-curves (the "ears")
///   - Bottom edges: larger outward quad-curves (rounded bottom)
/// </summary>
public class NotchShape : System.Windows.Shapes.Shape
{
    private static void ConstrainRadii(double available, ref double top, ref double bottom)
    {
        top = Math.Max(0, top);
        bottom = Math.Max(0, bottom);
        double total = top + bottom;
        if (total <= 0 || total <= available) return;
        double scale = Math.Max(0, available) / total;
        top *= scale;
        bottom *= scale;
    }
    public static readonly DependencyProperty TopCornerRadiusProperty =
        DependencyProperty.Register(nameof(TopCornerRadius), typeof(double), typeof(NotchShape),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BottomCornerRadiusProperty =
        DependencyProperty.Register(nameof(BottomCornerRadius), typeof(double), typeof(NotchShape),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TopCornerRadius
    {
        get => (double)GetValue(TopCornerRadiusProperty);
        set => SetValue(TopCornerRadiusProperty, value);
    }

    public double BottomCornerRadius
    {
        get => (double)GetValue(BottomCornerRadiusProperty);
        set => SetValue(BottomCornerRadiusProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            var rect = new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            if (rect.Width <= 0 || rect.Height <= 0)
                return Geometry.Empty;

            return CreateNotchGeometry(rect, TopCornerRadius, BottomCornerRadius);
        }
    }

    /// <summary>
    /// Creates the notch path geometry matching boring.notch's shape exactly.
    /// 
    /// The shape is:
    ///   - Top-left: straight to (minX, minY), then quad-curve inward to (minX + topR, minY + topR)
    ///   - Left side: straight down to (minX + topR, maxY - bottomR)
    ///   - Bottom-left: quad-curve outward to (minX + topR + bottomR, maxY)
    ///   - Bottom: straight across to (maxX - topR - bottomR, maxY)
    ///   - Bottom-right: quad-curve outward to (maxX - topR, maxY - bottomR)
    ///   - Right side: straight up to (maxX - topR, minY + topR)
    ///   - Top-right: quad-curve inward to (maxX, minY)
    ///   - Top: close back to start
    /// </summary>
    public static Geometry CreateNotchGeometry(Rect rect, double topR, double bottomR)
    {
        ConstrainRadii(Math.Min(rect.Height, rect.Width / 2), ref topR, ref bottomR);
        double minX = rect.Left;
        double minY = rect.Top;
        double maxX = rect.Right;
        double maxY = rect.Bottom;

        var fig = new PathFigure
        {
            StartPoint = new Point(minX, minY),
            IsClosed = true,
            IsFilled = true
        };

        // Top-left ear: quad curve inward
        fig.Segments.Add(new QuadraticBezierSegment(
            new Point(minX + topR, minY),               // control
            new Point(minX + topR, minY + topR),         // end
            true));

        // Left side down
        fig.Segments.Add(new LineSegment(
            new Point(minX + topR, maxY - bottomR), true));

        // Bottom-left curve outward
        fig.Segments.Add(new QuadraticBezierSegment(
            new Point(minX + topR, maxY),                // control
            new Point(minX + topR + bottomR, maxY),      // end
            true));

        // Bottom straight across
        fig.Segments.Add(new LineSegment(
            new Point(maxX - topR - bottomR, maxY), true));

        // Bottom-right curve outward
        fig.Segments.Add(new QuadraticBezierSegment(
            new Point(maxX - topR, maxY),                // control
            new Point(maxX - topR, maxY - bottomR),      // end
            true));

        // Right side up
        fig.Segments.Add(new LineSegment(
            new Point(maxX - topR, minY + topR), true));

        // Top-right ear: quad curve inward
        fig.Segments.Add(new QuadraticBezierSegment(
            new Point(maxX - topR, minY),                // control
            new Point(maxX, minY),                       // end
            true));

        // Close (top across)
        fig.Segments.Add(new LineSegment(
            new Point(minX, minY), true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Creates a notch geometry for side-docked (left/right) mode.
    /// The shape is a vertically-oriented notch anchored to a screen edge.
    /// For left dock: "ears" on the left edge, curves expand rightward.
    /// For right dock: "ears" on the right edge, curves expand leftward.
    /// </summary>
    public static Geometry CreateSideNotchGeometry(Rect rect, double topR, double bottomR, bool isLeft)
    {
        ConstrainRadii(Math.Min(rect.Width, rect.Height / 2), ref topR, ref bottomR);
        double minX = rect.Left;
        double minY = rect.Top;
        double maxX = rect.Right;
        double maxY = rect.Bottom;

        // topR acts as the "edge radius" (small ear curves along the docked edge)
        // bottomR acts as the "open radius" (larger outward curves on the free side)
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        if (isLeft)
        {
            // Anchored to left edge. Shape: top-left ear, down left side, bottom-left ear,
            // bottom across, bottom-right curve, up right side, top-right curve, back to start.
            fig.StartPoint = new Point(minX, minY);

            // Top-left ear: quad curve inward (going down)
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(minX, minY + topR),
                new Point(minX + topR, minY + topR),
                true));

            // Right side down to near bottom
            fig.Segments.Add(new LineSegment(
                new Point(maxX - bottomR, minY + topR), true));

            // Top-right curve outward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(maxX, minY + topR),
                new Point(maxX, minY + topR + bottomR),
                true));

            // Down the right (free) side
            fig.Segments.Add(new LineSegment(
                new Point(maxX, maxY - topR - bottomR), true));

            // Bottom-right curve outward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(maxX, maxY - topR),
                new Point(maxX - bottomR, maxY - topR),
                true));

            // Back to left side
            fig.Segments.Add(new LineSegment(
                new Point(minX + topR, maxY - topR), true));

            // Bottom-left ear: quad curve inward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(minX, maxY - topR),
                new Point(minX, maxY),
                true));

            // Close
            fig.Segments.Add(new LineSegment(new Point(minX, minY), true));
        }
        else
        {
            // Anchored to right edge. Mirror of left.
            fig.StartPoint = new Point(maxX, minY);

            // Top-right ear: quad curve inward (going down)
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(maxX, minY + topR),
                new Point(maxX - topR, minY + topR),
                true));

            // Left side down
            fig.Segments.Add(new LineSegment(
                new Point(minX + bottomR, minY + topR), true));

            // Top-left curve outward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(minX, minY + topR),
                new Point(minX, minY + topR + bottomR),
                true));

            // Down the left (free) side
            fig.Segments.Add(new LineSegment(
                new Point(minX, maxY - topR - bottomR), true));

            // Bottom-left curve outward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(minX, maxY - topR),
                new Point(minX + bottomR, maxY - topR),
                true));

            // Back to right side
            fig.Segments.Add(new LineSegment(
                new Point(maxX - topR, maxY - topR), true));

            // Bottom-right ear: quad curve inward
            fig.Segments.Add(new QuadraticBezierSegment(
                new Point(maxX, maxY - topR),
                new Point(maxX, maxY),
                true));

            // Close
            fig.Segments.Add(new LineSegment(new Point(maxX, minY), true));
        }

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }
}
