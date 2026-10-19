using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RambiBox.Core.Devices;

/// <summary>Input checks for the device settings, with messages meant for the user. Null = valid.</summary>
public static class DeviceValidation
{
    /// <summary>
    /// Checks an IP address (v4 or v6) or host name ("wled-tv", "wled-tv.local"). Returns the cleaned-up value in
    /// <paramref name="normalized"/> (trimmed; "http://192.168.2.40/" pasted from a browser becomes "192.168.2.40").
    /// </summary>
    public static string? ValidateHost(string? text, out string normalized)
    {
        normalized = (text ?? "").Trim();
        if (normalized.Length == 0) return "Enter the IP address or host name of the controller.";

        // People paste the WLED web address: accept "http://host[:port]/path" and keep only the host.
        if (normalized.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            normalized = uri.Host.Trim('[', ']');
        else
            normalized = normalized.TrimEnd('/');

        if (normalized.Contains(' ')) return "An address can't contain spaces. Use an IP address like 192.168.1.40 or a name like wled-tv.local.";

        // Something that looks like an IPv4 address must be a valid one (catches 192.168.1.300 and 192.168.1).
        bool digitsAndDots = normalized.All(c => char.IsAsciiDigit(c) || c == '.');
        if (digitsAndDots)
        {
            var parts = normalized.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3
                    || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v > 255))
                return "That is not a valid IP address. It should look like 192.168.1.40 (four numbers from 0 to 255).";
            if (normalized == "0.0.0.0" || normalized == "255.255.255.255")
                return "Enter the address of the controller itself, not 0.0.0.0 or a broadcast address.";
            return null;
        }

        if (normalized.Contains(':'))
        {
            return IPAddress.TryParse(normalized, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? null
                : "Enter the address without a port (set the port separately), e.g. 192.168.1.40.";
        }

        // Host name (RFC 1123): labels of letters, digits and hyphens, not starting/ending with a hyphen.
        if (normalized.Length > 253) return "That host name is too long.";
        foreach (var label in normalized.TrimEnd('.').Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')
                || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return "That is not a valid host name. Use letters, digits and hyphens, e.g. wled-tv.local, or an IP address.";
        }
        return null;
    }

    /// <summary>
    /// Checks a UDP port typed by the user. Empty (or "0") means the protocol default, returned as 0.
    /// </summary>
    public static string? ValidatePort(string? text, int defaultPort, out int port)
    {
        port = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t == "0" || t.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p is < 1 or > 65535)
            return $"Use a port between 1 and 65535, or leave it empty for the default ({defaultPort}).";
        port = p == defaultPort ? 0 : p;
        return null;
    }

    /// <summary>Checks an E1.31 start universe (1–63999).</summary>
    public static string? ValidateUniverse(string? text, out int universe)
    {
        universe = 1;
        if (!int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int u) || u is < 1 or > 63999)
            return "Use a universe between 1 and 63999.";
        universe = u;
        return null;
    }
}
