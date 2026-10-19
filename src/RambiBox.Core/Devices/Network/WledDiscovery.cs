using System.Net;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// Finds WLED controllers on the LAN: an mDNS browse for "_wled._tcp.local" (WLED advertises it since 0.10;
/// PTR → SRV/A records), then GET http://ip/json/info for name, LED count, MAC and version.
/// Results have <see cref="DeviceType.Wled"/>; WLED also receives DDP and E1.31, so when the user picked one of those
/// types the UI can use a result with <c>device with { Type = … }</c>. Never throws; honours timeout and cancellation.
/// </summary>
public sealed class WledDiscovery : IDeviceDiscovery
{
    public const string ServiceType = "_wled._tcp.local";

    public IReadOnlyList<DeviceType> Types { get; } = [DeviceType.Wled, DeviceType.Ddp, DeviceType.E131];

    /// <summary>HTTP port of the JSON API (tests use an emulated WLED).</summary>
    internal int HttpPort { get; init; } = 80;

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var found = await FindAsync(timeout, ct).ConfigureAwait(false);
            return found.Select(f => ToDiscovered(f.Address, f.Info)).ToList();
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log.Warn($"WLED discovery failed: {ex.Message}");
            return Array.Empty<DiscoveredDevice>();
        }
    }

    /// <summary>Maps a /json/info answer to the UI's entry.</summary>
    internal static DiscoveredDevice ToDiscovered(IPAddress address, WledInfo info) => new(
        DeviceType.Wled,
        Id: info.Mac.Length > 0 ? info.Mac : address.ToString(),
        DisplayName: $"{info.Name} ({address})",
        Detail: $"WLED {info.Version} · {info.LedCount} LEDs",
        LedCount: info.LedCount > 0 ? info.LedCount : null,
        Host: address.ToString());

    /// <summary>
    /// Browses mDNS for about 60 % of the time budget (max 3 s), then queries every responder's JSON API in parallel
    /// with the rest (at least 1 s).
    /// </summary>
    internal async Task<List<(IPAddress Address, WledInfo Info)>> FindAsync(TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var browse = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds * 0.6, 300, 3000));
        var services = await MdnsClient.BrowseAsync(ServiceType, browse, ct).ConfigureAwait(false);
        var addresses = services.Select(s => s.Address).Distinct().ToList();
        if (addresses.Count == 0 || ct.IsCancellationRequested) return [];
        var left = timeout - (DateTime.UtcNow - start);
        if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
        return await QueryAllAsync(addresses, HttpPort, left, ct).ConfigureAwait(false);
    }

    internal static async Task<List<(IPAddress Address, WledInfo Info)>> QueryAllAsync(IEnumerable<IPAddress> addresses, int httpPort,
        TimeSpan timeout, CancellationToken ct)
    {
        var tasks = addresses.Select(async a => (Address: a, Info: await WledApi.GetInfoAsync(a, httpPort, timeout, ct).ConfigureAwait(false))).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var list = new List<(IPAddress, WledInfo)>();
        foreach (var (a, info) in results)
        {
            if (info is not null) list.Add((a, info));
            else Log.Info($"WLED discovery: {a} advertises WLED but /json/info did not answer");
        }
        return list;
    }

    /// <summary>Looks for the controller with MAC <paramref name="mac"/> (normalized, 12 hex digits).</summary>
    internal static async Task<(IPAddress Address, WledInfo Info)?> FindByMacAsync(string mac, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var all = await new WledDiscovery().FindAsync(timeout, ct).ConfigureAwait(false);
            foreach (var f in all)
                if (NormalizeMac(f.Info.Mac) == mac) return f;
        }
        catch (Exception) { /* not found */ }
        return null;
    }

    /// <summary>"AA:BB:CC:DD:EE:FF", "aa-bb-…" or "aabbccddeeff" → "aabbccddeeff"; null when not a MAC.</summary>
    internal static string? NormalizeMac(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s.Where(c => c is not (':' or '-' or '.' or ' ')).ToArray()).ToLowerInvariant();
        return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? hex : null;
    }
}
