using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace RambiBox.Core.Devices.Network;

/// <summary>What a WLED controller reports at GET /json/info.</summary>
/// <param name="Name">Device name ("WLED-TV").</param>
/// <param name="Version">Firmware version ("0.15.0").</param>
/// <param name="LedCount">leds.count — LEDs configured in WLED.</param>
/// <param name="Mac">MAC address as WLED prints it (12 lowercase hex digits), or "".</param>
/// <param name="UdpPort">"udpport": WLED's UDP notifier port, which is also where it receives realtime (WARLS/DRGB/DNRGB) data.</param>
/// <param name="Architecture">"arch": "esp32", "esp8266", …</param>
public sealed record WledInfo(string Name, string Version, int LedCount, string Mac, int UdpPort, string? Architecture)
{
    /// <summary>Parses the /json/info document (or the "info" object of /json); null when it does not look like WLED.</summary>
    public static WledInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("info", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
            if (!root.TryGetProperty("leds", out var leds) || leds.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("ver", out var ver)) return null;
            int count = leds.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? n : 0;
            return new WledInfo(
                Str(root, "name") ?? "WLED",
                ver.ValueKind == JsonValueKind.String ? ver.GetString() ?? "" : ver.ToString(),
                count,
                (Str(root, "mac") ?? "").ToLowerInvariant(),
                root.TryGetProperty("udpport", out var up) && up.TryGetInt32(out int u) ? u : 0,
                Str(root, "arch"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"aa:bb:cc:dd:ee:ff" for display; the raw value when it is not 12 hex digits.</summary>
    public string FormattedMac => Mac.Length == 12 && Mac.All(Uri.IsHexDigit)
        ? string.Join(":", Enumerable.Range(0, 6).Select(i => Mac.Substring(i * 2, 2)))
        : Mac;
}

/// <summary>WLED's JSON HTTP API (only GET /json/info, read-only).</summary>
internal static class WledApi
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        AllowAutoRedirect = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // per-request via cancellation
        MaxResponseContentBufferSize = 256 * 1024,
    };

    /// <summary>GET http://ip:port/json/info. Null when the host does not answer as WLED in time. Never throws.</summary>
    public static async Task<WledInfo?> GetInfoAsync(IPAddress ip, int httpPort, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            string host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
            var uri = new Uri($"http://{host}:{httpPort}/json/info");
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return WledInfo.Parse(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
