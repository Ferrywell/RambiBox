using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RambiBox.Core.Devices.Network;

/// <summary>Parses user-entered hosts ("192.168.1.40", "wled-tv.local", "http://wled-tv/", "10.0.0.5:4048") and resolves them.</summary>
internal static class HostResolver
{
    /// <summary>
    /// Strips a URL scheme, path and an explicit ":port" from <paramref name="raw"/>. Returns false for an empty host.
    /// <paramref name="port"/> is 0 when none was given.
    /// </summary>
    public static bool TryParse(string? raw, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOfAny(['/', '?', '#']);
        if (slash >= 0) s = s[..slash];
        if (s.StartsWith('[')) // [IPv6]:port
        {
            int close = s.IndexOf(']');
            if (close < 0) return false;
            if (close + 2 < s.Length && s[close + 1] == ':' && int.TryParse(s.AsSpan(close + 2), NumberStyles.None, CultureInfo.InvariantCulture, out int p6))
                port = p6 is > 0 and <= 65535 ? p6 : 0;
            s = s[1..close];
        }
        else if (s.Count(c => c == ':') == 1)
        {
            int colon = s.IndexOf(':');
            if (int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p))
                port = p is > 0 and <= 65535 ? p : 0;
            s = s[..colon];
        }
        host = s.Trim().TrimEnd('.');
        return host.Length > 0;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to an address (IPv4 preferred): literal IPs directly, names through the
    /// system resolver (Windows resolves ".local" names via mDNS too), and ".local" names through RambiBox's own mDNS
    /// query when the system resolver fails. Null when unresolvable within <paramref name="timeout"/>. Never throws.
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, cts.Token).ConfigureAwait(false);
            var best = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (best is not null) return best;
        }
        catch (Exception) { /* fall through */ }
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !ct.IsCancellationRequested)
        {
            try { return await MdnsClient.ResolveAsync(host, TimeSpan.FromSeconds(Math.Min(2, timeout.TotalSeconds)), ct).ConfigureAwait(false); }
            catch (Exception) { /* unresolvable */ }
        }
        return null;
    }
}
