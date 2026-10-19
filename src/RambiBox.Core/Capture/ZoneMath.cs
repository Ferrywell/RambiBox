using System.Runtime.CompilerServices;
using RambiBox.Core.Config;

namespace RambiBox.Core.Capture;

/// <summary>Normalized rectangle (0..1 of the monitor).</summary>
internal readonly record struct RectF(float Left, float Top, float Right, float Bottom)
{
    public static readonly RectF Full = new(0, 0, 1, 1);
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>A zone resolved to (fractional) pixel coordinates of the small image.</summary>
internal struct ZoneGeom
{
    public float X0, X1, Y0, Y1;   // fractional pixel edges
    public int Ix0, Ix1, Iy0, Iy1; // covered pixel range [Ix0, Ix1) × [Iy0, Iy1)
    public float InvArea;
    public bool Enabled;
}

/// <summary>
/// Zone averaging on the small (downscaled) image with an exact box filter: pixels only partially covered by a
/// zone contribute with their covered area, so even zones much narrower than a pixel average correctly.
/// </summary>
internal static class ZoneMath
{
    /// <summary>Smallest zone extent in pixels (avoids division by zero for degenerate zones).</summary>
    private const float MinExtentPx = 0.01f;

    /// <summary>Linear remap of a full-screen zone into the content rectangle (the picture area inside black bars).</summary>
    public static RectF Remap(LedZone z, RectF content) => new(
        content.Left + z.Left * content.Width,
        content.Top + z.Top * content.Height,
        content.Left + z.Right * content.Width,
        content.Top + z.Bottom * content.Height);

    /// <summary>Content rectangle for black bars given as fractions of the screen.</summary>
    public static RectF ContentRect(float top, float bottom, float left, float right)
    {
        float l = Math.Clamp(left, 0f, 0.49f), r = Math.Clamp(right, 0f, 0.49f);
        float t = Math.Clamp(top, 0f, 0.49f), b = Math.Clamp(bottom, 0f, 0.49f);
        return new RectF(l, t, 1f - r, 1f - b);
    }

    public static void BuildGeometry(IReadOnlyList<LedZone> zones, int width, int height, RectF content, Span<ZoneGeom> geoms)
    {
        for (int i = 0; i < zones.Count && i < geoms.Length; i++)
            geoms[i] = Build(zones[i], width, height, content);
    }

    public static ZoneGeom Build(LedZone zone, int width, int height, RectF content)
    {
        var r = Remap(zone, content);
        var g = new ZoneGeom { Enabled = zone.Enabled };
        (g.X0, g.X1) = Span(r.Left, r.Right, width);
        (g.Y0, g.Y1) = Span(r.Top, r.Bottom, height);
        g.Ix0 = Math.Clamp((int)MathF.Floor(g.X0), 0, width);
        g.Ix1 = Math.Clamp((int)MathF.Ceiling(g.X1), g.Ix0, width);
        g.Iy0 = Math.Clamp((int)MathF.Floor(g.Y0), 0, height);
        g.Iy1 = Math.Clamp((int)MathF.Ceiling(g.Y1), g.Iy0, height);
        g.InvArea = 1f / ((g.X1 - g.X0) * (g.Y1 - g.Y0));
        return g;
    }

    private static (float A, float B) Span(float a, float b, int size)
    {
        if (b < a) (a, b) = (b, a);
        if (float.IsNaN(a) || float.IsNaN(b)) (a, b) = (0f, 1f);
        a = Math.Clamp(a, 0f, 1f) * size;
        b = Math.Clamp(b, 0f, 1f) * size;
        if (b - a < MinExtentPx)
        {
            float c = Math.Clamp((a + b) * 0.5f, MinExtentPx * 0.5f, size - MinExtentPx * 0.5f);
            a = c - MinExtentPx * 0.5f;
            b = c + MinExtentPx * 0.5f;
        }
        return (a, b);
    }

    /// <summary>
    /// Averages each zone of the linear RGB image (interleaved, desktop orientation) and writes sRGB-encoded 0..1
    /// values to <paramref name="destRgb"/>. Averaging happens in linear light (physically correct light mixing).
    /// Disabled zones produce black.
    /// </summary>
    public static void Average(ReadOnlySpan<float> rgb, int width, ReadOnlySpan<ZoneGeom> geoms, Span<float> destRgb)
    {
        int n = Math.Min(geoms.Length, destRgb.Length / 3);
        for (int z = 0; z < n; z++)
        {
            ref readonly ZoneGeom g = ref geoms[z];
            int o = z * 3;
            if (!g.Enabled)
            {
                destRgb[o] = destRgb[o + 1] = destRgb[o + 2] = 0f;
                continue;
            }
            AverageLinear(rgb, width, in g, out float r, out float gg, out float b);
            destRgb[o] = ColorMath.LinearToSrgb(r);
            destRgb[o + 1] = ColorMath.LinearToSrgb(gg);
            destRgb[o + 2] = ColorMath.LinearToSrgb(b);
        }
    }

    /// <summary>Area-weighted linear average of one zone.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AverageLinear(ReadOnlySpan<float> rgb, int width, in ZoneGeom g, out float r, out float gr, out float b)
    {
        float sr = 0, sg = 0, sb = 0;
        for (int y = g.Iy0; y < g.Iy1; y++)
        {
            float wy = MathF.Min(y + 1, g.Y1) - MathF.Max(y, g.Y0);
            if (wy <= 0f) continue;
            float rr = 0, rg = 0, rb = 0;
            int p = (y * width + g.Ix0) * 3;
            for (int x = g.Ix0; x < g.Ix1; x++, p += 3)
            {
                float wx = MathF.Min(x + 1, g.X1) - MathF.Max(x, g.X0);
                if (wx <= 0f) continue;
                rr += wx * rgb[p];
                rg += wx * rgb[p + 1];
                rb += wx * rgb[p + 2];
            }
            sr += wy * rr; sg += wy * rg; sb += wy * rb;
        }
        float inv = g.InvArea;
        r = sr * inv; gr = sg * inv; b = sb * inv;
    }
}
