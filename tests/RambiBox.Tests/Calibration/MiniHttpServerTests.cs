using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RambiBox.Core.Calibration;
using RambiBox.Tests.Devices;

namespace RambiBox.Tests.Calibration;

public class MiniHttpServerTests
{
    private static readonly IPAddress OtherHost = IPAddress.Parse("192.168.77.2");

    /// <summary>Server answering "ok" to everything; connections from local ports in <paramref name="otherHostPorts"/>
    /// appear to come from <see cref="OtherHost"/>.</summary>
    private static MiniHttpServer StartServer(ConcurrentDictionary<int, bool> otherHostPorts, TimeSpan? headerTimeout = null)
    {
        var server = new MiniHttpServer(
            (_, _) => Task.FromResult(MiniHttpResponse.Text(200, "ok")),
            (_, _, _) => null,
            1024)
        {
            HeaderTimeout = headerTimeout ?? TimeSpan.FromSeconds(5),
            ClientAddress = ep => otherHostPorts.ContainsKey(ep.Port) ? OtherHost : ep.Address,
        };
        Assert.True(server.Start(47830, 47870));
        return server;
    }

    private static TcpClient Connect(int serverPort, ConcurrentDictionary<int, bool>? markOtherHost = null)
    {
        var c = new TcpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (markOtherHost is not null) markOtherHost[((IPEndPoint)c.Client.LocalEndPoint!).Port] = true;
        c.Connect(IPAddress.Loopback, serverPort);
        return c;
    }

    private static string ReadAll(TcpClient c, int timeoutMs = 3000)
    {
        c.ReceiveTimeout = timeoutMs;
        var ms = new MemoryStream();
        var buf = new byte[4096];
        try
        {
            int n;
            while ((n = c.GetStream().Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        }
        catch (IOException) { /* reset / timeout: return what arrived */ }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static string Get(TcpClient c)
    {
        c.GetStream().Write("GET /x HTTP/1.1\r\nHost: test\r\n\r\n"u8);
        return ReadAll(c);
    }

    [Fact]
    public void HeaderTimeout_Defaults_To5s()
    {
        using var server = new MiniHttpServer((_, _) => Task.FromResult(MiniHttpResponse.Text(200, "")), (_, _, _) => null, 1);
        Assert.Equal(TimeSpan.FromSeconds(5), server.HeaderTimeout);
    }

    [Fact]
    public void SlowHeaders_AreDropped_AfterHeaderTimeout()
    {
        var other = new ConcurrentDictionary<int, bool>();
        using var server = StartServer(other, TimeSpan.FromMilliseconds(300));
        using var slow = Connect(server.Port);
        slow.GetStream().Write("GET /x HTTP/1.1\r\nHost: te"u8); // never finishes the header block
        Assert.True(Wait.Until(() => server.ActiveConnections == 1, 2000));

        var sw = Stopwatch.StartNew();
        string got = ReadAll(slow, 5000);
        Assert.Equal("", got); // closed without a response
        Assert.InRange(sw.ElapsedMilliseconds, 100, 2500);
        Assert.True(Wait.Until(() => server.ActiveConnections == 0, 2000));
    }

    [Fact]
    public void PerClientCap_RejectsFifthConnection_With503_OtherHostsStillServed()
    {
        var other = new ConcurrentDictionary<int, bool>();
        using var server = StartServer(other);
        var idle = new List<TcpClient>();
        try
        {
            for (int i = 0; i < MiniHttpServer.MaxPerClient; i++) idle.Add(Connect(server.Port)); // slow-header attacker
            Assert.True(Wait.Until(() => server.ActiveConnections == MiniHttpServer.MaxPerClient, 2000));

            // Same IP: refused immediately (well before the 5 s header timeout of the idle ones).
            var sw = Stopwatch.StartNew();
            using (var fifth = Connect(server.Port))
            {
                string resp = ReadAll(fifth);
                Assert.StartsWith("HTTP/1.1 503", resp);
            }
            Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");

            // Another LAN host (the phone) still gets through.
            using (var phone = Connect(server.Port, other))
                Assert.StartsWith("HTTP/1.1 200", Get(phone));
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }

        // Slots are released when the attacker's connections go away.
        Assert.True(Wait.Until(() => server.ActiveConnections == 0, 3000));
        using var again = Connect(server.Port);
        Assert.StartsWith("HTTP/1.1 200", Get(again));
    }
}
