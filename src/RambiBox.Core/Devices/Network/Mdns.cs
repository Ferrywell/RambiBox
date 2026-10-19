using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RambiBox.Core.Devices.Network;

/// <summary>One resource record of an mDNS / DNS message (only the fields RambiBox uses are decoded).</summary>
internal sealed record DnsRecord(string Name, ushort Type, ushort Class, uint Ttl)
{
    /// <summary>PTR / SRV / CNAME target name.</summary>
    public string? Target { get; init; }
    /// <summary>SRV port.</summary>
    public ushort Port { get; init; }
    /// <summary>A / AAAA address.</summary>
    public IPAddress? Address { get; init; }
    /// <summary>TXT strings.</summary>
    public IReadOnlyList<string>? Text { get; init; }
}

/// <summary>A decoded DNS message: header + all answer, authority and additional records.</summary>
internal sealed record DnsMessage(ushort Id, ushort Flags, IReadOnlyList<string> Questions, IReadOnlyList<DnsRecord> Records)
{
    public bool IsResponse => (Flags & 0x8000) != 0;
}

/// <summary>A service instance found by an mDNS browse (e.g. "WLED-TV._wled._tcp.local").</summary>
internal sealed record MdnsService(string Instance, string? HostName, int Port, IPAddress Address);

/// <summary>
/// Minimal DNS wire format codec (RFC 1035) with the mDNS conventions of RFC 6762: class top bit = "unicast response
/// requested" in questions / "cache flush" in answers, name compression, case-insensitive names.
/// </summary>
internal static class DnsWire
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33, TypeAny = 255;
    public const ushort ClassIn = 1, ClassTopBit = 0x8000;

    /// <summary>Builds a standard query (id 0, no flags) with the given questions.</summary>
    public static byte[] BuildQuery(ushort id, bool unicastResponse, params (string Name, ushort Type)[] questions)
    {
        var buf = new List<byte>(64);
        void U16(int v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        U16(id);
        U16(0); // flags: standard query
        U16(questions.Length);
        U16(0); U16(0); U16(0);
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length is 0 or > 63) throw new ArgumentException($"Invalid DNS label in '{name}'", nameof(questions));
                buf.Add((byte)bytes.Length);
                buf.AddRange(bytes);
            }
            buf.Add(0);
            U16(type);
            U16(ClassIn | (unicastResponse ? ClassTopBit : 0));
        }
        return buf.ToArray();
    }

    /// <summary>Parses a DNS message; false for anything malformed (never throws).</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage? message)
    {
        message = null;
        try
        {
            if (data.Length < 12) return false;
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data);
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            int rr = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) + BinaryPrimitives.ReadUInt16BigEndian(data[8..])
                     + BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            int pos = 12;
            var questions = new List<string>(qd);
            for (int i = 0; i < qd; i++)
            {
                if (!TryReadName(data, ref pos, out var qn) || pos + 4 > data.Length) return false;
                questions.Add(qn);
                pos += 4;
            }
            var records = new List<DnsRecord>(rr);
            for (int i = 0; i < rr; i++)
            {
                if (!TryReadName(data, ref pos, out var name) || pos + 10 > data.Length) return false;
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                ushort cls = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
                int rdlen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
                pos += 10;
                if (pos + rdlen > data.Length) return false;
                var rec = new DnsRecord(name, type, (ushort)(cls & 0x7FFF), ttl);
                var rd = data.Slice(pos, rdlen);
                switch (type)
                {
                    case TypeA when rdlen == 4:
                        rec = rec with { Address = new IPAddress(rd) };
                        break;
                    case TypeAaaa when rdlen == 16:
                        rec = rec with { Address = new IPAddress(rd) };
                        break;
                    case TypePtr:
                    {
                        int p = pos;
                        if (TryReadName(data, ref p, out var target)) rec = rec with { Target = target };
                        break;
                    }
                    case TypeSrv when rdlen >= 7:
                    {
                        int p = pos + 6;
                        if (TryReadName(data, ref p, out var target))
                            rec = rec with { Target = target, Port = BinaryPrimitives.ReadUInt16BigEndian(rd[4..]) };
                        break;
                    }
                    case TypeTxt:
                    {
                        var txt = new List<string>();
                        for (int p = 0; p < rd.Length;)
                        {
                            int l = rd[p++];
                            if (p + l > rd.Length) break;
                            txt.Add(Encoding.UTF8.GetString(rd.Slice(p, l)));
                            p += l;
                        }
                        rec = rec with { Text = txt };
                        break;
                    }
                }
                records.Add(rec);
                pos += rdlen;
            }
            message = new DnsMessage(id, flags, questions, records);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads a (possibly compressed) name at <paramref name="pos"/>; advances past it.</summary>
    public static bool TryReadName(ReadOnlySpan<byte> data, ref int pos, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        int p = pos, jumps = 0, end = -1;
        while (true)
        {
            if (p >= data.Length) return false;
            int len = data[p];
            if (len == 0)
            {
                p++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= data.Length || ++jumps > 32) return false;
                if (end < 0) end = p + 2;
                p = ((len & 0x3F) << 8) | data[p + 1];
                continue;
            }
            if ((len & 0xC0) != 0 || p + 1 + len > data.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(data.Slice(p + 1, len)));
            if (sb.Length > 1024) return false;
            p += 1 + len;
        }
        pos = end >= 0 ? end : p;
        name = sb.ToString();
        return true;
    }

    public static bool NameEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Small mDNS querier (RFC 6762) for service discovery and ".local" name resolution, without Bonjour or the Windows
/// DNS-SD API. Queries go to 224.0.0.251:5353 from every IPv4 LAN interface, from an ephemeral port with the
/// "unicast response" bit set (responders answer such one-shot queries directly to the sender); a second socket on
/// port 5353 (shared with Windows' own mDNS responder) also collects multicast answers. Never throws.
/// </summary>
internal static class MdnsClient
{
    public static readonly IPEndPoint MulticastEndPoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Browses for a service type such as "_wled._tcp.local"; returns once <paramref name="timeout"/> elapsed.</summary>
    public static async Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan timeout, CancellationToken ct)
    {
        var messages = await QueryAsync([(serviceType, DnsWire.TypePtr)], timeout, stopEarly: null, ct).ConfigureAwait(false);
        return Collect(serviceType, messages);
    }

    /// <summary>Resolves "name.local" to an IPv4 address via mDNS; null when nobody answered in time.</summary>
    public static async Task<IPAddress?> ResolveAsync(string hostName, TimeSpan timeout, CancellationToken ct)
    {
        IPAddress? found = null;
        await QueryAsync([(hostName, DnsWire.TypeA)], timeout, m =>
        {
            foreach (var r in m.Records)
            {
                if (r.Type == DnsWire.TypeA && r.Address is not null && DnsWire.NameEquals(r.Name, hostName))
                {
                    found = r.Address;
                    return true;
                }
            }
            return false;
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// Turns the received responses into service instances: PTR (service → instance), SRV (instance → host, port),
    /// A (host → address). An instance without an A record uses the address it was received from.
    /// </summary>
    internal static IReadOnlyList<MdnsService> Collect(string serviceType, IEnumerable<(DnsMessage Message, IPAddress From)> messages)
    {
        var all = messages.ToList();
        var instances = new List<(string Name, IPAddress From)>();
        var srv = new Dictionary<string, DnsRecord>(StringComparer.OrdinalIgnoreCase);
        var addr = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var (m, from) in all)
        {
            if (!m.IsResponse) continue;
            foreach (var r in m.Records)
            {
                string key = r.Name.TrimEnd('.');
                switch (r.Type)
                {
                    case DnsWire.TypePtr when r.Target is not null && DnsWire.NameEquals(r.Name, serviceType) && r.Ttl > 0:
                        if (!instances.Any(i => DnsWire.NameEquals(i.Name, r.Target))) instances.Add((r.Target.TrimEnd('.'), from));
                        break;
                    case DnsWire.TypeSrv when r.Target is not null:
                        srv[key] = r;
                        break;
                    case DnsWire.TypeA when r.Address is not null:
                        addr.TryAdd(key, r.Address);
                        break;
                }
            }
        }
        var result = new List<MdnsService>();
        foreach (var (name, from) in instances)
        {
            srv.TryGetValue(name, out var s);
            string? host = s?.Target?.TrimEnd('.');
            IPAddress ip = host is not null && addr.TryGetValue(host, out var a) ? a : from;
            if (result.Any(r => r.Address.Equals(ip) && string.Equals(r.Instance, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new MdnsService(name, host, s?.Port ?? 0, ip));
        }
        return result;
    }

    /// <summary>
    /// Sends the questions on every LAN interface (again after a third of the timeout, mDNS answers are unreliable on
    /// Wi-Fi) and collects every response until the timeout, or until <paramref name="stopEarly"/> returns true.
    /// </summary>
    private static async Task<List<(DnsMessage, IPAddress)>> QueryAsync((string Name, ushort Type)[] questions, TimeSpan timeout,
        Func<DnsMessage, bool>? stopEarly, CancellationToken ct)
    {
        var results = new ConcurrentQueue<(DnsMessage, IPAddress)>();
        var sockets = new List<Socket>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            byte[] query = DnsWire.BuildQuery(0, unicastResponse: true, questions);
            var local = LocalIPv4Addresses();
            foreach (var ip in local)
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sockets.Add(s);
                    s.Bind(new IPEndPoint(ip, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    s.MulticastLoopback = false;
                }
                catch (Exception ex) { Log.Warn($"mDNS: cannot use interface {ip}: {ex.Message}"); }
            }
            // Shared listener for responders that answer by multicast only.
            try
            {
                var l = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(l);
                l.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Bind(new IPEndPoint(IPAddress.Any, 5353));
                foreach (var ip in local)
                {
                    try { l.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastEndPoint.Address, ip)); }
                    catch { /* interface without multicast */ }
                }
            }
            catch { /* port 5353 unavailable: unicast answers still work */ }

            var senders = sockets.Where(s => s.IsBound && ((IPEndPoint)s.LocalEndPoint!).Port != 5353).ToList();
            var receivers = sockets.Where(s => s.IsBound).Select(s => ReceiveLoop(s, results, stopEarly, cts, cts.Token)).ToList();
            Send(senders, query);
            try
            {
                await Task.Delay(timeout / 3, cts.Token).ConfigureAwait(false);
                Send(senders, query);
                await Task.Delay(timeout - timeout / 3, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* stopped early or cancelled */ }
            cts.Cancel();
            foreach (var s in sockets) s.Dispose();
            try { await Task.WhenAll(receivers).ConfigureAwait(false); } catch { /* receive loops end with errors on dispose */ }
        }
        catch (Exception ex)
        {
            Log.Warn($"mDNS query failed: {ex.Message}");
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        return results.ToList();
    }

    private static void Send(List<Socket> senders, byte[] query)
    {
        foreach (var s in senders)
        {
            try { s.SendTo(query, MulticastEndPoint); }
            catch { /* interface went away */ }
        }
    }

    private static async Task ReceiveLoop(Socket s, ConcurrentQueue<(DnsMessage, IPAddress)> results, Func<DnsMessage, bool>? stopEarly,
        CancellationTokenSource cts, CancellationToken ct)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
            if (!DnsWire.TryParse(buf.AsSpan(0, r.ReceivedBytes), out var m) || m is null || !m.IsResponse) continue;
            results.Enqueue((m, ((IPEndPoint)r.RemoteEndPoint).Address));
            if (stopEarly is not null && stopEarly(m))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
        }
    }

    /// <summary>IPv4 addresses of all operational, multicast-capable, non-loopback interfaces.</summary>
    internal static List<IPAddress> LocalIPv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)
                        && !list.Contains(ua.Address))
                        list.Add(ua.Address);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"mDNS: cannot enumerate network interfaces: {ex.Message}"); }
        return list;
    }
}
