using RambiBox.Core.Devices.Hid;

namespace RambiBox.Tests.Devices.Hid;

/// <summary>In-memory HID bus: devices can be plugged/unplugged, every written report is recorded per device.</summary>
internal sealed class FakeHidTransport : IHidTransport
{
    private readonly object _gate = new();
    private readonly List<HidDeviceInfo> _present = [];
    public readonly List<FakeHidConnection> Opened = [];
    public int EnumerateCalls;
    public int OpenFailError; // != 0: Open throws HidException with this error
    public Func<HidDeviceInfo, byte[]>? InputReport;

    public static HidDeviceInfo Lightpack(string? serial, string? path = null, bool old = false, int outLen = 65)
        => new(path ?? $@"\\?\hid#vid_{(old ? "03eb" : "1d50")}&pid_{(old ? "204f" : "6022")}#{serial ?? Guid.NewGuid().ToString("N")}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            old ? LightpackProtocol.OldVendorId : LightpackProtocol.VendorId,
            old ? LightpackProtocol.OldProductId : LightpackProtocol.ProductId,
            0x0001, serial, "Lightpack", 65, outLen, 0xFF00, 0x01);

    public void Plug(HidDeviceInfo d)
    {
        lock (_gate) _present.Add(d);
    }

    public void Unplug(HidDeviceInfo d)
    {
        lock (_gate)
        {
            _present.RemoveAll(x => x.Path == d.Path);
            foreach (var c in Opened.Where(c => c.Device.Path == d.Path)) c.Unplugged = true;
        }
    }

    public IReadOnlyList<HidDeviceInfo> Enumerate(Func<ushort, ushort, bool> match)
    {
        lock (_gate)
        {
            EnumerateCalls++;
            return _present.Where(d => match(d.VendorId, d.ProductId)).ToList();
        }
    }

    public IHidConnection Open(HidDeviceInfo device, bool readOnly = false)
    {
        lock (_gate)
        {
            if (OpenFailError != 0) throw new HidException("fake open failure", OpenFailError);
            if (!_present.Any(d => d.Path == device.Path)) throw new HidException("not present", 2);
            var c = new FakeHidConnection(this, device, readOnly);
            Opened.Add(c);
            return c;
        }
    }

    /// <summary>All reports written to the device with this path, across connections, in order.</summary>
    public List<byte[]> Writes(HidDeviceInfo d)
    {
        lock (_gate) return Opened.Where(c => c.Device.Path == d.Path).SelectMany(c => c.Snapshot()).ToList();
    }

    public List<byte[]> WritesWithCommand(HidDeviceInfo d, byte command) => Writes(d).Where(r => r[1] == command).ToList();

    public byte[] Input(HidDeviceInfo d) => InputReport?.Invoke(d) ?? [];
}

internal sealed class FakeHidConnection(FakeHidTransport bus, HidDeviceInfo device, bool readOnly) : IHidConnection
{
    private readonly List<byte[]> _writes = [];
    public HidDeviceInfo Device { get; } = device;
    public bool ReadOnly { get; } = readOnly;
    public volatile bool Unplugged;
    public volatile bool Disposed;
    public int FailNextWrites;

    public bool Write(ReadOnlySpan<byte> report, int timeoutMs, out int error)
    {
        error = 0;
        if (ReadOnly) throw new InvalidOperationException("discovery must never write");
        if (Disposed || Unplugged)
        {
            error = 1167; // ERROR_DEVICE_NOT_CONNECTED
            return false;
        }
        if (FailNextWrites > 0)
        {
            FailNextWrites--;
            error = 31;
            return false;
        }
        lock (_writes) _writes.Add(report.ToArray());
        return true;
    }

    public bool TryGetInputReport(Span<byte> buffer)
    {
        var data = bus.Input(Device);
        if (data.Length == 0) return false;
        buffer.Clear();
        data.AsSpan(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
        return true;
    }

    public void CancelIo() { }

    public void Dispose() => Disposed = true;

    public List<byte[]> Snapshot()
    {
        lock (_writes) return _writes.ToList();
    }
}
