using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.AlienFx;

/// <summary>
/// Reports the Alienware lighting system when LightFX.dll is installed, initializes and has at least one device with
/// lights. Only queries (LFX_Initialize / GetNumDevices / GetNumLights / GetDeviceDescription / LFX_Release); never
/// changes a light. Initialization is reference counted, so this is safe while <see cref="AlienFxDevice"/> runs.
/// </summary>
public sealed class AlienFxDiscovery : IDeviceDiscovery
{
    /// <summary>DeviceSettings.DeviceId of the single AlienFX entry (all LightFX devices are driven together).</summary>
    public const string DeviceId = "lightfx";

    private readonly ILightFxProvider _provider;

    public AlienFxDiscovery() : this(NativeLightFx.Shared) { }

    internal AlienFxDiscovery(ILightFxProvider provider) => _provider = provider;

    public IReadOnlyList<DeviceType> Types { get; } = [DeviceType.AlienFx];

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var scan = Task.Run(Scan, ct);
            return await scan.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (TimeoutException)
        {
            Log.Warn($"AlienFX discovery did not finish within {timeout.TotalMilliseconds:F0} ms");
            return [];
        }
        catch (Exception ex)
        {
            Log.Warn($"AlienFX discovery failed: {ex.Message}");
            return [];
        }
    }

    internal IReadOnlyList<DiscoveredDevice> Scan()
    {
        var api = _provider.TryGet(out _);
        if (api is null) return [];
        uint r = api.Initialize();
        if (r is not (LfxResult.Success or LfxResult.NoDevs)) return [];
        try
        {
            if (r != LfxResult.Success || api.GetNumDevices(out uint devices) != LfxResult.Success) return [];
            int total = 0;
            var parts = new List<string>();
            for (uint d = 0; d < Math.Min(devices, 64u); d++)
            {
                if (api.GetNumLights(d, out uint n) != LfxResult.Success || n == 0) continue;
                total += (int)Math.Min(n, 1024u);
                string desc = api.GetDeviceDescription(d, out string s, out _) == LfxResult.Success && s.Length > 0 ? s : $"device {d}";
                parts.Add($"{desc} ({n} lights)");
            }
            if (total == 0) return [];
            return
            [
                new DiscoveredDevice(DeviceType.AlienFx, DeviceId, "Alienware AlienFX",
                    $"LightFX: {string.Join(", ", parts)}", LedCount: total),
            ];
        }
        finally
        {
            api.Release();
        }
    }
}
