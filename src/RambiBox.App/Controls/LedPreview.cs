using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RambiBox.Core.Config;

namespace RambiBox.App.Controls;

public enum LedPreviewKind
{
    /// <summary>Screen picture + glowing LED dots (Home page).</summary>
    Live,
    /// <summary>Capture zones, LED numbers, first LED and direction (LED layout page).</summary>
    Layout,
}

/// <summary>
/// Draws a monitor with the LED strip around it. Data is pushed from code (<see cref="Zones"/>, <see cref="Colors"/>,
/// <see cref="Screen"/>) and <see cref="Refresh"/> is called by the owning page's preview timer.
/// </summary>
public sealed class LedPreview : FrameworkElement
{
    private static readonly Brush BezelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0F)));
    private static readonly Pen BezelPen = FrozenPen(Color.FromArgb(0x60, 0x80, 0x80, 0x90), 1);
    private static readonly Brush EmptyScreen = Frozen(new LinearGradientBrush(Color.FromRgb(0x1E, 0x22, 0x2C), Color.FromRgb(0x10, 0x12, 0x18), 90));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Pen DotPen = FrozenPen(Color.FromArgb(0x50, 0x80, 0x80, 0x80), 1);
    private static readonly Pen ZonePen = FrozenPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.75);
    private static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x14)));
    private static readonly Brush OffDot = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));

    public LedPreviewKind Kind { get; set; } = LedPreviewKind.Live;
    public IReadOnlyList<LedZone> Zones { get; set; } = Array.Empty<LedZone>();
    /// <summary>R,G,B bytes per zone (may be shorter than the zone list).</summary>
    public byte[]? Colors { get; set; }
    public ImageSource? Screen { get; set; }
    public double ScreenAspect { get; set; } = 16.0 / 9.0;
    /// <summary>Darken the picture (lights off / not in ambilight mode).</summary>
    public bool Dimmed { get; set; }

    public LedPreview()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SnapsToDevicePixels = true;
    }

    public void Refresh() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? w / ScreenAspect * 0.85 : availableSize.Height;
        return new Size(w, h);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static Pen FrozenPen(Color c, double w) => Frozen(new Pen(new SolidColorBrush(c), w));

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width < 40 || size.Height < 40) return;
        var zones = Zones;
        int n = zones.Count;
        bool layout = Kind == LedPreviewKind.Layout;

        // Fit the screen rectangle, leaving room for the LED ring (and labels in layout mode).
        double r = 4, outer = 0;
        Rect s = default;
        for (int iter = 0; iter < 2; iter++)
        {
            outer = 6 + r * 2 + 6 + (layout ? 26 : 8);
            double aw = Math.Max(10, size.Width - 2 * outer), ah = Math.Max(10, size.Height - 2 * outer);
            double w = aw, h = w / ScreenAspect;
            if (h > ah) { h = ah; w = h * ScreenAspect; }
            s = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
            double spacing = n > 0 ? 2 * (s.Width + s.Height) / n : 20;
            r = Math.Clamp(spacing * 0.34, 2.2, layout ? 5 : 6);
        }
        double gap = 6 + r + 5;

        var colors = Colors;
        var points = new Point[n];
        var sides = new int[n];
        for (int i = 0; i < n; i++) (points[i], sides[i]) = DotPosition(zones, i, s, gap);

        Color ColorOf(int i)
        {
            if (colors is null || 3 * i + 2 >= colors.Length) return System.Windows.Media.Colors.Black;
            return Color.FromRgb(colors[3 * i], colors[3 * i + 1], colors[3 * i + 2]);
        }

        // ---- glow behind the monitor (live mode)
        if (!layout && colors is not null)
        {
            double gr = Math.Max(r * 4.2, 14);
            for (int i = 0; i < n; i++)
            {
                var c = ColorOf(i);
                int peak = Math.Max(c.R, Math.Max(c.G, c.B));
                if (peak < 40) continue;
                // glow strength follows LED brightness; colors normalized so dim LEDs don't produce grey smudges
                byte a = (byte)(0x80 * peak / 255);
                var lit = Color.FromRgb((byte)(c.R * 255 / peak), (byte)(c.G * 255 / peak), (byte)(c.B * 255 / peak));
                var brush = new RadialGradientBrush(Color.FromArgb(a, lit.R, lit.G, lit.B), Color.FromArgb(0, lit.R, lit.G, lit.B));
                brush.Freeze();
                dc.DrawEllipse(brush, null, points[i], gr, gr);
            }
        }

        // ---- monitor
        var bezel = Rect.Inflate(s, 5, 5);
        dc.DrawRoundedRectangle(BezelBrush, BezelPen, bezel, 6, 6);
        if (Screen is not null) dc.DrawImage(Screen, s);
        else dc.DrawRectangle(EmptyScreen, null, s);
        if (Dimmed) dc.DrawRectangle(DimBrush, null, s);

        // ---- capture zones (layout mode)
        if (layout)
        {
            for (int i = 0; i < n; i++)
            {
                var z = zones[i];
                var zr = new Rect(s.X + z.Left * s.Width, s.Y + z.Top * s.Height,
                    Math.Max(0.5, z.Width * s.Width), Math.Max(0.5, z.Height * s.Height));
                var c = ColorOf(i);
                var fill = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
                fill.Freeze();
                dc.DrawRectangle(fill, ZonePen, zr);
            }
        }

        // ---- LED dots
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        for (int i = 0; i < n; i++)
        {
            var c = ColorOf(i);
            Brush fill = c.R + c.G + c.B < 12 ? OffDot : new SolidColorBrush(c);
            if (fill.CanFreeze) fill.Freeze();
            bool enabled = zones[i].Enabled;
            dc.DrawEllipse(enabled ? fill : null, DotPen, points[i], r, r);
        }

        if (!layout || n == 0) return;

        // ---- first LED, direction arrow, numbers
        var label = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        dc.DrawEllipse(null, new Pen(accent, 2), points[0], r + 3, r + 3);

        for (int i = 0; i < n; i++)
        {
            int num = i + 1;
            bool first = i == 0;
            if (!first && num % 5 != 0 && i != n - 1) continue;
            if (i == n - 1 && num % 5 != 0 && n > 5 && (n - 1) % 5 < 2) continue; // avoid overlapping the previous label
            var ft = new FormattedText(first ? "1" : num.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, first ? bold : typeface, first ? 12 : 10, first ? accent : label, dpi);
            var p = points[i];
            double d = r + (first ? 7 : 5);
            var o = sides[i] switch
            {
                0 => new Point(p.X - ft.Width / 2, p.Y - d - ft.Height),   // top: label above
                1 => new Point(p.X + d, p.Y - ft.Height / 2),              // right
                2 => new Point(p.X - ft.Width / 2, p.Y + d),               // bottom
                _ => new Point(p.X - d - ft.Width, p.Y - ft.Height / 2),   // left
            };
            dc.DrawText(ft, o);
        }

        // Direction arrow: inside the screen, next to the first LEDs, pointing along the strip.
        if (n >= 3)
        {
            int k = Math.Min(n - 1, Math.Max(2, n / 20));
            var a = points[0];
            var b = points[k];
            var dir = b - a;
            if (dir.Length > 1)
            {
                dir.Normalize();
                // inward normal: towards the screen center
                var center = new Point(s.X + s.Width / 2, s.Y + s.Height / 2);
                var normal = new Vector(-dir.Y, dir.X);
                if (Vector.Multiply(normal, center - a) < 0) normal = -normal;
                double inset = gap + Math.Min(s.Width, s.Height) * 0.12;
                var start = a + normal * inset + dir * 4;
                double len = Math.Max(36, (b - a).Length);
                var end = start + dir * len;
                var pen = new Pen(accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, start, end);
                var side1 = end - dir * 9 + new Vector(-dir.Y, dir.X) * 6;
                var side2 = end - dir * 9 - new Vector(-dir.Y, dir.X) * 6;
                dc.DrawLine(pen, end, side1);
                dc.DrawLine(pen, end, side2);
                var ft = new FormattedText("Start", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, 11, Brushes.White, dpi);
                var textPos = start + normal * 10;
                var tp = new Point(textPos.X - (dir.X < -0.5 ? ft.Width : 0), textPos.Y - (normal.Y < 0 ? ft.Height : 0) - (Math.Abs(dir.Y) > 0.5 ? ft.Height / 2 : 0));
                dc.DrawRoundedRectangle(LabelBack, null, new Rect(tp.X - 5, tp.Y - 2, ft.Width + 10, ft.Height + 4), 4, 4);
                dc.DrawText(ft, tp);
            }
        }
    }

    /// <summary>
    /// Places the dot for a zone just outside its screen edge. Side: 0 top, 1 right, 2 bottom, 3 left.
    /// Zones are often all the same normalized size (AmbiBox), so the edge is decided by the direction the strip
    /// travels (towards the next zone, or from the previous one at corners), like the AmbiBox importer does.
    /// </summary>
    private static (Point, int) DotPosition(IReadOnlyList<LedZone> zones, int i, Rect s, double gap)
    {
        var z = zones[i];
        double cx = (z.Left + z.Right) / 2, cy = (z.Top + z.Bottom) / 2;
        (double dx, double dy) Delta(int j)
        {
            if (j < 0 || j >= zones.Count) return (0, 0);
            var o = zones[j];
            return (Math.Abs((o.Left + o.Right) / 2 - cx) * s.Width, Math.Abs((o.Top + o.Bottom) / 2 - cy) * s.Height);
        }
        var (dx, dy) = Delta(i + 1);
        if (dx + dy < 0.5) (dx, dy) = Delta(i - 1);
        bool horizontal;
        if (dx + dy >= 0.5) horizontal = dx >= dy;
        else
        {
            // single zone: use the nearest edge
            double dTop = cy, dBottom = 1 - cy, dLeft = cx, dRight = 1 - cx;
            horizontal = Math.Min(dTop, dBottom) <= Math.Min(dLeft, dRight);
        }
        double x = s.X + cx * s.Width, y = s.Y + cy * s.Height;
        if (horizontal)
            return cy >= 0.5 ? (new Point(x, s.Bottom + gap), 2) : (new Point(x, s.Top - gap), 0);
        return cx >= 0.5 ? (new Point(s.Right + gap, y), 1) : (new Point(s.Left - gap, y), 3);
    }
}
