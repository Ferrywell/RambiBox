using System.Diagnostics;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using Xunit.Abstractions;

namespace RambiBox.Tests.Capture;

/// <summary>
/// Windows.Graphics.Capture on the real screen (shows the <see cref="TestPatternWindow"/> in the top-left corner of the
/// primary monitor for a few seconds), plus the DXGI-vs-WGC benchmark. Never touches a COM port.
/// <para>Benchmark knobs: RAMBIBOX_BENCH_SECONDS (default 3 per run); RAMBIBOX_BENCH_ONLY = comma list of dxgi | wgc |
/// wgcN (WGC with MinUpdateInterval N ms, wgc0 = Windows default) for long runs while GPU counters are recorded
/// externally; RAMBIBOX_BENCH_MODE = paced | max | both.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection("Hardware")]
public class WgcScreenSamplerHardwareTests(ITestOutputHelper output)
{
    private static MonitorInfo? Primary() => DisplayTopology.GetMonitors().FirstOrDefault(m => m.IsPrimary);

    /// <summary>Waits until <paramref name="sampler"/> produced a frame (session start + first frame).</summary>
    private static int WarmUp(IScreenSampler sampler, float[] dest, int ms = 700)
    {
        var sw = Stopwatch.StartNew();
        int frames = 0;
        while (sw.ElapsedMilliseconds < ms)
            if (sampler.Sample(dest, 50) == SampleResult.NewFrame) frames++;
        return frames;
    }

    [Fact]
    public void Captures_real_screen_with_correct_colors_and_no_steady_state_allocations()
    {
        Assert.True(WgcScreenSampler.IsSupported(), "Windows Graphics Capture is not supported on this machine");
        var primary = Primary();
        if (primary is null) { output.WriteLine("No monitors (headless session) - nothing to test."); return; }

        using var sampler = new WgcScreenSampler();
        var zones = CaptureTestKit.ZonesWithPattern(primary);
        sampler.Configure("", zones, blackBarDetection: false); // desktop content must not remap the test zones
        var dest = new float[zones.Count * 3];

        using var window = new TestPatternWindow();
        Thread.Sleep(300);

        int warmFrames = WarmUp(sampler, dest);
        output.WriteLine($"Warm-up: {warmFrames} frames; capturing {sampler.CurrentMonitor?.FriendlyName}, HDR={sampler.CurrentMonitor?.IsHdr}, " +
                         $"FP16={sampler.IsFp16}, pool {sampler.PoolFormat} × {sampler.PoolBuffers}, rotation {sampler.Rotation}, " +
                         $"small image {sampler.SmallImageSize}, mip {sampler.MipLevel}");
        output.WriteLine($"Session: {sampler.SessionOptions}");
        Assert.True(warmFrames > 0, "no WGC frame during warm-up");

        // Measured run (~3 s, engine-like busy loop: Sample(timeout 100) back to back)
        int frames = 0, noChange = 0, unavailable = 0;
        var work = new List<double>(2000);
        long arrived0 = sampler.FramesArrived, dropped0 = sampler.FramesDropped;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var run = Stopwatch.StartNew();
        while (run.ElapsedMilliseconds < 3000)
        {
            switch (sampler.Sample(dest, 100))
            {
                case SampleResult.NewFrame:
                    frames++;
                    if (work.Count < work.Capacity) work.Add(sampler.LastWorkMs);
                    break;
                case SampleResult.NoChange: noChange++; break;
                default: unavailable++; break;
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        double secs = run.Elapsed.TotalSeconds;
        work.Sort();
        output.WriteLine($"Frames: {frames} in {secs:0.00} s = {frames / secs:0.0} fps (NoChange {noChange}, Unavailable {unavailable}); " +
                         $"FrameArrived {(sampler.FramesArrived - arrived0) / secs:0.0}/s, dropped (older frames skipped) {sampler.FramesDropped - dropped0}");
        output.WriteLine($"Sample() work after a frame was available: avg {work.Average():0.000} ms, p50 {work[work.Count / 2]:0.000}, " +
                         $"p99 {work[Math.Min(work.Count - 1, (int)(work.Count * 0.99))]:0.000}, max {work.Max():0.000} ms");
        output.WriteLine($"Managed allocations on the sampling thread: {allocated} bytes ({(double)allocated / Math.Max(1, frames):0.0} B/frame)");

        int ri = (zones.Count - 2) * 3, si = (zones.Count - 1) * 3;
        output.WriteLine($"Red zone: R={dest[ri]:0.000} G={dest[ri + 1]:0.000} B={dest[ri + 2]:0.000}");
        output.WriteLine($"Stripes zone (≈0.735 = linear-light averaging, 0.5 = gamma space): {dest[si]:0.000} {dest[si + 1]:0.000} {dest[si + 2]:0.000}");
        Assert.True(sampler.TryGetPreview(out var pv, out int pw, out int ph));
        Assert.Equal(pw * ph * 4, pv.Length);
        output.WriteLine($"Preview {pw}x{ph}");

        Assert.True(frames >= 20, $"only {frames} frames in 3 s");
        Assert.Equal(0, unavailable);
        Assert.Equal(SurfaceRotation.Identity, sampler.Rotation); // WGC hands out the desktop upright
        Assert.True(allocated < 1024, $"steady-state allocations: {allocated} bytes");
        Assert.True(dest[ri] > 0.85f && dest[ri + 1] < 0.15f && dest[ri + 2] < 0.15f, "red zone is not red");
        Assert.InRange(dest[si], 0.65f, 0.82f);
        Assert.InRange(Math.Abs(dest[si] - dest[si + 1]) + Math.Abs(dest[si] - dest[si + 2]), 0f, 0.05f);
    }

    [Fact]
    public void Wgc_and_dxgi_produce_the_same_colors()
    {
        var primary = Primary();
        if (primary is null || !WgcScreenSampler.IsSupported()) return;
        var zones = CaptureTestKit.ZonesWithPattern(primary);
        using var window = new TestPatternWindow();
        Thread.Sleep(300);
        float[] Capture(IScreenSampler s)
        {
            using (s)
            {
                s.Configure("", zones, blackBarDetection: false);
                var d = new float[zones.Count * 3];
                Assert.True(WarmUp(s, d, 600) > 0);
                return d;
            }
        }
        var wgc = Capture(new WgcScreenSampler());
        var dxgi = Capture(new DxgiScreenSampler());
        // The test pattern zones are static; compare those (the rest of the desktop may change between the captures).
        for (int i = (zones.Count - 2) * 3; i < zones.Count * 3; i++)
        {
            output.WriteLine($"[{i}] WGC {wgc[i]:0.0000}  DXGI {dxgi[i]:0.0000}");
            Assert.InRange(Math.Abs(wgc[i] - dxgi[i]), 0f, 0.01f);
        }
    }

    [Fact]
    public void Cursor_is_not_captured()
    {
        var primary = Primary();
        if (primary is null || !WgcScreenSampler.IsSupported()) return;
        var (red, _) = CaptureTestKit.PatternZones(primary);
        // A small zone around the cursor hotspot in the middle of the red area.
        int cx = CaptureTestKit.WindowX + 160, cy = CaptureTestKit.WindowY + 80;
        var zone = new LedZone((float)(cx - 8) / primary.Width, (float)(cy - 8) / primary.Height,
                               (float)(cx + 40) / primary.Width, (float)(cy + 48) / primary.Height);
        Assert.True(zone.Left > red.Left && zone.Right < red.Right && zone.Bottom < red.Bottom + 0.01f);

        using var window = new TestPatternWindow();
        CaptureTestKit.GetCursorPos(out var saved);
        try
        {
            (float R, float G, float B) Read(bool cursorEnabled)
            {
                using var s = new WgcScreenSampler { CursorCaptureEnabled = cursorEnabled };
                s.Configure("", [zone], blackBarDetection: false);
                var d = new float[3];
                CaptureTestKit.SetCursorPos(cx, cy);
                Thread.Sleep(150);
                WarmUp(s, d, 400);
                CaptureTestKit.SetCursorPos(cx, cy);
                WarmUp(s, d, 400); // the animated part of the window keeps frames coming
                output.WriteLine($"cursor capture {(cursorEnabled ? "on " : "off")}: {s.SessionOptions}");
                return (d[0], d[1], d[2]);
            }
            var with = Read(true);
            var without = Read(false);
            output.WriteLine($"Zone under the cursor: cursor captured R={with.R:0.000} G={with.G:0.000} B={with.B:0.000}; " +
                             $"cursor excluded R={without.R:0.000} G={without.G:0.000} B={without.B:0.000}");
            Assert.True(without.R > 0.85f && without.G < 0.03f && without.B < 0.03f, "cursor pixels leaked into the capture");
            if (with.G < 0.03f) output.WriteLine("Note: the cursor did not show even with capture enabled (hidden cursor?) - control inconclusive.");
        }
        finally
        {
            CaptureTestKit.SetCursorPos(saved.X, saved.Y);
        }
    }

    [Fact]
    public void Capture_border_is_suppressed()
    {
        var primary = Primary();
        if (primary is null || !WgcScreenSampler.IsSupported()) return;
        // The yellow border is drawn on screen around the captured monitor. Count yellow pixels on all four edges
        // (GDI read of what the user sees) with the border requested vs. not requested.
        int Yellow()
        {
            int count = 0;
            foreach (var (x, y, w, h) in new[] { (primary.X, primary.Y, primary.Width, 4), (primary.X, primary.Y + primary.Height - 4, primary.Width, 4),
                                                 (primary.X, primary.Y, 4, primary.Height), (primary.X + primary.Width - 4, primary.Y, 4, primary.Height) })
            {
                var px = CaptureTestKit.ReadScreen(x - primary.X, y - primary.Y, w, h);
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i + 2] > 200 && px[i + 1] > 140 && px[i + 1] < 235 && px[i] < 90) count++; // R high, G mid-high, B low
            }
            return count;
        }
        int Run(bool border)
        {
            using var s = new WgcScreenSampler { BorderRequired = border };
            s.Configure("", [new LedZone(0, 0, 1, 1)], blackBarDetection: false);
            var d = new float[3];
            WarmUp(s, d, 300);
            Thread.Sleep(400); // let the border animate in
            int n = Yellow();
            output.WriteLine($"border {(border ? "required" : "off     ")}: {n} yellow edge pixels; {s.SessionOptions}");
            return n;
        }
        int baseline = Yellow();
        int withBorder = Run(true);
        int without = Run(false);
        output.WriteLine($"baseline (no capture): {baseline} yellow edge pixels");
        if (withBorder <= baseline + 100)
            output.WriteLine("Note: the border was not visible to GDI even when requested - check visually.");
        else
            Assert.True(without <= baseline + 100, $"yellow border visible: {without} pixels (baseline {baseline})");
    }

    /// <summary>
    /// The sampler treats "no frame within 3 s after StartCapture" as a broken session (counts toward the DXGI fallback),
    /// which relies on WGC delivering the current picture right away, even when nothing on screen changes.
    /// Runs without the animated test window.
    /// </summary>
    [Fact]
    public void First_frame_arrives_promptly_without_screen_changes()
    {
        if (Primary() is null || !WgcScreenSampler.IsSupported()) return;
        var dest = new float[3];
        var times = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            using var s = new WgcScreenSampler();
            s.Configure("", [new LedZone(0, 0, 1, 1)], blackBarDetection: false);
            var sw = Stopwatch.StartNew();
            var r = SampleResult.Unavailable;
            while (r != SampleResult.NewFrame && sw.ElapsedMilliseconds < WgcScreenSampler.FirstFrameTimeoutMs)
                r = s.Sample(dest, 10);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(SampleResult.NewFrame, r);
            Thread.Sleep(200);
        }
        output.WriteLine($"Session start → first frame (incl. device + session creation): {string.Join(", ", times.Select(t => t.ToString("0")))} ms");
        Assert.All(times, t => Assert.True(t < 1000, $"first frame after {t:0} ms"));
    }

    [Fact]
    public void Unknown_monitor_id_falls_back_to_primary()
    {
        if (Primary() is null || !WgcScreenSampler.IsSupported()) return;
        using var sampler = new WgcScreenSampler();
        sampler.Configure("no-such-monitor", [new LedZone(0, 0, 1, 1)], blackBarDetection: true);
        var dest = new float[3];
        var sw = Stopwatch.StartNew();
        var r = SampleResult.Unavailable;
        while (sw.ElapsedMilliseconds < 1500 && r == SampleResult.Unavailable) r = sampler.Sample(dest, 50);
        Assert.NotEqual(SampleResult.Unavailable, r);
        Assert.True(sampler.CurrentMonitor?.IsPrimary);
        output.WriteLine($"Fallback capture: {sampler.CurrentMonitor}");
    }

    [Fact]
    public void Switching_sampler_switches_live_between_methods()
    {
        var primary = Primary();
        if (primary is null) return;
        using var window = new TestPatternWindow();
        using var sampler = new SwitchingScreenSampler();
        var zones = CaptureTestKit.ZonesWithPattern(primary);
        var dest = new float[zones.Count * 3];
        int ri = (zones.Count - 2) * 3;
        foreach (var m in new[] { CaptureMethod.Auto, CaptureMethod.DesktopDuplication, CaptureMethod.WindowsGraphicsCapture, CaptureMethod.Auto })
        {
            sampler.SetCaptureMethod(m);
            sampler.Configure("", zones, blackBarDetection: false); // the switch happens here (engine thread)
            int frames = WarmUp(sampler, dest, 600);
            output.WriteLine($"{m}: effective {sampler.EffectiveMethod}, fallback {sampler.IsFallback}, {frames} frames, red zone {dest[ri]:0.000}");
            Assert.True(frames > 0);
            Assert.True(dest[ri] > 0.85f);
            var expected = m == CaptureMethod.DesktopDuplication || !WgcScreenSampler.IsSupported()
                ? CaptureMethod.DesktopDuplication : CaptureMethod.WindowsGraphicsCapture;
            Assert.Equal(expected, sampler.EffectiveMethod);
            Assert.True(sampler.TryGetPreview(out _, out _, out _));
        }
    }

    // =====================================================================================================
    // Benchmark
    // =====================================================================================================

    private sealed record BenchResult(string Name, string Mode, int Frames, double Seconds, double ThreadCpuMs, double ProcessCpuMs,
        long ThreadAlloc, long ProcessAlloc, double WorkAvgMs, double CallAvgMs, double ArrivedPerSec, double CallbackBytesPerArrival, int Unavailable);

    [Fact]
    public void Benchmark_dxgi_vs_wgc()
    {
        var primary = Primary();
        if (primary is null) return;
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("RAMBIBOX_BENCH_SECONDS"), out var bs) ? bs : 3;
        string only = Environment.GetEnvironmentVariable("RAMBIBOX_BENCH_ONLY")?.ToLowerInvariant() ?? "";
        var zones = CaptureTestKit.ZonesWithPattern(primary);
        var dest = new float[zones.Count * 3];
        using var window = new TestPatternWindow();
        Thread.Sleep(300);
        output.WriteLine($"Monitor {primary.FriendlyName}, {primary.RefreshHz} Hz, HDR {primary.IsHdr}; pid {Environment.ProcessId}; {seconds} s per run");

        string mode = Environment.GetEnvironmentVariable("RAMBIBOX_BENCH_MODE")?.ToLowerInvariant() ?? (only.Length > 0 ? "paced" : "both");
        // "wgc" = default MinUpdateInterval; "wgcN" = N ms (0 = leave the Windows default).
        string[] variants = only.Length > 0 ? only.Split(',') : ["dxgi", "wgc", "wgc0"];

        var results = new List<BenchResult>();
        foreach (var name in variants)
        {
            if (name == "none") // baseline for external GPU counters: test window only, no capture
            {
                output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] start none");
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] end none");
                continue;
            }
            Func<IScreenSampler> make = name == "dxgi" ? () => new DxgiScreenSampler()
                : name == "wgc" ? () => new WgcScreenSampler()
                : () => new WgcScreenSampler { MinUpdateIntervalMs = int.Parse(name[3..]) };
            if (name != "dxgi" && !WgcScreenSampler.IsSupported()) continue;
            foreach (bool paced in new[] { true, false })
            {
                if ((mode == "paced" && !paced) || (mode == "max" && paced)) continue;
                using var s = make();
                s.Configure("", zones, blackBarDetection: true);
                Assert.True(WarmUp(s, dest, 600) > 0, $"{name}: no frames");
                output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] start {name} {(paced ? "paced" : "max")}");
                results.Add(Measure(name, s, window, dest, seconds, paced));
                output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] end {name}");
            }
        }

        output.WriteLine("");
        output.WriteLine("method  mode   fps    thread CPU ms/frame  process CPU ms/frame*  work ms  call ms  B/frame (thread / process)  FrameArrived/s  B per FrameArrived callback");
        foreach (var r in results)
        {
            double f = Math.Max(1, r.Frames);
            output.WriteLine($"{r.Name,-6}  {r.Mode,-5}  {r.Frames / r.Seconds,5:0.0}  {r.ThreadCpuMs / f,19:0.000}  {r.ProcessCpuMs / f,21:0.000}  " +
                             $"{r.WorkAvgMs,7:0.000}  {r.CallAvgMs,7:0.000}  {r.ThreadAlloc / f,10:0.0} / {r.ProcessAlloc / f,-10:0.0}  " +
                             $"{(r.Name == "dxgi" ? "-" : $"{r.ArrivedPerSec:0.0}  {r.CallbackBytesPerArrival:0.0}")}{(r.Unavailable > 0 ? $"  ({r.Unavailable} unavailable!)" : "")}");
        }
        output.WriteLine("* process CPU = all threads of the test process minus the test-pattern drawing thread (includes WGC callbacks).");
        output.WriteLine("paced = engine-like: Sample(timeout 0) every 29.4 ms (34 fps LED link); max = Sample(timeout 100) back to back.");
        Assert.All(results, r => Assert.Equal(0, r.Unavailable));
    }

    private static BenchResult Measure(string name, IScreenSampler s, TestPatternWindow window, float[] dest, double seconds, bool paced)
    {
        const double LinkIntervalMs = 1000.0 / 34;
        double cpm = CaptureTestKit.CyclesPerMs();
        var wgc = s as WgcScreenSampler;
        var dxgi = s as DxgiScreenSampler;
        int frames = 0, unavailable = 0;
        double workSum = 0, callSum = 0;
        long arrived0 = wgc?.FramesArrived ?? 0, cb0 = wgc?.CallbackAllocatedBytes ?? 0;
        GC.Collect();
        long ta0 = GC.GetAllocatedBytesForCurrentThread(), pa0 = GC.GetTotalAllocatedBytes(true);
        ulong tc0 = CaptureTestKit.ThreadCycles(), pc0 = CaptureTestKit.ProcessCycles(), wc0 = window.Cycles;
        var sw = Stopwatch.StartNew();
        double next = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (paced)
            {
                next += LinkIntervalMs;
                double wait;
                while ((wait = next - sw.Elapsed.TotalMilliseconds) > 0) Thread.Sleep(Math.Max(1, (int)wait));
            }
            long c0 = Stopwatch.GetTimestamp();
            var r = s.Sample(dest, paced ? 0 : 100);
            double call = Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
            if (r == SampleResult.NewFrame)
            {
                frames++;
                callSum += call;
                workSum += wgc?.LastWorkMs ?? dxgi?.LastWorkMs ?? call;
            }
            else if (r == SampleResult.Unavailable) unavailable++;
        }
        double secs = sw.Elapsed.TotalSeconds;
        double threadCpu = (CaptureTestKit.ThreadCycles() - tc0) / cpm;
        double procCpu = ((double)(CaptureTestKit.ProcessCycles() - pc0) - (window.Cycles - wc0)) / cpm;
        long ta = GC.GetAllocatedBytesForCurrentThread() - ta0, pa = GC.GetTotalAllocatedBytes(true) - pa0;
        long arrivedCount = (wgc?.FramesArrived ?? 0) - arrived0;
        double arrived = arrivedCount / secs;
        double cbBytes = ((wgc?.CallbackAllocatedBytes ?? 0) - cb0) / (double)Math.Max(1, arrivedCount);
        return new BenchResult(name, paced ? "paced" : "max", frames, secs, threadCpu, procCpu, ta, pa,
            workSum / Math.Max(1, frames), callSum / Math.Max(1, frames), arrived, cbBytes, unavailable);
    }
}
