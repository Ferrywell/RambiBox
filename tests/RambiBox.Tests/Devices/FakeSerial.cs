using System.Diagnostics;
using System.Text;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>A simulated device behind a COM port.</summary>
internal sealed class FakeDevice
{
    public required string Port { get; init; }
    public string Description { get; init; } = "USB-SERIAL CH340";
    public string? UsbId { get; init; } = "1A86:7523";
    public bool IsBluetooth { get; init; }

    /// <summary>Open throws UnauthorizedAccessException.</summary>
    public volatile bool Busy;
    /// <summary>Firmware sends the greeting after open.</summary>
    public volatile bool Greets = true;
    public int GreetDelayMs { get; set; } = 15;
    public byte[] Greeting { get; set; } = Encoding.ASCII.GetBytes("Ada\n");
    /// <summary>When ≥ 0: the write with this (0-based, per device) index throws IOException once.</summary>
    public int FailAtWrite = -1;
    /// <summary>Per-write blocking time (simulates a driver that blocks until data is on the wire).</summary>
    public int WriteBlockMs { get; set; }

    public int OpenCount;
    public int WriteCount;
    public readonly List<FakeLink> Links = new();
}

internal sealed class FakeEnvironment : IPortEnumerator, ISerialLinkFactory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> OpenLog = new();

    public FakeDevice Add(FakeDevice d)
    {
        lock (_lock) _devices[d.Port] = d;
        return d;
    }

    /// <summary>Unplug: the port disappears and open links start failing.</summary>
    public void Remove(string port)
    {
        lock (_lock)
        {
            if (_devices.Remove(port, out var d))
                foreach (var l in d.Links) l.Unplugged = true;
        }
    }

    public FakeDevice Get(string port)
    {
        lock (_lock) return _devices[port];
    }

    public string[] Opened
    {
        get { lock (_lock) return OpenLog.ToArray(); }
    }

    public IReadOnlyCollection<string> GetPresentPortNames()
    {
        lock (_lock) return new HashSet<string>(_devices.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SerialPortInfo> ListPorts()
    {
        lock (_lock)
            return _devices.Values
                .Select(d => new SerialPortInfo(d.Port, d.Description, d.IsBluetooth ? null : d.UsbId) { IsBluetooth = d.IsBluetooth })
                .OrderBy(p => p.PortName, StringComparer.Ordinal)
                .ToList();
    }

    public ISerialLink Open(string portName, int baudRate)
    {
        FakeDevice? d;
        lock (_lock)
        {
            OpenLog.Add(portName);
            _devices.TryGetValue(portName, out d);
        }
        if (d is null) throw new FileNotFoundException($"{portName} does not exist");
        if (d.Busy) throw new UnauthorizedAccessException($"{portName} busy");
        Interlocked.Increment(ref d.OpenCount);
        var link = new FakeLink(d, portName, baudRate);
        lock (_lock) d.Links.Add(link);
        return link;
    }
}

internal sealed class FakeLink : ISerialLink
{
    private readonly FakeDevice _dev;
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private bool _greetingSent;
    private readonly object _lock = new();
    private readonly List<(long Timestamp, byte[] Data)> _writes = new();

    public FakeLink(FakeDevice dev, string port, int baud)
    {
        _dev = dev;
        PortName = port;
        Baud = baud;
    }

    public string PortName { get; }
    public int Baud { get; }
    public volatile bool Unplugged;
    public volatile bool Disposed;

    public (long Timestamp, byte[] Data)[] Writes
    {
        get { lock (_lock) return _writes.ToArray(); }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Unplugged) throw new IOException("device removed");
        int idx = Interlocked.Increment(ref _dev.WriteCount) - 1;
        if (idx == _dev.FailAtWrite)
        {
            _dev.FailAtWrite = -1;
            throw new IOException("simulated write failure");
        }
        long ts = Stopwatch.GetTimestamp();
        var copy = data.ToArray();
        lock (_lock) _writes.Add((ts, copy));
        if (_dev.WriteBlockMs > 0) Thread.Sleep(_dev.WriteBlockMs);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Unplugged) throw new IOException("device removed");
        if (!_dev.Greets || _greetingSent)
        {
            Thread.Sleep(timeoutMs);
            return 0;
        }
        double sinceOpenMs = (Stopwatch.GetTimestamp() - _openedAt) * 1000.0 / Stopwatch.Frequency;
        double remaining = _dev.GreetDelayMs - sinceOpenMs;
        if (remaining > timeoutMs)
        {
            Thread.Sleep(timeoutMs);
            return 0;
        }
        if (remaining > 0) Thread.Sleep((int)Math.Ceiling(remaining));
        _greetingSent = true;
        int n = Math.Min(buffer.Length, _dev.Greeting.Length);
        _dev.Greeting.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public int BytesToWrite => 0;
    public void DiscardInput() { }
    public void CancelPendingIo() { }
    public void Dispose() => Disposed = true;
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
