using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

public enum AutoSwitchAction
{
    /// <summary>Nothing new was found; leave everything as it is.</summary>
    None,
    /// <summary>Exactly one controller and the user hasn't set anything up yet: use it automatically.</summary>
    Switch,
    /// <summary>One controller found, but the user configured another device: offer it, don't switch.</summary>
    Suggest,
    /// <summary>Several controllers found: ask the user to choose one.</summary>
    Choose,
}

/// <param name="Action">What to do.</param>
/// <param name="Candidates">The usable controllers that differ from the current configuration.</param>
public sealed record AutoSwitchDecision(AutoSwitchAction Action, IReadOnlyList<DiscoveredDevice> Candidates)
{
    /// <summary>The single candidate for <see cref="AutoSwitchAction.Switch"/> / <see cref="AutoSwitchAction.Suggest"/>.</summary>
    public DiscoveredDevice? Device => Candidates.Count == 1 ? Candidates[0] : null;
}

/// <summary>
/// Decides what the background discovery at startup may do with its results. It never switches away from a device
/// the user set up themselves: automatic switching only happens when the device settings are still untouched
/// defaults (first run without an AmbiBox import, or Adalight on "Automatic" that never found anything).
/// </summary>
public static class DeviceAutoSwitchPolicy
{
    /// <summary>True when nobody has chosen or ever successfully used a device with these settings.</summary>
    public static bool IsUntouchedDefault(DeviceSettings s) =>
        s.Type == DeviceType.Adalight
        && string.Equals(s.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
        && s.LastPort is null && s.LastUsbId is null
        && string.IsNullOrWhiteSpace(s.Host) && s.DeviceId is null;

    /// <summary>True when <paramref name="d"/> is what <paramref name="s"/> already points at (nothing to switch to).</summary>
    public static bool IsCurrent(DeviceSettings s, DiscoveredDevice d)
    {
        if (d.Type != s.Type) return false;
        var info = DeviceCatalog.Get(d.Type);
        if (info.IsSerial)
            return string.Equals(s.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(s.Port, d.Port, StringComparison.OrdinalIgnoreCase);
        if (info.IsNetwork)
            return string.Equals(s.Host, d.Host, StringComparison.OrdinalIgnoreCase)
                   || (s.DeviceId is not null && string.Equals(s.DeviceId, d.Id, StringComparison.OrdinalIgnoreCase));
        if (info.Family == DeviceFamily.UsbHid)
            return s.DeviceId is null || string.Equals(s.DeviceId, d.Id, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <param name="current">The device settings in effect.</param>
    /// <param name="firstRunWithoutImport">First start and nothing was imported from AmbiBox.</param>
    /// <param name="found">Everything the discovery reported.</param>
    /// <param name="isSupported">Whether this build has a driver for a type (<see cref="LedDeviceFactory.IsSupported"/>).</param>
    public static AutoSwitchDecision Decide(DeviceSettings current, bool firstRunWithoutImport,
        IReadOnlyList<DiscoveredDevice> found, Func<DeviceType, bool> isSupported)
    {
        // Unverified candidates (a USB-serial port that didn't greet: maybe a 3D printer) are only listed in the UI.
        var candidates = found
            .Where(d => d.Verified && d.Type != DeviceType.Virtual && isSupported(d.Type) && !IsCurrent(current, d))
            .GroupBy(d => (d.Type, Id: d.Id.ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();
        if (candidates.Count == 0) return new(AutoSwitchAction.None, candidates);
        if (candidates.Count > 1) return new(AutoSwitchAction.Choose, candidates);
        bool mayAutoSwitch = firstRunWithoutImport || IsUntouchedDefault(current);
        return new(mayAutoSwitch ? AutoSwitchAction.Switch : AutoSwitchAction.Suggest, candidates);
    }
}
