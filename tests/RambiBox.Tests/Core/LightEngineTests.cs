using System.Collections.Concurrent;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.Tests.Core;

internal sealed class FakeSampler : IScreenSampler
{
    public volatile float Value = 0.5f;
    public volatile bool Changed = true;
    public int SampleCalls;
    public IReadOnlyList<MonitorInfo> GetMonitors() => [];
    public MonitorInfo? CurrentMonitor => null;
    public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection) { }
    public SampleResult Sample(Span<float> destRgb, int timeoutMs)
    {
        Interlocked.Increment(ref SampleCalls);
        if (!Changed) { if (timeoutMs > 0) Thread.Sleep(timeoutMs); return SampleResult.NoChange; }
        Changed = false;
        destRgb.Fill(Value);
        return SampleResult.NewFrame;
    }
    public bool TryGetPreview(out byte[] bgra, out int width, out int height) { bgra = []; width = height = 0; return false; }
    public (float Top, float Bottom, float Left, float Right) BlackBars => default;
    public void Dispose() { }
}

internal sealed class FakeDevice : ILedDevice
{
    public volatile DeviceStatus StatusValue = DeviceStatus.Connected;
    public readonly ConcurrentQueue<byte[]> Frames = new();
    public int LedCount;
    public DeviceStatus Status => StatusValue;
    public string? PortName => "COM9";
    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }
    public void Configure(DeviceSettings settings, int ledCount) => LedCount = ledCount;
    public void Submit(ReadOnlySpan<byte> rgb) => Frames.Enqueue(rgb.ToArray());
    public bool WaitForSlot(int timeoutMs)
    {
        if (StatusValue != DeviceStatus.Connected) return false;
        Thread.Sleep(5);
        return true;
    }
    public double MaxFrameRate => 200;
    public double FramesPerSecond => 0;
    public void Rescan() { }
    public void Blackout() => Frames.Enqueue(new byte[LedCount * 3]);
    public IReadOnlyList<SerialPortInfo> ListPorts() => [];
    public void RaiseStatus() => StatusChanged?.Invoke();
    public void Dispose() { }
}

public class LightEngineTests
{
    private static AppSettings Settings(int leds = 10)
    {
        var s = new AppSettings();
        s.Layout = new LayoutSettings { Bottom = leds, Right = 0, Top = 0, Left = 0 };
        s.Layout.Zones = LayoutGenerator.Generate(s.Layout);
        s.Color = new ColorSettings { Saturation = 1, WhiteR = 1, WhiteG = 1, WhiteB = 1, SmoothingMs = 0, DarkThreshold = 0 };
        return s;
    }

    private static byte[] WaitFor(FakeDevice d, Func<byte[], bool> pred, int timeoutMs = 2000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            while (d.Frames.TryDequeue(out var f)) if (pred(f)) return f;
            Thread.Sleep(5);
        }
        throw new TimeoutException("expected frame never arrived");
    }

    [Fact]
    public void Ambilight_sends_captured_colors()
    {
        var sampler = new FakeSampler { Value = 0.5f };
        var dev = new FakeDevice();
        using var e = new LightEngine(sampler, dev);
        e.Start(Settings());
        var f = WaitFor(dev, f => f.Length == 30 && f[0] == 128);
        Assert.All(f, b => Assert.Equal(128, b));
    }

    [Fact]
    public void Offset_leds_stay_black()
    {
        var s = Settings(4);
        s.Layout.Offset = 2;
        var dev = new FakeDevice();
        using var e = new LightEngine(new FakeSampler { Value = 1f }, dev);
        e.Start(s);
        var f = WaitFor(dev, f => f.Length == 18 && f[17] == 255);
        Assert.All(f[..6], b => Assert.Equal(0, b));
        Assert.Equal(6, dev.LedCount);
    }

    [Fact]
    public void Pause_sends_black_and_resume_restores()
    {
        var sampler = new FakeSampler { Value = 1f };
        var dev = new FakeDevice();
        using var e = new LightEngine(sampler, dev);
        e.Start(Settings());
        WaitFor(dev, f => f[0] == 255);
        e.SetPaused(PauseReason.DisplayOff, true);
        WaitFor(dev, f => f.All(b => b == 0));
        e.SetPaused(PauseReason.DisplayOff, false);
        sampler.Changed = true;
        WaitFor(dev, f => f[0] == 255);
    }

    [Fact]
    public void Disabled_master_switch_sends_black()
    {
        var dev = new FakeDevice();
        using var e = new LightEngine(new FakeSampler { Value = 1f }, dev);
        var s = Settings();
        e.Start(s);
        WaitFor(dev, f => f[0] == 255);
        s.Enabled = false;
        e.UpdateSettings(s);
        WaitFor(dev, f => f.All(b => b == 0));
        Assert.Equal("Lights off", WaitStats(e, st => st.StatusText == "Lights off").StatusText);
    }

    [Fact]
    public void Static_color_mode_shows_color_with_white_balance()
    {
        var dev = new FakeDevice();
        using var e = new LightEngine(new FakeSampler(), dev);
        var s = Settings();
        s.Mode = LightMode.StaticColor;
        s.Static.Color = 0xFF8000;
        s.Color.WhiteB = 0.5;
        s.Color.WhiteG = 0.5;
        e.Start(s);
        var f = WaitFor(dev, f => f[0] == 255);
        Assert.Equal(new byte[] { 255, 64, 0 }, f[..3]);
    }

    [Fact]
    public void Override_takes_over_and_releases()
    {
        var dev = new FakeDevice();
        var sampler = new FakeSampler { Value = 0.2f };
        using var e = new LightEngine(sampler, dev);
        e.Start(Settings());
        WaitFor(dev, f => f[0] == 51);
        e.SetOverride(new SolidOverride());
        WaitFor(dev, f => f[0] == 255 && f[1] == 0);
        e.SetOverride(null);
        sampler.Changed = true;
        WaitFor(dev, f => f[0] == 51);
    }

    [Fact]
    public void Static_picture_idles_without_resending()
    {
        var dev = new FakeDevice();
        var sampler = new FakeSampler { Value = 0.3f };
        using var e = new LightEngine(sampler, dev);
        e.Start(Settings());
        WaitFor(dev, f => f[0] == 77);
        Thread.Sleep(100);
        while (dev.Frames.TryDequeue(out _)) { }
        int calls0 = sampler.SampleCalls;
        Thread.Sleep(500);
        // No changes on screen: no frames (keep-alive is 1/s) and only a handful of blocking capture calls
        Assert.True(dev.Frames.Count <= 1, $"sent {dev.Frames.Count} frames while idle");
        Assert.True(sampler.SampleCalls - calls0 < 15, $"{sampler.SampleCalls - calls0} sample calls while idle");
    }

    [Fact]
    public void Disconnected_device_does_not_capture()
    {
        var dev = new FakeDevice { StatusValue = DeviceStatus.Searching };
        var sampler = new FakeSampler();
        using var e = new LightEngine(sampler, dev);
        e.Start(Settings());
        Thread.Sleep(400);
        Assert.Equal(0, sampler.SampleCalls);
        dev.StatusValue = DeviceStatus.Connected;
        dev.RaiseStatus();
        WaitFor(dev, f => f[0] == 128);
    }

    private static EngineStats WaitStats(LightEngine e, Func<EngineStats, bool> pred)
    {
        var end = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < end)
        {
            var st = e.GetStats();
            if (pred(st)) return st;
            Thread.Sleep(20);
        }
        return e.GetStats();
    }

    private sealed class SolidOverride : ILedOverride
    {
        public bool ApplyWhiteBalance => false;
        public void Render(Span<float> rgb, int ledCount, double timeSeconds)
        {
            for (int i = 0; i < ledCount; i++) { rgb[3 * i] = 1; rgb[3 * i + 1] = 0; rgb[3 * i + 2] = 0; }
        }
    }
}
