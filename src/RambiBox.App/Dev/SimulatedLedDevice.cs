using System.Diagnostics;
using RambiBox.Core;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.App.Dev;

/// <summary>
/// Fake LED controller for running the UI without touching real hardware (<c>--simulate</c>).
/// For the serial types it behaves like a CH340 on COM3: "finds" the device after a short search and paces frames
/// like a real serial link would (≈33 fps for 112 LEDs at 115200 baud). For the other types it pretends to reach the
/// configured host / device and runs at 60 fps; a network type without an address keeps searching.
/// </summary>
public sealed class SimulatedLedDevice : ILedDevice
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private double _nextSlot, _fpsWindow;
    private int _frames, _leds = 1, _baud = 115200;
    private double _fps;
    private bool _started, _disposed;
    private DeviceStatus _status = DeviceStatus.Stopped;
    private string? _port;
    private DeviceSettings _settings = new();
    private int _generation;

    private readonly SerialPortInfo SimPort;

    /// <param name="demo">Demo mode (<c>--demo</c>): describe the port like a real CH340, for clean screenshots.</param>
    public SimulatedLedDevice(bool demo = false) =>
        SimPort = new("COM3", demo ? "USB-SERIAL CH340" : "USB-SERIAL CH340 (simulated)", "1A86:7523");

    public DeviceStatus Status => _status;
    public string? PortName => _port;
    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound;

    public void Configure(DeviceSettings settings, int ledCount)
    {
        int generation;
        lock (_lock)
        {
            _leds = Math.Max(1, ledCount);
            _baud = Math.Max(1200, settings.BaudRate);
            bool targetChanged = _settings.Type != settings.Type || !string.Equals(_settings.Host, settings.Host, StringComparison.OrdinalIgnoreCase)
                                 || _settings.E131Multicast != settings.E131Multicast;
            _settings = new DeviceSettings
            {
                Type = settings.Type, Host = settings.Host, E131Multicast = settings.E131Multicast, DeviceId = settings.DeviceId,
            };
            if (_started && !targetChanged) return;
            _started = true;
            generation = ++_generation;
        }
        var type = settings.Type;
        Log.Info($"Simulated LED device ({type}): searching");
        SetStatus(DeviceStatus.Searching, null);
        bool needsHost = DeviceCatalog.IsNetwork(type) && !(type == DeviceType.E131 && settings.E131Multicast);
        if (needsHost && string.IsNullOrWhiteSpace(settings.Host)) return; // nothing to reach
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(800);
            if (_disposed || generation != Volatile.Read(ref _generation)) return;
            string port = DeviceCatalog.IsSerial(type) ? SimPort.PortName
                : type switch
                {
                    DeviceType.Lightpack => "Lightpack",
                    DeviceType.AlienFx => "AlienFX",
                    DeviceType.Virtual => "Virtual",
                    DeviceType.E131 when settings.E131Multicast => "multicast",
                    _ => settings.Host!,
                };
            SetStatus(DeviceStatus.Connected, port);
            Log.Info($"Simulated LED device connected: {type} on {port} ({_leds} LEDs)");
            if (DeviceCatalog.IsSerial(type)) DeviceFound?.Invoke(SimPort);
        });
    }

    private void SetStatus(DeviceStatus s, string? port)
    {
        _status = s;
        _port = port;
        try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("StatusChanged handler failed", ex); }
    }

    public double MaxFrameRate => DeviceCatalog.IsSerial(_settings.Type) ? _baud / 10.0 / (6 + 3.0 * _leds) : 60;
    public double FramesPerSecond => _fps;

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _nextSlot = Math.Max(now, _nextSlot) + 1.0 / MaxFrameRate;
        _frames++;
        if (now - _fpsWindow >= 1)
        {
            _fps = _frames / (now - _fpsWindow);
            _frames = 0;
            _fpsWindow = now;
        }
    }

    /// <summary>
    /// Paced per granted slot (not only per submitted frame), like a real link that is busy for one frame period:
    /// the engine skips Submit when a frame is unchanged, and must not spin in that case.
    /// </summary>
    public bool WaitForSlot(int timeoutMs)
    {
        if (_status != DeviceStatus.Connected) return false;
        double now = _clock.Elapsed.TotalSeconds;
        double next = Math.Max(_nextSlot, _lastGrant + 1.0 / MaxFrameRate);
        double wait = next - now;
        if (wait * 1000 > timeoutMs)
        {
            Thread.Sleep(timeoutMs);
            return false;
        }
        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        _lastGrant = _clock.Elapsed.TotalSeconds;
        return true;
    }
    private double _lastGrant = -1;

    public void Rescan() => Log.Info("Simulated LED device: rescan requested");

    public void Blackout() => Log.Info("Simulated LED device: blackout");

    public IReadOnlyList<SerialPortInfo> ListPorts() => [SimPort];

    public void Dispose()
    {
        _disposed = true;
        _status = DeviceStatus.Stopped;
    }
}
