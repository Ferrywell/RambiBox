using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Hid;

/// <summary>Timing knobs of <see cref="LightpackDevice"/> (tests shorten them).</summary>
internal sealed record LightpackOptions
{
    /// <summary>How often to look for the device(s) while searching.</summary>
    public int RescanIntervalMs { get; init; } = 1000;
    /// <summary>While connected: how often to check whether Lightpacks were added/removed.</summary>
    public int ConnectedCheckIntervalMs { get; init; } = 2000;
    /// <summary>Timeout of one output report write (SET_REPORT normally completes in 1–3 ms).</summary>
    public int WriteTimeoutMs { get; init; } = 250;
    /// <summary>
    /// Frame rate for one device. Each frame is one 65-byte SET_REPORT control transfer per device (~1–3 ms on full-speed
    /// USB); Prismatik's grab loop typically feeds 25–60 fps, and with firmware smoothing off 60 fps is the useful
    /// maximum. Constant on purpose (no measuring), see <see cref="MaxReportsPerSecond"/>.
    /// </summary>
    public double MaxFrameRate { get; init; } = 60;
    /// <summary>Upper bound of LED update reports per second over all chained devices (frame rate = min(60, 240 / devices)).</summary>
    public double MaxReportsPerSecond { get; init; } = 240;
    /// <summary>Firmware smoothing steps sent on connect; 0 = off (RambiBox smooths in software).</summary>
    public byte FirmwareSmoothing { get; init; }

    public static LightpackOptions Default { get; } = new();
}

/// <summary>
/// Lightpack / Lightpack 2 USB HID controllers (10 LEDs each; several devices are chained like Prismatik does:
/// ordered by serial number, LEDs 0–9 on the first, 10–19 on the second, …). See <see cref="LightpackProtocol"/>.
/// <para>
/// One background thread owns the devices: it enumerates (every second while searching, and on <see cref="Rescan"/>),
/// opens them, sends Prismatik's default timer/color-depth options and switches the firmware's own smoothing off,
/// then sends frames from a latest-wins mailbox at a constant pace. A failed write (unplug) closes everything and
/// searches again; a Lightpack added while connected is picked up within ~2 s.
/// </para>
/// <para>Never throws; all public members are thread-safe. <see cref="Blackout"/> and <see cref="Dispose"/> block up
/// to ~0.5 s.</para>
/// </summary>
[LedDevice(DeviceType.Lightpack)]
public sealed class LightpackDevice : ILedDevice
{
    private static readonly long Freq = Stopwatch.Frequency;

    private readonly IHidTransport _hid;
    private readonly LightpackOptions _opt;
    private readonly FramePacer _pacer;
    private readonly Func<bool> _isConnected;

    // ---- configuration (guarded by _cfgLock)
    private readonly object _cfgLock = new();
    private string? _deviceId;
    private int _ledCount = LightpackProtocol.LedsPerDevice;
    private volatile ColorOrder _order = ColorOrder.RGB;
    private Thread? _thread;

    // ---- control
    private readonly AutoResetEvent _control = new(false);
    private volatile bool _stop, _suspended;
    private int _reconnect, _rescan, _disposed;
    private BlackoutRequest? _blackout;

    // ---- status
    private volatile DeviceStatus _status = DeviceStatus.Stopped;
    private volatile string? _portName;

    // ---- device thread
    private HighResolutionTimer? _timer;
    private IHidConnection?[] _slots = [];
    private byte[][] _reports = [];
    private volatile IHidConnection?[] _activeSlots = [];

    public LightpackDevice() : this(Win32HidTransport.Shared, LightpackOptions.Default) { }

    internal LightpackDevice(IHidTransport hid, LightpackOptions? options = null)
    {
        _hid = hid;
        _opt = options ?? LightpackOptions.Default;
        _pacer = new FramePacer(_opt.MaxFrameRate);
        _isConnected = () => _status == DeviceStatus.Connected;
    }

    // =====================================================================================
    // ILedDevice
    // =====================================================================================

    public DeviceStatus Status => _status;
    public string? PortName => _portName;
    public event Action? StatusChanged;
    /// <summary>Lightpacks are identified by serial (DeviceSettings.DeviceId), not by a serial port: never raised.</summary>
    public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }

    public double MaxFrameRate => _pacer.FrameRate;
    public double FramesPerSecond => _status == DeviceStatus.Connected ? _pacer.FramesPerSecond : 0;

    public void Configure(DeviceSettings settings, int ledCount)
    {
        if (settings is null || Volatile.Read(ref _disposed) != 0) return;
        string? id = LightpackProtocol.IsAll(settings.DeviceId) ? null : settings.DeviceId!.Trim();
        bool start;
        lock (_cfgLock)
        {
            bool idChanged = !string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase);
            _deviceId = id;
            _ledCount = Math.Max(1, ledCount);
            _order = settings.ColorOrder;
            start = _thread is null;
            if (start)
            {
                _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RambiBox Lightpack", Priority = ThreadPriority.Highest };
            }
            else if (idChanged)
            {
                Interlocked.Exchange(ref _reconnect, 1);
                Log.Info($"Lightpack: device selection changed ({id ?? "all"}) - reconnecting");
            }
        }
        if (start)
        {
            Log.Info($"Lightpack: starting ({id ?? "all Lightpacks"}, {ledCount} LEDs, {settings.ColorOrder})");
            _thread!.Start();
        }
        if (_suspended)
        {
            _suspended = false;
            Log.Info("Lightpack: resuming after blackout");
        }
        Signal();
    }

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        if (_status != DeviceStatus.Connected || Volatile.Read(ref _disposed) != 0) return;
        _pacer.Post(rgb);
    }

    public bool WaitForSlot(int timeoutMs) => _pacer.WaitForSlot(timeoutMs, _isConnected);

    public void Rescan()
    {
        Interlocked.Exchange(ref _rescan, 1);
        Signal();
    }

    public void Blackout()
    {
        var t = _thread;
        if (t is null || !t.IsAlive || Volatile.Read(ref _disposed) != 0) return;
        if (Thread.CurrentThread == t) { _suspended = true; return; }
        var req = new BlackoutRequest();
        Volatile.Write(ref _blackout, req);
        Signal();
        if (!req.Done.Wait(400))
        {
            CancelActiveIo();
            Log.Warn("Lightpack: blackout did not complete in time");
        }
    }

    public IReadOnlyList<SerialPortInfo> ListPorts() => Array.Empty<SerialPortInfo>();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        Signal();
        Thread? t;
        lock (_cfgLock) t = _thread;
        bool joined = true;
        if (t is not null && t.IsAlive && Thread.CurrentThread != t)
        {
            joined = t.Join(1000);
            if (!joined)
            {
                CancelActiveIo();
                joined = t.Join(1000);
            }
            if (!joined) Log.Warn("Lightpack: device thread did not stop in time");
        }
        if (joined)
        {
            _control.Dispose();
            _pacer.Dispose();
        }
    }

    // =====================================================================================
    // Diagnostics (tests)
    // =====================================================================================

    internal long FramesWritten => _pacer.FramesWritten;
    /// <summary>Paths of the open devices in LED order (null = absent slot).</summary>
    internal IReadOnlyList<string?> ConnectedPaths => _activeSlots.Select(s => s?.Device.Path).ToList();

    // =====================================================================================
    // Device thread
    // =====================================================================================

    private void ThreadMain()
    {
        _timer = new HighResolutionTimer();
        try
        {
            while (!_stop)
            {
                try
                {
                    if (TakeBlackout() is { } req)
                    {
                        _suspended = true;
                        SetStatus(DeviceStatus.Stopped, null);
                        req.Done.Set();
                        continue;
                    }
                    if (_suspended)
                    {
                        SetStatus(DeviceStatus.Stopped, null);
                        _control.WaitOne(1000);
                        continue;
                    }
                    Interlocked.Exchange(ref _reconnect, 0);
                    if (Connect()) RunConnected();
                }
                catch (Exception ex)
                {
                    Log.Error("Lightpack: device thread error", ex);
                    CloseAll();
                    try { _control.WaitOne(1000); } catch { Thread.Sleep(1000); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lightpack: device thread crashed", ex);
        }
        finally
        {
            CloseAll();
            TakeBlackout()?.Done.Set();
            SetStatus(DeviceStatus.Stopped, null);
            _timer.Dispose();
        }
    }

    private bool Interrupted()
        => _stop || _suspended || Volatile.Read(ref _blackout) is not null || Volatile.Read(ref _reconnect) != 0;

    /// <summary>Searches until the selected device(s) are open and initialized. False when interrupted.</summary>
    private bool Connect()
    {
        if (_status != DeviceStatus.PortBusy) SetStatus(DeviceStatus.Searching, null);
        string? lastProblem = null;
        while (!Interrupted())
        {
            Interlocked.Exchange(ref _rescan, 0);
            string? id;
            lock (_cfgLock) id = _deviceId;

            string? problem;
            bool busy = false;
            var present = SafeEnumerate(out string? enumError);
            var selected = LightpackProtocol.Select(present ?? [], id);
            if (present is null)
            {
                problem = $"enumerating HID devices failed: {enumError}";
            }
            else if (selected.All(s => s is null))
            {
                problem = present.Count == 0
                    ? "no Lightpack found; waiting for one to be plugged in"
                    : $"Lightpack {id} not found (present: {string.Join(", ", present.Select(Describe))})";
            }
            else
            {
                problem = TryOpen(selected, out busy);
                if (problem is null) return true;
            }

            if (problem != lastProblem) Log.Info($"Lightpack: {problem}");
            lastProblem = problem;
            SetStatus(busy ? DeviceStatus.PortBusy : DeviceStatus.Searching, null);
            _control.WaitOne(_opt.RescanIntervalMs);
        }
        return false;
    }

    /// <summary>Opens and initializes every selected device. Returns null on success, else the problem (all closed).</summary>
    private string? TryOpen(List<HidDeviceInfo?> selected, out bool busy)
    {
        busy = false;
        var slots = new IHidConnection?[selected.Count];
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is not { } dev) continue;
                try
                {
                    slots[i] = _hid.Open(dev);
                }
                catch (HidException ex)
                {
                    busy = ex.IsBusy;
                    CloseSlots(slots);
                    return busy ? $"{Describe(dev)} is in use by another program; retrying" : $"cannot open {Describe(dev)}: {ex.Message}";
                }
                catch (Exception ex)
                {
                    CloseSlots(slots);
                    return $"cannot open {Describe(dev)}: {ex.Message}";
                }
            }

            int reportLen = LightpackProtocol.ReportLength;
            foreach (var s in slots) if (s is not null) reportLen = Math.Max(reportLen, s.Device.OutputReportLength);
            var reports = new byte[slots.Length][];
            for (int i = 0; i < reports.Length; i++) reports[i] = new byte[reportLen];

            // Prismatik's updateDeviceSettings: refresh delay, color depth, smoothing (here: off).
            var cmd = new byte[reportLen];
            var fw = new List<string>();
            var input = new byte[Math.Max(LightpackProtocol.ReportLength, slots.Max(s => s?.Device.InputReportLength ?? 0))];
            foreach (var s in slots)
            {
                if (s is null) continue;
                LightpackProtocol.BuildSetTimerOptions(LightpackProtocol.DefaultRefreshDelay, cmd);
                bool ok = WriteWithRetry(s, cmd, out int err);
                LightpackProtocol.BuildSetColorDepth(LightpackProtocol.DefaultColorDepth, cmd);
                ok = ok && WriteWithRetry(s, cmd, out err);
                LightpackProtocol.BuildSetSmoothSlowdown(_opt.FirmwareSmoothing, cmd);
                ok = ok && WriteWithRetry(s, cmd, out err);
                if (!ok)
                {
                    CloseSlots(slots);
                    return $"cannot initialize {Describe(s.Device)} (error {err})";
                }
                fw.Add(s.TryGetInputReport(input) && LightpackProtocol.TryParseFirmwareVersion(input, out int ma, out int mi, out int un)
                    ? $"{Describe(s.Device)} firmware {LightpackProtocol.FormatFirmware(ma, mi, un)}"
                    : $"{Describe(s.Device)} firmware unknown");
            }

            _slots = slots;
            _reports = reports;
            _activeSlots = slots;
            int open = slots.Count(s => s is not null);
            int missing = slots.Length - open;
            Log.Info($"Lightpack: connected {string.Join("; ", fw)}" +
                     (missing > 0 ? $"; {missing} selected device(s) absent, their LEDs are skipped" : ""));
            return null;
        }
        catch
        {
            CloseSlots(slots);
            throw;
        }
    }

    private void RunConnected()
    {
        var slots = _slots;
        int open = slots.Count(s => s is not null);
        _pacer.SetFrameRate(Math.Min(_opt.MaxFrameRate, _opt.MaxReportsPerSecond / Math.Max(1, open)));
        _pacer.Reset();
        int capacity = slots.Length * LightpackProtocol.LedsPerDevice;
        int leds;
        lock (_cfgLock) leds = _ledCount;
        if (leds > capacity)
            Log.Warn($"Lightpack: {leds} LEDs configured but {slots.Length} device(s) drive {capacity}; the rest are not shown");
        string name = open == 1
            ? $"Lightpack {slots.First(s => s is not null)!.Device.SerialNumber ?? "(no serial)"}"
            : $"Lightpack ({open} devices)";
        Log.Info($"Lightpack: {capacity} LEDs, max {_pacer.FrameRate:F0} fps");
        SetStatus(DeviceStatus.Connected, name);

        long checkInterval = _opt.ConnectedCheckIntervalMs * Freq / 1000;
        long nextCheck = Stopwatch.GetTimestamp() + checkInterval;
        bool black = false;
        BlackoutRequest? req = null;
        string? lost = null;
        try
        {
            while (true)
            {
                if (_stop || _suspended || Volatile.Read(ref _reconnect) != 0) { black = true; break; }
                if ((req = TakeBlackout()) is not null) { black = true; _suspended = true; break; }

                long now = Stopwatch.GetTimestamp();
                if (Interlocked.Exchange(ref _rescan, 0) != 0) nextCheck = now;
                if (now >= nextCheck)
                {
                    if (SelectionChanged()) { lost = "Lightpack devices were added or removed"; black = true; break; }
                    nextCheck = Stopwatch.GetTimestamp() + checkInterval;
                }

                if (!TrySendPending(out lost))
                {
                    if (lost is not null) break;
                    long waitMs = (nextCheck - Stopwatch.GetTimestamp()) * 1000 / Freq + 1;
                    _pacer.WaitForFrame((int)Math.Clamp(waitMs, 1, _opt.ConnectedCheckIntervalMs));
                }
            }
        }
        catch (Exception ex)
        {
            lost = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (black) SendBlack();
        CloseAll();
        if (lost is not null) Log.Warn($"Lightpack: {lost}; searching again");
        if (req is not null)
        {
            Log.Info("Lightpack: blackout sent, device closed");
            SetStatus(DeviceStatus.Stopped, null);
            req.Done.Set();
        }
        else if (_stop || _suspended)
        {
            SetStatus(DeviceStatus.Stopped, null);
        }
        else
        {
            SetStatus(DeviceStatus.Searching, null);
        }
    }

    /// <summary>Sends the mailbox frame (one report per device). False when empty or when a write failed (<paramref name="lost"/> set).</summary>
    private bool TrySendPending(out string? lost)
    {
        lost = null;
        if (!_pacer.TryTake(_timer!, out var frame)) return false;
        var order = _order;
        long t0 = Stopwatch.GetTimestamp();
        const int perDevice = LightpackProtocol.LedsPerDevice * 3;
        for (int i = 0; i < _slots.Length; i++)
        {
            var conn = _slots[i];
            if (conn is null) continue;
            int start = i * perDevice;
            var part = start < frame.Length ? frame.Slice(start, Math.Min(perDevice, frame.Length - start)) : default;
            LightpackProtocol.BuildUpdateLeds(part, order, _reports[i]);
            if (!WriteWithRetry(conn, _reports[i], out int err))
            {
                lost = $"write to {Describe(conn.Device)} failed (error {err})";
                return false;
            }
        }
        _pacer.MarkWritten(t0);
        return true;
    }

    /// <summary>Prismatik retries a failed hid_write once before reopening.</summary>
    private bool WriteWithRetry(IHidConnection conn, byte[] report, out int error)
        => conn.Write(report, _opt.WriteTimeoutMs, out error) || conn.Write(report, _opt.WriteTimeoutMs, out error);

    /// <summary>Black LED update (what Prismatik's switchOffLeds sends) followed by CMD_OFF_ALL, to every open device.</summary>
    private void SendBlack()
    {
        _pacer.DropPending();
        for (int i = 0; i < _slots.Length; i++)
        {
            var conn = _slots[i];
            if (conn is null) continue;
            try
            {
                LightpackProtocol.BuildBlack(_reports[i]);
                if (conn.Write(_reports[i], _opt.WriteTimeoutMs, out _))
                {
                    LightpackProtocol.BuildCommand(LightpackProtocol.CmdOffAll, _reports[i]);
                    conn.Write(_reports[i], _opt.WriteTimeoutMs, out _);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Lightpack: could not send black to {Describe(conn.Device)}: {ex.Message}");
            }
        }
    }

    /// <summary>True when the set of devices that should be driven differs from the open one.</summary>
    private bool SelectionChanged()
    {
        var present = SafeEnumerate(out _);
        if (present is null) return false; // unknown: keep the working connection
        string? id;
        lock (_cfgLock) id = _deviceId;
        var selected = LightpackProtocol.Select(present, id);
        if (selected.Count != _slots.Length) return true;
        for (int i = 0; i < selected.Count; i++)
        {
            if (!string.Equals(selected[i]?.Path, _slots[i]?.Device.Path, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private List<HidDeviceInfo>? SafeEnumerate(out string? error)
    {
        error = null;
        try
        {
            return LightpackProtocol.OrderLikePrismatik(
                _hid.Enumerate(LightpackProtocol.IsLightpackId).Where(LightpackProtocol.IsUsable));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void CloseAll()
    {
        var slots = _slots;
        _slots = [];
        _activeSlots = [];
        CloseSlots(slots);
    }

    private static void CloseSlots(IHidConnection?[] slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            try { slots[i]?.Dispose(); } catch { /* after surprise removal closing may fail; nothing to do */ }
            slots[i] = null;
        }
    }

    private void CancelActiveIo()
    {
        foreach (var s in _activeSlots)
        {
            try { s?.CancelIo(); } catch { /* ignore */ }
        }
    }

    internal static string Describe(HidDeviceInfo d)
        => d.SerialNumber is { Length: > 0 } s ? $"Lightpack {s} ({d.UsbId})" : $"Lightpack without serial ({d.UsbId})";

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private void Signal()
    {
        try { _control.Set(); } catch (ObjectDisposedException) { /* disposed */ }
        _pacer.Signal();
    }

    private BlackoutRequest? TakeBlackout() => Interlocked.Exchange(ref _blackout, null);

    private void SetStatus(DeviceStatus status, string? port)
    {
        if (_status == status && string.Equals(_portName, port, StringComparison.Ordinal)) return;
        _portName = port;
        _status = status;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("Lightpack: StatusChanged handler failed", ex); }
    }

    private sealed class BlackoutRequest
    {
        public readonly ManualResetEventSlim Done = new(false);
    }
}
