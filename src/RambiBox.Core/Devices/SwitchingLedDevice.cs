using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// The device the engine talks to: owns the driver for the configured <see cref="DeviceSettings.Type"/> and swaps it
/// live when the type changes (inside <see cref="Configure"/>, i.e. on the engine thread). The old driver is blacked
/// out and disposed first so a controller never keeps showing a stale frame.
/// </summary>
public sealed class SwitchingLedDevice : ILedDevice
{
    private readonly Func<DeviceType, ILedDevice> _create;
    private readonly object _gate = new();
    private ILedDevice? _current;
    private DeviceType? _currentType;
    private bool _disposed;

    public SwitchingLedDevice() : this(LedDeviceFactory.Create) { }

    /// <param name="create">Driver factory (tests inject fakes).</param>
    public SwitchingLedDevice(Func<DeviceType, ILedDevice> create) => _create = create;

    /// <summary>The device type whose driver is active (null before the first Configure).</summary>
    public DeviceType? ActiveType { get { lock (_gate) return _currentType; } }

    /// <summary>The active driver (for type-specific UI such as the serial port list). May change after Configure.</summary>
    public ILedDevice? ActiveDriver { get { lock (_gate) return _current; } }

    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound;

    public void Configure(DeviceSettings settings, int ledCount)
    {
        ILedDevice? old = null, now;
        lock (_gate)
        {
            if (_disposed) return;
            if (_current is null || _currentType != settings.Type)
            {
                old = _current;
                if (old is not null) Detach(old);
                _current = _create(settings.Type);
                _currentType = settings.Type;
                Attach(_current);
                Log.Info($"LED device type: {settings.Type}");
            }
            now = _current;
        }
        if (old is not null)
        {
            try { old.Blackout(); } catch (Exception ex) { Log.Warn($"Blackout of the previous device failed: {ex.Message}"); }
            try { old.Dispose(); } catch (Exception ex) { Log.Warn($"Disposing the previous device failed: {ex.Message}"); }
        }
        now.Configure(settings, ledCount);
        if (old is not null) StatusChanged?.Invoke();
    }

    private void Attach(ILedDevice d)
    {
        d.StatusChanged += OnStatusChanged;
        d.DeviceFound += OnDeviceFound;
    }

    private void Detach(ILedDevice d)
    {
        d.StatusChanged -= OnStatusChanged;
        d.DeviceFound -= OnDeviceFound;
    }

    private void OnStatusChanged() => StatusChanged?.Invoke();
    private void OnDeviceFound(SerialPortInfo p) => DeviceFound?.Invoke(p);

    private ILedDevice? Cur { get { lock (_gate) return _current; } }

    public DeviceStatus Status => Cur?.Status ?? DeviceStatus.Stopped;
    public string? PortName => Cur?.PortName;
    public void Submit(ReadOnlySpan<byte> rgb) => Cur?.Submit(rgb);
    public bool WaitForSlot(int timeoutMs) => Cur?.WaitForSlot(timeoutMs) ?? false;
    public double MaxFrameRate => Cur?.MaxFrameRate ?? 0;
    public double FramesPerSecond => Cur?.FramesPerSecond ?? 0;
    public void Rescan() => Cur?.Rescan();
    public void Blackout() => Cur?.Blackout();

    /// <summary>Serial ports are listed regardless of the active type (the UI shows them for the serial families).</summary>
    public IReadOnlyList<SerialPortInfo> ListPorts() => Cur?.ListPorts() is { Count: > 0 } p ? p : SerialPortList.Value();

    // A serial enumerator independent of the active driver (e.g. while a network device is active).
    private static readonly Lazy<Func<IReadOnlyList<SerialPortInfo>>> SerialPortList = new(() =>
    {
        var probe = new AdalightDevice();
        return probe.ListPorts;
    });

    public void Dispose()
    {
        ILedDevice? d;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            d = _current;
            _current = null;
            if (d is not null) Detach(d);
        }
        d?.Dispose();
    }
}
