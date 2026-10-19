using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// Shared engine of the UDP LED devices (WLED realtime, DDP, E1.31). One background thread owns the socket and does
/// everything that can block: resolving the host (IP, DNS name or "name.local" via mDNS; retried every few seconds),
/// the optional device check (WLED: HTTP /json/info), creating the socket, sending, and recreating it when the network
/// changes (adapter down/up, new address). The engine thread only touches <see cref="Submit"/> (copies the frame into a
/// pre-allocated mailbox, latest-wins) and <see cref="WaitForSlot"/> (paces at <see cref="MaxFrameRate"/> with the
/// high-resolution timer). Steady state allocates nothing per frame: frames are swapped between two buffers, encoded
/// into pre-allocated datagrams and sent with pre-serialized socket addresses. Public members never throw; problems are
/// logged once per state change.
/// </summary>
public abstract class UdpLedDevice : ILedDevice
{
    /// <summary>Snapshot of the settings this device uses (DeviceSettings is a mutable DTO).</summary>
    private protected sealed record NetSettings(
        string Host, int HostPort, int Port, NetworkProtocol Protocol, int Universe, bool Multicast, int TimeoutSeconds,
        ColorOrder Order, int LedCount, string? DeviceId)
    {
        /// <summary>UDP port: explicit setting, else a ":port" typed into the host field, else 0 (protocol default).</summary>
        public int ExplicitPort => Port is > 0 and <= 65535 ? Port : HostPort;
    }

    private sealed class Link
    {
        public required NetSettings Settings;
        public required int Version;
        public IPAddress? Address;
        public int Port;
        public required Socket Socket;
        public required UdpPacketizer Packetizer;
        public SocketAddress[] Destinations = [];
        public string Display = "";
        /// <summary>A frame went out on this link (only then is there anything to black out / release).</summary>
        public bool SentAny;
    }

    private sealed class BlackoutRequest
    {
        public readonly ManualResetEventSlim Done = new(false);
    }

    private static readonly long Freq = Stopwatch.Frequency;
    private static readonly long RetryTicks = 3 * Stopwatch.Frequency;

    private readonly string _name;
    private readonly object _cfgLock = new();
    private readonly object _mail = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _cts = new();
    private NetSettings? _cfg;
    private int _cfgVersion;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _suspended;
    private int _disposed;
    private int _rescan;
    private int _netChanged;
    private BlackoutRequest? _blackout;

    // Mailbox (engine → device thread), latest-wins.
    private byte[] _pending = new byte[3];
    private byte[] _work = new byte[3];
    private int _pendingLen, _workLen;
    private bool _hasPending;
    private long _nextAllowed;
    private long _intervalTicks = Freq / 60;
    private double _maxFrameRate = 60;
    private HighResolutionTimer? _slotTimer;

    // Device-thread state.
    private Link? _link;
    private HighResolutionTimer? _timer;
    private long _retryAt; // written by Configure too (benign race: worst case one extra/late retry)
    private string? _lastProblem;
    private Task<bool>? _health;
    private long _nextHealth;
    private int _healthFailures;
    private int _frames;
    private long _fpsWindowStart;
    private double _fps;

    private volatile DeviceStatus _status = DeviceStatus.Stopped;
    private volatile string? _portName;

    private protected UdpLedDevice(string name) => _name = name;

    // ---------------------------------------------------------------- protocol hooks

    /// <summary>UDP port used when the settings leave it at 0.</summary>
    private protected abstract int DefaultPort { get; }

    /// <summary>False when the destination does not need a host (E1.31 multicast).</summary>
    private protected virtual bool NeedsHost(NetSettings s) => true;

    private protected abstract UdpPacketizer CreatePacketizer(NetSettings s);

    /// <summary>Destination of each <see cref="UdpPacketizer.Destination"/> index.</summary>
    private protected virtual IPEndPoint[] Destinations(NetSettings s, IPAddress? address, int port, UdpPacketizer packetizer) =>
        address is null ? [] : [new IPEndPoint(address, port)];

    /// <summary>
    /// Checks that the device is there before reporting Connected (WLED: HTTP /json/info). May replace the address
    /// (device found elsewhere) or the port (reported by the device). Returns null when OK, else the problem.
    /// Runs on the device thread; may block a few seconds.
    /// </summary>
    private protected virtual string? Probe(NetSettings s, ref IPAddress? address, ref int port, CancellationToken ct) =>
        address is null && NeedsHost(s) ? $"cannot resolve '{s.Host}'" : null;

    /// <summary>Periodic liveness check while connected (null = none). Two failures in a row → Searching.</summary>
    private protected virtual Task<bool>? StartHealthCheck(NetSettings s, IPAddress? address, CancellationToken ct) => null;

    /// <summary>How often <see cref="StartHealthCheck"/> runs while connected (tests shorten it).</summary>
    internal TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra socket setup (E1.31 multicast interface).</summary>
    private protected virtual void ConfigureSocket(Socket socket, NetSettings s) { }

    private protected virtual string Describe(NetSettings s, IPAddress? address, int port) =>
        address is null ? s.Host
        : IPAddress.TryParse(s.Host, out _) ? Format(address, port)
        : $"{s.Host} ({Format(address, port)})";

    private protected static string Format(IPAddress a, int port) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]:{port}" : $"{a}:{port}";

    /// <summary>Called on the device thread after (re)building the packetizer, e.g. to log truncation.</summary>
    private protected virtual void OnPacketizerBuilt(UdpPacketizer packetizer, NetSettings s) { }

    // ---------------------------------------------------------------- ILedDevice

    public DeviceStatus Status => _status;
    public string? PortName => _portName;
    public event Action? StatusChanged;

    /// <summary>Network devices never report serial ports.</summary>
    public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }

    /// <summary>Frame rate cap (default 60 fps; 1..1000). Settable so the app can expose it per device.</summary>
    public double MaxFrameRate
    {
        get => _maxFrameRate;
        set
        {
            double v = double.IsFinite(value) ? Math.Clamp(value, 1, 1000) : 60;
            _maxFrameRate = v;
            Interlocked.Exchange(ref _intervalTicks, (long)(Freq / v));
        }
    }

    public double FramesPerSecond => Volatile.Read(ref _fps);

    public void Configure(DeviceSettings settings, int ledCount)
    {
        if (settings is null || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            HostResolver.TryParse(settings.Host, out var host, out int hostPort);
            var ns = new NetSettings(host, hostPort, settings.NetworkPort, settings.Protocol, settings.E131Universe,
                settings.E131Multicast, settings.WledTimeoutSeconds, settings.ColorOrder, Math.Max(1, ledCount), settings.DeviceId);
            bool start = false;
            lock (_cfgLock)
            {
                if (!ns.Equals(_cfg))
                {
                    _cfg = ns;
                    Interlocked.Increment(ref _cfgVersion);
                    _retryAt = 0; // new settings: try right away
                }
                if (_thread is null)
                {
                    _thread = new Thread(ThreadMain) { IsBackground = true, Name = $"RambiBox {_name}", Priority = ThreadPriority.AboveNormal };
                    start = true;
                }
            }
            if (start)
            {
                Log.Info($"{_name}: starting ({(ns.Host.Length > 0 ? ns.Host : "no host")}, {ns.LedCount} LEDs, {ns.Order})");
                _thread!.Start();
            }
            if (_suspended)
            {
                _retryAt = 0;
                _suspended = false;
                Log.Info($"{_name}: resuming after blackout");
            }
            Signal();
        }
        catch (Exception ex)
        {
            Log.Error($"{_name}: configure failed", ex);
        }
    }

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        if (_status != DeviceStatus.Connected || _suspended) return;
        lock (_mail)
        {
            if (_pending.Length < rgb.Length) _pending = new byte[rgb.Length]; // only when the LED count grows
            rgb.CopyTo(_pending);
            _pendingLen = rgb.Length;
            _hasPending = true;
        }
        Signal();
    }

    public bool WaitForSlot(int timeoutMs)
    {
        if (_status != DeviceStatus.Connected) return false;
        long deadline = Stopwatch.GetTimestamp() + Math.Max(0, timeoutMs) * Freq / 1000;
        HighResolutionTimer? timer = null;
        try
        {
            while (true)
            {
                if (_status != DeviceStatus.Connected || _suspended) return false;
                long now = Stopwatch.GetTimestamp();
                long slot;
                lock (_mail)
                {
                    slot = _nextAllowed;
                    // An unsent frame goes out at max(now, nextAllowed) and takes one interval.
                    if (_hasPending) slot = Math.Max(slot, now) + Interlocked.Read(ref _intervalTicks);
                }
                if (now >= slot) return true;
                if (now >= deadline) return false;
                timer ??= Interlocked.Exchange(ref _slotTimer, null) ?? new HighResolutionTimer();
                timer.WaitUntil(Math.Min(slot, deadline));
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (timer is not null && Interlocked.CompareExchange(ref _slotTimer, timer, null) is not null) timer.Dispose();
        }
    }

    /// <summary>Re-resolves the host and re-checks the device now.</summary>
    public void Rescan()
    {
        Interlocked.Exchange(ref _rescan, 1);
        Signal();
    }

    /// <summary>Sends the protocol's stop sequence (black, then "release") and closes the socket until the next Configure.</summary>
    public void Blackout()
    {
        try
        {
            var t = _thread;
            if (t is null || !t.IsAlive || Volatile.Read(ref _disposed) != 0) { _suspended = true; return; }
            if (Thread.CurrentThread == t || _status != DeviceStatus.Connected)
            {
                _suspended = true;
                Signal();
                return;
            }
            var req = new BlackoutRequest();
            Volatile.Write(ref _blackout, req);
            Signal();
            if (!req.Done.Wait(400)) Log.Warn($"{_name}: blackout did not complete in time");
        }
        catch (Exception ex)
        {
            Log.Warn($"{_name}: blackout failed: {ex.Message}");
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
                try { _cts.Cancel(); } catch { /* ignore */ }
                joined = t.Join(1000);
            }
            if (!joined) Log.Warn($"{_name}: device thread did not stop in time");
        }
        else if (t is null) SetStatus(DeviceStatus.Stopped, null);
        try { _cts.Cancel(); } catch { /* ignore */ }
        if (joined)
        {
            _signal.Dispose();
            _cts.Dispose();
            Interlocked.Exchange(ref _slotTimer, null)?.Dispose();
        }
    }

    // ---------------------------------------------------------------- device thread

    private void ThreadMain()
    {
        _timer = new HighResolutionTimer();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        try
        {
            while (!_stop)
            {
                try { RunOnce(); }
                catch (Exception ex)
                {
                    Problem($"unexpected error: {ex.Message}");
                    CloseLink();
                    _retryAt = Stopwatch.GetTimestamp() + Freq;
                    WaitSignal(1000);
                }
            }
            // Dispose without a preceding Blackout: still hand the LEDs back.
            if (_link is { SentAny: true } && !_suspended && _status == DeviceStatus.Connected)
            {
                try { SendBlackout(_link); } catch { /* best effort */ }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            CloseLink();
            Interlocked.Exchange(ref _blackout, null)?.Done.Set();
            SetStatus(DeviceStatus.Stopped, null);
            _timer.Dispose();
            Log.Info($"{_name}: stopped");
        }
    }

    private void RunOnce()
    {
        if (Interlocked.Exchange(ref _blackout, null) is { } req)
        {
            try
            {
                if (_link is { SentAny: true } && _status == DeviceStatus.Connected)
                {
                    SendBlackout(_link);
                    Log.Info($"{_name}: blackout sent");
                }
            }
            finally
            {
                _suspended = true;
                CloseLink();
                SetStatus(DeviceStatus.Stopped, _portName);
                req.Done.Set();
            }
            return;
        }
        var cfg = Volatile.Read(ref _cfg);
        if (cfg is null || _suspended)
        {
            CloseLink();
            SetStatus(DeviceStatus.Stopped, _portName);
            WaitSignal(1000);
            return;
        }
        if (_link is null)
        {
            if (_status == DeviceStatus.Connected) SetStatus(DeviceStatus.Searching, _portName); // link lost
            if (!Connect(cfg))
            {
                long wait = (_retryAt - Stopwatch.GetTimestamp()) * 1000 / Freq;
                WaitSignal((int)Math.Clamp(wait, 10, 3000));
            }
            return;
        }
        ServeConnected(_link);
    }

    private bool Connect(NetSettings cfg)
    {
        long now = Stopwatch.GetTimestamp();
        bool rescan = Interlocked.Exchange(ref _rescan, 0) != 0;
        if (now < _retryAt && !rescan) return false;
        _retryAt = now + RetryTicks;
        int version = Volatile.Read(ref _cfgVersion);
        cfg = Volatile.Read(ref _cfg) ?? cfg;
        int port = cfg.ExplicitPort > 0 ? cfg.ExplicitPort : DefaultPort;
        SetStatus(DeviceStatus.Searching, cfg.Host.Length > 0 ? cfg.Host : _portName);

        IPAddress? address = null;
        if (NeedsHost(cfg))
        {
            if (cfg.Host.Length == 0)
            {
                Problem("no IP address or host name configured");
                return false;
            }
            address = HostResolver.ResolveAsync(cfg.Host, TimeSpan.FromSeconds(3), _cts.Token).GetAwaiter().GetResult();
        }
        if (_stop || _suspended) return false;
        string? problem = Probe(cfg, ref address, ref port, _cts.Token);
        if (problem is not null)
        {
            Problem(problem);
            return false;
        }
        if (_stop || _suspended) return false;

        Socket? socket = null;
        try
        {
            socket = CreateSocket(address?.AddressFamily ?? AddressFamily.InterNetwork, cfg);
            var pk = CreatePacketizer(cfg);
            var link = new Link { Settings = cfg, Version = version, Address = address, Port = port, Socket = socket, Packetizer = pk };
            SetDestinations(link);
            link.Display = Describe(cfg, address, port);
            OnPacketizerBuilt(pk, cfg);
            lock (_mail)
            {
                _hasPending = false;
                _nextAllowed = Stopwatch.GetTimestamp();
            }
            _link = link;
            _health = null;
            _healthFailures = 0;
            _nextHealth = Stopwatch.GetTimestamp() + (long)(HealthInterval.TotalSeconds * Freq);
            _lastProblem = null;
            _retryAt = 0;
            Log.Info($"{_name}: connected to {link.Display}, {pk.LedCount} LEDs in {pk.Capacity} datagram(s) per frame");
            SetStatus(DeviceStatus.Connected, link.Display);
            return true;
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            Problem($"cannot open a UDP socket: {ex.Message}");
            return false;
        }
    }

    private void ServeConnected(Link link)
    {
        while (!_stop)
        {
            if (Volatile.Read(ref _blackout) is not null || _suspended) return;

            int version = Volatile.Read(ref _cfgVersion);
            if (version != link.Version)
            {
                var cfg = Volatile.Read(ref _cfg);
                if (cfg is null) return;
                if (!SameLink(link.Settings, cfg))
                {
                    Log.Info($"{_name}: destination changed - reconnecting");
                    CloseLink();
                    _retryAt = 0;
                    return;
                }
                link.Settings = cfg;
                link.Version = version;
                link.Packetizer = CreatePacketizer(cfg);
                SetDestinations(link);
                OnPacketizerBuilt(link.Packetizer, cfg);
            }

            if (Interlocked.Exchange(ref _rescan, 0) != 0)
            {
                CloseLink();
                _retryAt = 0;
                return;
            }

            if (Interlocked.Exchange(ref _netChanged, 0) != 0 && !Reopen(link)) return;

            if (!CheckHealth(link)) return;

            bool has;
            lock (_mail) has = _hasPending;
            if (has)
            {
                long now = Stopwatch.GetTimestamp();
                long next;
                lock (_mail) next = _nextAllowed;
                if (now < next)
                {
                    _timer!.WaitUntil(next);
                    continue;
                }
                lock (_mail)
                {
                    (_pending, _work) = (_work, _pending);
                    _workLen = _pendingLen;
                    _hasPending = false;
                    _nextAllowed = now + Interlocked.Read(ref _intervalTicks);
                }
                if (!SendFrame(link, _work.AsSpan(0, _workLen)))
                {
                    CloseLink();
                    _retryAt = Stopwatch.GetTimestamp() + Freq;
                    return;
                }
                CountFrame(now);
                continue;
            }
            WaitSignal(250);
        }
    }

    private static bool SameLink(NetSettings a, NetSettings b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.ExplicitPort == b.ExplicitPort
        && a.Multicast == b.Multicast && a.DeviceId == b.DeviceId && (!a.Multicast || a.Universe == b.Universe);

    /// <summary>Network changed: re-resolve names and recreate the socket. False → link closed (back to Searching).</summary>
    private bool Reopen(Link link)
    {
        try
        {
            var address = link.Address;
            if (NeedsHost(link.Settings) && !IPAddress.TryParse(link.Settings.Host, out _))
            {
                var fresh = HostResolver.ResolveAsync(link.Settings.Host, TimeSpan.FromSeconds(3), _cts.Token).GetAwaiter().GetResult();
                if (fresh is null)
                {
                    Problem($"network changed and '{link.Settings.Host}' no longer resolves");
                    CloseLink();
                    return false;
                }
                if (!fresh.Equals(address)) Log.Info($"{_name}: {link.Settings.Host} is now {fresh}");
                address = fresh;
            }
            var socket = CreateSocket(address?.AddressFamily ?? AddressFamily.InterNetwork, link.Settings);
            try { link.Socket.Dispose(); } catch { /* ignore */ }
            link.Socket = socket;
            link.Address = address;
            SetDestinations(link);
            string display = Describe(link.Settings, address, link.Port);
            link.Display = display;
            SetStatus(DeviceStatus.Connected, display);
            return true;
        }
        catch (Exception ex)
        {
            Problem($"network changed, cannot reopen the socket: {ex.Message}");
            CloseLink();
            _retryAt = Stopwatch.GetTimestamp() + Freq;
            return false;
        }
    }

    private bool CheckHealth(Link link)
    {
        long now = Stopwatch.GetTimestamp();
        if (_health is null)
        {
            if (now < _nextHealth) return true;
            _health = StartHealthCheck(link.Settings, link.Address, _cts.Token);
            _nextHealth = now + (long)(HealthInterval.TotalSeconds * Freq);
            return true;
        }
        if (!_health.IsCompleted) return true;
        bool ok = _health.Status == TaskStatus.RanToCompletion && _health.Result;
        _health = null;
        if (ok)
        {
            _healthFailures = 0;
            return true;
        }
        if (++_healthFailures < 2) return true;
        Problem($"{link.Display} stopped answering");
        CloseLink();
        _retryAt = Stopwatch.GetTimestamp() + Freq;
        return false;
    }

    private void SetDestinations(Link link)
    {
        var eps = Destinations(link.Settings, link.Address, link.Port, link.Packetizer);
        var arr = new SocketAddress[eps.Length];
        for (int i = 0; i < eps.Length; i++) arr[i] = eps[i].Serialize();
        link.Destinations = arr;
    }

    private Socket CreateSocket(AddressFamily family, NetSettings cfg)
    {
        var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            ConfigureSocket(s, cfg);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private bool SendFrame(Link link, ReadOnlySpan<byte> rgb)
    {
        int n = link.Packetizer.Build(rgb);
        link.SentAny = true;
        return SendPackets(link, n);
    }

    private bool SendPackets(Link link, int count)
    {
        var pk = link.Packetizer;
        var dests = link.Destinations;
        if (dests.Length == 0) return true;
        for (int i = 0; i < count; i++)
        {
            try
            {
                link.Socket.SendTo(pk.Packet(i), SocketFlags.None, dests[Math.Min(pk.Destination(i), dests.Length - 1)]);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock
                                                 or SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Transient: drop this datagram, the next frame (or the 1 s keep-alive) repairs it.
            }
            catch (SocketException ex)
            {
                Problem($"sending to {link.Display} failed: {ex.SocketErrorCode}");
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        return true;
    }

    private void SendBlackout(Link link)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            int n = link.Packetizer.BuildBlackout(pass);
            if (n == 0) break;
            if (pass > 0) _timer?.WaitUntil(Stopwatch.GetTimestamp() + 5 * Freq / 1000);
            if (!SendPackets(link, n)) break;
        }
    }

    private void CloseLink()
    {
        var link = _link;
        _link = null;
        _health = null;
        if (link is null) return;
        try { link.Socket.Dispose(); } catch { /* ignore */ }
        lock (_mail) _hasPending = false;
        Volatile.Write(ref _fps, 0);
    }

    private void CountFrame(long now)
    {
        _frames++;
        if (_fpsWindowStart == 0) _fpsWindowStart = now;
        long elapsed = now - _fpsWindowStart;
        if (elapsed >= Freq)
        {
            Volatile.Write(ref _fps, _frames * (double)Freq / elapsed);
            _frames = 0;
            _fpsWindowStart = now;
        }
    }

    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        Log.Warn($"{_name}: {message}");
    }

    private void SetStatus(DeviceStatus status, string? port)
    {
        if (_status == status && _portName == port) return;
        _status = status;
        _portName = port;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn($"{_name}: StatusChanged handler failed: {ex.Message}"); }
    }

    private void Signal()
    {
        try { _signal.Set(); } catch (ObjectDisposedException) { /* racing Dispose */ }
    }

    private void WaitSignal(int ms)
    {
        try { _signal.WaitOne(ms); } catch (ObjectDisposedException) { Thread.Sleep(ms); }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _netChanged, 1);
        if (_link is null) _retryAt = 0; // retry a failed connect right away
        Signal();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, e);
}
