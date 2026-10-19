using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RambiBox.Core.Calibration;

/// <summary>Picks the IPv4 address a phone on the same Wi-Fi/LAN can reach, and classifies client addresses.</summary>
internal static class LanAddress
{
    private static readonly string[] VirtualHints =
    [
        "vethernet", "hyper-v", "virtualbox", "vmware", "wsl", "vpn", "tunnel", "tap-", "tap ", "tun", "wireguard",
        "tailscale", "zerotier", "hamachi", "openvpn", "fortinet", "cisco anyconnect", "globalprotect", "nordlynx",
        "npcap", "loopback", "docker", "virtual", "pseudo", "teredo", "isatap", "bluetooth",
    ];

    /// <summary>Best LAN IPv4 address to advertise, or null when the PC has no usable network.</summary>
    public static IPAddress? Pick()
    {
        try
        {
            var cands = new List<(IPAddress Ip, int Score)>();
            IPAddress? routeIp = DefaultRouteAddress();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                    continue;
                string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VirtualHints.Any(text.Contains)) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                               && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || IsLinkLocal(ip)) continue;
                    int score = 0;
                    if (gateway) score += 100;
                    if (IsPrivate(ip)) score += 50;
                    if (ip.Equals(routeIp)) score += 20;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.GigabitEthernet) score += 10;
                    if (ip.ToString().StartsWith("192.168.", StringComparison.Ordinal)) score += 1; // typical home LAN
                    cands.Add((ip, score));
                }
            }
            return cands.OrderByDescending(c => c.Score).Select(c => c.Ip).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"Calibration: could not enumerate network interfaces: {ex.Message}");
            return null;
        }
    }

    /// <summary>Local address the OS would use for the default route (no packet is sent for a UDP "connect").</summary>
    private static IPAddress? DefaultRouteAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)); // TEST-NET-1, never actually contacted
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { return null; }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    /// <summary>Clients we answer: same-LAN private ranges, link-local and loopback only.</summary>
    public static bool IsAllowedClient(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip) || IsPrivate(ip) || IsLinkLocal(ip);
    }
}
