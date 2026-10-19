using RambiBox.Core.Capture;

namespace RambiBox.Tests.Capture;

public class BlackBarDetectorTests
{
    private const int W = 160, H = 90;

    /// <summary>Image with black bars; picture content is a bright-ish noisy pattern.</summary>
    private static float[] Frame(int top, int bottom, int left, int right, float picture = 0.4f, Action<float[]>? edit = null)
    {
        var img = new float[W * H * 3];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            bool inPicture = y >= top && y < H - bottom && x >= left && x < W - right;
            float v = inPicture ? picture * (0.5f + ((x * 31 + y * 17) % 10) / 20f) : 0.001f;
            int o = (y * W + x) * 3;
            img[o] = v; img[o + 1] = v * 0.8f; img[o + 2] = v * 0.6f;
        }
        edit?.Invoke(img);
        return img;
    }

    private static void Set(float[] img, int x, int y, float v)
    {
        int o = (y * W + x) * 3;
        img[o] = img[o + 1] = img[o + 2] = v;
    }

    /// <summary>Feeds the same frame for <paramref name="seconds"/> at 60 fps.</summary>
    private static double Run(BlackBarDetector d, float[] img, double start, double seconds)
    {
        double t = start;
        for (; t < start + seconds; t += 1 / 60.0) d.Update(img, W, H, t);
        return t;
    }

    [Fact]
    public void Measure_finds_letterbox_and_pillarbox_lines()
    {
        var m = BlackBarDetector.Measure(Frame(11, 11, 0, 0), W, H, new int[H], new int[W]);
        Assert.Equal((11, 11, 0, 0, false), (m.Top, m.Bottom, m.Left, m.Right, m.Dark));
        m = BlackBarDetector.Measure(Frame(0, 0, 20, 20), W, H, new int[H], new int[W]);
        Assert.Equal((0, 0, 20, 20), (m.Top, m.Bottom, m.Left, m.Right));
    }

    [Fact]
    public void Letterbox_applies_only_after_being_stable_for_half_a_second()
    {
        var d = new BlackBarDetector();
        var img = Frame(11, 11, 0, 0);
        double t = Run(d, img, 0, 0.4);
        Assert.Equal(0, d.Bars.Top);
        Run(d, img, t, 0.2);
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
        Assert.Equal(11 / 90f, d.Bars.Bottom, 4);
        Assert.Equal(0f, d.Bars.Left);
    }

    [Fact]
    public void Pillarbox_is_detected()
    {
        var d = new BlackBarDetector();
        Run(d, Frame(0, 0, 20, 20), 0, 0.7);
        Assert.Equal(20 / 160f, d.Bars.Left, 4);
        Assert.Equal(20 / 160f, d.Bars.Right, 4);
        Assert.Equal(0f, d.Bars.Top);
    }

    [Fact]
    public void Bars_release_quickly_when_content_appears()
    {
        var d = new BlackBarDetector();
        double t = Run(d, Frame(11, 11, 0, 0), 0, 0.7);
        Assert.True(d.Bars.Top > 0);
        var full = Frame(0, 0, 0, 0);
        t = Run(d, full, t, 0.15);
        Assert.True(d.Bars.Top > 0, "released too early");
        Run(d, full, t, 0.1);
        Assert.Equal(0f, d.Bars.Top);
    }

    [Fact]
    public void Dark_frames_neither_create_nor_release_bars()
    {
        var d = new BlackBarDetector();
        // Fade to black from a full picture must not count as bars.
        double t = Run(d, Frame(0, 0, 0, 0, picture: 0.001f), 0, 2);
        Assert.Equal(default, d.Bars);

        // Active bars survive a fade to black.
        t = Run(d, Frame(11, 11, 0, 0), t, 0.7);
        Assert.True(d.Bars.Top > 0);
        Run(d, Frame(0, 0, 0, 0, picture: 0.001f), t, 2);
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
    }

    [Fact]
    public void Asymmetric_dark_regions_are_not_bars()
    {
        var d = new BlackBarDetector();
        Run(d, Frame(25, 0, 0, 0), 0, 1);
        Assert.Equal(0f, d.Bars.Top);
        Run(d, Frame(0, 0, 40, 0), 1, 1);
        Assert.Equal(0f, d.Bars.Left);
    }

    [Fact]
    public void Huge_dark_areas_are_dark_content_not_bars()
    {
        var d = new BlackBarDetector();
        Run(d, Frame(40, 40, 0, 0), 0, 1); // 44% per side
        Assert.Equal(0f, d.Bars.Top);
    }

    [Fact]
    public void Subtitles_in_the_bottom_bar_keep_the_bars()
    {
        var d = new BlackBarDetector();
        double t = Run(d, Frame(11, 11, 0, 0), 0, 0.7);
        var withSubs = Frame(11, 11, 0, 0, edit: img =>
        {
            for (int x = 50; x < 110; x++) { Set(img, x, 84, 1f); Set(img, x, 85, 1f); }
        });
        Run(d, withSubs, t, 1);
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
        Assert.Equal(11 / 90f, d.Bars.Bottom, 4);
    }

    [Fact]
    public void Few_noise_pixels_in_a_bar_still_count_as_black()
    {
        var d = new BlackBarDetector();
        var img = Frame(11, 11, 0, 0, edit: im => { Set(im, 3, 2, 1f); Set(im, 100, 88, 1f); });
        Run(d, img, 0, 0.7);
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
    }

    [Fact]
    public void Bars_grow_only_after_stability_and_never_overestimate()
    {
        var d = new BlackBarDetector();
        double t = Run(d, Frame(11, 13, 0, 0), 0, 0.7); // slightly asymmetric → smaller side wins
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
        Assert.Equal(11 / 90f, d.Bars.Bottom, 4);
        t = Run(d, Frame(20, 20, 0, 0), t, 0.3);
        Assert.Equal(11 / 90f, d.Bars.Top, 4);
        Run(d, Frame(20, 20, 0, 0), t, 0.4);
        Assert.Equal(20 / 90f, d.Bars.Top, 4);
    }
}
