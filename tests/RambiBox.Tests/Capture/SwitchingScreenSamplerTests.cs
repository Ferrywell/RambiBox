using System.Collections.Concurrent;
using System.Diagnostics;
using RambiBox.Core;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Engine;
using RambiBox.Tests.Core;

namespace RambiBox.Tests.Capture;

/// <summary>Method selection, live switching and automatic fallback of <see cref="SwitchingScreenSampler"/> with fake backends.</summary>
public class SwitchingScreenSamplerTests
{
    /// <summary>Fake backend; records what happened to it.</summary>
    private sealed class FakeBackend(CaptureMethod kind) : IScreenSampler, ICaptureBackendHealth
    {
        public CaptureMethod Kind { get; } = kind;
        public volatile bool Disposed;
        public string? MonitorId;
        public IReadOnlyList<LedZone>? Zones;
        public bool BlackBars;
        public int Configures, Samples;
        public int InitFailures;           // reported through ICaptureBackendHealth
        public int FailEverySample;        // >0: each Sample adds one init failure
        public int SampleSleepMs;
        public int CreatedOnThread = Environment.CurrentManagedThreadId;
        public int DisposedOnThread;

        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public MonitorInfo? CurrentMonitor => Disposed ? null : new MonitorInfo("m", Kind.ToString(), "", 0, 0, 1, 1, true, false, 60);
        public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection)
        {
            Configures++;
            MonitorId = monitorId;
            Zones = zones;
            BlackBars = blackBarDetection;
        }
        public SampleResult Sample(Span<float> destRgb, int timeoutMs)
        {
            Samples++;
            if (SampleSleepMs > 0) Thread.Sleep(SampleSleepMs);
            if (Disposed) return SampleResult.Unavailable;
            if (FailEverySample > 0) { InitFailures += FailEverySample; return SampleResult.Unavailable; }
            destRgb.Fill(Kind == CaptureMethod.WindowsGraphicsCapture ? 0.25f : 0.75f);
            return SampleResult.NewFrame;
        }
        public bool TryGetPreview(out byte[] bgra, out int width, out int height)
        {
            if (Disposed) throw new ObjectDisposedException("preview read after dispose");
            bgra = [(byte)Kind];
            width = height = 1;
            return true;
        }
        (float Top, float Bottom, float Left, float Right) IScreenSampler.BlackBars
            => Disposed ? throw new ObjectDisposedException("bars read after dispose") : default;
        public int ConsecutiveInitFailures => InitFailures;
        public void Dispose()
        {
            Disposed = true;
            DisposedOnThread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class Rig
    {
        public readonly List<FakeBackend> Created = [];
        public bool WgcSupported = true;
        public bool WgcCtorThrows;
        public Action<FakeBackend>? OnCreate;
        public readonly SwitchingScreenSampler Sampler;

        public Rig()
        {
            Sampler = new SwitchingScreenSampler(m =>
            {
                if (m == CaptureMethod.WindowsGraphicsCapture && WgcCtorThrows) throw new InvalidOperationException("no WGC here");
                var b = new FakeBackend(m);
                OnCreate?.Invoke(b);
                lock (Created) Created.Add(b);
                return b;
            }, () => WgcSupported, () => []);
        }

        public FakeBackend Current => (FakeBackend)Sampler.Current!;
    }

    private static readonly LedZone[] Zones = [new(0, 0, 0.1f, 0.1f), new(0.9f, 0.9f, 1, 1)];

    [Theory]
    [InlineData(CaptureMethod.Auto, true, false, CaptureMethod.WindowsGraphicsCapture, false)]
    [InlineData(CaptureMethod.Auto, false, false, CaptureMethod.DesktopDuplication, true)]
    [InlineData(CaptureMethod.Auto, true, true, CaptureMethod.DesktopDuplication, true)]
    [InlineData(CaptureMethod.WindowsGraphicsCapture, true, false, CaptureMethod.WindowsGraphicsCapture, false)]
    [InlineData(CaptureMethod.WindowsGraphicsCapture, false, false, CaptureMethod.DesktopDuplication, true)]
    [InlineData(CaptureMethod.WindowsGraphicsCapture, true, true, CaptureMethod.DesktopDuplication, true)]
    [InlineData(CaptureMethod.DesktopDuplication, true, false, CaptureMethod.DesktopDuplication, false)]
    [InlineData(CaptureMethod.DesktopDuplication, false, true, CaptureMethod.DesktopDuplication, false)]
    public void Resolve_picks_the_method(CaptureMethod requested, bool supported, bool failed, CaptureMethod expected, bool fallback)
    {
        Assert.Equal((expected, fallback), SwitchingScreenSampler.Resolve(requested, supported, failed));
    }

    [Fact]
    public void Nothing_is_created_before_the_engine_configures_it()
    {
        var rig = new Rig();
        Assert.Null(rig.Sampler.EffectiveMethod);
        Assert.Empty(rig.Created);
        Assert.False(rig.Sampler.TryGetPreview(out var bgra, out _, out _));
        Assert.Empty(bgra);
        Assert.Null(rig.Sampler.CurrentMonitor);
        Assert.Equal(default, rig.Sampler.BlackBars);
    }

    [Fact]
    public void Auto_uses_wgc_when_supported_and_passes_the_configuration_on()
    {
        var rig = new Rig();
        rig.Sampler.Configure("mon-1", Zones, blackBarDetection: true);
        Assert.Equal(CaptureMethod.WindowsGraphicsCapture, rig.Sampler.EffectiveMethod);
        Assert.False(rig.Sampler.IsFallback);
        var b = Assert.Single(rig.Created);
        Assert.Equal("mon-1", b.MonitorId);
        Assert.Equal(Zones, b.Zones);
        Assert.True(b.BlackBars);
        Assert.Equal(1, b.Configures);

        // Further configuration goes to the same backend.
        rig.Sampler.Configure("mon-2", Zones, blackBarDetection: false);
        Assert.Single(rig.Created);
        Assert.Equal("mon-2", b.MonitorId);
        Assert.Equal(2, b.Configures);
    }

    [Fact]
    public void Auto_falls_back_to_desktop_duplication_when_wgc_is_unsupported()
    {
        var rig = new Rig { WgcSupported = false };
        rig.Sampler.Configure("", Zones, false);
        Assert.Equal(CaptureMethod.DesktopDuplication, rig.Sampler.EffectiveMethod);
        Assert.True(rig.Sampler.IsFallback);
        Assert.Equal(CaptureMethod.DesktopDuplication, Assert.Single(rig.Created).Kind);
    }

    [Fact]
    public void Explicit_desktop_duplication_is_not_a_fallback()
    {
        var rig = new Rig();
        rig.Sampler.SetCaptureMethod(CaptureMethod.DesktopDuplication);
        rig.Sampler.Configure("", Zones, false);
        Assert.Equal(CaptureMethod.DesktopDuplication, rig.Sampler.EffectiveMethod);
        Assert.False(rig.Sampler.IsFallback);
    }

    [Fact]
    public void Switches_live_on_the_calling_thread_and_disposes_the_old_backend_there()
    {
        var rig = new Rig();
        var dest = new float[6];
        rig.Sampler.Configure("m", Zones, true);
        Assert.Equal(SampleResult.NewFrame, rig.Sampler.Sample(dest, 0));
        Assert.Equal(0.25f, dest[0]);
        var wgc = rig.Current;

        // The UI thread only records the request ...
        var ui = new Thread(() => rig.Sampler.SetCaptureMethod(CaptureMethod.DesktopDuplication));
        ui.Start();
        ui.Join();
        Assert.Same(wgc, rig.Current);
        Assert.False(wgc.Disposed);

        // ... the engine thread switches inside Sample (or Configure).
        Assert.Equal(SampleResult.NewFrame, rig.Sampler.Sample(dest, 0));
        Assert.Equal(0.75f, dest[0]);
        Assert.True(wgc.Disposed);
        Assert.Equal(Environment.CurrentManagedThreadId, wgc.DisposedOnThread);
        var dxgi = rig.Current;
        Assert.Equal(CaptureMethod.DesktopDuplication, dxgi.Kind);
        Assert.Equal(Environment.CurrentManagedThreadId, dxgi.CreatedOnThread);
        Assert.Equal("m", dxgi.MonitorId); // the new backend got the current configuration
        Assert.Equal(Zones, dxgi.Zones);
        Assert.True(dxgi.BlackBars);
        Assert.Equal(CaptureMethod.DesktopDuplication, rig.Sampler.EffectiveMethod);

        // And back.
        rig.Sampler.SetCaptureMethod(CaptureMethod.Auto);
        rig.Sampler.Configure("m", Zones, true);
        Assert.True(dxgi.Disposed);
        Assert.Equal(CaptureMethod.WindowsGraphicsCapture, rig.Current.Kind);
        Assert.Equal(3, rig.Created.Count);
    }

    [Fact]
    public void Requesting_the_same_effective_method_keeps_the_backend()
    {
        var rig = new Rig();
        rig.Sampler.Configure("", Zones, false);
        rig.Sampler.SetCaptureMethod(CaptureMethod.WindowsGraphicsCapture); // Auto → WGC: same backend
        rig.Sampler.Sample(new float[6], 0);
        Assert.Single(rig.Created);
    }

    [Fact]
    public void Repeated_wgc_init_failures_fall_back_to_desktop_duplication_until_the_setting_changes()
    {
        var rig = new Rig();
        rig.OnCreate = b => { if (b.Kind == CaptureMethod.WindowsGraphicsCapture) b.FailEverySample = 1; };
        var lines = new ConcurrentQueue<string>();
        void OnLine(string l) => lines.Enqueue(l);
        Log.LineLogged += OnLine;
        try
        {
            rig.Sampler.Configure("", Zones, false);
            var dest = new float[6];
            for (int i = 1; i < SwitchingScreenSampler.MaxWgcInitFailures; i++)
            {
                Assert.Equal(SampleResult.Unavailable, rig.Sampler.Sample(dest, 0));
                Assert.Equal(CaptureMethod.WindowsGraphicsCapture, rig.Sampler.EffectiveMethod);
            }
            rig.Sampler.Sample(dest, 0); // N-th failure → switch
            Assert.Equal(CaptureMethod.DesktopDuplication, rig.Sampler.EffectiveMethod);
            Assert.True(rig.Sampler.IsFallback);
            Assert.True(rig.Created[0].Disposed);
            Assert.Contains(lines, l => l.Contains("falling back to Desktop Duplication"));

            // Sticky: further samples / configures stay on DXGI.
            Assert.Equal(SampleResult.NewFrame, rig.Sampler.Sample(dest, 0));
            rig.Sampler.Configure("", Zones, false);
            Assert.Equal(2, rig.Created.Count);

            // A new choice gives WGC another chance.
            rig.Sampler.SetCaptureMethod(CaptureMethod.WindowsGraphicsCapture);
            rig.Sampler.Configure("", Zones, false);
            Assert.Equal(CaptureMethod.WindowsGraphicsCapture, rig.Sampler.EffectiveMethod);
            Assert.False(rig.Sampler.IsFallback);
            Assert.Equal(3, rig.Created.Count);
        }
        finally
        {
            Log.LineLogged -= OnLine;
        }
    }

    [Fact]
    public void Wgc_constructor_failure_falls_back_to_desktop_duplication()
    {
        var rig = new Rig { WgcCtorThrows = true };
        rig.Sampler.Configure("", Zones, false);
        Assert.Equal(CaptureMethod.DesktopDuplication, rig.Sampler.EffectiveMethod);
        Assert.True(rig.Sampler.IsFallback);
        Assert.Equal(SampleResult.NewFrame, rig.Sampler.Sample(new float[6], 0));
    }

    [Fact]
    public void Ui_thread_reads_stay_safe_while_switching()
    {
        var rig = new Rig();
        rig.Sampler.Configure("", Zones, false);
        using var stop = new CancellationTokenSource();
        Exception? error = null;
        int reads = 0;
        var reader = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    rig.Sampler.TryGetPreview(out _, out _, out _); // FakeBackend throws if read after dispose
                    _ = rig.Sampler.BlackBars;
                    _ = rig.Sampler.CurrentMonitor;
                    _ = rig.Sampler.EffectiveMethod;
                    reads++;
                }
            }
            catch (Exception ex) { error = ex; }
        });
        reader.Start();
        var dest = new float[6];
        for (int i = 0; i < 300; i++)
        {
            rig.Sampler.SetCaptureMethod(i % 2 == 0 ? CaptureMethod.DesktopDuplication : CaptureMethod.Auto);
            rig.Sampler.Sample(dest, 0);
        }
        stop.Cancel();
        reader.Join();
        Assert.Null(error);
        Assert.True(reads > 0);
        Assert.Equal(301, rig.Created.Count);
        Assert.Equal(300, rig.Created.Count(b => b.Disposed));
    }

    [Fact]
    public void Dispose_disposes_the_backend_and_sample_afterwards_is_unavailable_without_spinning()
    {
        var rig = new Rig();
        rig.Sampler.Configure("", Zones, false);
        var b = rig.Current;
        rig.Sampler.Dispose();
        rig.Sampler.Dispose();
        Assert.True(b.Disposed);
        var sw = Stopwatch.StartNew();
        Assert.Equal(SampleResult.Unavailable, rig.Sampler.Sample(new float[6], 0));
        Assert.True(sw.ElapsedMilliseconds >= 10, "returned without sleeping");
        rig.Sampler.Configure("", Zones, false); // ignored
        Assert.Single(rig.Created);
    }

    [Fact]
    public void A_sample_call_blocking_far_beyond_its_timeout_is_logged()
    {
        var rig = new Rig();
        rig.Sampler.Configure("", Zones, false);
        rig.Current.SampleSleepMs = (int)SwitchingScreenSampler.SlowSampleMarginMs + 150;
        var lines = new ConcurrentQueue<string>();
        void OnLine(string l) => lines.Enqueue(l);
        Log.LineLogged += OnLine;
        try
        {
            rig.Sampler.Sample(new float[6], 0);
        }
        finally
        {
            Log.LineLogged -= OnLine;
        }
        Assert.Contains(lines, l => l.Contains("[WARN]") && l.Contains("Sample() blocked") && l.Contains("Windows Graphics Capture"));
    }

    [Fact]
    public void Engine_hands_the_method_setting_to_the_sampler_and_switches_on_its_own_thread()
    {
        var rig = new Rig();
        var dev = new FakeDevice();
        using var engine = new LightEngine(rig.Sampler, dev);
        var s = new AppSettings();
        s.Layout = new LayoutSettings { Bottom = 4, Right = 0, Top = 0, Left = 0 };
        s.Layout.Zones = LayoutGenerator.Generate(s.Layout);
        engine.Start(s);
        SpinWait.SpinUntil(() => rig.Sampler.EffectiveMethod is not null, 2000);
        Assert.Equal(CaptureMethod.WindowsGraphicsCapture, rig.Sampler.EffectiveMethod);
        int engineThread = rig.Created[0].CreatedOnThread;
        Assert.NotEqual(Environment.CurrentManagedThreadId, engineThread);

        s.Capture.Method = CaptureMethod.DesktopDuplication;
        engine.UpdateSettings(s);
        Assert.True(SpinWait.SpinUntil(() => rig.Sampler.EffectiveMethod == CaptureMethod.DesktopDuplication, 2000));
        Assert.True(SpinWait.SpinUntil(() => rig.Created[0].Disposed, 2000));
        Assert.Equal(engineThread, rig.Created[1].CreatedOnThread);
        Assert.Equal(engineThread, rig.Created[0].DisposedOnThread);
    }
}
