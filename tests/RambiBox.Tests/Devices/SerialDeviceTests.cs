using System.Diagnostics;
using System.Text;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>Ardulight / AWA / TPM2 drivers on the shared serial base (fake serial layer).</summary>
public class SerialDeviceTests
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

    private static FakeEnvironment Env(params FakeDevice[] devices)
    {
        var env = new FakeEnvironment();
        foreach (var d in devices) env.Add(d);
        return env;
    }

    /// <summary>A silent board (Ardulight / TPM2 firmware never says anything).</summary>
    private static FakeDevice Silent(string port, string usbId = "1A86:7523") => new() { Port = port, UsbId = usbId, Greets = false };

    /// <summary>HyperSerial firmware: answers the AWA hello at 2 Mbaud only.</summary>
    private static FakeDevice AwaBoard(string port, string usbId = "10C4:EA60") => new()
    {
        Port = port,
        UsbId = usbId,
        Description = "Silicon Labs CP210x USB to UART Bridge",
        Greets = false,
        Reply = (data, baud) => baud == 2_000_000 && data.AsSpan().SequenceEqual(AwaProtocol.HelloRequest)
            ? Encoding.ASCII.GetBytes("\r\nWelcome!\r\nAwa driver 11.\r\n")
            : null,
    };

    private static DeviceSettings Auto(string? lastPort = null, string? lastUsbId = null, int baud = 115200)
        => new() { Port = DeviceSettings.AutoPort, LastPort = lastPort, LastUsbId = lastUsbId, BaudRate = baud };

    private static bool StreamUntil(ILedDevice dev, Func<bool> done, int leds = 4, byte value = 7, int timeoutMs = 3000)
    {
        var frame = Enumerable.Repeat(value, leds * 3).ToArray();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            if (dev.WaitForSlot(20)) dev.Submit(frame);
            else Thread.Sleep(2);
        }
        return done();
    }

    private static byte[][] DataWrites(FakeEnvironment env, string port)
        => env.Get(port).Links.SelectMany(l => l.Writes).Select(w => w.Data).ToArray();

    [Fact]
    public void Factory_CreatesTheSerialDrivers()
    {
        // Creating a driver opens nothing (the device thread starts on the first Configure).
        using var a = LedDeviceFactory.Create(DeviceType.Adalight);
        using var b = LedDeviceFactory.Create(DeviceType.Ardulight);
        using var c = LedDeviceFactory.Create(DeviceType.Awa);
        using var d = LedDeviceFactory.Create(DeviceType.Tpm2);
        Assert.IsType<AdalightDevice>(a);
        Assert.IsType<ArdulightDevice>(b);
        Assert.IsType<AwaDevice>(c);
        Assert.IsType<Tpm2Device>(d);
        Assert.Contains(LedDeviceFactory.DiscoveryServices, d => d is SerialDeviceDiscovery);
    }

    // ---------------- protocols without greeting (Ardulight, TPM2)

    [Fact]
    public void Ardulight_Auto_NothingRemembered_OpensNoPortAtAll()
    {
        var env = Env(Silent("COM3"), Silent("COM4", "0403:6001"), new FakeDevice { Port = "COM7", UsbId = "1234:0001" });
        using var dev = new ArdulightDevice(env, env, Fast);
        dev.Configure(Auto(), 4);
        Thread.Sleep(400);
        Assert.Empty(env.Opened); // a silent firmware can't be recognised: don't reset anything
        Assert.Equal(DeviceStatus.Searching, dev.Status);
    }

    [Fact]
    public void Ardulight_Auto_RememberedUsbId_ReconnectsAfterReplugToAnotherPort()
    {
        var env = Env(Silent("COM3"), Silent("COM9", "0403:6001"));
        using var dev = new ArdulightDevice(env, env, Fast);
        var found = new List<string>();
        dev.DeviceFound += i => { lock (found) found.Add(i.PortName); };
        dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 4);

        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal("COM3", dev.PortName);
        Assert.True(StreamUntil(dev, () => DataWrites(env, "COM3").Length >= 2));
        Assert.All(DataWrites(env, "COM3"), w => Assert.Equal(new byte[] { 0xFF, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 }, w));

        // Re-plug into another USB socket: same USB id, new COM number → found again (unambiguous: one such port).
        env.Remove("COM3");
        env.Add(Silent("COM5"));
        dev.Rescan();
        Assert.True(StreamUntil(dev, () => dev.Status == DeviceStatus.Connected && dev.PortName == "COM5"));
        Assert.Equal(0, env.OpenCountOf("COM9")); // other chip, never opened
        lock (found) Assert.Equal(new[] { "COM3", "COM5" }, found);
    }

    [Fact]
    public void Ardulight_Auto_TwoPortsWithRememberedId_OnlyTheRememberedPortIsUsed()
    {
        var env = Env(Silent("COM5"), Silent("COM6"));
        using var dev = new ArdulightDevice(env, env, Fast);
        dev.Configure(Auto(lastPort: "COM8", lastUsbId: "1A86:7523"), 4); // remembered port absent, id ambiguous
        Thread.Sleep(400);
        Assert.Empty(env.Opened);
        Assert.Equal(DeviceStatus.Searching, dev.Status);
    }

    [Fact]
    public void Ardulight_Auto_RememberedDeviceSendingData_IsRejected()
    {
        var env = Env(new FakeDevice { Port = "COM3", Greets = true, Greeting = "garbage"u8.ToArray() });
        using var dev = new ArdulightDevice(env, env, Fast);
        dev.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 4);
        Assert.True(Wait.Until(() => env.OpenCountOf("COM3") >= 1));
        Thread.Sleep(200);
        Assert.NotEqual(DeviceStatus.Connected, dev.Status);
    }

    [Fact]
    public void Tpm2_FixedPort_SendsFrames_AndRemembersTheUsbId()
    {
        var env = Env(Silent("COM4", "10C4:EA60"));
        using var dev = new Tpm2Device(env, env, Fast);
        SerialPortInfo? found = null;
        dev.DeviceFound += i => found = i;
        dev.Configure(new DeviceSettings { Port = "COM4", BaudRate = 500000, ColorOrder = ColorOrder.GRB }, 2);

        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal(500000, env.Get("COM4").Links[0].Baud);
        Assert.NotNull(found);
        Assert.Equal("10C4:EA60", found!.UsbId);

        var sw = Stopwatch.StartNew();
        while (DataWrites(env, "COM4").Length < 2 && sw.ElapsedMilliseconds < 3000)
        {
            if (dev.WaitForSlot(20)) dev.Submit([1, 2, 3, 4, 5, 6]);
        }
        Assert.Equal(new byte[] { 0xC9, 0xDA, 0x00, 0x06, 2, 1, 3, 5, 4, 6, 0x36 }, DataWrites(env, "COM4")[0]);
        Assert.Equal(11 * 10.0 / 500000, dev.Format.WireSeconds, 12);
        Assert.Equal(500000 / 110.0, dev.MaxFrameRate, 6);
    }

    [Fact]
    public void Tpm2_Blackout_SendsBlackFrame()
    {
        var env = Env(Silent("COM4"));
        using var dev = new Tpm2Device(env, env, Fast);
        dev.Configure(new DeviceSettings { Port = "COM4" }, 2);
        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.True(StreamUntil(dev, () => DataWrites(env, "COM4").Length >= 1, leds: 2));
        dev.Blackout();
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        Assert.Equal(new byte[] { 0xC9, 0xDA, 0x00, 0x06, 0, 0, 0, 0, 0, 0, 0x36 }, DataWrites(env, "COM4")[^1]);
    }

    // ---------------- AWA (greets on request)

    [Fact]
    public void Awa_Auto_FindsBoardByHello_At2Mbaud()
    {
        var env = Env(Silent("COM3"), AwaBoard("COM6"),
            new FakeDevice { Port = "COM8", Description = "Bluetooth link", UsbId = null, IsBluetooth = true });
        using var dev = new AwaDevice(env, env, Fast);
        SerialPortInfo? found = null;
        dev.DeviceFound += i => found = i;
        dev.Configure(Auto(baud: 115200), 2); // the generic default maps to the firmware rate

        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal("COM6", dev.PortName);
        Assert.Equal("COM6", found?.PortName);
        Assert.Equal(2_000_000, dev.BaudRate);
        Assert.Equal(2_000_000, env.Get("COM6").Links[^1].Baud);
        Assert.DoesNotContain("COM8", env.Opened);

        var sw = Stopwatch.StartNew();
        while (DataWrites(env, "COM6").Count(w => w[0] == 0x41 && w[2] == 0x61) < 2 && sw.ElapsedMilliseconds < 3000)
        {
            if (dev.WaitForSlot(20)) dev.Submit([0x10, 0x20, 0x30, 0x40, 0x50, 0x60]);
        }
        var writes = DataWrites(env, "COM6");
        Assert.Equal(AwaProtocol.HelloRequest, writes[0]);
        Assert.Contains(writes, w => w.AsSpan().SequenceEqual(new byte[]
            { 0x41, 0x77, 0x61, 0x00, 0x01, 0x54, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x51, 0x83, 0x60 }));
        Assert.Equal(1.0 / (15 * 10.0 / 2_000_000), dev.MaxFrameRate, 3);
    }

    [Fact]
    public void Awa_Auto_SilentKnownChip_IsNotAccepted_ButRememberedSilentOneIs()
    {
        var env = Env(Silent("COM3"));
        using (var dev = new AwaDevice(env, env, Fast))
        {
            dev.Configure(Auto(), 2);
            Assert.True(Wait.Until(() => env.OpenCountOf("COM3") >= 1));
            Thread.Sleep(400);
            Assert.NotEqual(DeviceStatus.Connected, dev.Status);
        }

        // Same board, but remembered (e.g. an older firmware without hello support): accepted when silent.
        var env2 = Env(Silent("COM3"));
        using var dev2 = new AwaDevice(env2, env2, Fast);
        dev2.Configure(Auto(lastPort: "COM3", lastUsbId: "1A86:7523"), 2);
        Assert.True(Wait.Until(() => dev2.Status == DeviceStatus.Connected));
    }

    [Fact]
    public void Awa_ExplicitBaudRate_IsKept()
    {
        var env = Env(Silent("COM4"));
        using var dev = new AwaDevice(env, env, Fast);
        dev.Configure(new DeviceSettings { Port = "COM4", BaudRate = 921600 }, 2);
        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal(921600, env.Get("COM4").Links[0].Baud);
    }

    [Fact]
    public void Adalight_FixedPort_StillDoesNotRaiseDeviceFound()
    {
        var env = Env(new FakeDevice { Port = "COM4" });
        using var dev = new AdalightDevice(env, env, Fast);
        int found = 0;
        dev.DeviceFound += _ => Interlocked.Increment(ref found);
        dev.Configure(new DeviceSettings { Port = "COM4" }, 2);
        Assert.True(Wait.Until(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal(0, found);
        Assert.Equal("Adalight", dev.ProtocolName);
    }
}
