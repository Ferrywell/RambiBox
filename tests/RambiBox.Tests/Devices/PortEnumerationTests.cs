using System.Diagnostics;
using RambiBox.Core.Devices;
using Xunit.Abstractions;

namespace RambiBox.Tests.Devices;

public class PortEnumerationTests
{
    private readonly ITestOutputHelper _out;
    public PortEnumerationTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(@"USB\VID_1A86&PID_7523\8&f2cb6fa&0&15", "1A86:7523")]
    [InlineData(@"USB\VID_2341&PID_8036&MI_00\7&1234&0&0000", "2341:8036")]
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000", "0403:6001")]
    [InlineData(@"USB\VID_10c4&PID_ea60\0001", "10C4:EA60")]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1&0&000000000000_00000000", null)]
    [InlineData(@"ACPI\PNP0501\1", null)]
    [InlineData("", null)]
    public void ParseUsbId(string instance, string? expected)
        => Assert.Equal(expected, Win32PortEnumerator.ParseUsbId(instance));

    [Theory]
    [InlineData("USB-SERIAL CH340 (COM3)", "COM3", "USB-SERIAL CH340")]
    [InlineData("@oem49.inf,%ch340ser.devicedesc%;USB-SERIAL CH340", "COM3", "USB-SERIAL CH340")]
    [InlineData(null, "COM7", "COM7")]
    public void CleanDescription(string? text, string port, string expected)
        => Assert.Equal(expected, Win32PortEnumerator.CleanDescription(text, port));

    [Fact]
    public void PortNames_SortNumerically()
    {
        var list = new List<string> { "COM10", "COM3", "COM1", "COM22" };
        list.Sort(Win32PortEnumerator.ComparePortNames);
        Assert.Equal(new[] { "COM1", "COM3", "COM10", "COM22" }, list);
    }

    [Fact]
    public void CandidateOrder_LastPort_LastUsbId_KnownChips_Others_NoBluetooth()
    {
        var ports = new[]
        {
            new SerialPortInfo("COM1", "Communications Port", null),
            new SerialPortInfo("COM4", "Standard Serial over Bluetooth link", null) { IsBluetooth = true },
            new SerialPortInfo("COM5", "USB-SERIAL CH340", "1A86:7523"),
            new SerialPortInfo("COM6", "Arduino Leonardo", "2341:8036"),
            new SerialPortInfo("COM7", "CP210x", "10C4:EA60"),
            new SerialPortInfo("COM8", "Some modem", "1234:5678"),
            new SerialPortInfo("COM9", "FT232R", "0403:6001"),
        };
        var order = AdalightDevice.OrderCandidates(ports, lastPort: "COM8", lastUsbId: "10C4:EA60");
        Assert.Equal(new[] { "COM8", "COM7", "COM5", "COM6", "COM9", "COM1" }, order.Select(o => o.Info.PortName));
        Assert.Equal(new[] { 0, 1, 2, 2, 2, 3 }, order.Select(o => o.Rank));
    }

    [Theory]
    [InlineData("1A86:7523", true)]
    [InlineData("1a86:55d4", true)]
    [InlineData("0403:6015", true)]
    [InlineData("2341:0043", true)]
    [InlineData("2A03:0043", true)]
    [InlineData("0E8D:0003", false)]
    [InlineData(null, false)]
    public void KnownChips(string? id, bool known) => Assert.Equal(known, AdalightDevice.IsKnownChip(id));

    [Fact]
    public void RealEnumerator_IsFast_AndConsistent()
    {
        var e = new Win32PortEnumerator();
        var sw = Stopwatch.StartNew();
        var ports = e.ListPorts();
        var first = sw.Elapsed;
        sw.Restart();
        var present = e.GetPresentPortNames();
        var again = e.ListPorts(); // cached
        var cached = sw.Elapsed;

        _out.WriteLine($"ListPorts uncached {first.TotalMilliseconds:F2} ms, presence+cached {cached.TotalMilliseconds:F3} ms, {ports.Count} ports");
        Assert.True(first.TotalMilliseconds < 1000, $"ListPorts took {first.TotalMilliseconds} ms");
        Assert.True(cached.TotalMilliseconds < 50, $"cached ListPorts took {cached.TotalMilliseconds} ms");
        Assert.Equal(present.Count, ports.Count);
        foreach (var p in ports)
        {
            Assert.Contains(p.PortName, present);
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            if (p.IsBluetooth) Assert.Null(p.UsbId);
        }
        Assert.Same(ports, again);
    }
}
