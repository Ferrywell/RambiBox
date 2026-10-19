using RambiBox.Core.Devices.Network;
using Xunit.Abstractions;

namespace RambiBox.Tests.Devices.Network;

/// <summary>
/// Runs the real mDNS discovery on this PC's LAN (multicast queries only, plus GET /json/info to WLEDs that answer;
/// never LED data). Excluded from CI via the Hardware category; prints what it found.
/// </summary>
[Trait("Category", "Hardware")]
public class LanDiscoveryHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browse_lan_for_wled_and_any_dns_sd_service()
    {
        output.WriteLine("Interfaces: " + string.Join(", ", MdnsClient.LocalIPv4Addresses()));
        var services = await MdnsClient.BrowseAsync("_services._dns-sd._udp.local", TimeSpan.FromSeconds(3), default);
        output.WriteLine($"DNS-SD service enumeration: {services.Count} answer(s)");
        foreach (var s in services) output.WriteLine($"  {s.Instance} from {s.Address}");

        var found = await new WledDiscovery().DiscoverAsync(TimeSpan.FromSeconds(4), default);
        output.WriteLine($"WLED devices: {found.Count}");
        foreach (var d in found) output.WriteLine($"  {d.DisplayName} | {d.Detail} | id {d.Id} | host {d.Host}");
    }
}
