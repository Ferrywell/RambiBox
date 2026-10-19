using RambiBox.Core.Config;
using RambiBox.Core.Devices.Hid;
using Xunit.Abstractions;

namespace RambiBox.Tests.Devices.Hid;

public class LightpackDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Lists_lightpacks_with_firmware_and_an_all_entry_without_writing()
    {
        var bus = new FakeHidTransport();
        var b = FakeHidTransport.Lightpack("B");
        var a = FakeHidTransport.Lightpack("A");
        var noSerial = FakeHidTransport.Lightpack(null, path: "nopath", old: true);
        bus.Plug(new HidDeviceInfo("kbd", 0x046D, 0xC31C, OutputReportLength: 2));
        bus.Plug(b);
        bus.Plug(noSerial);
        bus.Plug(a);
        bus.InputReport = d =>
        {
            if (d.SerialNumber != "A") return [];
            var r = new byte[65];
            r[2] = 6; r[3] = 6;
            return r;
        };

        var found = await new LightpackDiscovery(bus).DiscoverAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(4, found.Count);
        Assert.All(found, f => Assert.Equal(DeviceType.Lightpack, f.Type));
        Assert.Equal(LightpackDiscovery.AllDevicesId, found[0].Id);
        Assert.Equal(30, found[0].LedCount);
        Assert.Equal("All Lightpacks (3 devices)", found[0].DisplayName);
        Assert.Equal(["A", "B", "nopath"], found.Skip(1).Select(f => f.Id));
        Assert.Equal("Lightpack (serial A)", found[1].DisplayName);
        Assert.Contains("Firmware 6.6", found[1].Detail);
        Assert.Contains("Firmware unknown", found[2].Detail);
        Assert.Equal("Lightpack #3", found[3].DisplayName);
        Assert.Contains("03EB:204F", found[3].Detail);
        Assert.All(found.Skip(1), f => Assert.Equal(10, f.LedCount));
        Assert.All(bus.Opened, c => Assert.True(c.ReadOnly && c.Disposed));
        Assert.All(bus.Opened, c => Assert.Empty(c.Snapshot()));

        var s = new DeviceSettings();
        found[0].ApplyTo(s);
        Assert.Equal("*", s.DeviceId);
        Assert.Equal(DeviceType.Lightpack, s.Type);
    }

    [Fact]
    public async Task Single_lightpack_has_no_all_entry_and_none_found_is_empty()
    {
        var bus = new FakeHidTransport();
        var disc = new LightpackDiscovery(bus);
        Assert.Empty(await disc.DiscoverAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        bus.Plug(FakeHidTransport.Lightpack("ONLY"));
        var one = await disc.DiscoverAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("ONLY", Assert.Single(one).Id);
    }

    private sealed class ThrowingTransport : IHidTransport
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(Func<ushort, ushort, bool> match) => throw new InvalidOperationException("boom");
        public IHidConnection Open(HidDeviceInfo device, bool readOnly = false) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Discovery_never_throws()
    {
        Assert.Empty(await new LightpackDiscovery(new ThrowingTransport()).DiscoverAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Empty(await new LightpackDiscovery(new FakeHidTransport()).DiscoverAsync(TimeSpan.FromSeconds(1), cts.Token));
    }

    /// <summary>
    /// Smoke test on the real system (not Hardware-tagged): read-only enumeration of every HID interface must not throw,
    /// and anything reported as a Lightpack must really carry a Lightpack VID/PID with a full-size output report.
    /// Nothing is written to any device.
    /// </summary>
    [Fact]
    public async Task Real_hid_enumeration_is_safe_and_finds_no_false_lightpack()
    {
        var seen = new List<(ushort, ushort)>();
        var none = Win32HidTransport.Shared.Enumerate((v, p) => { lock (seen) seen.Add((v, p)); return false; });
        Assert.Empty(none);
        lock (seen) output.WriteLine($"HID interfaces with USB ids: {seen.Count} ({string.Join(", ", seen.Distinct().Select(x => $"{x.Item1:X4}:{x.Item2:X4}"))})");

        var lightpacks = Win32HidTransport.Shared.Enumerate(LightpackProtocol.IsLightpackId);
        Assert.All(lightpacks, d => Assert.True(LightpackProtocol.IsLightpackId(d.VendorId, d.ProductId)));

        var found = await new LightpackDiscovery().DiscoverAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(lightpacks.Count(LightpackProtocol.IsUsable) + (lightpacks.Count(LightpackProtocol.IsUsable) >= 2 ? 1 : 0), found.Count);
        foreach (var f in found.Where(f => f.Id != LightpackDiscovery.AllDevicesId))
            Assert.Contains(lightpacks, d => d.SerialNumber == f.Id || d.Path == f.Id);
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_1d50&pid_6022#7&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x1D50, 0x6022)]
    [InlineData(@"\\?\HID#VID_03EB&PID_204F&MI_00#8&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x03EB, 0x204F)]
    public void Vid_pid_is_parsed_from_interface_paths(string path, int vid, int pid)
    {
        Assert.True(Win32HidTransport.TryParseVidPid(path, out ushort v, out ushort p));
        Assert.Equal((ushort)vid, v);
        Assert.Equal((ushort)pid, p);
    }

    [Fact]
    public void Paths_without_usb_ids_are_not_parsed()
        => Assert.False(Win32HidTransport.TryParseVidPid(@"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b023", out _, out _));
}
