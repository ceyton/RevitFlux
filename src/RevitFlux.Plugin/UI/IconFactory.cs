using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitFlux.Plugin.UI;

/// <summary>
/// Draws the RevitFlux icons on a 32-unit grid, at 2x for high-DPI screens. The mark is two
/// streams bending into an F. Transparent background, so it suits light and dark ribbons.
/// </summary>
public static class IconFactory
{
    private static readonly Color Blue = Color.FromRgb(46, 107, 230);
    private static readonly Color Teal = Color.FromRgb(16, 168, 190);
    private static readonly Color GrayA = Color.FromRgb(128, 138, 152);
    private static readonly Color GrayB = Color.FromRgb(142, 152, 166);
    private static readonly Color Green = Color.FromRgb(22, 163, 92);

    public static BitmapSource CreateBrand(int size) => Render(size, dc => Mark(dc, Gradient(Blue, Teal), 0, 0, 1));

    /// <summary>On: colored mark with a filled check badge. Off: gray mark with an empty ring.</summary>
    public static BitmapSource CreateConnectionIcon(int size, bool isActive = false) => Render(size, dc =>
    {
        Mark(dc, isActive ? Gradient(Blue, Teal) : Gradient(GrayA, GrayB), -1, -1, 0.94);
        var center = new Point(24.5, 24.5);
        if (isActive)
        {
            dc.DrawEllipse(Solid(Green), null, center, 6.5, 6.5);
            var check = new StreamGeometry();
            using (var c = check.Open())
            {
                c.BeginFigure(new Point(21.4, 24.6), false, false);
                c.LineTo(new Point(23.7, 26.9), true, true);
                c.LineTo(new Point(27.8, 22.3), true, true);
            }
            check.Freeze();
            dc.DrawGeometry(null, Stroke(Brushes.White, 2), check);
        }
        else
        {
            dc.DrawEllipse(null, Stroke(Solid(GrayA), 2.6), center, 5, 5);
        }
    });

    /// <summary>Two sliders.</summary>
    public static BitmapSource CreateSettingsIcon(int size) => Render(size, dc =>
    {
        var brush = Gradient(Blue, Teal);
        var pen = Stroke(brush, 2.8);
        dc.DrawLine(pen, new Point(4, 10), new Point(15.5, 10));
        dc.DrawLine(pen, new Point(25.5, 10), new Point(28, 10));
        dc.DrawEllipse(null, pen, new Point(20.5, 10), 4.2, 4.2);
        dc.DrawLine(pen, new Point(4, 22), new Point(6.5, 22));
        dc.DrawLine(pen, new Point(16.5, 22), new Point(28, 22));
        dc.DrawEllipse(null, pen, new Point(11.5, 22), 4.2, 4.2);
    });

    private static void Mark(DrawingContext dc, Brush brush, double dx, double dy, double scale)
    {
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, dx + 16 * (1 - scale) - (1 - scale) * 8, dy + 16 * (1 - scale) - (1 - scale) * 8));
        var pen = Stroke(brush, 4.4);
        var outer = new StreamGeometry();
        using (var c = outer.Open())
        {
            c.BeginFigure(new Point(6, 28), false, false);
            c.LineTo(new Point(6, 13), true, true);
            c.ArcTo(new Point(14, 5), new Size(8, 8), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(27, 5), true, true);
        }
        outer.Freeze();
        dc.DrawGeometry(null, pen, outer);

        var inner = new StreamGeometry();
        using (var c = inner.Open())
        {
            c.BeginFigure(new Point(14, 28), false, false);
            c.LineTo(new Point(14, 19.5), true, true);
            c.ArcTo(new Point(19, 14.5), new Size(5, 5), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(24, 14.5), true, true);
        }
        inner.Freeze();
        dc.DrawGeometry(null, pen, inner);
        dc.Pop();
    }

    private static Pen Stroke(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(32, 32)) { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private static BitmapSource Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            draw(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size * 2, size * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
