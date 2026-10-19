using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// A device without hardware: always "connected", paced like a 60 fps link, and it simply keeps the last frame
/// (the app's live preview shows the colors). Useful for testing layouts and effects, and for demos.
/// </summary>
[LedDevice(DeviceType.Virtual)]
public sealed class VirtualLedDevice : ILedDevice
{
    private const double FrameRate = 60;
    private readonly object _gate = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private byte[] _last = Array.Empty<byte>();
    private double _nextSlot;
    private int _frames;
    private double _fpsWindowStart;
    private double _fps;
    private bool _configured;

    public DeviceStatus Status => _configured ? DeviceStatus.Connected : DeviceStatus.Stopped;
    public string? PortName => _configured ? "Virtual" : null;
    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }

    public void Configure(DeviceSettings settings, int ledCount)
    {
        bool changed = !_configured;
        _configured = true;
        if (changed) StatusChanged?.Invoke();
    }

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        lock (_gate)
        {
            if (_last.Length != rgb.Length) _last = new byte[rgb.Length];
            rgb.CopyTo(_last);
            _frames++;
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _fpsWindowStart >= 1)
            {
                _fps = _frames / (now - _fpsWindowStart);
                _frames = 0;
                _fpsWindowStart = now;
            }
        }
    }

    /// <summary>The last submitted frame (R,G,B per LED). For tests and diagnostics.</summary>
    public byte[] LastFrame
    {
        get { lock (_gate) return (byte[])_last.Clone(); }
    }

    public bool WaitForSlot(int timeoutMs)
    {
        if (!_configured) return false;
        double now = _clock.Elapsed.TotalSeconds;
        double wait = _nextSlot - now;
        if (wait * 1000 > timeoutMs) return false;
        if (wait > 0.0005) Thread.Sleep(TimeSpan.FromSeconds(wait));
        _nextSlot = Math.Max(now, _nextSlot) + 1.0 / FrameRate;
        return true;
    }

    public double MaxFrameRate => FrameRate;
    public double FramesPerSecond { get { lock (_gate) return _fps; } }
    public void Rescan() { }
    public void Blackout() { lock (_gate) Array.Clear(_last); }
    public IReadOnlyList<SerialPortInfo> ListPorts() => Array.Empty<SerialPortInfo>();
    public void Dispose() => _configured = false;
}
