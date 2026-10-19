using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Hid;

/// <summary>
/// Lists attached Lightpacks (USB HID 1D50:6022 and the early 03EB:204F). Read-only: devices are enumerated, and each
/// Lightpack's firmware version is read with a GET_REPORT request on a read-only handle; nothing is ever written, so it
/// is safe while a <see cref="LightpackDevice"/> (or Prismatik) drives them.
/// <para>
/// One entry per device (Id = serial number, or the interface path when the device has none). With two or more
/// devices an extra first entry "All Lightpacks" (Id "*") selects all of them chained in Prismatik's order.
/// </para>
/// </summary>
public sealed class LightpackDiscovery : IDeviceDiscovery
{
    /// <summary>DeviceSettings.DeviceId value meaning "all attached Lightpacks, chained".</summary>
    public const string AllDevicesId = "*";

    private readonly IHidTransport _hid;

    public LightpackDiscovery() : this(Win32HidTransport.Shared) { }

    internal LightpackDiscovery(IHidTransport hid) => _hid = hid;

    public IReadOnlyList<DeviceType> Types { get; } = [DeviceType.Lightpack];

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var scan = Task.Run(() => Scan(ct), ct);
            return await scan.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (TimeoutException)
        {
            Log.Warn($"Lightpack discovery did not finish within {timeout.TotalMilliseconds:F0} ms");
            return [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Lightpack discovery failed: {ex.Message}");
            return [];
        }
    }

    internal IReadOnlyList<DiscoveredDevice> Scan(CancellationToken ct)
    {
        var devices = LightpackProtocol.OrderLikePrismatik(
            _hid.Enumerate(LightpackProtocol.IsLightpackId).Where(LightpackProtocol.IsUsable));
        var result = new List<DiscoveredDevice>();
        for (int i = 0; i < devices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = devices[i];
            string fw = ReadFirmware(d) ?? "unknown";
            string name = d.SerialNumber is { Length: > 0 } s ? $"Lightpack (serial {s})" : $"Lightpack #{i + 1}";
            result.Add(new DiscoveredDevice(
                DeviceType.Lightpack,
                Id: d.SerialNumber is { Length: > 0 } sn ? sn : d.Path,
                DisplayName: name,
                Detail: $"Firmware {fw}, {LightpackProtocol.LedsPerDevice} LEDs, USB {d.UsbId}",
                LedCount: LightpackProtocol.LedsPerDevice));
        }
        if (devices.Count >= 2)
        {
            result.Insert(0, new DiscoveredDevice(
                DeviceType.Lightpack,
                Id: AllDevicesId,
                DisplayName: $"All Lightpacks ({devices.Count} devices)",
                Detail: $"Chained like Prismatik, {LightpackProtocol.LedsPerDevice} LEDs each in this order: " +
                        string.Join(", ", devices.Select((d, i) => d.SerialNumber ?? $"#{i + 1}")),
                LedCount: devices.Count * LightpackProtocol.LedsPerDevice));
        }
        return result;
    }

    private string? ReadFirmware(HidDeviceInfo d)
    {
        try
        {
            using var conn = _hid.Open(d, readOnly: true);
            var buf = new byte[Math.Max(LightpackProtocol.ReportLength, d.InputReportLength)];
            return conn.TryGetInputReport(buf) && LightpackProtocol.TryParseFirmwareVersion(buf, out int ma, out int mi, out int un)
                ? LightpackProtocol.FormatFirmware(ma, mi, un)
                : null;
        }
        catch
        {
            return null; // in use exclusively / unplugged meanwhile: still list it
        }
    }
}
