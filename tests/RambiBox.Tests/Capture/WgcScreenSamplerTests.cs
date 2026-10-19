using System.Diagnostics;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using Windows.Graphics.DirectX;

namespace RambiBox.Tests.Capture;

/// <summary>Windows.Graphics.Capture parts that need no GPU: pool/format/rotation decisions, stall detection, lifecycle.</summary>
public class WgcScreenSamplerTests
{
    [Theory]
    [InlineData(false, DirectXPixelFormat.B8G8R8A8UIntNormalized)]
    [InlineData(true, DirectXPixelFormat.R16G16B16A16Float)]
    public void Pixel_format_follows_the_monitor_hdr_state(bool hdr, DirectXPixelFormat expected)
    {
        Assert.Equal(expected, WgcCapturePolicy.PixelFormat(hdr));
        // Same numeric values as DXGI_FORMAT, which the shared GPU pipeline understands.
        Assert.Equal((int)GpuFrameReducer.FormatBgra, (int)WgcCapturePolicy.PixelFormat(false));
        Assert.Equal((int)GpuFrameReducer.FormatFp16, (int)WgcCapturePolicy.PixelFormat(true));
    }

    [Theory]
    [InlineData(2560, 1440, false, 2)]
    [InlineData(2560, 1440, true, 2)]
    [InlineData(3840, 2160, false, 2)]
    [InlineData(3840, 2160, true, 2)]   // 66 MB per buffer: just below the limit
    [InlineData(5120, 2880, true, 1)]   // 118 MB
    [InlineData(7680, 4320, false, 1)]  // 133 MB
    [InlineData(0, 0, false, 2)]
    public void Buffer_count_is_two_except_for_huge_surfaces(int w, int h, bool hdr, int expected)
    {
        Assert.Equal(expected, WgcCapturePolicy.BufferCount(w, h, hdr));
    }

    [Theory]
    // Upright frames (what WGC delivers) → no rotation, whatever the monitor rotation is.
    [InlineData(2560, 1440, 2560, 1440, (int)SurfaceRotation.Identity, (int)SurfaceRotation.Identity)]
    [InlineData(1440, 2560, 1440, 2560, (int)SurfaceRotation.Rotate90, (int)SurfaceRotation.Identity)]
    [InlineData(1440, 2560, 1440, 2560, (int)SurfaceRotation.Rotate270, (int)SurfaceRotation.Identity)]
    [InlineData(2560, 1440, 2560, 1440, (int)SurfaceRotation.Rotate180, (int)SurfaceRotation.Identity)]
    // Defensive: frames in the panel's native orientation on a portrait desktop → apply the DXGI rotation.
    [InlineData(2560, 1440, 1440, 2560, (int)SurfaceRotation.Rotate90, (int)SurfaceRotation.Rotate90)]
    [InlineData(2560, 1440, 1440, 2560, (int)SurfaceRotation.Rotate270, (int)SurfaceRotation.Rotate270)]
    // Mismatch that is not a 90° swap (mode change in flight) → Identity.
    [InlineData(1920, 1080, 2560, 1440, (int)SurfaceRotation.Identity, (int)SurfaceRotation.Identity)]
    public void Rotation_detects_upright_frames(int fw, int fh, int dw, int dh, int dxgi, int expected)
    {
        Assert.Equal((SurfaceRotation)expected, WgcCapturePolicy.Rotation(fw, fh, dw, dh, (SurfaceRotation)dxgi));
    }

    [Fact]
    public void Default_min_update_interval_stays_above_the_ui_frame_rates()
    {
        // 20 ms = 50 fps: never limits the LEDs below the 50 fps option and the default ~34 fps link.
        Assert.InRange(WgcCapturePolicy.DefaultMinUpdateIntervalMs, 10, 1000 / 50);
    }

    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static void Feed(DeliveryGapTracker t, ref double now, int frames, double intervalMs)
    {
        for (int i = 0; i < frames; i++)
        {
            now += intervalMs;
            t.OnFrame(Ticks(now));
        }
    }

    [Fact]
    public void Gap_tracker_counts_a_stall_inside_steady_delivery()
    {
        var t = new DeliveryGapTracker();
        double now = 1000;
        Feed(t, ref now, 30, 16.7);
        Feed(t, ref now, 1, 2400);   // stall
        Feed(t, ref now, 5, 16.7);   // delivery resumes
        var (count, max, total) = t.Take();
        Assert.Equal(1, count);
        Assert.Equal(2400, max, 1);
        Assert.Equal(2400, total, 1);
        Assert.Equal((0, 0.0, 0.0), t.Take()); // reset
    }

    [Fact]
    public void Gap_tracker_ignores_a_static_screen_and_normal_pauses()
    {
        var t = new DeliveryGapTracker();
        double now = 1000;
        // A few frames (cursor blink / mouse over a button), then the screen stays static for a long time.
        Feed(t, ref now, 3, 16.7);
        Feed(t, ref now, 1, 5000);
        Feed(t, ref now, 5, 16.7);
        // Steady video, then a long pause that is followed by sparse frames (video paused → occasional UI updates).
        Feed(t, ref now, 30, 16.7);
        Feed(t, ref now, 1, 3000);
        Feed(t, ref now, 1, 16.7);
        Feed(t, ref now, 1, 400);
        Feed(t, ref now, 3, 16.7);
        // Short hiccups (< 500 ms) are not stalls.
        Feed(t, ref now, 30, 16.7);
        Feed(t, ref now, 1, 300);
        Feed(t, ref now, 5, 16.7);
        Assert.Equal(0, t.Take().Count);
    }

    [Fact]
    public void Rate_limited_log_lets_one_line_through_per_interval_and_counts_the_rest()
    {
        var gate = new RateLimitedLog(TimeSpan.FromMilliseconds(150));
        Assert.True(gate.ShouldLog(out int s0));
        Assert.Equal(0, s0);
        Assert.False(gate.ShouldLog(out _));
        Assert.False(gate.ShouldLog(out _));
        Thread.Sleep(200);
        Assert.True(gate.ShouldLog(out int s1));
        Assert.Equal(2, s1);
    }

    [Fact]
    public void Support_check_never_throws()
    {
        bool supported = WgcScreenSampler.IsSupported();
        Assert.Equal(supported, WgcScreenSampler.IsSupported()); // cached
    }

    [Fact]
    public void Sample_after_dispose_is_unavailable_never_throws_and_does_not_spin()
    {
        var sampler = new WgcScreenSampler();
        sampler.Configure("", [new LedZone(0, 0, 1, 1)], true);
        sampler.Dispose();
        sampler.Dispose(); // idempotent
        var dest = new float[6];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) Assert.Equal(SampleResult.Unavailable, sampler.Sample(dest, 0));
        Assert.True(sw.ElapsedMilliseconds >= 30, $"returned too fast ({sw.ElapsedMilliseconds} ms) → would spin the engine");
        Assert.Equal(SampleResult.Unavailable, sampler.Sample(dest, -5));
        Assert.False(sampler.TryGetPreview(out var bgra, out _, out _));
        Assert.Empty(bgra);
        Assert.Null(sampler.CurrentMonitor);
        Assert.Equal(default, sampler.BlackBars);
        sampler.Configure("x", [], false); // ignored after dispose
    }
}
