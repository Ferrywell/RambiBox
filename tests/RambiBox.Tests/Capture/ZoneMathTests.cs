using RambiBox.Core.Capture;
using RambiBox.Core.Config;

namespace RambiBox.Tests.Capture;

public class ZoneMathTests
{
    private static float[] Image(int w, int h, Func<int, int, float> f)
    {
        var img = new float[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float v = f(x, y);
            int o = (y * w + x) * 3;
            img[o] = v; img[o + 1] = v * 0.5f; img[o + 2] = 0f;
        }
        return img;
    }

    /// <summary>Brute-force reference: supersample the piecewise-constant image over the zone.</summary>
    private static float Reference(float[] img, int w, int h, RectF r, int samples = 600)
    {
        double sum = 0;
        for (int j = 0; j < samples; j++)
        for (int i = 0; i < samples; i++)
        {
            double u = r.Left + (i + 0.5) / samples * r.Width, v = r.Top + (j + 0.5) / samples * r.Height;
            int x = Math.Min(w - 1, (int)(u * w)), y = Math.Min(h - 1, (int)(v * h));
            sum += img[(y * w + x) * 3];
        }
        return (float)(sum / (samples * samples));
    }

    /// <summary>Exact reference in double precision: overlap area of the zone with every pixel of the image.</summary>
    private static float ExactReference(float[] img, int w, int h, RectF r)
    {
        double x0 = r.Left * (double)w, x1 = r.Right * (double)w, y0 = r.Top * (double)h, y1 = r.Bottom * (double)h;
        double sum = 0, area = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double a = Math.Max(0, Math.Min(x + 1, x1) - Math.Max(x, x0)) * Math.Max(0, Math.Min(y + 1, y1) - Math.Max(y, y0));
            sum += a * img[(y * w + x) * 3];
            area += a;
        }
        return (float)(sum / area);
    }

    [Fact]
    public void Uniform_image_gives_the_color_in_every_zone()
    {
        var img = Image(160, 90, (_, _) => 0.25f);
        var zones = LayoutGenerator.Generate(new LayoutSettings());
        var geoms = new ZoneGeom[zones.Count];
        ZoneMath.BuildGeometry(zones, 160, 90, RectF.Full, geoms);
        var dest = new float[zones.Count * 3];
        ZoneMath.Average(img, 160, geoms, dest);
        for (int z = 0; z < zones.Count; z++)
        {
            Assert.Equal(ColorMath.LinearToSrgb(0.25f), dest[3 * z], 4);
            Assert.Equal(ColorMath.LinearToSrgb(0.125f), dest[3 * z + 1], 4);
            Assert.Equal(0f, dest[3 * z + 2]);
        }
    }

    [Fact]
    public void Partially_covered_pixels_are_weighted_by_covered_area()
    {
        // Column 0 is 1.0, everything else 0. Zone covers x = 0.5..1.5 px → exactly half of it is bright.
        var img = Image(10, 4, (x, _) => x == 0 ? 1f : 0f);
        var g = ZoneMath.Build(new LedZone(0.05f, 0f, 0.15f, 1f), 10, 4, RectF.Full);
        ZoneMath.AverageLinear(img, 10, in g, out float r, out _, out _);
        Assert.Equal(0.5f, r, 5);

        // Zone fully inside one pixel (sub-pixel zone) takes that pixel's value.
        g = ZoneMath.Build(new LedZone(0.02f, 0.3f, 0.07f, 0.4f), 10, 4, RectF.Full);
        ZoneMath.AverageLinear(img, 10, in g, out r, out _, out _);
        Assert.Equal(1f, r, 5);
    }

    [Fact]
    public void Narrow_zones_one_37th_of_the_width_are_exact()
    {
        // Arbitrary non-smooth image: hash-like values per pixel.
        const int w = 160, h = 90;
        var img = Image(w, h, (x, y) => ((x * 7919 + y * 104729) % 97) / 96f);
        for (int i = 0; i < 37; i++)
        {
            var zone = new LedZone(i / 37f, 0f, (i + 1) / 37f, 0.0527f);
            var g = ZoneMath.Build(zone, w, h, RectF.Full);
            ZoneMath.AverageLinear(img, w, in g, out float r, out _, out _);
            var rect = new RectF(zone.Left, zone.Top, zone.Right, zone.Bottom);
            float exact = ExactReference(img, w, h, rect);
            Assert.True(Math.Abs(exact - r) < 1e-4f, $"zone {i}: {r} vs exact {exact}");
            float sampled = Reference(img, w, h, rect);
            Assert.True(Math.Abs(sampled - r) < 1e-2f, $"zone {i}: {r} vs supersampled {sampled}");
        }
    }

    [Fact]
    public void Disabled_zones_are_black_and_extra_output_untouched_by_length_mismatch()
    {
        var img = Image(16, 9, (_, _) => 1f);
        LedZone[] zones = [new(0, 0, 0.5f, 0.5f), new(0.5f, 0.5f, 1, 1, Enabled: false)];
        var geoms = new ZoneGeom[2];
        ZoneMath.BuildGeometry(zones, 16, 9, RectF.Full, geoms);
        var dest = new float[6];
        ZoneMath.Average(img, 16, geoms, dest);
        Assert.Equal(1f, dest[0], 5);
        Assert.Equal(0f, dest[3]);
        Assert.Equal(0f, dest[4]);
        Assert.Equal(0f, dest[5]);
    }

    [Fact]
    public void Degenerate_and_out_of_range_zones_do_not_produce_nan()
    {
        var img = Image(16, 9, (x, _) => x / 15f);
        LedZone[] zones = [new(0.3f, 0.3f, 0.3f, 0.3f), new(-1f, -1f, 2f, 2f), new(1f, 1f, 1f, 1f), new(0.8f, 0.9f, 0.2f, 0.1f)];
        foreach (var z in zones)
        {
            var g = ZoneMath.Build(z, 16, 9, RectF.Full);
            ZoneMath.AverageLinear(img, 16, in g, out float r, out _, out _);
            Assert.False(float.IsNaN(r) || float.IsInfinity(r));
            Assert.InRange(r, 0f, 1f);
        }
    }

    [Fact]
    public void Zones_are_remapped_linearly_into_the_content_rect()
    {
        var content = ZoneMath.ContentRect(top: 0.125f, bottom: 0.125f, left: 0f, right: 0f);
        var top = ZoneMath.Remap(new LedZone(0.5f, 0f, 0.6f, 0.0527f), content);
        Assert.Equal(0.5f, top.Left, 5);
        Assert.Equal(0.125f, top.Top, 5);
        Assert.Equal(0.125f + 0.0527f * 0.75f, top.Bottom, 5);
        var bottom = ZoneMath.Remap(new LedZone(0f, 0.9473f, 0.1f, 1f), content);
        Assert.Equal(0.875f, bottom.Bottom, 5);

        var pillar = ZoneMath.ContentRect(0, 0, 0.125f, 0.125f);
        var left = ZoneMath.Remap(new LedZone(0f, 0.2f, 0.027f, 0.3f), pillar);
        Assert.Equal(0.125f, left.Left, 5);
        Assert.Equal(0.125f + 0.027f * 0.75f, left.Right, 5);
        Assert.Equal(0.2f, left.Top, 5);
    }

    [Fact]
    public void Letterboxed_top_zone_samples_inside_the_picture()
    {
        // 90 rows: 11 black rows at top and bottom, picture (value 1) in between.
        var img = Image(160, 90, (_, y) => y >= 11 && y < 79 ? 1f : 0f);
        var content = ZoneMath.ContentRect(11 / 90f, 11 / 90f, 0, 0);
        var g = ZoneMath.Build(new LedZone(0.4f, 0f, 0.43f, 0.0527f), 160, 90, content);
        ZoneMath.AverageLinear(img, 160, in g, out float r, out _, out _);
        Assert.Equal(1f, r, 4);
        g = ZoneMath.Build(new LedZone(0.4f, 0f, 0.43f, 0.0527f), 160, 90, RectF.Full);
        ZoneMath.AverageLinear(img, 160, in g, out r, out _, out _);
        Assert.Equal(0f, r, 4);
    }
}
