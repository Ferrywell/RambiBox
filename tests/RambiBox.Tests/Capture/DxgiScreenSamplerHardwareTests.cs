using System.Diagnostics;
using System.Runtime.InteropServices;
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
        double cyclesPerMs = CalibrateCyclesPerMs();
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        ulong cyc0 = ThreadCycles();
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
        double cpuMs = (ThreadCycles() - cyc0) / cyclesPerMs;
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

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    private static ulong ThreadCycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong c);
        return c;
    }

    /// <summary>Thread cycle counts are in TSC units; calibrate against wall time with a short busy loop.</summary>
    private static double CalibrateCyclesPerMs()
    {
        ulong c0 = ThreadCycles();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 50) { }
        return (ThreadCycles() - c0) / sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Topmost GDI window drawing the test pattern on its own thread (physical pixels via PMv2 DPI awareness).</summary>
    private sealed class TestPatternWindow : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly ManualResetEventSlim _ready = new();

        public TestPatternWindow(int x, int y, int size)
        {
            _thread = new Thread(() => Run(x, y, size)) { IsBackground = true, Name = "Test pattern" };
            _thread.Start();
            _ready.Wait(2000);
        }

        private void Run(int x, int y, int size)
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            var hwnd = CreateWindowExW(0x8 | 0x80 | 0x08000000, "STATIC", "", 0x80000000 | 0x10000000, x, y, size, size,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _ready.Set();
            if (hwnd == IntPtr.Zero) return;
            var red = CreateSolidBrush(0x0000FF);
            var white = CreateSolidBrush(0xFFFFFF);
            var black = CreateSolidBrush(0x000000);
            int half = size / 2, split = size * 3 / 4;
            try
            {
                for (int frame = 0; !_stop; frame++)
                {
                    while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessageW(ref msg);
                    }
                    var dc = GetDC(hwnd);
                    var r = new RECT { Left = 0, Top = 0, Right = size, Bottom = half };
                    FillRect(dc, ref r, red);
                    for (int c = 0; c < split; c++)
                    {
                        r = new RECT { Left = c, Top = half, Right = c + 1, Bottom = size };
                        FillRect(dc, ref r, (c & 1) == 0 ? white : black);
                    }
                    int g = (frame * 9) & 0xFF;
                    var anim = CreateSolidBrush((uint)(g | (g << 8) | (g << 16)));
                    r = new RECT { Left = split, Top = half, Right = size, Bottom = size };
                    FillRect(dc, ref r, anim);
                    DeleteObject(anim);
                    ReleaseDC(hwnd, dc);
                    GdiFlush();
                    Thread.Sleep(4);
                }
            }
            finally
            {
                DeleteObject(red); DeleteObject(white); DeleteObject(black);
                DestroyWindow(hwnd);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
