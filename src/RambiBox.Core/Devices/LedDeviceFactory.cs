using System.Reflection;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Marks an <see cref="ILedDevice"/> implementation (public parameterless constructor) as the driver for one
/// <see cref="DeviceType"/>. The factory finds drivers by this attribute, so each device family lives in its own
/// files without a central switch statement.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class LedDeviceAttribute(DeviceType type) : Attribute
{
    public DeviceType Type { get; } = type;
}

/// <summary>Creates device drivers and discovery services for each <see cref="DeviceType"/>.</summary>
public static class LedDeviceFactory
{
    private static readonly Lazy<Dictionary<DeviceType, Type>> Drivers = new(() =>
    {
        var map = new Dictionary<DeviceType, Type>();
        foreach (var t in typeof(LedDeviceFactory).Assembly.GetTypes())
        {
            if (t.IsAbstract || !typeof(ILedDevice).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) is null) continue;
            foreach (var a in t.GetCustomAttributes<LedDeviceAttribute>()) map[a.Type] = t;
        }
        return map;
    });

    private static readonly Lazy<IReadOnlyList<IDeviceDiscovery>> Discoveries = new(() =>
        typeof(LedDeviceFactory).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IDeviceDiscovery).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IDeviceDiscovery)Activator.CreateInstance(t)!)
            .ToList());

    /// <summary>Device types that have a driver in this build.</summary>
    public static IReadOnlyCollection<DeviceType> SupportedTypes => Drivers.Value.Keys;

    public static bool IsSupported(DeviceType type) => Drivers.Value.ContainsKey(type);

    /// <summary>Creates a driver for <paramref name="type"/>; falls back to the virtual device when none exists.</summary>
    public static ILedDevice Create(DeviceType type)
    {
        if (Drivers.Value.TryGetValue(type, out var t))
        {
            try { return (ILedDevice)Activator.CreateInstance(t)!; }
            catch (Exception ex) { Log.Error($"Could not create the {type} driver", ex); }
        }
        else Log.Warn($"No driver for device type {type} in this build; using the virtual device");
        return new VirtualLedDevice();
    }

    /// <summary>All discovery services in this build (serial, HID, network, …).</summary>
    public static IReadOnlyList<IDeviceDiscovery> DiscoveryServices => Discoveries.Value;

    /// <summary>Runs every discovery service in parallel; failures of one service never affect the others.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAllAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tasks = DiscoveryServices.Select(async d =>
        {
            try { return await d.DiscoverAsync(timeout, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (IReadOnlyList<DiscoveredDevice>)Array.Empty<DiscoveredDevice>(); }
            catch (Exception ex)
            {
                Log.Warn($"Device discovery ({string.Join("/", d.Types)}) failed: {ex.Message}");
                return Array.Empty<DiscoveredDevice>();
            }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }
}
