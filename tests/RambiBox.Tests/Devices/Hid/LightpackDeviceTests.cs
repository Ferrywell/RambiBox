using System.Diagnostics;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Devices.Hid;

namespace RambiBox.Tests.Devices.Hid;

public class LightpackDeviceTests
{
    private static readonly LightpackOptions Fast = new()
    {
        RescanIntervalMs = 20,
        ConnectedCheckIntervalMs = 50,
        WriteTimeoutMs = 50,
        MaxFrameRate = 500,
        MaxReportsPerSecond = 5000,
    };

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("condition not met in time");
            Thread.Sleep(5);
        }
    }

    private static byte[] Frame(int leds, int seed)
    {
        var f = new byte[leds * 3];
        for (int i = 0; i < f.Length; i++) f[i] = (byte)(seed + i * 7);
        return f;
    }

    private static byte[] Expected(ReadOnlySpan<byte> rgb, ColorOrder order = ColorOrder.RGB)
    {
        var r = new byte[LightpackProtocol.ReportLength];
        LightpackProtocol.BuildUpdateLeds(rgb, order, r);
        return r;
    }

    private static void SubmitUntilWritten(LightpackDevice dev, byte[] frame)
    {
        long before = dev.FramesWritten;
        dev.Submit(frame);
        WaitUntil(() => dev.FramesWritten > before);
    }

    [Fact]
    public void Connects_sends_prismatik_options_with_firmware_smoothing_off_then_frames()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast);
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal("Lightpack A", dev.PortName);

        var w = bus.Writes(a);
        Assert.Equal(new byte[] { 0, LightpackProtocol.CmdSetTimerOptions, 100, 0 }, w[0][..4]);
        Assert.Equal(new byte[] { 0, LightpackProtocol.CmdSetPwmLevelMaxValue, 128 }, w[1][..3]);
        Assert.Equal(new byte[] { 0, LightpackProtocol.CmdSetSmoothSlowdown, 0 }, w[2][..3]);

        var frame = Frame(10, 3);
        SubmitUntilWritten(dev, frame);
        Assert.Equal(Expected(frame), bus.Writes(a)[^1]);
        Assert.Equal(500, dev.MaxFrameRate);
    }

    [Fact]
    public void Several_lightpacks_are_chained_by_serial_with_ten_leds_each()
    {
        var bus = new FakeHidTransport();
        var b = FakeHidTransport.Lightpack("SERIAL-B");
        var a = FakeHidTransport.Lightpack("SERIAL-A");
        bus.Plug(b); // enumeration order B, A: Prismatik sorts by serial -> A first
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast with { MaxReportsPerSecond = 600 });
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack, ColorOrder = ColorOrder.BGR }, 15);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal("Lightpack (2 devices)", dev.PortName);
        Assert.Equal([a.Path, b.Path], dev.ConnectedPaths);
        Assert.Equal(300, dev.MaxFrameRate); // min(500, 600 / 2)

        var frame = Frame(15, 11);
        SubmitUntilWritten(dev, frame);
        Assert.Equal(Expected(frame.AsSpan(0, 30), ColorOrder.BGR), bus.Writes(a)[^1]);
        Assert.Equal(Expected(frame.AsSpan(30, 15), ColorOrder.BGR), bus.Writes(b)[^1]); // LEDs 10..14, rest black
    }

    [Fact]
    public void Reconnects_after_unplug_and_replug()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A", path: "path-1");
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast);
        var statuses = new List<DeviceStatus>();
        dev.StatusChanged += () => { lock (statuses) statuses.Add(dev.Status); };
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);

        bus.Unplug(a);
        dev.Submit(Frame(10, 1)); // write fails -> searching
        WaitUntil(() => dev.Status == DeviceStatus.Searching);

        var replugged = FakeHidTransport.Lightpack("A", path: "path-2"); // other USB port
        bus.Plug(replugged);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal(["path-2"], dev.ConnectedPaths);
        Assert.Equal(LightpackProtocol.CmdSetSmoothSlowdown, bus.Writes(replugged)[2][1]); // re-initialized
        var frame = Frame(10, 9);
        SubmitUntilWritten(dev, frame);
        Assert.Equal(Expected(frame), bus.Writes(replugged)[^1]);
        lock (statuses) Assert.Equal([DeviceStatus.Searching, DeviceStatus.Connected, DeviceStatus.Searching, DeviceStatus.Connected], statuses);
        Assert.True(bus.Opened.Where(c => c.Device.Path == "path-1").All(c => c.Disposed));
    }

    [Fact]
    public void A_lightpack_added_while_connected_is_picked_up()
    {
        var bus = new FakeHidTransport();
        var b = FakeHidTransport.Lightpack("B");
        bus.Plug(b);
        using var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 20);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Single(dev.ConnectedPaths);

        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        dev.Rescan();
        WaitUntil(() => dev.Status == DeviceStatus.Connected && dev.ConnectedPaths.Count == 2);
        Assert.Equal([a.Path, b.Path], dev.ConnectedPaths);
    }

    [Fact]
    public void Transient_write_error_is_retried_once_like_prismatik()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        bus.Opened[^1].FailNextWrites = 1;
        var frame = Frame(10, 5);
        SubmitUntilWritten(dev, frame);
        Assert.Equal(DeviceStatus.Connected, dev.Status);
        Assert.Single(bus.Opened);
        Assert.Equal(Expected(frame), bus.Writes(a)[^1]);
    }

    [Fact]
    public void Blackout_sends_black_and_off_all_then_resumes_on_configure()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast);
        var s = new DeviceSettings { Type = DeviceType.Lightpack };
        dev.Configure(s, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        SubmitUntilWritten(dev, Frame(10, 77));

        dev.Blackout();
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        var w = bus.Writes(a);
        Assert.Equal(Expected([]), w[^2]); // all-black CMD_UPDATE_LEDS
        Assert.Equal(LightpackProtocol.CmdOffAll, w[^1][1]);
        Assert.True(bus.Opened.All(c => c.Disposed));

        dev.Submit(Frame(10, 1)); // ignored while stopped
        Thread.Sleep(50);
        Assert.Equal(w.Count, bus.Writes(a).Count);

        dev.Configure(s, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
    }

    [Fact]
    public void Dispose_blacks_out_and_closes()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        dev.Dispose();
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        Assert.Equal(LightpackProtocol.CmdOffAll, bus.Writes(a)[^1][1]);
        Assert.True(bus.Opened.All(c => c.Disposed));
        dev.Dispose(); // idempotent
        dev.Submit(Frame(10, 1));
        dev.Configure(new DeviceSettings(), 10);
    }

    [Fact]
    public void Device_id_selects_one_lightpack_and_changing_it_reconnects()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        var b = FakeHidTransport.Lightpack("B");
        bus.Plug(a);
        bus.Plug(b);
        using var dev = new LightpackDevice(bus, Fast);
        var s = new DeviceSettings { Type = DeviceType.Lightpack, DeviceId = "B" };
        dev.Configure(s, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal([b.Path], dev.ConnectedPaths);
        Assert.Empty(bus.Writes(a));

        s.DeviceId = "A";
        dev.Configure(s, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected && dev.ConnectedPaths.SequenceEqual([a.Path]));
        Assert.Equal(LightpackProtocol.CmdOffAll, bus.Writes(b)[^1][1]); // the old one was blacked out
    }

    [Fact]
    public void Explicit_list_keeps_led_positions_when_a_device_is_absent()
    {
        var bus = new FakeHidTransport();
        var b = FakeHidTransport.Lightpack("B");
        bus.Plug(b);
        using var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack, DeviceId = "A,B" }, 20);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.Equal([null, b.Path], dev.ConnectedPaths);
        var frame = Frame(20, 40);
        SubmitUntilWritten(dev, frame);
        Assert.Equal(Expected(frame.AsSpan(30, 30)), bus.Writes(b)[^1]); // B still shows LEDs 10..19

        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        WaitUntil(() => dev.Status == DeviceStatus.Connected && dev.ConnectedPaths.SequenceEqual([a.Path, b.Path]));
    }

    [Fact]
    public void Searches_without_device_and_reports_busy_device()
    {
        var bus = new FakeHidTransport();
        using var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Searching && bus.EnumerateCalls >= 3);
        Assert.False(dev.WaitForSlot(10));
        Assert.Equal(0, dev.FramesPerSecond);

        bus.OpenFailError = 32; // ERROR_SHARING_VIOLATION
        bus.Plug(FakeHidTransport.Lightpack("A"));
        WaitUntil(() => dev.Status == DeviceStatus.PortBusy);
        bus.OpenFailError = 0;
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        Assert.True(dev.WaitForSlot(1000));
    }

    [Fact]
    public void Non_lightpack_and_short_report_devices_are_ignored()
    {
        var bus = new FakeHidTransport();
        bus.Plug(new HidDeviceInfo(@"\\?\hid#vid_046d&pid_c52b#x", 0x046D, 0xC52B, OutputReportLength: 65));
        bus.Plug(FakeHidTransport.Lightpack("demo", old: true, outLen: 9)); // a LUFA GenericHID demo board
        using var dev = new LightpackDevice(bus, Fast);
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => bus.EnumerateCalls >= 3);
        Assert.Equal(DeviceStatus.Searching, dev.Status);
        Assert.Empty(bus.Opened);
    }

    [Fact]
    public void Latest_frame_wins_and_pacing_limits_the_rate()
    {
        var bus = new FakeHidTransport();
        var a = FakeHidTransport.Lightpack("A");
        bus.Plug(a);
        using var dev = new LightpackDevice(bus, Fast with { MaxFrameRate = 20 });
        dev.Configure(new DeviceSettings { Type = DeviceType.Lightpack }, 10);
        WaitUntil(() => dev.Status == DeviceStatus.Connected);
        SubmitUntilWritten(dev, Frame(10, 1));
        long before = dev.FramesWritten;
        for (int i = 0; i < 5; i++) dev.Submit(Frame(10, 100 + i)); // within one 50 ms slot
        var last = Frame(10, 104);
        WaitUntil(() => bus.Writes(a)[^1].AsSpan().SequenceEqual(Expected(last)));
        Thread.Sleep(120);
        Assert.Equal(before + 1, dev.FramesWritten);
    }

    [Fact]
    public void Factory_creates_lightpack_and_alienfx_drivers()
    {
        Assert.True(LedDeviceFactory.IsSupported(DeviceType.Lightpack));
        Assert.True(LedDeviceFactory.IsSupported(DeviceType.AlienFx));
        using var d = LedDeviceFactory.Create(DeviceType.Lightpack);
        Assert.IsType<LightpackDevice>(d);
        Assert.Contains(LedDeviceFactory.DiscoveryServices, x => x is LightpackDiscovery);
    }
}
