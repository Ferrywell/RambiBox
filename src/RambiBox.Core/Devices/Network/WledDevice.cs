using System.Diagnostics;
using System.Net;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// WLED over Wi-Fi with its UDP realtime protocols (Prismatik's "WARLS" / "DRGB" / "DNRGB" devices), see
/// <see cref="WledPacketizer"/>. Connected once the controller answers its JSON API (GET /json/info); the reported
/// "udpport" is used when the port setting is 0, and the API is polled every 5 s so a controller that went away shows
/// as Searching. When the host stops answering and <see cref="DeviceSettings.DeviceId"/> holds its MAC (from discovery),
/// the controller is looked up again via mDNS, so a new DHCP address is followed automatically.
/// <para>WLED behaviour: the first realtime packet switches WLED into realtime mode (its own effect pauses, the web UI
/// shows "realtime"). It leaves realtime mode <c>timeout</c> seconds after the last packet and restores its previous
/// state (on/off, effect, brightness). RambiBox sends a keep-alive every second, so a 2 s timeout never blanks the strip;
/// on stop it sends black with timeout 1 so WLED returns to its own state about a second later instead of after the
/// configured timeout (or never, for 255). WLED applies its own brightness and gamma setting to realtime data.</para>
/// </summary>
[LedDevice(DeviceType.Wled)]
public sealed class WledDevice : UdpLedDevice
{
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(2.5);
    private long _nextRelocate;
    private volatile WledInfo? _info;
    private string? _truncationLogged;

    public WledDevice() : base("WLED") { }

    /// <summary>What the controller reported at the last successful connect (null before).</summary>
    public WledInfo? Info => _info;

    /// <summary>HTTP port of the JSON API (80; tests use an emulated WLED on another port).</summary>
    internal int HttpPort { get; set; } = 80;

    /// <summary>Allows the MAC-based mDNS relocation (tests disable it to stay off the LAN).</summary>
    internal bool AllowRelocation { get; set; } = true;

    private protected override int DefaultPort => WledPacketizer.DefaultPort;

    private protected override UdpPacketizer CreatePacketizer(NetSettings s) =>
        new WledPacketizer(s.Protocol, s.LedCount, s.TimeoutSeconds, s.Order);

    private protected override void OnPacketizerBuilt(UdpPacketizer packetizer, NetSettings s)
    {
        if (packetizer is not WledPacketizer w) return;
        string key = $"{w.Protocol}/{w.RequestedLeds}";
        if (w.Truncated && _truncationLogged != key)
        {
            _truncationLogged = key;
            Log.Warn($"WLED: {w.Protocol} carries at most {w.LedCount} LEDs; the other {w.RequestedLeds - w.LedCount} stay dark (choose DNRGB or Auto)");
        }
        else if (!w.Truncated) _truncationLogged = null;
    }

    private protected override string? Probe(NetSettings s, ref IPAddress? address, ref int port, CancellationToken ct)
    {
        WledInfo? info = address is null ? null : WledApi.GetInfoAsync(address, HttpPort, InfoTimeout, ct).GetAwaiter().GetResult();
        if (info is null && AllowRelocation && WledDiscovery.NormalizeMac(s.DeviceId) is { } mac
            && Stopwatch.GetTimestamp() >= _nextRelocate && !ct.IsCancellationRequested)
        {
            _nextRelocate = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            var found = WledDiscovery.FindByMacAsync(mac, TimeSpan.FromSeconds(3), ct).GetAwaiter().GetResult();
            if (found is not null)
            {
                Log.Info($"WLED: {found.Value.Info.Name} ({found.Value.Info.FormattedMac}) found at {found.Value.Address}");
                address = found.Value.Address;
                info = found.Value.Info;
            }
        }
        if (address is null) return $"cannot resolve '{s.Host}'";
        if (info is null) return $"no answer from WLED at {address} (http://{address}/json/info)";

        _info = info;
        if (s.ExplicitPort == 0 && info.UdpPort is > 0 and <= 65535) port = info.UdpPort;
        var proto = WledPacketizer.Choose(s.Protocol, s.LedCount);
        Log.Info($"WLED: '{info.Name}' {info.Version} ({info.Architecture ?? "?"}, {info.FormattedMac}), {info.LedCount} LEDs; sending {proto}, timeout {Math.Clamp(s.TimeoutSeconds, 1, 255)} s");
        if (info.LedCount > 0 && info.LedCount < s.LedCount)
            Log.Warn($"WLED: the controller has {info.LedCount} LEDs but the layout has {s.LedCount}; the last {s.LedCount - info.LedCount} are ignored");
        return null;
    }

    private protected override Task<bool>? StartHealthCheck(NetSettings s, IPAddress? address, CancellationToken ct) =>
        address is null ? null : CheckAsync(address, ct);

    private async Task<bool> CheckAsync(IPAddress address, CancellationToken ct) =>
        await WledApi.GetInfoAsync(address, HttpPort, InfoTimeout, ct).ConfigureAwait(false) is not null;
}
