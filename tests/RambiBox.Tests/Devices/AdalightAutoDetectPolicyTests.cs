using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>
/// Auto-detect must not keep resetting unrelated serial devices (3D printers, CNC controllers, modems):
/// only remembered ports and known USB-serial chips are probed periodically; unknown ports at most once per arrival;
/// modem ports never.
/// </summary>
public class AdalightAutoDetectPolicyTests
{
    private static readonly AdalightOptions Fast = new()
    {
        ProbeTimeoutMs = 150,
        FixedGreetingTimeoutMs = 200,
        RescanIntervalMs = 30,
        FailedRetryMs = 150,
        MaxRetryMs = 300,
        BusyRetryMs = 100,
        MinReopenIntervalMs = 0,
    };

    private const string UnknownId = "1234:0001"; // e.g. a 3D printer's own USB id

    private static (AdalightDevice Dev, FakeEnvironment Env) Create(params FakeDevice[] devices)
    {
        var env = new FakeEnvironment();
        foreach (var d in devices) env.Add(d);
        return (new AdalightDevice(env, env, Fast), env);
    }

    private static DeviceSettings Auto(string? lastPort = null, string? lastUsbId = null)
        => new() { Port = DeviceSettings.AutoPort, LastPort = lastPort, LastUsbId = lastUsbId };

    private static FakeDevice Unknown(string port, bool greets = false)
        => new() { Port = port, Description = "Printer", UsbId = UnknownId, Greets = greets };

    private static FakeDevice KnownChip(string port, string usbId = "1A86:7523")
        => new() { Port = port, Description = "USB-SERIAL CH340", UsbId = usbId, Greets = false };

    /// <summary>Keeps rescanning (like WM_DEVICECHANGE storms would) for the given time.</summary>
    private static void RescanFor(AdalightDevice dev, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            dev.Rescan();
            Thread.Sleep(40);
        }
    }

    [Fact]
    public void UnknownPort_IsProbedOnceAtStartup_AndNeverAgain()
    {
        var (dev, env) = Create(Unknown("COM3"), new FakeDevice { Port = "COM1", Description = "Communications Port", UsbId = null, Greets = false });
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => env.OpenCountOf("COM3") == 1 && env.OpenCountOf("COM1") == 1));
            // Many retry cycles (a known chip would have been re-probed ~5 times by now), plus rescans.
            RescanFor(dev, 1500);
            Assert.Equal(1, env.OpenCountOf("COM3"));
            Assert.Equal(1, env.OpenCountOf("COM1"));
            Assert.Equal(DeviceStatus.Searching, dev.Status);
        }
    }

    [Fact]
    public void KnownChip_IsProbedPeriodically_UnknownPortsAtStartupAreLeftAlone()
    {
        var (dev, env) = Create(KnownChip("COM4"), Unknown("COM3"));
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => env.OpenCountOf("COM4") >= 4, 5000), $"COM4 opened {env.OpenCountOf("COM4")}x");
            // A known-chip candidate exists, so the unknown port is not even probed once at startup.
            Assert.Equal(0, env.OpenCountOf("COM3"));
        }
    }

    [Fact]
    public void NewArrivalOfUnknownPort_IsProbedOnce_AndAgainOnlyAfterReplug()
    {
        var (dev, env) = Create(KnownChip("COM4"));
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => env.OpenCountOf("COM4") >= 1));

            env.Add(Unknown("COM7"));
            dev.Rescan();
            Assert.True(Wait.Until(() => env.OpenCountOf("COM7") == 1));
            RescanFor(dev, 1200);
            Assert.Equal(1, env.OpenCountOf("COM7"));
            Assert.True(env.OpenCountOf("COM4") >= 3); // the known chip keeps being retried meanwhile

            // Unplug + replug = a new arrival → one more probe.
            env.Remove("COM7");
            Thread.Sleep(400); // let a scan see it gone (a COM4 probe may be in progress)
            env.Add(Unknown("COM7"));
            dev.Rescan();
            Assert.True(Wait.Until(() => env.OpenCountOf("COM7") == 2));
            RescanFor(dev, 800);
            Assert.Equal(2, env.OpenCountOf("COM7"));
        }
    }

    [Fact]
    public void NewArrivalOfUnknownAdalight_IsFoundAndRemembered()
    {
        var (dev, env) = Create();
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Thread.Sleep(100);
            env.Add(Unknown("COM9", greets: true));
            dev.Rescan();
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM9", dev.PortName);
            Assert.Equal(UnknownId, dev.RememberedUsbId);

            // Once remembered it is a rank-0/1 candidate: re-plugging is picked up again (periodic probing).
            env.Remove("COM9");
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Searching));
            Thread.Sleep(100);
            var again = env.Add(Unknown("COM9", greets: true));
            again.Busy = true; // e.g. the driver isn't ready yet: first attempts fail
            dev.Rescan();
            Thread.Sleep(300);
            again.Busy = false;
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        }
    }

    [Fact]
    public void ModemPorts_AreNeverProbed()
    {
        var (dev, env) = Create(
            new FakeDevice { Port = "COM2", Description = "Standard 56000 bps Modem", UsbId = null, IsModem = true, Greets = false },
            new FakeDevice { Port = "COM5", Description = "LTE Modem AT port", UsbId = "2341:0043", IsModem = true, Greets = true });
        using (dev)
        {
            dev.Configure(Auto(), 10);
            RescanFor(dev, 600);
            env.Add(new FakeDevice { Port = "COM6", Description = "USB Modem", UsbId = UnknownId, IsModem = true, Greets = false });
            RescanFor(dev, 600);
            Assert.Empty(env.Opened);
            Assert.Equal(DeviceStatus.Searching, dev.Status);
        }
    }

    [Fact]
    public void RememberedDevice_UnknownPortsAreNotProbed_WhileKnownCandidatesExist()
    {
        var (dev, env) = Create(KnownChip("COM4", "0403:6001"), Unknown("COM8"));
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 10);
            Assert.True(Wait.Until(() => env.OpenCountOf("COM4") >= 2));

            // Arrival of an unknown port while a known chip is present: not probed.
            env.Add(Unknown("COM9"));
            RescanFor(dev, 700);
            Assert.Equal(0, env.OpenCountOf("COM8"));
            Assert.Equal(0, env.OpenCountOf("COM9"));

            // No known/remembered candidate left: the next arrival gets its one probe.
            env.Remove("COM4");
            env.Add(Unknown("COM10"));
            dev.Rescan();
            Assert.True(Wait.Until(() => env.OpenCountOf("COM10") == 1));
            RescanFor(dev, 900);
            Assert.Equal(1, env.OpenCountOf("COM10"));
            Assert.Equal(0, env.OpenCountOf("COM8")); // present since startup: never an arrival
            Assert.Equal(0, env.OpenCountOf("COM9"));
        }
    }

    [Fact]
    public void RememberedDevice_UnknownPortsPresentAtStartup_AreNotProbed()
    {
        var (dev, env) = Create(Unknown("COM8"));
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 10);
            RescanFor(dev, 800);
            Assert.Empty(env.Opened);

            // The remembered device comes back on another port: found by its USB id.
            env.Add(new FakeDevice { Port = "COM5", UsbId = "1A86:7523" });
            dev.Rescan();
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected && dev.PortName == "COM5"));
            Assert.Equal(0, env.OpenCountOf("COM8"));
        }
    }

    [Fact]
    public void Candidates_ExcludeBluetooth_RankUnknownLast()
    {
        var ports = new[]
        {
            new SerialPortInfo("COM1", "Communications Port", null),
            new SerialPortInfo("COM4", "BT", null) { IsBluetooth = true },
            new SerialPortInfo("COM7", "CP2102", "10C4:EA60"),
            new SerialPortInfo("COM9", "Printer", UnknownId),
        };
        var c = AdalightDevice.OrderCandidates(ports, lastPort: "COM9", lastUsbId: null);
        Assert.Equal(new[] { ("COM9", 0), ("COM7", 2), ("COM1", 3) }, c.Select(x => (x.Info.PortName, x.Rank)));
    }
}
