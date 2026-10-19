using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Devices.Network;

namespace RambiBox.Tests.Devices.Network;

/// <summary>Loopback UDP receiver standing in for an LED controller.</summary>
internal sealed class UdpReceiver : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public byte[]? Receive(int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return _udp.ReceiveAsync(cts.Token).AsTask().GetAwaiter().GetResult().Buffer; }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Everything that arrives within <paramref name="ms"/>.</summary>
    public List<byte[]> Drain(int ms)
    {
        var list = new List<byte[]>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var p = Receive((int)Math.Max(1, ms - sw.ElapsedMilliseconds));
            if (p is null) break;
            list.Add(p);
        }
        return list;
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>Emulated WLED JSON API on loopback (GET /json/info), switchable between answering and refusing.</summary>
internal sealed class FakeWled : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public volatile bool Answer = true;
    public volatile string Json;
    public int Requests;

    public FakeWled(int ledCount, int udpPort)
    {
        Json = $$"""{"ver":"0.15.0","leds":{"count":{{ledCount}}},"name":"Fake WLED","udpport":{{udpPort}},"arch":"esp32","mac":"0123456789ab"}""";
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                Interlocked.Increment(ref Requests);
                if (!Answer) return; // close without a response
                var s = c.GetStream();
                var buf = new byte[4096];
                var req = new StringBuilder();
                while (!req.ToString().Contains("\r\n\r\n"))
                {
                    int n = await s.ReadAsync(buf);
                    if (n == 0) return;
                    req.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                bool ok = req.ToString().StartsWith("GET /json/info ", StringComparison.Ordinal);
                var body = Encoding.UTF8.GetBytes(ok ? Json : "not found");
                var head = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                await s.WriteAsync(body);
            }
            catch { /* client went away */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class NetworkDeviceTests
{
    private static bool WaitFor(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    private static byte[] Frame(int leds, byte seed)
    {
        var f = new byte[leds * 3];
        for (int i = 0; i < f.Length; i++) f[i] = (byte)(seed + i);
        return f;
    }

    private static void Send(ILedDevice d, byte[] frame)
    {
        Assert.True(d.WaitForSlot(1000));
        d.Submit(frame);
    }

    [Fact]
    public void Wled_connects_when_json_info_answers_and_uses_the_reported_udp_port()
    {
        using var rx = new UdpReceiver();
        using var wled = new FakeWled(ledCount: 3, udpPort: rx.Port);
        using var dev = new WledDevice { HttpPort = wled.Port, AllowRelocation = false };
        dev.Configure(new DeviceSettings { Type = DeviceType.Wled, Host = "127.0.0.1", WledTimeoutSeconds = 2 }, 3);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));
        Assert.Equal($"127.0.0.1:{rx.Port}", dev.PortName);
        Assert.Equal(3, dev.Info?.LedCount);
        Assert.Equal("Fake WLED", dev.Info?.Name);

        Send(dev, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Assert.Equal(new byte[] { 1, 2, 0, 1, 2, 3, 1, 4, 5, 6, 2, 7, 8, 9 }, rx.Receive());

        dev.Blackout();
        var off = rx.Receive();
        Assert.NotNull(off);
        Assert.Equal(new byte[] { 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0 }, off);
        Assert.Equal(DeviceStatus.Stopped, dev.Status);

        // Configure resumes after a blackout.
        dev.Configure(new DeviceSettings { Type = DeviceType.Wled, Host = "127.0.0.1", WledTimeoutSeconds = 2 }, 3);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));
    }

    [Fact]
    public void Wled_stays_searching_until_the_api_answers_then_drops_back_when_it_stops()
    {
        using var rx = new UdpReceiver();
        using var wled = new FakeWled(ledCount: 600, udpPort: rx.Port) { Answer = false };
        using var dev = new WledDevice { HttpPort = wled.Port, AllowRelocation = false, HealthInterval = TimeSpan.FromMilliseconds(100) };
        dev.Configure(new DeviceSettings { Host = "127.0.0.1", NetworkPort = rx.Port }, 600);
        Assert.True(WaitFor(() => Volatile.Read(ref wled.Requests) > 0));
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Searching));
        Assert.False(dev.WaitForSlot(10));
        dev.Submit(Frame(600, 1)); // ignored while not connected
        Assert.Null(rx.Receive(200));

        wled.Answer = true;
        dev.Rescan();
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));

        var f = Frame(600, 9);
        Send(dev, f);
        var a = rx.Receive();
        var b = rx.Receive();
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(new byte[] { 4, 2, 0, 0 }, a![..4]);
        Assert.Equal(f[..(489 * 3)], a[4..]);
        Assert.Equal(new byte[] { 4, 2, 0x01, 0xE9 }, b![..4]);
        Assert.Equal(f[(489 * 3)..], b[4..]);

        wled.Answer = false;
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Searching, 8000));
    }

    [Fact]
    public void Ddp_sends_frames_and_black_on_blackout()
    {
        using var rx = new UdpReceiver();
        using var dev = new DdpDevice();
        dev.Configure(new DeviceSettings { Type = DeviceType.Ddp, Host = "127.0.0.1", NetworkPort = rx.Port, ColorOrder = ColorOrder.GRB }, 2);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));
        Send(dev, [1, 2, 3, 4, 5, 6]);
        Assert.Equal(new byte[] { 0x41, 1, 0x0B, 1, 0, 0, 0, 0, 0, 6, 2, 1, 3, 5, 4, 6 }, rx.Receive());
        dev.Blackout();
        var black = rx.Drain(300);
        Assert.Equal(2, black.Count);
        Assert.All(black, p => Assert.All(p[10..], v => Assert.Equal(0, v)));
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
    }

    [Fact]
    public void E131_unicast_sends_one_packet_per_universe_and_terminates_streams()
    {
        using var rx = new UdpReceiver();
        using var dev = new E131Device();
        dev.Configure(new DeviceSettings { Type = DeviceType.E131, Host = "127.0.0.1", NetworkPort = rx.Port, E131Universe = 5 }, 200);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));
        Send(dev, Frame(200, 0));
        var a = rx.Receive()!;
        var b = rx.Receive()!;
        Assert.Equal(5, (a[113] << 8) | a[114]);
        Assert.Equal(6, (b[113] << 8) | b[114]);
        Assert.Equal(636, a.Length);
        Assert.Equal(126 + 90, b.Length);
        Assert.Equal(0, a[111]);
        Send(dev, Frame(200, 1));
        Assert.Equal(1, rx.Receive()![111]);
        Assert.Equal(1, rx.Receive()![111]);

        dev.Blackout();
        var tail = rx.Drain(400);
        Assert.Equal(10, tail.Count); // 2 universes × (2 black + 3 terminated)
        Assert.Equal(6, tail.Count(p => p[112] == 0x40));
        Assert.All(tail, p => Assert.All(p[126..], v => Assert.Equal(0, v)));
    }

    [Fact]
    public void Frames_are_paced_and_latest_wins()
    {
        using var rx = new UdpReceiver();
        using var dev = new DdpDevice { MaxFrameRate = 10 };
        dev.Configure(new DeviceSettings { Host = "127.0.0.1", NetworkPort = rx.Port }, 1);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Connected));
        Send(dev, [1, 1, 1]);
        Assert.NotNull(rx.Receive());
        // The link is busy for 100 ms: these replace each other, only the last one goes out.
        for (byte i = 2; i <= 6; i++) dev.Submit([i, i, i]);
        var got = rx.Drain(400);
        var last = Assert.Single(got);
        Assert.Equal(6, last[10]);
        // WaitForSlot paces at MaxFrameRate.
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) Send(dev, [7, 7, 7]);
        Assert.InRange(sw.ElapsedMilliseconds, 150, 1500);
    }

    [Fact]
    public void Unresolvable_host_is_searching_and_dispose_is_quick()
    {
        var dev = new DdpDevice();
        int changes = 0;
        dev.StatusChanged += () => Interlocked.Increment(ref changes);
        dev.Configure(new DeviceSettings { Host = "no-such-host.invalid" }, 10);
        Assert.True(WaitFor(() => dev.Status == DeviceStatus.Searching));
        Assert.Equal("no-such-host.invalid", dev.PortName);
        Assert.False(dev.WaitForSlot(5));
        dev.Blackout(); // not connected: returns immediately
        var sw = Stopwatch.StartNew();
        dev.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 2500);
        Assert.Equal(DeviceStatus.Stopped, dev.Status);
        Assert.True(changes >= 1);
    }

    [Fact]
    public void Missing_host_is_searching_but_e131_multicast_needs_none()
    {
        using var ddp = new DdpDevice();
        ddp.Configure(new DeviceSettings { Host = null }, 10);
        Assert.True(WaitFor(() => ddp.Status == DeviceStatus.Searching));

        // Multicast: connected without a host. No frame is submitted, so nothing is sent to the LAN.
        using var e131 = new E131Device();
        e131.Configure(new DeviceSettings { E131Multicast = true, E131Universe = 1 }, 10);
        Assert.True(WaitFor(() => e131.Status == DeviceStatus.Connected));
        Assert.StartsWith("multicast 239.255.0.1:5568", e131.PortName);
    }

    [Fact]
    public void Factory_creates_network_drivers_and_finds_the_discovery()
    {
        Assert.IsType<WledDevice>(LedDeviceFactory.Create(DeviceType.Wled));
        Assert.IsType<DdpDevice>(LedDeviceFactory.Create(DeviceType.Ddp));
        Assert.IsType<E131Device>(LedDeviceFactory.Create(DeviceType.E131));
        Assert.Contains(LedDeviceFactory.DiscoveryServices, d => d is WledDiscovery);
    }

    [Fact]
    public async Task Discovery_queries_json_info_of_found_hosts()
    {
        using var wled = new FakeWled(ledCount: 77, udpPort: 21324);
        var found = await WledDiscovery.QueryAllAsync([IPAddress.Loopback], wled.Port, TimeSpan.FromSeconds(2), default);
        var (addr, info) = Assert.Single(found);
        var d = WledDiscovery.ToDiscovered(addr, info);
        Assert.Equal("Fake WLED (127.0.0.1)", d.DisplayName);
        Assert.Equal("WLED 0.15.0 · 77 LEDs", d.Detail);
        Assert.Equal("0123456789ab", d.Id);
        Assert.Equal(77, d.LedCount);

        wled.Answer = false;
        Assert.Empty(await WledDiscovery.QueryAllAsync([IPAddress.Loopback], wled.Port, TimeSpan.FromSeconds(2), default));
    }
}
