using System.Diagnostics;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>Serial discovery must never disturb devices in use, Bluetooth/modem ports, or unknown USB devices.</summary>
public class SerialDeviceDiscoveryTests
{
    private static readonly AdalightOptions Fast = new() { ProbeTimeoutMs = 400, MinReopenIntervalMs = 0 };

    private static FakeEnvironment Env()
    {
        var env = new FakeEnvironment();
        env.Add(new FakeDevice { Port = "COM3" });                                                     // CH340 Adalight
        env.Add(new FakeDevice { Port = "COM4", Description = "Bluetooth link", UsbId = null, IsBluetooth = true });
        env.Add(new FakeDevice { Port = "COM5", Description = "LTE modem AT port", UsbId = "1A86:7523", IsModem = true });
        env.Add(new FakeDevice { Port = "COM6", UsbId = "0403:6001", Busy = true });                  // in use (e.g. RambiBox)
        env.Add(new FakeDevice { Port = "COM7", Description = "3D printer", UsbId = "1234:0001" });   // unknown chip
        env.Add(new FakeDevice { Port = "COM1", Description = "Communications Port", UsbId = null, Greets = false }); // motherboard
        env.Add(new FakeDevice { Port = "COM8", UsbId = "10C4:EA60", Greets = false });               // silent CP210x
        return env;
    }

    [Fact]
    public async Task Default_ProbesOnlyIdleKnownChips_ReportsUsbPorts()
    {
        var env = Env();
        var disco = new SerialDeviceDiscovery(env, env, deepProbe: false, Fast);
        var sw = Stopwatch.StartNew();
        var found = await disco.DiscoverAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(new[] { "COM3", "COM6", "COM8", "COM7" }, found.Select(d => d.Port));
        Assert.All(found, d => Assert.Equal(d.Port, d.Id));

        var ada = found[0];
        Assert.Equal(DeviceType.Adalight, ada.Type);
        Assert.Contains("answered", ada.Detail);
        Assert.Contains("in use", found[1].Detail);
        Assert.Contains("no Adalight greeting", found[2].Detail);
        Assert.Contains("not probed", found[3].Detail);

        // Never opened: Bluetooth, modem, unknown chip, port without USB id. COM6's open attempt failed (busy).
        Assert.DoesNotContain("COM4", env.Opened);
        Assert.DoesNotContain("COM5", env.Opened);
        Assert.DoesNotContain("COM7", env.Opened);
        Assert.DoesNotContain("COM1", env.Opened);
        Assert.Equal(0, env.Get("COM6").OpenCount);
        Assert.Empty(env.Get("COM6").Links);
        // Nothing is ever written during discovery, and every probe link is closed again.
        Assert.All(new[] { "COM3", "COM8" }, p => Assert.All(env.Get(p).Links, l => { Assert.Empty(l.Writes); Assert.True(l.Disposed); }));
    }

    [Fact]
    public async Task DeepProbe_AlsoProbesUnknownChipsAndNonUsbPorts_NeverBluetoothOrModem()
    {
        var env = Env();
        var disco = new SerialDeviceDiscovery(env, env, deepProbe: true, Fast);
        var found = await disco.DiscoverAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Contains("COM7", env.Opened);
        Assert.Contains("COM1", env.Opened);
        Assert.DoesNotContain("COM4", env.Opened);
        Assert.DoesNotContain("COM5", env.Opened);
        // COM7 runs Adalight firmware after all → verified, listed before the unverified ones.
        Assert.Equal(new[] { "COM3", "COM7" }, found.Where(d => d.Detail.Contains("answered")).Select(d => d.Port));
        Assert.Contains(found, d => d.Port == "COM1");
        Assert.DoesNotContain(found, d => d.Port is "COM4" or "COM5");
    }

    [Fact]
    public async Task Cancelled_ReturnsQuickly_WithoutOpeningPorts()
    {
        var env = Env();
        var disco = new SerialDeviceDiscovery(env, env, deepProbe: false, Fast);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var found = await disco.DiscoverAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Empty(env.Opened);
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public async Task CancelledWhileProbing_StopsAndClosesPorts()
    {
        var env = new FakeEnvironment();
        env.Add(new FakeDevice { Port = "COM3", Greets = false });
        var disco = new SerialDeviceDiscovery(env, env, deepProbe: false, new AdalightOptions { ProbeTimeoutMs = 5000, MinReopenIntervalMs = 0 });
        using var cts = new CancellationTokenSource(150);
        var sw = Stopwatch.StartNew();
        var found = await disco.DiscoverAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Single(found);
        Assert.True(Wait.Until(() => env.Get("COM3").Links.All(l => l.Disposed), 1000));
    }

    [Fact]
    public async Task ShortTimeout_OnlyLists()
    {
        var env = Env();
        var disco = new SerialDeviceDiscovery(env, env, deepProbe: false, Fast);
        var found = await disco.DiscoverAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Empty(env.Opened);
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public void Types_AreTheSerialFamilies()
        => Assert.Equal(new[] { DeviceType.Adalight, DeviceType.Ardulight, DeviceType.Awa, DeviceType.Tpm2 }, new SerialDeviceDiscovery().Types);
}
