using System.Diagnostics;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using Xunit.Abstractions;

namespace RambiBox.Tests.Devices;

/// <summary>
/// Runs only when an Adalight-capable USB-serial chip (CH340 1A86:7523) is plugged in and RAMBIBOX_NO_HW is not set.
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RAMBIBOX_NO_HW") is { Length: > 0 })
            Skip = "RAMBIBOX_NO_HW is set";
        else if (AdalightHardwareTests.FindPort() is null)
            Skip = $"No {AdalightHardwareTests.UsbId} serial port present";
    }
}

[Trait("Category", "Hardware")]
[Collection("Hardware")] // never run hardware tests in parallel with each other
public class AdalightHardwareTests
{
    public const string UsbId = "1A86:7523";
    private const int Leds = 112;
    private readonly ITestOutputHelper _out;

    public AdalightHardwareTests(ITestOutputHelper output) => _out = output;

    internal static SerialPortInfo? FindPort()
        => new Win32PortEnumerator().ListPorts().FirstOrDefault(p => p.UsbId == UsbId);

    private static bool WaitConnected(AdalightDevice dev, int timeoutMs) => Wait.Until(() => dev.Status == DeviceStatus.Connected, timeoutMs);

    /// <summary>Dim moving pattern, all channels ≤ 80.</summary>
    private static void Pattern(byte[] rgb, int frame)
    {
        Array.Clear(rgb);
        for (int i = 0; i < Leds; i++)
        {
            rgb[3 * i + 2] = 6; // faint blue background
        }
        int p = frame % Leds;
        rgb[3 * p] = 80;
        rgb[3 * ((p + 1) % Leds) + 1] = 60;
    }

    [HardwareFact]
    public void ListPorts_ReportsCh340WithUsbId()
    {
        var sw = Stopwatch.StartNew();
        var ports = new AdalightDevice().ListPorts();
        double ms = sw.Elapsed.TotalMilliseconds;
        foreach (var p in ports) _out.WriteLine($"{p.PortName}: {p.Description} [{p.UsbId ?? "-"}]{(p.IsBluetooth ? " (Bluetooth)" : "")}");
        _out.WriteLine($"ListPorts: {ms:F2} ms");
        var ch = Assert.Single(ports, p => p.UsbId == UsbId);
        Assert.Contains("CH340", ch.Description);
        Assert.False(ch.IsBluetooth);
    }

    [HardwareFact]
    public void AutoDetect_FindsTheController()
    {
        var expected = FindPort()!;
        using var dev = new AdalightDevice();
        SerialPortInfo? found = null;
        dev.DeviceFound += i => found = i;
        var sw = Stopwatch.StartNew();
        dev.Configure(new DeviceSettings { Port = DeviceSettings.AutoPort }, Leds);
        Assert.True(WaitConnected(dev, 15000), $"status {dev.Status} {dev.PortName}");
        _out.WriteLine($"Auto-detect connected on {dev.PortName} after {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(found);
        Assert.Equal(UsbId, found!.UsbId);
        Assert.Equal(expected.PortName, found.PortName);
        Assert.Equal(expected.PortName, dev.PortName);

        // Remembered port → connects quickly the next time.
        dev.Dispose();
        using var dev2 = new AdalightDevice();
        sw.Restart();
        dev2.Configure(new DeviceSettings { Port = DeviceSettings.AutoPort, LastPort = found.PortName, LastUsbId = found.UsbId }, Leds);
        Assert.True(WaitConnected(dev2, 5000));
        _out.WriteLine($"Auto-detect with remembered port, immediately after closing: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 1400, "greeting missed?");
    }

    [HardwareFact]
    public void Stream20s_AtMaxRate_ReportsFpsAndLatency()
    {
        var port = FindPort()!.PortName;
        using var dev = new AdalightDevice();
        dev.Configure(new DeviceSettings { Port = port }, Leds);
        Assert.True(WaitConnected(dev, 5000));

        var rgb = new byte[Leds * 3];
        var latencies = new List<double>(800);
        var writeDurations = new List<double>(800);
        long startFrames = dev.FramesWritten;
        var sw = Stopwatch.StartNew();
        int frame = 0, slotTimeouts = 0;
        double fpsSample = 0;
        while (sw.Elapsed.TotalSeconds < 20)
        {
            if (!dev.WaitForSlot(200)) { slotTimeouts++; Assert.Equal(DeviceStatus.Connected, dev.Status); continue; }
            long t0 = Stopwatch.GetTimestamp();
            Pattern(rgb, frame++);
            long before = dev.FramesWritten;
            dev.Submit(rgb);
            var spin = Stopwatch.StartNew();
            while (dev.FramesWritten == before && spin.ElapsedMilliseconds < 100) Thread.SpinWait(20);
            if (dev.FramesWritten != before)
            {
                latencies.Add((dev.LastWriteStartTimestamp - t0) * 1000.0 / Stopwatch.Frequency);
                writeDurations.Add(dev.LastWriteDurationTicks * 1000.0 / Stopwatch.Frequency);
            }
            if (sw.Elapsed.TotalSeconds > 10 && fpsSample == 0) fpsSample = dev.FramesPerSecond;
        }
        double seconds = sw.Elapsed.TotalSeconds;
        long frames = dev.FramesWritten - startFrames;
        double fps = frames / seconds;
        latencies.Sort();
        writeDurations.Sort();
        _out.WriteLine($"Port {port}: {frames} frames in {seconds:F2} s = {fps:F2} fps (FramesPerSecond property {fpsSample:F2}, theoretical max {dev.MaxFrameRate:F2})");
        _out.WriteLine($"WaitForSlot→write start latency: avg {latencies.Average():F3} ms, p50 {latencies[latencies.Count / 2]:F3}, p99 {latencies[(int)(latencies.Count * 0.99)]:F3}, max {latencies[^1]:F3} ms");
        _out.WriteLine($"WriteFile call duration: avg {writeDurations.Average():F3} ms, p50 {writeDurations[writeDurations.Count / 2]:F3}, max {writeDurations[^1]:F3} ms");
        _out.WriteLine($"slot timeouts: {slotTimeouts}");

        Assert.InRange(fps, 30, 34.5);
        Assert.Equal(0, slotTimeouts);
        Assert.True(latencies[latencies.Count / 2] < 1.0, "median WaitForSlot→write latency should be well below 1 ms");

        var bsw = Stopwatch.StartNew();
        dev.Blackout();
        _out.WriteLine($"Blackout: {bsw.ElapsedMilliseconds} ms");
        Assert.True(bsw.ElapsedMilliseconds <= 250);
    }

    [HardwareFact]
    public void OpenCloseReconnect_FiveCycles_AreStable()
    {
        var port = FindPort()!.PortName;
        var rgb = new byte[Leds * 3];
        for (int cycle = 0; cycle < 5; cycle++)
        {
            var settings = cycle % 2 == 0
                ? new DeviceSettings { Port = port }
                : new DeviceSettings { Port = DeviceSettings.AutoPort, LastPort = port, LastUsbId = UsbId };
            using var dev = new AdalightDevice();
            var sw = Stopwatch.StartNew();
            dev.Configure(settings, Leds);
            Assert.True(WaitConnected(dev, 5000), $"cycle {cycle}: {dev.Status}");
            long connectMs = sw.ElapsedMilliseconds;

            long f0 = dev.FramesWritten;
            sw.Restart();
            int n = 0;
            while (sw.ElapsedMilliseconds < 1000)
            {
                if (dev.WaitForSlot(200)) { Pattern(rgb, n++ * 3); dev.Submit(rgb); }
            }
            double fps = (dev.FramesWritten - f0) / sw.Elapsed.TotalSeconds;

            // In-place reconnect (port setting changes) on the same instance.
            sw.Restart();
            dev.Configure(cycle % 2 == 0 ? new DeviceSettings { Port = DeviceSettings.AutoPort, LastPort = port, LastUsbId = UsbId } : new DeviceSettings { Port = port }, Leds);
            Assert.True(Wait.Until(() => dev.Status != DeviceStatus.Connected, 1000));
            Assert.True(WaitConnected(dev, 5000), $"cycle {cycle} reconnect: {dev.Status}");
            long reconnectMs = sw.ElapsedMilliseconds;

            sw.Restart();
            dev.Blackout();
            long blackoutMs = sw.ElapsedMilliseconds;
            Assert.Equal(DeviceStatus.Stopped, dev.Status);
            _out.WriteLine($"cycle {cycle} ({(cycle % 2 == 0 ? "fixed" : "auto")}): connect {connectMs} ms, {fps:F1} fps, reconnect {reconnectMs} ms, blackout {blackoutMs} ms");
            Assert.InRange(fps, 25, 35);
            Assert.True(blackoutMs <= 250, $"blackout {blackoutMs} ms");
            // Re-opening right after the previous close: the device waits until 1 s after the previous open
            // (otherwise the DTR edge doesn't reset the board) and then gets the greeting ~60 ms later.
            // Taking ProbeTimeout/FixedGreetingTimeout instead would mean the greeting was missed.
            Assert.True(connectMs < 1400, $"connect {connectMs} ms - greeting missed?");
            Assert.True(reconnectMs < 1400, $"reconnect {reconnectMs} ms - greeting missed?");
        }
    }
}
