using System.Net;
using System.Net.Sockets;
using RambiBox.Core.Calibration;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// E1.31 / sACN over UDP, port 5568 (see <see cref="E131Packetizer"/>): 170 RGB LEDs per universe starting at
/// <see cref="DeviceSettings.E131Universe"/>, priority 100, source name "RambiBox" and a per-PC CID derived from the
/// machine name. Unicast to <see cref="DeviceSettings.Host"/>, or multicast to 239.255.{hi}.{lo} per universe (sent
/// from the LAN interface) when <see cref="DeviceSettings.E131Multicast"/> is set — then no host is needed. On stop it
/// sends black and the Stream_Terminated packets so receivers release the universes immediately.
/// </summary>
[LedDevice(DeviceType.E131)]
public sealed class E131Device : UdpLedDevice
{
    private static readonly Lazy<Guid> Cid = new(E131Packetizer.MachineCid);
    private string? _truncationLogged;

    public E131Device() : base("E1.31") { }

    private protected override int DefaultPort => E131Packetizer.DefaultPort;

    private protected override bool NeedsHost(NetSettings s) => !s.Multicast;

    private protected override UdpPacketizer CreatePacketizer(NetSettings s) =>
        new E131Packetizer(s.LedCount, s.Universe, s.Order, Cid.Value);

    private protected override void OnPacketizerBuilt(UdpPacketizer packetizer, NetSettings s)
    {
        if (packetizer is not E131Packetizer e) return;
        string key = $"{e.FirstUniverse}/{e.RequestedLeds}";
        if (e.Truncated && _truncationLogged != key)
        {
            _truncationLogged = key;
            Log.Warn($"E1.31: universes {e.FirstUniverse}..{E131Packetizer.MaxUniverse} hold only {e.LedCount} LEDs; the rest stay dark");
        }
    }

    private protected override IPEndPoint[] Destinations(NetSettings s, IPAddress? address, int port, UdpPacketizer packetizer)
    {
        var e = (E131Packetizer)packetizer;
        var list = new IPEndPoint[e.UniverseCount];
        for (int u = 0; u < list.Length; u++)
        {
            var ip = s.Multicast ? E131Packetizer.MulticastAddress(e.FirstUniverse + u) : address;
            if (ip is null) return [];
            list[u] = new IPEndPoint(ip, port);
        }
        return list;
    }

    private protected override void ConfigureSocket(Socket socket, NetSettings s)
    {
        if (!s.Multicast) return;
        // Send the multicast from the LAN adapter rather than whatever the routing table prefers (VPN, Hyper-V…).
        if (LanAddress.Pick() is { } lan)
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, lan.GetAddressBytes());
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
    }

    private protected override string Describe(NetSettings s, IPAddress? address, int port)
    {
        if (!s.Multicast) return base.Describe(s, address, port);
        int first = Math.Clamp(s.Universe, E131Packetizer.MinUniverse, E131Packetizer.MaxUniverse);
        int count = (s.LedCount + E131Packetizer.LedsPerUniverse - 1) / E131Packetizer.LedsPerUniverse;
        return count > 1
            ? $"multicast {E131Packetizer.MulticastAddress(first)}:{port} (+{count - 1} universes)"
            : $"multicast {E131Packetizer.MulticastAddress(first)}:{port}";
    }
}
