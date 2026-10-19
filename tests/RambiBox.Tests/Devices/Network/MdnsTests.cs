using System.Net;
using System.Text;
using RambiBox.Core.Devices.Network;

namespace RambiBox.Tests.Devices.Network;

public class MdnsTests
{
    /// <summary>Tiny DNS message writer for building responses the way ESP mDNS responders send them (compressed names).</summary>
    private sealed class Writer
    {
        public readonly List<byte> B = [];
        public int Pos => B.Count;
        public void U16(int v) { B.Add((byte)(v >> 8)); B.Add((byte)v); }
        public void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        public void Labels(params string[] labels)
        {
            foreach (var l in labels) { B.Add((byte)l.Length); B.AddRange(Encoding.ASCII.GetBytes(l)); }
        }
        public void End() => B.Add(0);
        public void Pointer(int offset) { B.Add((byte)(0xC0 | (offset >> 8))); B.Add((byte)offset); }
        /// <summary>Writes type/class/ttl and a placeholder rdlength; returns the rdlength position.</summary>
        public int RecordHeader(int type, int cls, uint ttl)
        {
            U16(type); U16(cls); U32(ttl);
            int at = Pos;
            U16(0);
            return at;
        }
        public void FixLength(int at)
        {
            int len = Pos - at - 2;
            B[at] = (byte)(len >> 8);
            B[at + 1] = (byte)len;
        }
    }

    /// <summary>Response: PTR _wled._tcp.local → wled-tv._wled._tcp.local; additional SRV, TXT, A (all compressed).</summary>
    private static byte[] WledResponse(bool includeA = true)
    {
        var w = new Writer();
        w.U16(0); w.U16(0x8400); w.U16(0); w.U16(1); w.U16(0); w.U16(includeA ? 3 : 2);
        int service = w.Pos;
        w.Labels("_wled", "_tcp", "local"); w.End();
        int local = service + 1 + 5 + 1 + 4; // "local" label inside the service name
        int rd = w.RecordHeader(12, 1, 120);
        int instance = w.Pos;
        w.Labels("wled-tv"); w.Pointer(service);
        w.FixLength(rd);
        // SRV
        w.Pointer(instance);
        rd = w.RecordHeader(33, 0x8001, 120);
        w.U16(0); w.U16(0); w.U16(80);
        int host = w.Pos;
        w.Labels("wled-tv"); w.Pointer(local);
        w.FixLength(rd);
        // TXT
        w.Pointer(instance);
        rd = w.RecordHeader(16, 0x8001, 4500);
        w.B.Add(7); w.B.AddRange("mac=abc"u8.ToArray());
        w.FixLength(rd);
        if (includeA)
        {
            w.Pointer(host);
            rd = w.RecordHeader(1, 0x8001, 120);
            w.B.AddRange(new byte[] { 192, 168, 1, 40 });
            w.FixLength(rd);
        }
        return w.B.ToArray();
    }

    [Fact]
    public void Query_is_standard_question_with_unicast_response_bit()
    {
        var q = DnsWire.BuildQuery(0, true, ("_wled._tcp.local", DnsWire.TypePtr));
        var expected = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        expected.Add(5); expected.AddRange("_wled"u8.ToArray());
        expected.Add(4); expected.AddRange("_tcp"u8.ToArray());
        expected.Add(5); expected.AddRange("local"u8.ToArray());
        expected.AddRange(new byte[] { 0, 0, 12, 0x80, 0x01 });
        Assert.Equal(expected.ToArray(), q);
    }

    [Fact]
    public void Parses_compressed_wled_response()
    {
        Assert.True(DnsWire.TryParse(WledResponse(), out var m));
        Assert.NotNull(m);
        Assert.True(m!.IsResponse);
        Assert.Equal(4, m.Records.Count);
        var ptr = m.Records[0];
        Assert.Equal(DnsWire.TypePtr, ptr.Type);
        Assert.Equal("_wled._tcp.local", ptr.Name);
        Assert.Equal("wled-tv._wled._tcp.local", ptr.Target);
        var srv = m.Records[1];
        Assert.Equal("wled-tv._wled._tcp.local", srv.Name);
        Assert.Equal("wled-tv.local", srv.Target);
        Assert.Equal(80, srv.Port);
        Assert.Equal(1, srv.Class); // cache-flush bit stripped
        Assert.Equal(["mac=abc"], m.Records[2].Text!);
        var a = m.Records[3];
        Assert.Equal("wled-tv.local", a.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.40"), a.Address);
    }

    [Fact]
    public void Collect_joins_ptr_srv_and_a_records()
    {
        DnsWire.TryParse(WledResponse(), out var m);
        var services = MdnsClient.Collect("_wled._tcp.local", [(m!, IPAddress.Parse("10.9.9.9"))]);
        var s = Assert.Single(services);
        Assert.Equal("wled-tv._wled._tcp.local", s.Instance);
        Assert.Equal("wled-tv.local", s.HostName);
        Assert.Equal(80, s.Port);
        Assert.Equal(IPAddress.Parse("192.168.1.40"), s.Address);
    }

    [Fact]
    public void Collect_falls_back_to_the_sender_address_without_an_A_record()
    {
        DnsWire.TryParse(WledResponse(includeA: false), out var m);
        var s = Assert.Single(MdnsClient.Collect("_wled._tcp.local", [(m!, IPAddress.Parse("192.168.1.77"))]));
        Assert.Equal(IPAddress.Parse("192.168.1.77"), s.Address);
        Assert.Empty(MdnsClient.Collect("_http._tcp.local", [(m!, IPAddress.Parse("192.168.1.77"))]));
    }

    [Fact]
    public void Malformed_packets_are_rejected_without_throwing()
    {
        var good = WledResponse();
        for (int len = 0; len < good.Length; len++)
            DnsWire.TryParse(good.AsSpan(0, len), out _); // must not throw
        Assert.False(DnsWire.TryParse(good.AsSpan(0, good.Length - 3), out _));

        // A name that points to itself.
        byte[] loop = [0, 0, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1];
        Assert.False(DnsWire.TryParse(loop, out _));
        int pos = 12;
        Assert.False(DnsWire.TryReadName(loop, ref pos, out _));
    }
}

public class NetworkHelperTests
{
    [Theory]
    [InlineData("192.168.1.40", "192.168.1.40", 0)]
    [InlineData(" wled-tv.local ", "wled-tv.local", 0)]
    [InlineData("http://wled-tv.local/", "wled-tv.local", 0)]
    [InlineData("http://192.168.1.40:8080/json/info", "192.168.1.40", 8080)]
    [InlineData("10.0.0.5:4048", "10.0.0.5", 4048)]
    [InlineData("[fe80::1]:21324", "fe80::1", 21324)]
    [InlineData("fe80::1", "fe80::1", 0)]
    public void Host_parsing(string raw, string host, int port)
    {
        Assert.True(HostResolver.TryParse(raw, out var h, out int p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("http://")]
    public void Empty_hosts_are_rejected(string? raw) => Assert.False(HostResolver.TryParse(raw, out _, out _));

    [Fact]
    public async Task Resolves_literals_and_localhost()
    {
        Assert.Equal(IPAddress.Parse("10.1.2.3"), await HostResolver.ResolveAsync("10.1.2.3", TimeSpan.FromSeconds(1), default));
        var lo = await HostResolver.ResolveAsync("localhost", TimeSpan.FromSeconds(3), default);
        Assert.NotNull(lo);
        Assert.True(IPAddress.IsLoopback(lo!));
    }

    [Fact]
    public void Wled_info_is_parsed_from_json_info()
    {
        const string json = """
            {"ver":"0.15.0","vid":2412100,"leds":{"count":120,"pwr":0,"fps":42,"maxpwr":850,"rgbw":false},
             "name":"WLED-TV","udpport":21324,"live":false,"arch":"esp32","mac":"A4CF12FDAEA9","ip":"192.168.1.40"}
            """;
        var info = WledInfo.Parse(json);
        Assert.NotNull(info);
        Assert.Equal("WLED-TV", info!.Name);
        Assert.Equal("0.15.0", info.Version);
        Assert.Equal(120, info.LedCount);
        Assert.Equal("a4cf12fdaea9", info.Mac);
        Assert.Equal("a4:cf:12:fd:ae:a9", info.FormattedMac);
        Assert.Equal(21324, info.UdpPort);
        Assert.Equal("esp32", info.Architecture);
        Assert.NotNull(WledInfo.Parse("{\"state\":{},\"info\":" + json + "}"));
        Assert.Null(WledInfo.Parse("{\"hello\":1}"));
        Assert.Null(WledInfo.Parse("<html>"));
    }

    [Fact]
    public void Discovered_device_entry()
    {
        var d = WledDiscovery.ToDiscovered(IPAddress.Parse("192.168.1.40"), new WledInfo("WLED-TV", "0.15.0", 120, "a4cf12fdaea9", 21324, "esp32"));
        Assert.Equal(RambiBox.Core.Config.DeviceType.Wled, d.Type);
        Assert.Equal("a4cf12fdaea9", d.Id);
        Assert.Equal("WLED-TV (192.168.1.40)", d.DisplayName);
        Assert.Equal("WLED 0.15.0 · 120 LEDs", d.Detail);
        Assert.Equal(120, d.LedCount);
        Assert.Equal("192.168.1.40", d.Host);
    }

    [Theory]
    [InlineData("A4:CF:12:FD:AE:A9", "a4cf12fdaea9")]
    [InlineData("a4-cf-12-fd-ae-a9", "a4cf12fdaea9")]
    [InlineData("a4cf12fdaea9", "a4cf12fdaea9")]
    [InlineData("COM3", null)]
    [InlineData(null, null)]
    public void Mac_normalization(string? raw, string? expected) => Assert.Equal(expected, WledDiscovery.NormalizeMac(raw));
}
