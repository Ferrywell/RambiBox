using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.App.Dev;

/// <summary>
/// "Find devices" in <c>--simulate</c> / <c>--demo</c>: never touches real ports or the network, just reports a few
/// plausible controllers after a short delay (screenshots, UI testing).
/// </summary>
public static class SimulatedDiscovery
{
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2500, timeout.TotalMilliseconds)), ct);
        return
        [
            new(DeviceType.Wled, "A8032A1B2C3D", "WLED-TV (192.168.2.40)", "WLED 0.15.0 · ESP32", 120, Host: "192.168.2.40"),
            new(DeviceType.Adalight, "COM3", "USB-SERIAL CH340 (COM3)", "CH340 · 1A86:7523", Port: "COM3"),
            new(DeviceType.Wled, "C45BBE4F5A6B", "WLED-Desk (192.168.2.57)", "WLED 0.14.4 · ESP8266", 60, Host: "192.168.2.57"),
            new(DeviceType.Adalight, "COM5", "Serial controller on COM5",
                "USB-SERIAL CH340 (1A86:7523) · no Adalight greeting: Ardulight, AWA or TPM2 controller, or another device",
                Port: "COM5", Verified: false),
        ];
    }
}
