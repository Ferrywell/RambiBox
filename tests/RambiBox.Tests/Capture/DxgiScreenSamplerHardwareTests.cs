using System.Diagnostics;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using Xunit.Abstractions;

namespace RambiBox.Tests.Capture;

/// <summary>
/// Captures the real screen. Shows a small topmost test window in the top-left corner of the primary monitor for ~3 s:
/// top half solid red, bottom-left 1-px black/white stripes (checks linear-light averaging), bottom-right animated
/// (forces new desktop frames). Doubles as the benchmark (prints Sample() timings, fps, allocations, memory).
/// </summary>
[Trait("Category", "Hardware")]
[Collection("Hardware")]
public class DxgiScreenSamplerHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void Captures_real_screen_and_reports_performance()
    {
        using var sampler = new DxgiScreenSampler();
        var monitors = sampler.GetMonitors();
        foreach (var m in monitors)
            output.WriteLine($"Monitor: {m.FriendlyName} id={m.Id} gdi={m.GdiDeviceName} at {m.X},{m.Y} {m.Width}x{m.Height} primary={m.IsPrimary} hdr={m.IsHdr} {m.RefreshHz} Hz");
        if (monitors.Count == 0)
        {
            output.WriteLine("No monitors (headless session) - nothing to test.");
            return;
        }
        Assert.All(monitors, m => Assert.False(string.IsNullOrEmpty(m.Id)));
        Assert.Single(monitors, m => m.IsPrimary);
        foreach (var t in DisplayTopology.GetTargets())
        {
            var fromPath = DisplayTopology.EdidKeyFromId(t.Info.Id);
            if (fromPath is not null && t.EdidKey.Length > 0)
                Assert.Equal(fromPath, t.EdidKey, ignoreCase: true); // validates EDID decoding against the PnP path
        }

        // A second call must come from the cache (fast enough for the UI).
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) sampler.GetMonitors();
        output.WriteLine($"GetMonitors (cached): {sw.Elapsed.TotalMilliseconds / 100 * 1000:0.0} µs/call");

        sw.Restart();
        for (int i = 0; i < 10; i++) DisplayTopology.GetTargets(maxAgeMs: 0);
        output.WriteLine($"Monitor enumeration (uncached, QueryDisplayConfig): {sw.Elapsed.TotalMilliseconds / 10:0.000} ms/call");

        var primary = monitors.First(m => m.IsPrimary);
        const int wx = 40, wy = 40, ws = 320;
        RectF Px(int x0, int y0, int x1, int y1) => new(
            (float)(wx + x0) / primary.Width, (float)(wy + y0) / primary.Height,
            (float)(wx + x1) / primary.Width, (float)(wy + y1) / primary.Height);
        var red = Px(48, 32, 272, 128);
        var stripes = Px(48, 192, 208, 288);

        var zones = LayoutGenerator.Generate(new LayoutSettings()); // realistic 112-zone layout
        zones.Add(new LedZone(red.Left, red.Top, red.Right, red.Bottom));
        zones.Add(new LedZone(stripes.Left, stripes.Top, stripes.Right, stripes.Bottom));
        sampler.Configure("", zones, blackBarDetection: false); // desktop content must not remap the test zones
        var dest = new float[zones.Count * 3];

        using var window = new TestPatternWindow(wx, wy, ws);
        Thread.Sleep(300); // let the window appear

        // Warm-up (device creation, first frame, JIT)
        var warm = Stopwatch.StartNew();
        int warmFrames = 0;
        while (warm.ElapsedMilliseconds < 500)
            if (sampler.Sample(dest, 50) == SampleResult.NewFrame) warmFrames++;
        output.WriteLine($"Warm-up: {warmFrames} frames; capturing {sampler.CurrentMonitor?.FriendlyName}, HDR={sampler.CurrentMonitor?.IsHdr}, " +
                         $"FP16={sampler.IsFp16}, SDR white={sampler.SdrWhiteNits:0} nits, small image {sampler.SmallImageSize}, mip {sampler.MipLevel}");
        Assert.True(warmFrames > 0, "no frame during warm-up");

        // Measured run (2 s)
        var work = new List<double>(1000);
        var mapWait = new List<double>(1000);
        int frames = 0, noChange = 0, unavailable = 0;
        double cyclesPerMs = CaptureTestKit.CyclesPerMs();
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        ulong cyc0 = CaptureTestKit.ThreadCycles();
        var run = Stopwatch.StartNew();
        while (run.ElapsedMilliseconds < 2000)
        {
            switch (sampler.Sample(dest, 100))
            {
                case SampleResult.NewFrame:
                    frames++;
                    if (work.Count < work.Capacity) { work.Add(sampler.LastWorkMs); mapWait.Add(sampler.LastMapWaitMs); }
                    break;
                case SampleResult.NoChange: noChange++; break;
                default: unavailable++; break;
            }
        }
        double secs = run.Elapsed.TotalSeconds;
        double cpuMs = (CaptureTestKit.ThreadCycles() - cyc0) / cyclesPerMs;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - alloc0;

        work.Sort();
        mapWait.Sort();
        double P(List<double> l, double q) => l.Count == 0 ? double.NaN : l[Math.Min(l.Count - 1, (int)(q * l.Count))];
        output.WriteLine($"Frames: {frames} in {secs:0.00} s = {frames / secs:0.0} fps (NoChange {noChange}, Unavailable {unavailable})");
        output.WriteLine($"Sample() work (after AcquireNextFrame returned): avg {work.Average():0.000} ms, p50 {P(work, 0.5):0.000}, p99 {P(work, 0.99):0.000}, max {work.Max():0.000} ms");
        output.WriteLine($"  of which Map() wait for GPU: avg {mapWait.Average():0.000} ms, p99 {P(mapWait, 0.99):0.000} ms");
        output.WriteLine($"Thread CPU time (cycle counter): {cpuMs:0.0} ms over {secs:0.00} s = {cpuMs / Math.Max(1, frames):0.000} ms/frame, {cpuMs / (secs * 10):0.00}% of one core");
        output.WriteLine($"Managed allocations during measured run: {allocated} bytes ({(double)allocated / Math.Max(1, frames):0.0} B/frame)");
        using (var p = Process.GetCurrentProcess())
            output.WriteLine($"Process: working set {p.WorkingSet64 / 1048576.0:0.0} MB, private {p.PrivateMemorySize64 / 1048576.0:0.0} MB, GC heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB");

        int ri = (zones.Count - 2) * 3, si = (zones.Count - 1) * 3;
        output.WriteLine($"Red zone: R={dest[ri]:0.000} G={dest[ri + 1]:0.000} B={dest[ri + 2]:0.000}");
        output.WriteLine($"Stripes zone (expect ≈0.735 for linear-light averaging, 0.5 if gamma-space): {dest[si]:0.000} {dest[si + 1]:0.000} {dest[si + 2]:0.000}");
        output.WriteLine($"Black bars: {sampler.BlackBars}");
        Assert.True(sampler.TryGetPreview(out var pv, out int pw, out int ph));
        output.WriteLine($"Preview {pw}x{ph}, {pv.Length} bytes");
        Assert.Equal(pw * ph * 4, pv.Length);

        Assert.True(frames >= 10, $"only {frames} frames in 2 s");
        Assert.Equal(0, unavailable);
        Assert.True(allocated < 1024, $"steady-state allocations: {allocated} bytes");
        Assert.True(dest[ri] > 0.85f && dest[ri + 1] < 0.15f && dest[ri + 2] < 0.15f, "red zone is not red");
        Assert.InRange(dest[si], 0.65f, 0.82f);
        Assert.InRange(Math.Abs(dest[si] - dest[si + 1]) + Math.Abs(dest[si] - dest[si + 2]), 0f, 0.05f);
    }

    [Fact]
    public void Unknown_monitor_id_falls_back_to_primary()
    {
        using var sampler = new DxgiScreenSampler();
        if (sampler.GetMonitors().Count == 0) return;
        sampler.Configure("no-such-monitor", [new LedZone(0, 0, 1, 1)], blackBarDetection: true);
        var dest = new float[3];
        var sw = Stopwatch.StartNew();
        var r = SampleResult.Unavailable;
        while (sw.ElapsedMilliseconds < 1000 && r == SampleResult.Unavailable) r = sampler.Sample(dest, 50);
        Assert.NotEqual(SampleResult.Unavailable, r);
        Assert.True(sampler.CurrentMonitor?.IsPrimary);
        output.WriteLine($"Fallback capture: {sampler.CurrentMonitor}");
    }
}
