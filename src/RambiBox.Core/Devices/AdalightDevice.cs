using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Adalight LED controller on a (USB-)serial port.
/// <para>
/// One background thread owns the port: it searches/opens the port, waits for the firmware's "Ada" greeting
/// (the board resets on open), then sends frames from a latest-wins mailbox paced by wire time
/// (next write at lastWriteStart + frameBytes*10/baud*1.03) so nothing ever queues in driver/USB buffers.
/// On any I/O error or when the port disappears it closes the port and searches again.
/// </para>
/// <para>Thread-safety: all public members may be called from any thread; nothing but <see cref="Blackout"/>,
/// <see cref="Dispose"/> and <see cref="WaitForSlot"/> blocks.</para>
/// </summary>
public sealed class AdalightDevice : ILedDevice
{
    private static readonly string[] KnownUsbIds =
    [
        "1A86:7523", "1A86:5523", "1A86:55D4", // CH340/CH341, CH341 alt, CH9102
        "0403:6001", "0403:6015",              // FTDI FT232R, FT-X
        "10C4:EA60",                           // CP210x
    ];
    private static readonly string[] KnownVendors = ["2341", "2A03"]; // Arduino LLC / Arduino SRL

    private readonly IPortEnumerator _ports;
    private readonly ISerialLinkFactory _links;
    private readonly AdalightOptions _opt;
    private static readonly long Freq = Stopwatch.Frequency;

    // ---- configuration (guarded by _cfgLock)
    private readonly object _cfgLock = new();
    private string _port = DeviceSettings.AutoPort;
    private int _baud = 115200;
    private string? _lastPort, _lastUsbId;
    private volatile AdalightFrameFormat _format;
    private Thread? _thread;

    // ---- control
    private readonly AutoResetEvent _control = new(false);    // wakes searching / idle waits
    private readonly AutoResetEvent _frameReady = new(false); // wakes the connected loop (Submit + control)
    private volatile bool _stop;
    private volatile bool _suspended; // after Blackout(), until the next Configure()
    private int _reconnect, _rescan;
    private BlackoutRequest? _blackout;
    private int _disposed;

    // ---- status
    private volatile DeviceStatus _status = DeviceStatus.Stopped;
    private volatile string? _portName;

    // ---- mailbox (guarded by _mail)
    private readonly object _mail = new();
    private byte[] _pending = [], _sending = [];
    private int _pendingLen, _sendingLen;
    private bool _hasPending;
    private long _nextAllowed; // Stopwatch timestamp of the next allowed write start

    // ---- device-thread state
    private byte[] _tx = [];
    private HighResolutionTimer? _senderTimer;
    private HighResolutionTimer? _slotTimer; // cached instance for WaitForSlot callers
    private readonly Dictionary<string, ProbeState> _probe = new(StringComparer.OrdinalIgnoreCase);

    // ---- auto-detect policy for "unknown" ports (device thread only), see AutoDetect
    private HashSet<string>? _seenPorts;          // present ports at the last successful read; null before the first
    private bool _startupScanDone;                // the first auto-detect scan of this instance has run
    private readonly HashSet<string> _oneShotPending = new(StringComparer.OrdinalIgnoreCase); // may be probed once
    private readonly HashSet<string> _oneShotSettled = new(StringComparer.OrdinalIgnoreCase); // decided for this presence
    private bool _presentReadFailed;

    // ---- link + statistics
    private volatile ISerialLink? _activeLink;
    private long _writeStartedAt; // != 0 while a write is in progress (watchdog)
    private long _framesWritten, _lastWriteStart, _lastWriteDuration;
    private long _fpsWindowStart;
    private int _fpsCount;
    private double _fps;

    public AdalightDevice() : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), AdalightOptions.Default) { }

    internal AdalightDevice(IPortEnumerator ports, ISerialLinkFactory links, AdalightOptions? options = null)
    {
        _ports = ports;
        _links = links;
        _opt = options ?? AdalightOptions.Default;
        _format = new AdalightFrameFormat(1, ColorOrder.RGB, _baud, _opt.PacingFactor);
    }

    // =====================================================================================
    // ILedDevice
    // =====================================================================================

    public DeviceStatus Status => _status;
    public string? PortName => _portName;
    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound;

    public double MaxFrameRate => _format.MaxFrameRate;

    public double FramesPerSecond
    {
        get
        {
            if (_status != DeviceStatus.Connected) return 0;
            long last = Volatile.Read(ref _lastWriteStart);
            if (last == 0 || Stopwatch.GetTimestamp() - last > 2 * Freq) return 0;
            return Volatile.Read(ref _fps);
        }
    }

    public void Configure(DeviceSettings settings, int ledCount)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _disposed) != 0) return;
        string port = NormalizePort(settings.Port);
        int baud = settings.BaudRate > 0 ? settings.BaudRate : 115200;
        bool start;
        lock (_cfgLock)
        {
            bool linkChanged = !string.Equals(port, _port, StringComparison.OrdinalIgnoreCase) || baud != _baud;
            _port = port;
            _baud = baud;
            if (!string.IsNullOrWhiteSpace(settings.LastPort)) _lastPort = settings.LastPort.Trim();
            if (!string.IsNullOrWhiteSpace(settings.LastUsbId)) _lastUsbId = settings.LastUsbId.Trim().ToUpperInvariant();

            var f = _format;
            int leds = Math.Clamp(ledCount, 1, AdalightFrameFormat.MaxLeds);
            if (f.LedCount != leds || f.Order != settings.ColorOrder || f.BaudRate != baud)
                _format = new AdalightFrameFormat(leds, settings.ColorOrder, baud, _opt.PacingFactor);

            start = _thread is null;
            if (start)
            {
                _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RambiBox Adalight", Priority = ThreadPriority.Highest };
            }
            else if (linkChanged)
            {
                Interlocked.Exchange(ref _reconnect, 1);
                Log.Info($"Adalight: settings changed (port {port}, {baud} baud) - reconnecting");
            }
        }
        if (start)
        {
            Log.Info($"Adalight: starting (port {port}, {baud} baud, {ledCount} LEDs, {settings.ColorOrder})");
            _thread!.Start();
        }
        if (_suspended)
        {
            _suspended = false;
            Log.Info("Adalight: resuming after blackout");
        }
        Signal();
    }

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        if (_status != DeviceStatus.Connected) return;
        lock (_mail)
        {
            if (_pending.Length < rgb.Length) _pending = new byte[rgb.Length]; // only when the LED count grows
            rgb.CopyTo(_pending);
            _pendingLen = rgb.Length;
            _hasPending = true;
        }
        try { _frameReady.Set(); } catch (ObjectDisposedException) { /* racing Dispose */ }
    }

    public bool WaitForSlot(int timeoutMs)
    {
        if (_status != DeviceStatus.Connected) return false;
        long start = Stopwatch.GetTimestamp();
        long deadline = start + Math.Max(0, timeoutMs) * Freq / 1000;
        HighResolutionTimer? timer = null;
        try
        {
            while (true)
            {
                if (_status != DeviceStatus.Connected) return false;
                CheckWriteWatchdog();
                long now = Stopwatch.GetTimestamp();
                long slot;
                lock (_mail)
                {
                    slot = _nextAllowed;
                    // An unsent frame will go out at max(now, nextAllowed) and occupy the link for one interval.
                    if (_hasPending) slot = Math.Max(slot, now) + _format.IntervalTicks;
                }
                if (now >= slot) return true;
                if (now >= deadline) return false;
                timer ??= Interlocked.Exchange(ref _slotTimer, null) ?? new HighResolutionTimer();
                timer.WaitUntil(Math.Min(slot, deadline));
            }
        }
        finally
        {
            if (timer is not null && Interlocked.CompareExchange(ref _slotTimer, timer, null) is not null) timer.Dispose();
        }
    }

    public void Rescan()
    {
        Interlocked.Exchange(ref _rescan, 1);
        Signal();
    }

    public void Blackout()
    {
        var t = _thread;
        if (t is null || !t.IsAlive || Volatile.Read(ref _disposed) != 0) return;
        if (Thread.CurrentThread == t) { _suspended = true; return; } // re-entrant call from an event handler
        var req = new BlackoutRequest();
        Volatile.Write(ref _blackout, req);
        Signal();
        if (!req.Done.Wait(400))
        {
            _activeLink?.CancelPendingIo();
            Log.Warn("Adalight: blackout did not complete in time");
        }
    }

    public IReadOnlyList<SerialPortInfo> ListPorts()
    {
        try { return _ports.ListPorts(); }
        catch (Exception ex)
        {
            Log.Warn($"Adalight: listing ports failed: {ex.Message}");
            return Array.Empty<SerialPortInfo>();
        }
    }

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
                _activeLink?.CancelPendingIo();
                joined = t.Join(1000);
            }
            if (!joined) Log.Warn("Adalight: device thread did not stop in time");
        }
        if (joined)
        {
            _control.Dispose();
            _frameReady.Dispose();
            Interlocked.Exchange(ref _slotTimer, null)?.Dispose();
        }
    }

    // =====================================================================================
    // Diagnostics (tests)
    // =====================================================================================

    internal long FramesWritten => Interlocked.Read(ref _framesWritten);
    /// <summary>Stopwatch timestamp when the most recent write started.</summary>
    internal long LastWriteStartTimestamp => Volatile.Read(ref _lastWriteStart);
    /// <summary>Duration of the most recent blocking write call, Stopwatch ticks.</summary>
    internal long LastWriteDurationTicks => Volatile.Read(ref _lastWriteDuration);
    internal AdalightFrameFormat Format => _format;
    internal string? RememberedPort { get { lock (_cfgLock) return _lastPort; } }
    internal string? RememberedUsbId { get { lock (_cfgLock) return _lastUsbId; } }

    // =====================================================================================
    // Device thread
    // =====================================================================================

    private void ThreadMain()
    {
        _senderTimer = new HighResolutionTimer();
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
                    string port;
                    int baud;
                    lock (_cfgLock) { port = _port; baud = _baud; }

                    var conn = IsAuto(port) ? AutoDetect(baud) : OpenFixed(port, baud);
                    if (conn is { } c) RunConnected(c.Link, c.Info);
                }
                catch (Exception ex)
                {
                    Log.Error("Adalight: device thread error", ex);
                    try { _control.WaitOne(1000); } catch { Thread.Sleep(1000); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Adalight: device thread crashed", ex);
        }
        finally
        {
            TakeBlackout()?.Done.Set();
            SetStatus(DeviceStatus.Stopped, null);
            _senderTimer.Dispose();
        }
    }

    private bool Interrupted()
        => _stop || _suspended || Volatile.Read(ref _blackout) is not null || Volatile.Read(ref _reconnect) != 0;

    // ---------------- auto-detect ----------------

    /// <summary>
    /// Searches all present ports for the Adalight greeting.
    /// <para>
    /// Opening a port asserts DTR, which resets most Arduino-based boards - including someone else's 3D printer or CNC
    /// controller in the middle of a job - and disturbs modem AT ports. Since the app runs 24/7, only ports that can
    /// plausibly be ours are probed periodically (with backoff): the remembered port, ports with the remembered USB id
    /// and known USB-serial chips (ranks 0..2 of <see cref="OrderCandidates"/>). Any other port ("unknown", rank 3)
    /// is probed at most once per arrival:
    /// </para>
    /// <list type="bullet">
    /// <item>when it newly appears in SERIALCOMM - but if a device is remembered, only while no rank 0..2 candidate
    /// is present;</item>
    /// <item>once on the first scan after start, only when nothing is remembered and no rank 0..2 candidate exists.</item>
    /// </list>
    /// <para>Modem-class ports are never opened in auto mode. <see cref="Rescan"/> (WM_DEVICECHANGE) does not re-probe
    /// unknown ports. A fixed port (not Auto) is handled by <see cref="OpenFixed"/> and unaffected.</para>
    /// </summary>
    private (ISerialLink Link, SerialPortInfo Info)? AutoDetect(int baud)
    {
        if (_status != DeviceStatus.PortBusy) SetStatus(DeviceStatus.Searching, null);
        Log.Info("Adalight: searching for the LED controller (auto-detect)");
        while (!Interrupted())
        {
            bool rescan = Interlocked.Exchange(ref _rescan, 0) != 0;
            var present = SafePresentPorts();
            var arrived = NotePresentPorts(present);

            // Forget ports that disappeared: when they come back they are probed immediately.
            if (_probe.Count > 0)
            {
                foreach (var gone in _probe.Keys.Where(k => !present.Contains(k)).ToList()) _probe.Remove(gone);
            }

            string? lastPort, lastUsbId;
            lock (_cfgLock) { lastPort = _lastPort; lastUsbId = _lastUsbId; }
            var listed = SafeListPorts().Where(p => present.Contains(p.PortName) && !p.IsBluetooth).ToList();
            var modems = SafeModemPorts();
            foreach (var m in listed)
            {
                if (modems.Contains(m.PortName) && _oneShotSettled.Add(m.PortName))
                    Log.Info($"Adalight: {m.PortName} ({m.Description}) is a modem port; auto-detect never opens it");
            }
            var candidates = OrderCandidates(listed.Where(p => !modems.Contains(p.PortName)), lastPort, lastUsbId);
            ClassifyUnknownPorts(candidates, arrived, remembered: lastPort is not null || lastUsbId is not null);
            int sameUsbId = lastUsbId is null ? 0 : candidates.Count(c => EqId(c.Info.UsbId, lastUsbId));

            string? busy = null;
            foreach (var (info, rank) in candidates)
            {
                if (Interrupted()) return null;
                bool preferred = rank <= 2;
                if (!preferred)
                {
                    if (!_oneShotPending.Contains(info.PortName)) continue; // unknown port: its one probe is used up
                }
                else if (_probe.TryGetValue(info.PortName, out var st))
                {
                    bool due = Stopwatch.GetTimestamp() >= st.RetryAt || (rescan && st.Last == ProbeOutcome.OpenFailed);
                    if (!due)
                    {
                        if (st.Last == ProbeOutcome.Busy && preferred) busy ??= info.PortName;
                        continue;
                    }
                }

                if (_status != DeviceStatus.PortBusy) SetStatus(DeviceStatus.Searching, info.PortName);
                // A silent port is only accepted when it is unambiguously the device that worked before
                // (boards that don't reset on open never repeat their greeting).
                bool acceptSilent = lastUsbId is not null && EqId(info.UsbId, lastUsbId)
                                    && (EqPort(info.PortName, lastPort) || sameUsbId == 1);
                var outcome = Probe(info, baud, acceptSilent, out var link);
                switch (outcome)
                {
                    case ProbeOutcome.Success:
                        _probe.Remove(info.PortName);
                        _oneShotPending.Remove(info.PortName);
                        lock (_cfgLock)
                        {
                            _lastPort = info.PortName;
                            if (info.UsbId is not null) _lastUsbId = info.UsbId;
                        }
                        RaiseDeviceFound(info);
                        return (link!, info);
                    case ProbeOutcome.Interrupted:
                        return null; // a pending one-shot probe stays pending
                    default:
                        if (!preferred)
                        {
                            _oneShotPending.Remove(info.PortName);
                            _oneShotSettled.Add(info.PortName);
                        }
                        RecordFailure(info, outcome, rank <= 1);
                        if (outcome == ProbeOutcome.Busy && preferred) busy ??= info.PortName;
                        break;
                }
            }

            if (busy is not null)
            {
                if (_status != DeviceStatus.PortBusy || !EqPort(_portName, busy))
                    Log.Warn($"Adalight: {busy} is in use by another program; retrying");
                SetStatus(DeviceStatus.PortBusy, busy);
            }
            else
            {
                SetStatus(DeviceStatus.Searching, null);
            }
            _control.WaitOne(_opt.RescanIntervalMs);
        }
        return null;
    }

    /// <summary>
    /// Decides once per presence whether each unknown (rank 3) port gets its single probe (→ <see cref="_oneShotPending"/>)
    /// or is left alone (→ <see cref="_oneShotSettled"/>). See <see cref="AutoDetect"/> for the rules.
    /// </summary>
    private void ClassifyUnknownPorts(List<(SerialPortInfo Info, int Rank)> candidates, HashSet<string> arrived, bool remembered)
    {
        bool startup = !_startupScanDone;
        _startupScanDone = true;
        bool anyPreferred = candidates.Any(c => c.Rank <= 2);
        List<string>? skipped = null;
        foreach (var (info, rank) in candidates)
        {
            if (rank <= 2 || _oneShotPending.Contains(info.PortName) || _oneShotSettled.Contains(info.PortName)) continue;
            bool probeOnce = arrived.Contains(info.PortName)
                ? !remembered || !anyPreferred
                : startup && !remembered && !anyPreferred;
            if (probeOnce)
            {
                _oneShotPending.Add(info.PortName);
            }
            else
            {
                _oneShotSettled.Add(info.PortName);
                (skipped ??= []).Add($"{info.PortName} ({info.Description}{(info.UsbId is null ? "" : ", " + info.UsbId)})");
            }
        }
        if (skipped is not null)
            Log.Info($"Adalight: not probing {string.Join(", ", skipped)}: not a known USB-serial adapter " +
                     "(opening it could reset another device). Select the port in the settings to use it.");
    }

    /// <summary>Records the present port set; returns the ports that appeared since the previous successful read.</summary>
    private HashSet<string> NotePresentPorts(IReadOnlyCollection<string> present)
    {
        var arrived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_presentReadFailed) return arrived; // unknown: don't treat the next good read as a mass arrival
        if (_seenPorts is not null)
        {
            foreach (var p in present)
                if (!_seenPorts.Contains(p)) arrived.Add(p);
        }
        _seenPorts = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        _oneShotPending.RemoveWhere(p => !_seenPorts.Contains(p));
        _oneShotSettled.RemoveWhere(p => !_seenPorts.Contains(p));
        return arrived;
    }

    /// <summary>
    /// Candidate order: last working port, same USB id as last time, known Arduino/USB-serial chips, then the rest.
    /// Bluetooth ports are never included. Rank 0..3 is returned alongside.
    /// </summary>
    internal static List<(SerialPortInfo Info, int Rank)> OrderCandidates(IEnumerable<SerialPortInfo> ports, string? lastPort, string? lastUsbId)
    {
        return ports
            .Where(p => !p.IsBluetooth)
            .Select(p => (Info: p, Rank:
                lastPort is not null && EqPort(p.PortName, lastPort) ? 0 :
                lastUsbId is not null && EqId(p.UsbId, lastUsbId) ? 1 :
                IsKnownChip(p.UsbId) ? 2 : 3))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Info.PortName, Comparer<string>.Create(Win32PortEnumerator.ComparePortNames))
            .ToList();
    }

    internal static bool IsKnownChip(string? usbId)
    {
        if (usbId is null) return false;
        foreach (var id in KnownUsbIds) if (EqId(usbId, id)) return true;
        foreach (var v in KnownVendors) if (usbId.StartsWith(v + ":", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private ProbeOutcome Probe(SerialPortInfo info, int baud, bool acceptSilent, out ISerialLink? link)
    {
        link = null;
        ISerialLink? l;
        try
        {
            l = OpenLink(info.PortName, baud);
            if (l is null) return ProbeOutcome.Interrupted;
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeOutcome.Busy;
        }
        catch (Exception ex)
        {
            _lastProbeError = ex.Message;
            return ProbeOutcome.OpenFailed;
        }

        var g = WaitForGreeting(l, _opt.ProbeTimeoutMs, out bool anyBytes);
        if (g == GreetingResult.Greeted)
        {
            Log.Info($"Adalight: found device on {info.PortName} ({info.Description}{(info.UsbId is null ? "" : ", " + info.UsbId)})");
            link = l;
            return ProbeOutcome.Success;
        }
        if (g == GreetingResult.Timeout && !anyBytes && acceptSilent)
        {
            Log.Info($"Adalight: {info.PortName} did not greet, but it is the remembered device ({info.UsbId}); using it");
            link = l;
            return ProbeOutcome.Success;
        }
        SafeDispose(l);
        return g switch
        {
            GreetingResult.Interrupted => ProbeOutcome.Interrupted,
            GreetingResult.Failed => ProbeOutcome.OpenFailed,
            _ => anyBytes ? ProbeOutcome.Garbage : ProbeOutcome.NoGreeting,
        };
    }

    private string? _lastProbeError;

    private void RecordFailure(SerialPortInfo info, ProbeOutcome outcome, bool remembered)
    {
        if (!_probe.TryGetValue(info.PortName, out var st)) _probe[info.PortName] = st = new ProbeState();
        st.Failures++;
        int delayMs = outcome == ProbeOutcome.Busy ? _opt.BusyRetryMs
            : remembered ? _opt.FailedRetryMs
            : (int)Math.Min((long)_opt.FailedRetryMs << Math.Min(st.Failures - 1, 16), _opt.MaxRetryMs);
        st.RetryAt = Stopwatch.GetTimestamp() + delayMs * Freq / 1000;
        if (st.Last != outcome)
        {
            string what = outcome switch
            {
                ProbeOutcome.Busy => "in use by another program",
                ProbeOutcome.NoGreeting => "no Adalight greeting",
                ProbeOutcome.Garbage => "unexpected data (not Adalight, or wrong baud rate)",
                _ => $"cannot open ({_lastProbeError})",
            };
            Log.Info($"Adalight: {info.PortName} ({info.Description}): {what}");
        }
        st.Last = outcome;
    }

    // ---------------- fixed port ----------------

    private (ISerialLink Link, SerialPortInfo Info)? OpenFixed(string port, int baud)
    {
        if (!(_status == DeviceStatus.PortBusy && EqPort(_portName, port))) SetStatus(DeviceStatus.Searching, port);
        string? lastProblem = null;
        while (!Interrupted())
        {
            Interlocked.Exchange(ref _rescan, 0);
            var present = SafePresentPorts();
            NotePresentPorts(present);
            if (!present.Contains(port))
            {
                if (lastProblem != "absent") Log.Info($"Adalight: waiting for {port} to appear");
                lastProblem = "absent";
                SetStatus(DeviceStatus.Searching, port);
                _control.WaitOne(_opt.RescanIntervalMs);
                continue;
            }

            ISerialLink? link;
            try
            {
                link = OpenLink(port, baud);
                if (link is null) return null;
            }
            catch (UnauthorizedAccessException)
            {
                if (lastProblem != "busy") Log.Warn($"Adalight: {port} is in use by another program; retrying");
                lastProblem = "busy";
                SetStatus(DeviceStatus.PortBusy, port);
                _control.WaitOne(_opt.BusyRetryMs);
                continue;
            }
            catch (Exception ex)
            {
                string p = "open:" + ex.Message;
                if (lastProblem != p) Log.Warn($"Adalight: cannot open {port}: {ex.Message}");
                lastProblem = p;
                SetStatus(DeviceStatus.Searching, port);
                _control.WaitOne(_opt.RescanIntervalMs);
                continue;
            }

            SetStatus(DeviceStatus.Searching, port);
            var g = WaitForGreeting(link, _opt.FixedGreetingTimeoutMs, out _);
            if (g == GreetingResult.Interrupted)
            {
                SafeDispose(link);
                return null;
            }
            if (g == GreetingResult.Failed)
            {
                SafeDispose(link);
                _control.WaitOne(_opt.RescanIntervalMs);
                continue;
            }
            if (g == GreetingResult.Timeout)
                Log.Info($"Adalight: no greeting from {port} within {_opt.FixedGreetingTimeoutMs} ms; sending anyway");
            var info = SafeListPorts().FirstOrDefault(p => EqPort(p.PortName, port)) ?? new SerialPortInfo(port, port, null);
            return (link, info);
        }
        return null;
    }

    /// <summary>
    /// Opens the port, but never sooner than <see cref="AdalightOptions.MinReopenIntervalMs"/> after the previous open
    /// of the same port (process-wide). Measured on the CH340 Nano: the DTR edge of an open within ~0.8 s of the
    /// previous one does not reset the board, so no greeting would come. Returns null when interrupted while waiting.
    /// </summary>
    private ISerialLink? OpenLink(string port, int baud)
    {
        if (_opt.MinReopenIntervalMs > 0 && LastOpenByPort.TryGetValue(port, out long last))
        {
            long earliest = last + _opt.MinReopenIntervalMs * Freq / 1000;
            while (Stopwatch.GetTimestamp() < earliest)
            {
                if (Interrupted()) return null;
                Thread.Sleep(10);
            }
        }
        var link = _links.Open(port, baud);
        LastOpenByPort[port] = Stopwatch.GetTimestamp();
        return link;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LastOpenByPort = new(StringComparer.OrdinalIgnoreCase);

    private GreetingResult WaitForGreeting(ISerialLink link, int timeoutMs, out bool anyBytes)
    {
        anyBytes = false;
        Span<byte> buf = stackalloc byte[256];
        int state = 0;
        long deadline = Stopwatch.GetTimestamp() + timeoutMs * Freq / 1000;
        try
        {
            while (true)
            {
                if (Interrupted()) return GreetingResult.Interrupted;
                long remainingMs = (deadline - Stopwatch.GetTimestamp()) * 1000 / Freq;
                if (remainingMs <= 0) return GreetingResult.Timeout;
                int n = link.Read(buf, (int)Math.Min(50, remainingMs));
                if (n <= 0) continue;
                anyBytes = true;
                if (AdalightProtocol.FeedGreeting(buf[..n], ref state)) return GreetingResult.Greeted;
            }
        }
        catch (Exception ex)
        {
            _lastProbeError = ex.Message;
            return GreetingResult.Failed;
        }
    }

    // ---------------- connected: sending ----------------

    private void RunConnected(ISerialLink link, SerialPortInfo info)
    {
        string port = link.PortName;
        lock (_mail)
        {
            _hasPending = false; // anything submitted before the reconnect is stale
            _nextAllowed = Stopwatch.GetTimestamp();
        }
        _fpsCount = 0;
        _fpsWindowStart = Stopwatch.GetTimestamp();
        Volatile.Write(ref _fps, 0);
        _activeLink = link;
        try { link.DiscardInput(); } catch { /* ignore */ }

        var fmt = _format;
        Log.Info($"Adalight: connected on {port} ({info.Description}{(info.UsbId is null ? "" : ", " + info.UsbId)}), " +
                 $"{fmt.LedCount} LEDs, {fmt.BaudRate} baud, max {fmt.MaxFrameRate:F1} fps");
        SetStatus(DeviceStatus.Connected, port);

        long presenceInterval = _opt.RescanIntervalMs * Freq / 1000;
        long nextPresence = Stopwatch.GetTimestamp() + presenceInterval;
        bool blackOnExit = false;
        BlackoutRequest? req = null;
        string? lostReason = null;
        try
        {
            while (true)
            {
                if (_stop) { blackOnExit = true; break; }
                if ((req = TakeBlackout()) is not null) { blackOnExit = true; _suspended = true; break; }
                if (_suspended) { blackOnExit = true; break; }
                if (Volatile.Read(ref _reconnect) != 0) { blackOnExit = true; break; }

                long now = Stopwatch.GetTimestamp();
                if (Interlocked.Exchange(ref _rescan, 0) != 0) nextPresence = now;
                if (now >= nextPresence)
                {
                    var present = SafePresentPorts();
                    NotePresentPorts(present); // ports plugged in while connected are not "arrivals" later
                    if (!present.Contains(port)) { lostReason = "port removed"; break; }
                    link.DiscardInput(); // firmware chatter must never fill the RX buffer
                    nextPresence = now + presenceInterval;
                }

                if (!TrySendPending(link))
                {
                    long waitMs = (nextPresence - Stopwatch.GetTimestamp()) * 1000 / Freq + 1;
                    _frameReady.WaitOne((int)Math.Clamp(waitMs, 1, _opt.RescanIntervalMs));
                }
            }
        }
        catch (Exception ex)
        {
            lostReason = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (blackOnExit) SendBlackAndDrain(link);
        _activeLink = null;
        SafeDispose(link);
        _probe.Remove(port);

        if (lostReason is not null) Log.Warn($"Adalight: lost device on {port} ({lostReason}); searching again");
        if (req is not null)
        {
            Log.Info($"Adalight: blackout sent, {port} closed");
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

    /// <summary>Sends the mailbox frame if there is one, waiting (high-res) for the pacing slot. Returns false when empty.</summary>
    private bool TrySendPending(ISerialLink link)
    {
        long due;
        lock (_mail)
        {
            if (!_hasPending) return false;
            due = _nextAllowed;
        }
        if (Stopwatch.GetTimestamp() < due) _senderTimer!.WaitUntil(due);

        var fmt = _format;
        lock (_mail)
        {
            if (!_hasPending) return false;
            (_pending, _sending) = (_sending, _pending);
            _sendingLen = _pendingLen;
            _hasPending = false;
            _nextAllowed = Stopwatch.GetTimestamp() + fmt.IntervalTicks;
        }
        if (_tx.Length < fmt.FrameBytes) _tx = new byte[fmt.FrameBytes];
        fmt.Build(_sending.AsSpan(0, _sendingLen), _tx);
        WriteFrame(link, fmt.FrameBytes, fmt.IntervalTicks);
        return true;
    }

    private void WriteFrame(ISerialLink link, int count, long intervalTicks)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (_mail) _nextAllowed = t0 + intervalTicks; // pace from the actual write start
        Volatile.Write(ref _writeStartedAt, t0);
        try
        {
            link.Write(_tx.AsSpan(0, count));
        }
        finally
        {
            Volatile.Write(ref _writeStartedAt, 0);
        }
        long t1 = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastWriteStart, t0);
        Volatile.Write(ref _lastWriteDuration, t1 - t0);
        Interlocked.Increment(ref _framesWritten);

        _fpsCount++;
        long win = t1 - _fpsWindowStart;
        if (win >= Freq)
        {
            Volatile.Write(ref _fps, _fpsCount * (double)Freq / win);
            _fpsCount = 0;
            _fpsWindowStart = t1;
        }
    }

    /// <summary>All-black frame, then wait until it has left the wire, so closing the port can't truncate it.</summary>
    private void SendBlackAndDrain(ISerialLink link)
    {
        try
        {
            var fmt = _format;
            long due;
            lock (_mail)
            {
                _hasPending = false;
                due = _nextAllowed;
            }
            _senderTimer!.WaitUntil(due); // previous frame is on the wire (≤ one interval)
            if (_tx.Length < fmt.FrameBytes) _tx = new byte[fmt.FrameBytes];
            fmt.BuildBlack(_tx);
            long t0 = Stopwatch.GetTimestamp();
            WriteFrame(link, fmt.FrameBytes, fmt.IntervalTicks);
            _senderTimer.WaitUntil(t0 + fmt.WireTicks + _opt.BlackoutDrainMarginMs * Freq / 1000);
            long limit = Stopwatch.GetTimestamp() + 50 * Freq / 1000;
            while (link.BytesToWrite > 0 && Stopwatch.GetTimestamp() < limit)
                _senderTimer.WaitUntil(Stopwatch.GetTimestamp() + 2 * Freq / 1000);
        }
        catch (Exception ex)
        {
            Log.Warn($"Adalight: could not send black on {link.PortName}: {ex.Message}");
        }
    }

    /// <summary>Some USB-serial drivers block forever in WriteFile after surprise removal; cancel such a write.</summary>
    private void CheckWriteWatchdog()
    {
        long started = Volatile.Read(ref _writeStartedAt);
        if (started != 0 && Stopwatch.GetTimestamp() - started > _opt.WriteWatchdogMs * Freq / 1000)
        {
            Volatile.Write(ref _writeStartedAt, 0);
            Log.Warn("Adalight: write is stuck; cancelling");
            _activeLink?.CancelPendingIo();
        }
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private void Signal()
    {
        try
        {
            _control.Set();
            _frameReady.Set();
        }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    private BlackoutRequest? TakeBlackout() => Interlocked.Exchange(ref _blackout, null);

    private void SetStatus(DeviceStatus status, string? port)
    {
        if (_status == status && string.Equals(_portName, port, StringComparison.OrdinalIgnoreCase)) return;
        _portName = port;
        _status = status;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("Adalight: StatusChanged handler failed", ex); }
    }

    private void RaiseDeviceFound(SerialPortInfo info)
    {
        try { DeviceFound?.Invoke(info); }
        catch (Exception ex) { Log.Error("Adalight: DeviceFound handler failed", ex); }
    }

    private IReadOnlyCollection<string> SafePresentPorts()
    {
        try
        {
            var p = _ports.GetPresentPortNames();
            _presentReadFailed = false;
            return p is HashSet<string> { Comparer: var c } hs && c.Equals(StringComparer.OrdinalIgnoreCase)
                ? hs
                : new HashSet<string>(p, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _presentReadFailed = true;
            Log.Warn($"Adalight: reading present ports failed: {ex.Message}");
            // Unknown: assume the current port is still there rather than dropping a working connection.
            var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_portName is { } cur) hs.Add(cur);
            return hs;
        }
    }

    private IReadOnlyCollection<string> SafeModemPorts()
    {
        try { return _ports.GetModemPortNames(); }
        catch (Exception ex)
        {
            Log.Warn($"Adalight: listing modem ports failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private IReadOnlyList<SerialPortInfo> SafeListPorts()
    {
        try { return _ports.ListPorts(); }
        catch (Exception ex)
        {
            Log.Warn($"Adalight: listing ports failed: {ex.Message}");
            return Array.Empty<SerialPortInfo>();
        }
    }

    private static void SafeDispose(ISerialLink link)
    {
        try { link.Dispose(); } catch { /* after surprise removal closing may throw; nothing to do */ }
    }

    private static string NormalizePort(string? port)
        => string.IsNullOrWhiteSpace(port) || port.Trim().Equals(DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
            ? DeviceSettings.AutoPort
            : port.Trim().ToUpperInvariant();

    private static bool IsAuto(string port) => port == DeviceSettings.AutoPort;
    private static bool EqPort(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool EqId(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private sealed class BlackoutRequest
    {
        public readonly ManualResetEventSlim Done = new(false);
    }

    private sealed class ProbeState
    {
        public long RetryAt;
        public int Failures;
        public ProbeOutcome Last = ProbeOutcome.None;
    }

    private enum ProbeOutcome { None, Success, Busy, OpenFailed, NoGreeting, Garbage, Interrupted }
    private enum GreetingResult { Greeted, Timeout, Interrupted, Failed }
}
