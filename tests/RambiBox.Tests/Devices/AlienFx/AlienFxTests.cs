using System.Diagnostics;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Devices.AlienFx;

namespace RambiBox.Tests.Devices.AlienFx;

/// <summary>Fake LightFX 2.0 system: devices with N lights, records every call.</summary>
internal sealed class FakeLightFx : ILightFx, ILightFxProvider
{
    private readonly object _gate = new();
    public bool Installed = true;
    public uint InitializeResult = LfxResult.Success;
    public uint UpdateResult = LfxResult.Success;
    public uint[] LightsPerDevice = [];
    public string[] Descriptions = [];
    public int InitCount;
    public readonly List<string> Calls = [];
    public readonly Dictionary<(uint, uint), LfxColor> Colors = [];
    public readonly List<Dictionary<(uint, uint), LfxColor>> Updates = [];

    public ILightFx? TryGet(out string? reason)
    {
        reason = Installed ? null : "LightFX.dll not found";
        return Installed ? this : null;
    }

    private void Add(string c) { lock (_gate) Calls.Add(c); }

    public List<string> CallsSnapshot() { lock (_gate) return Calls.ToList(); }

    public uint Initialize()
    {
        Add("Initialize");
        if (InitializeResult is LfxResult.Success or LfxResult.NoDevs) Interlocked.Increment(ref InitCount);
        return InitializeResult;
    }

    public uint Release()
    {
        Add("Release");
        Interlocked.Decrement(ref InitCount);
        return LfxResult.Success;
    }

    public uint Reset()
    {
        Add("Reset");
        lock (_gate) Colors.Clear();
        return Volatile.Read(ref InitCount) > 0 ? LfxResult.Success : LfxResult.NoInit;
    }

    public uint Update()
    {
        Add("Update");
        lock (_gate) Updates.Add(new(Colors));
        return UpdateResult;
    }

    public uint GetNumDevices(out uint count)
    {
        count = (uint)LightsPerDevice.Length;
        return count == 0 ? LfxResult.NoDevs : LfxResult.Success;
    }

    public uint GetDeviceDescription(uint device, out string description, out byte deviceType)
    {
        description = device < Descriptions.Length ? Descriptions[device] : "";
        deviceType = 1;
        return LfxResult.Success;
    }

    public uint GetNumLights(uint device, out uint count)
    {
        count = device < LightsPerDevice.Length ? LightsPerDevice[device] : 0;
        return count == 0 ? LfxResult.NoLights : LfxResult.Success;
    }

    public uint SetLightColor(uint device, uint light, in LfxColor color)
    {
        lock (_gate) Colors[(device, light)] = color;
        return LfxResult.Success;
    }

    public Dictionary<(uint, uint), LfxColor> LastUpdate() { lock (_gate) return Updates[^1]; }
    public int UpdateCount { get { lock (_gate) return Updates.Count; } }
}

public class AlienFxTests
{
    private static readonly AlienFxOptions Fast = new() { FrameRate = 500, SearchIntervalMs = 20 };

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("condition not met in time");
            Thread.Sleep(5);
        }
    }

    private static LfxColor C(byte r, byte g, byte b) => new(r, g, b, 255);

    [Fact]
    public void One_led_colors_every_light_like_prismatik()
    {
        var lights = new LfxColor[5];
        AlienFxDevice.MapToLights([10, 20, 30], lights);
        Assert.All(lights, l => Assert.Equal(C(10, 20, 30), l));
    }

    [Fact]
    public void More_leds_than_lights_are_averaged_per_group()
    {
        var lights = new LfxColor[2];
        // 4 LEDs: (0,0,0) (100,100,100) | (200,0,0) (255,0,1)
        AlienFxDevice.MapToLights([0, 0, 0, 100, 100, 100, 200, 0, 0, 255, 0, 1], lights);
        Assert.Equal(C(50, 50, 50), lights[0]);
        Assert.Equal(C(228, 0, 1), lights[1]); // (455+1)/2 = 228, (1+1)/2 = 1
    }

    [Fact]
    public void Fewer_leds_than_lights_spread_each_led_over_consecutive_lights()
    {
        var lights = new LfxColor[4];
        AlienFxDevice.MapToLights([1, 1, 1, 2, 2, 2], lights);
        Assert.Equal([C(1, 1, 1), C(1, 1, 1), C(2, 2, 2), C(2, 2, 2)], lights);
        AlienFxDevice.MapToLights([], lights);
        Assert.All(lights, l => Assert.Equal(LfxColor.Black, l));
    }

    [Fact]
    public void Missing_lightfx_keeps_searching_and_logs_once()
    {
        var fx = new FakeLightFx { Installed = false };
        var lines = new List<string>();
        void OnLine(string l) { lock (lines) lines.Add(l); }
        RambiBox.Core.Log.LineLogged += OnLine;
        try
        {
            using var dev = new AlienFxDevice(fx, Fast);
            dev.Configure(new DeviceSettings { Type = DeviceType.AlienFx }, 1);
            WaitUntil(() => dev.Status == DeviceStatus.Searching);
            Thread.Sleep(150); // several retries
            Assert.Equal(DeviceStatus.Searching, dev.Status);
            lock (lines)
                Assert.Single(lines, l => l.Contains("Alienware LightFX not available (Alienware Command Center/AlienFX not installed)"));
            Assert.False(dev.WaitForSlot(5));
            dev.Submit([1, 2, 3]);
        }
        finally
        {
            RambiBox.Core.Log.LineLogged -= OnLine;
        }
        Assert.Empty(fx.CallsSnapshot());
    }

    [Fact]
    public void Lifecycle_initialize_reset_frames_blackout_release()
    {
        var fx = new FakeLightFx { LightsPerDevice = [2, 3], Descriptions = ["Alienware M17", "AW Keyboard"] };
        var dev = new AlienFxDevice(fx, Fast);
        var s = new DeviceSettings { Type = DeviceType.AlienFx };
        dev.Configure(s, 5);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal("AlienFX (5 lights)", dev.PortName);
        Assert.Equal(5, dev.LightCount);
        Assert.Equal(["Initialize", "Reset"], fx.CallsSnapshot());

        // 5 LEDs onto 5 lights: device 0 lights 0,1 then device 1 lights 0..2
        long before = dev.FramesWritten;
        dev.Submit([1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5]);
        WaitUntil(() => dev.FramesWritten > before);
        var u = fx.LastUpdate();
        Assert.Equal(C(1, 1, 1), u[(0, 0)]);
        Assert.Equal(C(2, 2, 2), u[(0, 1)]);
        Assert.Equal(C(3, 3, 3), u[(1, 0)]);
        Assert.Equal(C(5, 5, 5), u[(1, 2)]);

        dev.Blackout();
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        var calls = fx.CallsSnapshot();
        Assert.Equal(["Reset", "Update"], calls[^2..]);
        Assert.Empty(fx.LastUpdate()); // all lights off
        Assert.Equal(1, fx.InitCount);  // still initialized while blacked out

        dev.Configure(s, 5);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        dev.Dispose();
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        Assert.Equal("Release", fx.CallsSnapshot()[^1]);
        Assert.Equal(0, fx.InitCount);
    }

    [Fact]
    public void No_devices_releases_and_retries_until_lights_appear()
    {
        var fx = new FakeLightFx { InitializeResult = LfxResult.NoDevs };
        using var dev = new AlienFxDevice(fx, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.AlienFx }, 1);
        WaitUntil(() => fx.CallsSnapshot().Count(c => c == "Initialize") >= 2);
        Assert.Equal(DeviceStatus.Searching, dev.Status);
        Assert.Equal(0, fx.InitCount);

        fx.LightsPerDevice = [4];
        fx.InitializeResult = LfxResult.Success;
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        long before = dev.FramesWritten;
        dev.Submit([9, 8, 7]);
        WaitUntil(() => dev.FramesWritten > before);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(C(9, 8, 7), fx.LastUpdate()[(0, (uint)i)]));
    }

    [Fact]
    public void Failed_update_releases_and_reconnects()
    {
        var fx = new FakeLightFx { LightsPerDevice = [1] };
        using var dev = new AlienFxDevice(fx, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.AlienFx }, 1);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        fx.UpdateResult = LfxResult.NoDevs;
        dev.Submit([1, 2, 3]);
        WaitUntil(() => fx.CallsSnapshot().Contains("Release"));
        fx.UpdateResult = LfxResult.Success;
        WaitUntil(() => dev.Status == DeviceStatus.Connected && fx.CallsSnapshot().Count(c => c == "Initialize") == 2);
    }

    [Fact]
    public async Task Discovery_reports_only_when_lightfx_has_lights_and_releases()
    {
        var missing = new AlienFxDiscovery(new FakeLightFx { Installed = false });
        Assert.Empty(await missing.DiscoverAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

        var noDevs = new FakeLightFx { InitializeResult = LfxResult.NoDevs };
        Assert.Empty(await new AlienFxDiscovery(noDevs).DiscoverAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(0, noDevs.InitCount);

        var fx = new FakeLightFx { LightsPerDevice = [3, 0, 6], Descriptions = ["Alienware Aurora", "", "AW Mouse"] };
        var found = await new AlienFxDiscovery(fx).DiscoverAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        var d = Assert.Single(found);
        Assert.Equal(DeviceType.AlienFx, d.Type);
        Assert.Equal(AlienFxDiscovery.DeviceId, d.Id);
        Assert.Equal(9, d.LedCount);
        Assert.Contains("Alienware Aurora (3 lights)", d.Detail);
        Assert.Equal(0, fx.InitCount);
        Assert.DoesNotContain("Update", fx.CallsSnapshot());
        Assert.DoesNotContain("Reset", fx.CallsSnapshot());
    }

    [Fact]
    public async Task Real_system_discovery_and_driver_never_throw()
    {
        // On a PC without Alienware Command Center this finds nothing; with it, it only queries.
        var found = await new AlienFxDiscovery().DiscoverAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.All(found, f => Assert.Equal(DeviceType.AlienFx, f.Type));
        Assert.True(LedDeviceFactory.IsSupported(DeviceType.AlienFx));
        Assert.Contains(LedDeviceFactory.DiscoveryServices, x => x is AlienFxDiscovery);
    }
}
