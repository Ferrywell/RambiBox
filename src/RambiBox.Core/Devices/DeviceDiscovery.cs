using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>A controller found by a discovery scan, ready to be selected in the UI.</summary>
/// <param name="Type">Device family.</param>
/// <param name="Id">Stable identifier within the family (COM port, HID serial/path, WLED MAC, IP…).</param>
/// <param name="DisplayName">Short human name, e.g. "WLED-TV (192.168.2.40)" or "Lightpack #2".</param>
/// <param name="Detail">One extra line: chip, firmware, protocol…</param>
/// <param name="LedCount">LED count reported by the device itself, when it knows (WLED, Lightpack).</param>
/// <param name="Port">Serial port name for serial devices.</param>
/// <param name="Host">IP/host for network devices.</param>
public sealed record DiscoveredDevice(
    DeviceType Type,
    string Id,
    string DisplayName,
    string Detail,
    int? LedCount = null,
    string? Port = null,
    string? Host = null)
{
    /// <summary>Copies this device's identity into <paramref name="s"/> (type, port/host/id) so the app uses it.</summary>
    public void ApplyTo(DeviceSettings s)
    {
        s.Type = Type;
        if (Port is not null) s.Port = Port;
        if (Host is not null) s.Host = Host;
        s.DeviceId = Id;
    }
}

/// <summary>
/// Finds controllers of one or more device families. Implementations must be safe to run concurrently with an active
/// device (they must not open a port that is in use, and must not reset unrelated hardware), never throw, and
/// honour the timeout / cancellation.
/// </summary>
public interface IDeviceDiscovery
{
    IReadOnlyList<DeviceType> Types { get; }

    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct);
}
