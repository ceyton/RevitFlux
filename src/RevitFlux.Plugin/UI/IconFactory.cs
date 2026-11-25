using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitFlux.Plugin.UI;

/// <summary>Draws the RevitFlux ribbon icons: white symbols on a blue-to-cyan tile.</summary>
public static class IconFactory
{
    private static readonly Color Blue = Color.FromRgb(37, 70, 200);
    private static readonly Color Cyan = Color.FromRgb(8, 178, 206);
    private static readonly Color Navy = Color.FromRgb(30, 41, 84);
    private static readonly Color Steel = Color.FromRgb(62, 84, 148);
    private static readonly Color OffDark = Color.FromRgb(100, 110, 124);
    private static readonly Color OffLight = Color.FromRgb(148, 158, 171);
    private static readonly Color OnGreen = Color.FromRgb(74, 222, 128);

    /// <summary>Flowing streams. Colored with a green dot when running, gray when stopped.</summary>
    public static BitmapSource CreateConnectionIcon(int size, bool isActive = false)
    {
        return Render(size, isActive ? Blue : OffDark, isActive ? Cyan : OffLight, (dc, s) =>
        {
            // Two streams stay legible at 16 px; three need the large size.
            var count = s < 24 ? 2 : 3;
            var gap = s * (count == 2 ? 0.26 : 0.19);
            var pen = RoundPen(s * (count == 2 ? 0.13 : 0.095));
            var first = s * 0.54 - gap * (count - 1) / 2;
            for (var i = 0; i < count; i++)
            {
                var y = first + i * gap;
                var stream = new StreamGeometry();
                using (var ctx = stream.Open())
                {
                    ctx.BeginFigure(new Point(s * 0.17, y + s * 0.07), false, false);
                    ctx.BezierTo(new Point(s * 0.40, y - s * 0.24), new Point(s * 0.60, y + s * 0.24),
                        new Point(s * 0.83, y - s * 0.07), true, true);
                }
                stream.Freeze();
                dc.DrawGeometry(null, pen, stream);
            }

            if (isActive)
            {
                var ring = new Pen(Brushes.White, s * 0.05);
                ring.Freeze();
                dc.DrawEllipse(Frozen(OnGreen), ring, new Point(s * 0.82, s * 0.18), s * 0.12, s * 0.12);
            }
        });
    }

    /// <summary>Three sliders.</summary>
    public static BitmapSource CreateSettingsIcon(int size)
    {
        return Render(size, Navy, Steel, (dc, s) =>
        {
            var pen = RoundPen(s * 0.075);
            var knobs = new[] { 0.66, 0.36, 0.58 };
            for (var i = 0; i < knobs.Length; i++)
            {
                var y = s * (0.27 + i * 0.23);
                dc.DrawLine(pen, new Point(s * 0.19, y), new Point(s * 0.81, y));
                dc.DrawEllipse(Brushes.White, null, new Point(s * knobs[i], y), s * 0.105, s * 0.105);
                dc.DrawEllipse(Frozen(Cyan), null, new Point(s * knobs[i], y), s * 0.06, s * 0.06);
            }
        });
    }

    private static Pen RoundPen(double thickness)
    {
        var pen = new Pen(Brushes.White, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static BitmapSource Render(int size, Color from, Color to, Action<DrawingContext, double> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var tile = new LinearGradientBrush(from, to, 45);
            tile.Freeze();
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            draw(dc, size);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
