using System.Diagnostics;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

public class AdalightDeviceTests
{
    private static readonly AdalightOptions Fast = new()
    {
        ProbeTimeoutMs = 300,
        FixedGreetingTimeoutMs = 200,
        RescanIntervalMs = 50,
        FailedRetryMs = 300,
        MaxRetryMs = 1000,
        BusyRetryMs = 100,
        MinReopenIntervalMs = 0,
    };

    private static (AdalightDevice Dev, FakeEnvironment Env) Create(params FakeDevice[] devices)
    {
        var env = new FakeEnvironment();
        foreach (var d in devices) env.Add(d);
        return (new AdalightDevice(env, env, Fast), env);
    }

    private static DeviceSettings Auto(string? lastPort = null, string? lastUsbId = null, ColorOrder order = ColorOrder.RGB)
        => new() { Port = DeviceSettings.AutoPort, LastPort = lastPort, LastUsbId = lastUsbId, ColorOrder = order };

    private static DeviceSettings Fixed(string port) => new() { Port = port };

    private static byte[] Rgb(int leds, byte v) => Enumerable.Repeat(v, leds * 3).ToArray();

    /// <summary>Submits frames (WaitForSlot → Submit) until the predicate holds.</summary>
    private static bool StreamUntil(AdalightDevice dev, Func<bool> done, int timeoutMs = 3000, int leds = 10, byte value = 7)
    {
        var frame = Rgb(leds, value);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            if (dev.WaitForSlot(20)) dev.Submit(frame);
            else Thread.Sleep(2);
        }
        return done();
    }

    [Fact]
    public void AutoDetect_FindsGreetingPort_NeverOpensBluetooth_RaisesDeviceFound()
    {
        var (dev, env) = Create(
            new FakeDevice { Port = "COM1", Description = "Communications Port", UsbId = null, Greets = false },
            new FakeDevice { Port = "COM4", Description = "Bluetooth link", UsbId = null, IsBluetooth = true },
            new FakeDevice { Port = "COM5" });
        using (dev)
        {
            SerialPortInfo? found = null;
            int statusEvents = 0;
            dev.DeviceFound += i => found = i;
            dev.StatusChanged += () => Interlocked.Increment(ref statusEvents);
            dev.Configure(Auto(), 10);

            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM5", dev.PortName);
            Assert.NotNull(found);
            Assert.Equal("COM5", found!.PortName);
            Assert.Equal("1A86:7523", found.UsbId);
            Assert.Equal("COM5", env.Opened[0]); // known chip probed first
            Assert.DoesNotContain("COM4", env.Opened);
            Assert.True(statusEvents > 0);
            Assert.Equal("COM5", dev.RememberedPort);
        }
        Assert.DoesNotContain("COM4", env.Opened);
    }

    [Fact]
    public void AutoDetect_TriesLastPortFirst()
    {
        var (dev, env) = Create(
            new FakeDevice { Port = "COM3", UsbId = "1A86:7523", Greets = false },
            new FakeDevice { Port = "COM9", UsbId = "1234:5678" });
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM9"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM9", dev.PortName);
            Assert.Equal(new[] { "COM9" }, env.Opened);
        }
    }

    [Fact]
    public void DeviceMovesFromCom3ToCom5_IsPickedUpAutomatically()
    {
        var (dev, env) = Create(new FakeDevice { Port = "COM3" });
        using (dev)
        {
            var found = new List<string>();
            dev.DeviceFound += i => { lock (found) found.Add(i.PortName); };
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM3", dev.PortName);
            Assert.True(StreamUntil(dev, () => env.Get("COM3").WriteCount >= 3));

            // Unplug, re-plug into another USB port → new COM number.
            env.Remove("COM3");
            env.Add(new FakeDevice { Port = "COM5" });
            dev.Rescan();

            Assert.True(StreamUntil(dev, () => dev.Status == DeviceStatus.Connected && dev.PortName == "COM5"));
            Assert.True(StreamUntil(dev, () => env.Get("COM5").WriteCount >= 3));
            lock (found) Assert.Equal(new[] { "COM3", "COM5" }, found);
            Assert.Equal("COM5", dev.RememberedPort);
        }
    }

    [Fact]
    public void PortRemovedWhileIdle_IsDetectedWithoutWrites()
    {
        var (dev, env) = Create(new FakeDevice { Port = "COM3" });
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            env.Remove("COM3");
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Searching, 1000));
            Assert.True(env.Get2("COM3") is null);
            Assert.False(dev.WaitForSlot(100));
        }
    }

    [Fact]
    public void WriteFailureMidStream_ClosesAndReconnects()
    {
        var com3 = new FakeDevice { Port = "COM3", FailAtWrite = 5 };
        var (dev, env) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.True(StreamUntil(dev, () => com3.OpenCount == 2 && dev.Status == DeviceStatus.Connected && com3.WriteCount >= 10));
            Assert.True(com3.Links[0].Disposed);
            Assert.False(com3.Links[1].Disposed);
        }
        Assert.True(com3.Links[1].Disposed);
    }

    [Fact]
    public void FixedPort_Busy_ReportsPortBusy_ThenConnectsWhenFreed()
    {
        var com3 = new FakeDevice { Port = "COM3", Busy = true };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Fixed("COM3"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.PortBusy));
            Assert.Equal("COM3", dev.PortName);
            Assert.False(dev.WaitForSlot(50));

            com3.Busy = false;
            dev.Rescan();
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM3", dev.PortName);
        }
    }

    [Fact]
    public void Auto_PreferredPortBusy_ReportsPortBusy()
    {
        var com3 = new FakeDevice { Port = "COM3", Busy = true };
        var (dev, env) = Create(com3, new FakeDevice { Port = "COM1", UsbId = null, Greets = false });
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM3"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.PortBusy));
            Assert.Equal("COM3", dev.PortName);

            com3.Busy = false; // AmbiBox closed
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM3", dev.PortName);
        }
    }

    [Fact]
    public void FixedPort_WithoutGreeting_IsAcceptedAfterTimeout()
    {
        var com7 = new FakeDevice { Port = "COM7", Greets = false };
        var (dev, _) = Create(com7);
        using (dev)
        {
            var sw = Stopwatch.StartNew();
            dev.Configure(Fixed("com7"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);
            Assert.Equal("COM7", dev.PortName);
        }
    }

    [Fact]
    public void FixedPort_Absent_WaitsForItToAppear()
    {
        var (dev, env) = Create();
        using (dev)
        {
            dev.Configure(Fixed("COM8"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Searching && dev.PortName == "COM8"));
            Thread.Sleep(100);
            Assert.Empty(env.Opened);
            env.Add(new FakeDevice { Port = "COM8" });
            dev.Rescan();
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        }
    }

    [Fact]
    public void Auto_SilentUnknownPort_IsNotAccepted()
    {
        var (dev, env) = Create(new FakeDevice { Port = "COM3", Greets = false });
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => env.Opened.Length >= 1));
            Thread.Sleep(500); // probe timeout 300 ms
            Assert.Equal(DeviceStatus.Searching, dev.Status);
        }
    }

    [Fact]
    public void Auto_SilentRememberedDevice_IsAccepted()
    {
        var (dev, _) = Create(new FakeDevice { Port = "COM6", UsbId = "1A86:7523", Greets = false });
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal("COM6", dev.PortName);
        }
    }

    [Fact]
    public void Auto_GarbageFromRememberedDevice_IsRejected()
    {
        var (dev, _) = Create(new FakeDevice { Port = "COM3", Greeting = "\u0012ðgarbage"u8.ToArray() });
        using (dev)
        {
            dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 10);
            Thread.Sleep(700);
            Assert.NotEqual(DeviceStatus.Connected, dev.Status);
        }
    }

    [Fact]
    public void Auto_FailedPortIsRetriedWithBackoff_NotHammered()
    {
        var com3 = new FakeDevice { Port = "COM3", UsbId = "0403:6001", Greets = false }; // known chip: retried (unknown ports: once)
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Thread.Sleep(1500);
            // probe 300 ms, retries after 300, 600, 1000 ms → at most ~3 opens in 1.5 s (rescans every 50 ms would be ~5+)
            Assert.InRange(com3.OpenCount, 1, 3);
        }
    }

    [Fact]
    public void WaitForSlot_ReturnsFalseImmediately_WhenNotConnected()
    {
        var (dev, _) = Create();
        using (dev)
        {
            var sw = Stopwatch.StartNew();
            Assert.False(dev.WaitForSlot(500));
            dev.Configure(Auto(), 10);
            Assert.False(dev.WaitForSlot(500));
            Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void FramesOnTheWire_HaveHeaderChecksumAndColorOrder()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(order: ColorOrder.GRB), 2);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.True(dev.WaitForSlot(100));
            dev.Submit(new byte[] { 10, 20, 30, 40, 50, 60 });
            Assert.True(Wait.Until(() => com3.Links[0].Writes.Length == 1));
            var w = com3.Links[0].Writes[0].Data;
            Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 1, 1 ^ 0x55, 20, 10, 30, 50, 40, 60 }, w);
        }
    }

    [Fact]
    public void Configure_LedCountAndColorOrder_ApplyWithoutReconnect_PortChangeReconnects()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var com4 = new FakeDevice { Port = "COM4" };
        var (dev, _) = Create(com3, com4);
        using (dev)
        {
            dev.Configure(Fixed("COM3"), 2);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            dev.Configure(new DeviceSettings { Port = "COM3", ColorOrder = ColorOrder.BGR }, 3);
            Assert.True(dev.WaitForSlot(200));
            dev.Submit(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            Assert.True(Wait.Until(() => com3.Links[0].Writes.Length >= 1));
            Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 2, 2 ^ 0x55, 3, 2, 1, 6, 5, 4, 9, 8, 7 }, com3.Links[0].Writes[^1].Data);
            Assert.Equal(1, com3.OpenCount);
            Assert.Equal(6 + 3 * 3, dev.Format.FrameBytes);

            dev.Configure(new DeviceSettings { Port = "COM4" }, 3);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected && dev.PortName == "COM4"));
            Assert.True(com3.Links[0].Disposed);
            // the old device was blacked out before switching
            Assert.All(com3.Links[0].Writes[^1].Data[6..], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void Pacing_NeverExceedsLinkRate_EvenWhenSubmittingFlatOut()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 112);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            var fmt = dev.Format;
            double interval = fmt.WireSeconds * 1.03;

            // Flat out, ignoring WaitForSlot: latest-wins mailbox must drop, not queue.
            var frame = Rgb(112, 1);
            var sw = Stopwatch.StartNew();
            int submitted = 0;
            while (sw.ElapsedMilliseconds < 1000) { frame[0] = (byte)submitted; dev.Submit(frame); submitted++; }
            Thread.Sleep(100);
            var writes = com3.Links[0].Writes;
            Assert.True(submitted > writes.Length * 10);
            Assert.InRange(writes.Length, 25, (int)Math.Ceiling(1.1 / interval) + 1);
            for (int i = 1; i < writes.Length; i++)
            {
                double gap = (writes[i].Timestamp - writes[i - 1].Timestamp) / (double)Stopwatch.Frequency;
                Assert.True(gap >= fmt.WireSeconds * 1.03 - 0.0005, $"gap {gap * 1000:F3} ms < {interval * 1000:F3} ms");
            }
            // The last frame written is the last one submitted (latest wins).
            Assert.Equal((byte)(submitted - 1), writes[^1].Data[6]);
        }
    }

    [Fact]
    public void WaitForSlot_PacesCallerToLinkRate()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 112);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            var frame = Rgb(112, 1);
            int slots = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                if (dev.WaitForSlot(100)) { dev.Submit(frame); slots++; }
            }
            double expected = 1.0 / (dev.Format.WireSeconds * 1.03); // ~32.7
            Assert.InRange(slots, expected * 0.85, expected * 1.05 + 1);
            Thread.Sleep(50);
            Assert.InRange(com3.Links[0].Writes.Length, slots - 1, slots);
            Assert.InRange(dev.FramesPerSecond, expected * 0.8, expected * 1.05);
            Assert.Equal(115200 / 3420.0, dev.MaxFrameRate, 3);
        }
    }

    [Fact]
    public void SubmitAndWaitForSlot_DoNotAllocate()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 112);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            var frame = Rgb(112, 3);
            for (int i = 0; i < 5; i++) { dev.WaitForSlot(100); dev.Submit(frame); } // warm-up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) { dev.WaitForSlot(100); dev.Submit(frame); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public void Blackout_SendsBlackFrame_ClosesPort_Fast_AndConfigureResumes()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        using (dev)
        {
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.True(StreamUntil(dev, () => com3.WriteCount >= 3, value: 50));

            var sw = Stopwatch.StartNew();
            dev.Blackout();
            Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
            Assert.Equal(DeviceStatus.Stopped, dev.Status);
            Assert.True(com3.Links[0].Disposed);
            var last = com3.Links[0].Writes[^1].Data;
            Assert.Equal(6 + 30, last.Length);
            Assert.All(last[6..], b => Assert.Equal(0, b));

            // Stays off (no reconnect) until configured again.
            dev.Rescan();
            Thread.Sleep(200);
            Assert.Equal(DeviceStatus.Stopped, dev.Status);
            Assert.Equal(1, com3.OpenCount);

            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
            Assert.Equal(2, com3.OpenCount);
        }
    }

    [Fact]
    public void Dispose_SendsBlack_AndClosesPort()
    {
        var com3 = new FakeDevice { Port = "COM3" };
        var (dev, _) = Create(com3);
        dev.Configure(Auto(), 10);
        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.True(StreamUntil(dev, () => com3.WriteCount >= 3, value: 50));
        var sw = Stopwatch.StartNew();
        dev.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 300, $"{sw.ElapsedMilliseconds} ms");
        Assert.True(com3.Links[0].Disposed);
        Assert.All(com3.Links[0].Writes[^1].Data[6..], b => Assert.Equal(0, b));
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        dev.Dispose(); // idempotent
    }

    [Fact]
    public void Dispose_WhileProbing_ReturnsQuickly()
    {
        var (dev, env) = Create(new FakeDevice { Port = "COM3", Greets = false });
        dev.Configure(Auto(), 10);
        Assert.True(Wait.Until(() => env.Opened.Length == 1));
        var sw = Stopwatch.StartNew();
        dev.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
        Assert.True(env.Get("COM3").Links[0].Disposed);
    }

    [Fact]
    public void ThrowingEventHandlers_DoNotBreakTheDevice()
    {
        var (dev, _) = Create(new FakeDevice { Port = "COM3" });
        using (dev)
        {
            dev.StatusChanged += () => throw new InvalidOperationException("ui bug");
            dev.DeviceFound += _ => throw new InvalidOperationException("ui bug");
            dev.Configure(Auto(), 10);
            Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        }
    }
}

internal static class FakeEnvironmentExtensions
{
    public static FakeDevice? Get2(this FakeEnvironment env, string port)
        => env.GetPresentPortNames().Contains(port) ? env.Get(port) : null;
}
