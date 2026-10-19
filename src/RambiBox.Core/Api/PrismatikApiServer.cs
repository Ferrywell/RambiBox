using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RambiBox.Core.Calibration;
using RambiBox.Core.Config;
using static RambiBox.Core.Api.PrismatikProtocol;

namespace RambiBox.Core.Api;

/// <summary>State shown in the Settings page.</summary>
/// <param name="Endpoint">"127.0.0.1:3636" or "192.168.2.5:3636" (all interfaces).</param>
/// <param name="LockedBy">Address of the client holding the lock.</param>
public sealed record PrismatikServerStatus(bool Enabled, bool Listening, string? Endpoint, int Clients, string? LockedBy, bool Persisting, string? Error)
{
    public static PrismatikServerStatus Off { get; } = new(false, false, null, 0, null, false, null);
}

/// <summary>
/// Prismatik / Lightpack compatible API server: line-based TCP text protocol (default port 3636) so existing
/// integrations (Home Assistant, the Python "lightpack" library, scripts, Stream Deck plugins) can control RambiBox.
/// Protocol details and sources: see <see cref="PrismatikProtocol"/>.
/// <para>
/// Robustness: fully async sockets, at most <see cref="MaxClients"/> clients, line length limit, idle timeout,
/// write timeout; nothing a client sends can throw out of the server. Security: loopback only unless
/// <see cref="ApiSettings.AllowLan"/> (then private-network clients only), optional API key (constant-time compare).
/// </para>
/// </summary>
public sealed class PrismatikApiServer : IDisposable
{
    private readonly IPrismatikHost _host;
    private readonly string _appVersion;
    private readonly object _gate = new();
    private readonly object _applyLock = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly Dictionary<int, Session> _sessions = new();

    private ApiSettings? _applied;
    private CancellationTokenSource? _cts;
    private bool _listening;
    private string? _endpoint, _error;
    private int _nextId, _boundPort;
    private byte[] _key = Array.Empty<byte>();

    // Lock / API colors (under _gate)
    private Session? _owner;
    private bool _persistOnUnlock, _persisting;
    private string? _persistOwner;
    private ApiColorOverride? _override;
    private byte[] _apiColors = Array.Empty<byte>();

    public PrismatikApiServer(IPrismatikHost host, string appVersion = "")
    {
        _host = host;
        _appVersion = appVersion;
    }

    public int MaxClients { get; init; } = 8;
    /// <summary>Longest accepted command line in bytes (a setcolor for 1000 LEDs is ~17 KB).</summary>
    public int MaxLineLength { get; init; } = 64 * 1024;
    /// <summary>A client that sends nothing for this long is disconnected (its lock is released).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a set-command waits for the application to apply it before answering "ok" anyway.</summary>
    public TimeSpan ApplyTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Delay before answering a wrong API key (slows down guessing).</summary>
    public TimeSpan AuthFailureDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Test hook: maps a remote endpoint to the address used for the network check.</summary>
    internal Func<IPEndPoint, IPAddress>? ClientAddress { get; init; }

    /// <summary>The port actually listened on (useful with port 0 in tests); 0 when not listening.</summary>
    public int Port { get { lock (_gate) return _listening ? _boundPort : 0; } }

    /// <summary>Raised (on a socket or caller thread) when listening state, clients or the lock changed.</summary>
    public event Action<PrismatikServerStatus>? StatusChanged;

    public PrismatikServerStatus Status
    {
        get
        {
            lock (_gate)
                return new PrismatikServerStatus(_applied?.Enabled == true, _listening, _endpoint, _sessions.Count,
                    _owner?.Address, _persisting, _error);
        }
    }

    /// <summary>The API currently drives the LEDs (locked, or persisted colors after an unlock).</summary>
    public PrismatikControl Control { get { lock (_gate) return BuildControl(); } }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Starts, stops or restarts the server to match <paramref name="settings"/>. A change of port, LAN access or key
    /// restarts it (connected clients are dropped, their lock released). Never throws; errors go to <see cref="Status"/>.
    /// </summary>
    public void Apply(ApiSettings settings)
    {
        var s = settings.Copy();
        ApiSettings.Normalize(s);
        lock (_applyLock)
        {
            if (_applied is not null && _applied.SameAs(s)) return;
            StopCore();
            lock (_gate) { _applied = s; _error = null; }
            if (s.Enabled) StartCore(s);
        }
        RaiseStatus();
    }

    /// <summary>Stops listening and disconnects everybody.</summary>
    public void Stop()
    {
        lock (_applyLock)
        {
            StopCore();
            lock (_gate) { if (_applied is not null) _applied.Enabled = false; _error = null; }
        }
        RaiseStatus();
    }

    private void StartCore(ApiSettings s)
    {
        var cts = new CancellationTokenSource();
        var listeners = new List<TcpListener>();
        string? error = null;
        int port = s.Port;
        try
        {
            if (s.AllowLan)
            {
                listeners.Add(Bind(IPAddress.IPv6Any, port, dualMode: true, fallback: IPAddress.Any));
            }
            else
            {
                var v4 = Bind(IPAddress.Loopback, port, dualMode: false, fallback: null);
                listeners.Add(v4);
                port = ((IPEndPoint)v4.LocalEndpoint).Port;
                // "localhost" may resolve to ::1 first; also listen there when IPv6 is available (best effort).
                try { listeners.Add(Bind(IPAddress.IPv6Loopback, port, dualMode: false, fallback: null)); }
                catch (Exception ex) when (ex is SocketException or NotSupportedException) { /* no IPv6 loopback */ }
            }
            port = ((IPEndPoint)listeners[0].LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            foreach (var l in listeners) { try { l.Stop(); } catch { /* ignore */ } }
            listeners.Clear();
            error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                ? $"Port {s.Port} is already in use by another program (Prismatik?)"
                : $"Could not start: {ex.Message}";
            Log.Warn($"Prismatik API: {error}");
        }

        string endpoint = s.AllowLan ? $"{LanAddress.Pick()?.ToString() ?? "0.0.0.0"}:{port}" : $"127.0.0.1:{port}";
        lock (_gate)
        {
            _error = error;
            _key = Encoding.UTF8.GetBytes(s.ApiKey);
            if (error is not null) { cts.Dispose(); return; }
            _cts = cts;
            _listeners.AddRange(listeners);
            _listening = true;
            _boundPort = port;
            _endpoint = endpoint;
        }
        Log.Info($"Prismatik API: listening on {(s.AllowLan ? $"all interfaces (LAN clients allowed), port {port}" : endpoint)}" +
                 $"{(s.ApiKey.Length > 0 ? ", API key required" : "")}");
        foreach (var l in listeners) _ = AcceptLoop(l, s.AllowLan, cts.Token);
    }

    private static TcpListener Bind(IPAddress address, int port, bool dualMode, IPAddress? fallback)
    {
        try
        {
            var l = new TcpListener(address, port) { ExclusiveAddressUse = true };
            if (dualMode) l.Server.DualMode = true;
            l.Start(16);
            return l;
        }
        catch (Exception ex) when (fallback is not null && ex is SocketException or NotSupportedException)
        {
            var l = new TcpListener(fallback, port) { ExclusiveAddressUse = true };
            l.Start(16);
            return l;
        }
    }

    private void StopCore()
    {
        List<Session> sessions;
        lock (_gate)
        {
            bool wasListening = _listening;
            _cts?.Cancel(); // not disposed: sessions may still be linking to its token while they wind down
            _cts = null;
            foreach (var l in _listeners) { try { l.Stop(); } catch { /* ignore */ } }
            _listeners.Clear();
            _listening = false;
            _endpoint = null;
            _boundPort = 0;
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
            bool controlChanged = _owner is not null || _persisting;
            _owner = null;
            _persisting = false;
            _persistOwner = null;
            _override = null;
            if (controlChanged) NotifyControl();
            if (wasListening) Log.Info("Prismatik API: stopped");
        }
        foreach (var s in sessions) s.Close();
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ accepting

    /// <summary>Loopback only, or (with LAN access) loopback + private/link-local networks.</summary>
    internal static bool IsClientAllowed(IPAddress ip, bool allowLan)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return allowLan ? LanAddress.IsAllowedClient(ip) : IPAddress.IsLoopback(ip);
    }

    private async Task AcceptLoop(TcpListener listener, bool allowLan, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"Prismatik API: accept failed: {ex.SocketErrorCode}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }
            catch (Exception) { return; }

            try { Admit(socket, allowLan, ct); }
            catch (Exception ex)
            {
                Log.Warn($"Prismatik API: could not accept a client: {ex.Message}");
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private void Admit(Socket socket, bool allowLan, CancellationToken ct)
    {
        var remote = socket.RemoteEndPoint as IPEndPoint;
        var ip = remote is null ? null : ClientAddress?.Invoke(remote) ?? remote.Address;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        string address = ip?.ToString() ?? "?";
        if (ip is null || !IsClientAllowed(ip, allowLan))
        {
            Log.Warn($"Prismatik API: refused connection from {address} (not a local/private network address)");
            socket.Dispose();
            return;
        }
        Session session;
        int count;
        lock (_gate)
        {
            if (ct.IsCancellationRequested || !_listening) { socket.Dispose(); return; }
            if (_sessions.Count >= MaxClients)
            {
                Log.Warn($"Prismatik API: refused {address} (already {_sessions.Count} clients)");
                socket.Dispose();
                return;
            }
            session = new Session(++_nextId, socket, address, $"{address}:{remote!.Port}", _key.Length == 0);
            _sessions.Add(session.Id, session);
            count = _sessions.Count;
        }
        Log.Info($"Prismatik API: {session.EndPoint} connected ({count} client{(count == 1 ? "" : "s")})");
        RaiseStatus();
        _ = RunSession(session, ct);
    }

    // ------------------------------------------------------------------ session I/O

    private sealed class Session(int id, Socket socket, string address, string endPoint, bool authorized)
    {
        public int Id { get; } = id;
        public Socket Socket { get; } = socket;
        public string Address { get; } = address;
        public string EndPoint { get; } = endPoint;
        public bool Authorized { get; set; } = authorized;
        private int _closed;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { Socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            try { Socket.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task RunSession(Session s, CancellationToken serverCt)
    {
        string reason = "disconnected";
        try
        {
            s.Socket.NoDelay = true;
            using var stream = new NetworkStream(s.Socket, ownsSocket: false);
            if (!await WriteAsync(stream, Greeting, serverCt).ConfigureAwait(false)) return;

            var buf = new byte[4096];
            var line = new byte[Math.Min(MaxLineLength, 4096)];
            int len = 0;
            bool overflow = false;
            while (!serverCt.IsCancellationRequested)
            {
                int n;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
                {
                    idle.CancelAfter(IdleTimeout);
                    try { n = await stream.ReadAsync(buf, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!serverCt.IsCancellationRequested)
                    {
                        reason = "idle timeout";
                        return;
                    }
                }
                if (n == 0) return;

                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == (byte)'\n')
                    {
                        string? reply;
                        bool close = false;
                        if (overflow)
                        {
                            overflow = false;
                            reply = Error;
                            Log.Warn($"Prismatik API: {s.EndPoint} sent a line longer than {MaxLineLength} bytes; ignored");
                        }
                        else
                        {
                            string cmd = Encoding.UTF8.GetString(line, 0, len).Trim();
                            (reply, close) = await HandleAsync(s, cmd).ConfigureAwait(false);
                        }
                        len = 0;
                        if (reply is not null && !await WriteAsync(stream, reply, serverCt).ConfigureAwait(false)) return;
                        if (close) { reason = "exit"; return; }
                    }
                    else if (!overflow)
                    {
                        if (len >= MaxLineLength) { overflow = true; len = 0; continue; }
                        if (len == line.Length) Array.Resize(ref line, Math.Min(MaxLineLength, line.Length * 2));
                        line[len++] = b;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // connection dropped / server stopped
        }
        catch (Exception ex)
        {
            reason = "error";
            Log.Error($"Prismatik API: session {s.EndPoint} failed", ex);
        }
        finally
        {
            s.Close();
            EndSession(s, serverCt.IsCancellationRequested ? "server stopped" : reason);
        }
    }

    private async Task<bool> WriteAsync(NetworkStream stream, string text, CancellationToken serverCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(WriteTimeout);
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private void EndSession(Session s, string reason)
    {
        int count;
        lock (_gate)
        {
            if (!_sessions.Remove(s.Id) && reason == "server stopped") return; // StopCore already cleaned up
            count = _sessions.Count;
            if (ReferenceEquals(_owner, s)) ReleaseLock(s, "client disconnected");
        }
        Log.Info($"Prismatik API: {s.EndPoint} {(reason == "exit" ? "said goodbye" : reason)} ({count} client{(count == 1 ? "" : "s")} left)");
        RaiseStatus();
    }

    // ------------------------------------------------------------------ commands

    private async Task<(string? Reply, bool Close)> HandleAsync(Session s, string cmd)
    {
        try
        {
            return await DispatchAsync(s, cmd).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Prismatik API: command from {s.EndPoint} failed", ex);
            return (Error, false);
        }
    }

    /// <summary>Command dispatch in Prismatik's order (ApiServer::clientProcessCommands).</summary>
    private async Task<(string? Reply, bool Close)> DispatchAsync(Session s, string cmd)
    {
        if (cmd.Length == 0) return (null, false);
        if (cmd == "exit") return (Goodbye, true);
        if (cmd == "help") return (HelpMessage(_appVersion), false);
        if (cmd == "?") return (ShortHelpMessage(), false);

        if (cmd.StartsWith("apikey:", StringComparison.Ordinal))
        {
            byte[] key;
            lock (_gate) key = _key;
            if (key.Length == 0) return (ApiKeyOk, false);
            var given = Encoding.UTF8.GetBytes(cmd["apikey:".Length..]);
            bool ok = given.Length == key.Length && CryptographicOperations.FixedTimeEquals(given, key);
            s.Authorized = ok;
            if (ok) return (ApiKeyOk, false);
            Log.Warn($"Prismatik API: wrong API key from {s.EndPoint}");
            await Task.Delay(AuthFailureDelay).ConfigureAwait(false);
            return (ApiKeyFail, false);
        }
        if (!s.Authorized) return (AuthRequired, false);

        switch (cmd)
        {
            case "getstatus":
                return (_host.GetState().Status switch
                {
                    PrismatikStatus.On => StatusOn,
                    PrismatikStatus.Off => StatusOff,
                    PrismatikStatus.DeviceError => StatusDeviceError,
                    _ => StatusUnknown,
                }, false);
            case "getstatusapi":
                lock (_gate) return (_owner is not null ? StatusApiBusy : StatusApiIdle, false);
            case "getprofiles":
                return ($"profiles:{ProfileName};\r\n", false);
            case "getprofile":
                return ($"profile:{ProfileName}\r\n", false);
            case "getdevices":
                return ($"devices:{_host.GetState().DeviceName};\r\n", false);
            case "getdevice":
                return ($"device:{_host.GetState().DeviceName}\r\n", false);
            case "getmaxleds":
                return ($"maxleds:{_host.GetState().MaxLeds}\r\n", false);
            case "getcountleds":
                return ($"countleds:{_host.GetState().LedCount}\r\n", false);
            case "getleds":
            {
                var st = _host.GetState();
                var sb = new StringBuilder("leds:", 16 + st.LedCount * 20);
                for (int i = 0; i < st.Zones.Count; i++)
                    sb.Append(i).Append('-').Append(ZoneToPixels(st.Zones[i], st.Screen).ToString()).Append(';');
                return (sb.Append(Eol).ToString(), false);
            }
            case "getcolors":
            {
                var c = _host.GetColors();
                var sb = new StringBuilder("colors:", 16 + c.Length * 4);
                for (int i = 0; i + 2 < c.Length; i += 3)
                    sb.Append(i / 3).Append('-').Append(c[i]).Append(',').Append(c[i + 1]).Append(',').Append(c[i + 2]).Append(';');
                return (sb.Append(Eol).ToString(), false);
            }
            case "getfps":
                return ($"fps:{FormatDouble(_host.GetState().Fps)}\r\n", false);
            case "getscreensize":
                return ($"screensize:{_host.GetState().Screen}\r\n", false);
            case "getcountmonitors":
                return ($"countmonitors:{_host.GetMonitors().Count}\r\n", false);
            case "getmode":
                return (_host.GetState().Mode == LightMode.Ambilight ? ModeAmbilight : ModeMoodlamp, false);
            case "getgamma":
                return ($"gamma:{FormatDouble(_host.GetState().Gamma)}\r\n", false);
            case "getbrightness":
                return ($"brightness:{BrightnessToApi(_host.GetState().Brightness)}\r\n", false);
            case "getsmooth":
                return ($"smooth:{MsToSmooth(_host.GetState().SmoothingMs)}\r\n", false);
            case "getpersistonunlock":
                lock (_gate) return (_persistOnUnlock ? PersistOn : PersistOff, false);
            case "getlockstatus":
                lock (_gate) return ($"lockstatus:{(_owner is null ? "no" : ReferenceEquals(_owner, s) ? "ok" : "busy")}\r\n", false);
            case "lock":
                return (Lock(s), false);
            case "unlock":
                lock (_gate)
                {
                    if (!ReferenceEquals(_owner, s)) return (UnlockNotLocked, false);
                    ReleaseLock(s, "unlocked");
                }
                RaiseStatus();
                return (UnlockSuccess, false);
        }

        if (cmd.StartsWith("getsizemonitor:", StringComparison.Ordinal))
        {
            var mons = _host.GetMonitors();
            return int.TryParse(Payload(cmd), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) && m >= 0 && m < mons.Count
                ? ($"sizemonitor:{mons[m]}\r\n", false)
                : (Error, false);
        }
        if (cmd.StartsWith("guid:", StringComparison.Ordinal))
            return (Error, false); // Prismatik plugin session keys; RambiBox has no plugins
        if (cmd.StartsWith("setdevice:", StringComparison.Ordinal) || cmd.StartsWith("setcountleds:", StringComparison.Ordinal))
            return (Deprecated, false);

        string? name = SetCommandName(cmd);
        if (name is null) return (Unknown, false);

        // Set-commands: only for the client holding the lock.
        lock (_gate)
        {
            if (_owner is null) return (NotLocked, false);
            if (!ReferenceEquals(_owner, s)) return (Busy, false);
        }
        return (await SetAsync(s, name, Payload(cmd)).ConfigureAwait(false), false);
    }

    private static readonly string[] SetCommands =
    [
        "setcolor:", "setgamma:", "setbrightness:", "setsmooth:", "setpersistonunlock:", "setprofile:",
        "setleds:", "newprofile:", "deleteprofile:", "setstatus:", "setmode:",
    ];

    private static string? SetCommandName(string cmd)
    {
        foreach (var c in SetCommands)
            if (cmd.StartsWith(c, StringComparison.Ordinal)) return c;
        return null;
    }

    private static string Payload(string cmd) => cmd[(cmd.IndexOf(':') + 1)..];

    private async Task<string> SetAsync(Session s, string name, string payload)
    {
        switch (name)
        {
            case "setcolor:":
                return SetColor(s, payload);

            case "setgamma:":
                // Prismatik: at most 5 characters, 0.01..10. RambiBox clamps the applied value to its own range.
                if (payload.Length > 5 || !double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out double gamma)
                    || !double.IsFinite(gamma) || gamma < GammaMin || gamma > GammaMax)
                    return Error;
                return await ApplyAsync(new PrismatikChange { Gamma = gamma }).ConfigureAwait(false);

            case "setbrightness:":
                if (payload.Length > 3 || !int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bright)
                    || bright < BrightnessMin || bright > BrightnessMax)
                    return Error;
                return await ApplyAsync(new PrismatikChange { Brightness = bright / 100.0 }).ConfigureAwait(false);

            case "setsmooth:":
                if (payload.Length > 3 || !int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int smooth)
                    || smooth < SmoothMin || smooth > SmoothMax)
                    return Error;
                return await ApplyAsync(new PrismatikChange { SmoothingMs = SmoothToMs(smooth) }).ConfigureAwait(false);

            case "setpersistonunlock:":
                if (payload is not ("on" or "off")) return Error;
                lock (_gate) _persistOnUnlock = payload == "on";
                return Ok;

            case "setprofile:":
                return payload == ProfileName ? Ok : Error;

            case "newprofile:":
            case "deleteprofile:":
                return Error; // RambiBox has a single settings set

            case "setstatus:":
                if (payload is not ("on" or "off")) return Error;
                return await ApplyAsync(new PrismatikChange { Enabled = payload == "on" }).ConfigureAwait(false);

            case "setmode:":
            {
                if (payload == "ambilight")
                    return await ApplyAsync(new PrismatikChange { Mode = LightMode.Ambilight }).ConfigureAwait(false);
                if (payload != "moodlamp") return Error;
                // Prismatik's mood lamp = RambiBox's static color or mood light; keep whichever is selected,
                // coming from ambilight use the mood light (Prismatik's default mood lamp cycles colors).
                return _host.GetState().Mode == LightMode.Ambilight
                    ? await ApplyAsync(new PrismatikChange { Mode = LightMode.Mood }).ConfigureAwait(false)
                    : Ok;
            }

            case "setleds:":
            {
                var st = _host.GetState();
                var rects = ParseSetLeds(payload, st.LedCount);
                if (rects.Count == 0) return Ok; // like Prismatik: invalid entries are dropped, answer stays ok
                var zones = rects.Select(r => PixelsToZone(r.Index, r.Rect, st.Screen)).ToList();
                return await ApplyAsync(new PrismatikChange { Zones = zones }).ConfigureAwait(false);
            }
        }
        return Error;
    }

    private string SetColor(Session s, string payload)
    {
        int n = _host.GetState().LedCount;
        var parsed = new List<LedColor>();
        if (!TryParseSetColor(payload, n, parsed)) return Error;
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, s) || _override is null) return NotLocked;
            if (_apiColors.Length != n * 3) _apiColors = new byte[n * 3]; // LED count changed: start from black
            foreach (var c in parsed)
            {
                _apiColors[c.Index * 3] = c.R;
                _apiColors[c.Index * 3 + 1] = c.G;
                _apiColors[c.Index * 3 + 2] = c.B;
            }
            _override.SetColors(_apiColors);
        }
        return Ok;
    }

    private async Task<string> ApplyAsync(PrismatikChange change)
    {
        try
        {
            var task = _host.ApplyAsync(change);
            var done = await Task.WhenAny(task, Task.Delay(ApplyTimeout)).ConfigureAwait(false);
            if (done != task) return Ok; // queued; the application applies it as soon as it can
            return await task.ConfigureAwait(false) ? Ok : Error;
        }
        catch (Exception ex)
        {
            Log.Warn($"Prismatik API: applying a change failed: {ex.Message}");
            return Error;
        }
    }

    // ------------------------------------------------------------------ lock

    private string Lock(Session s)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_owner, s)) return LockSuccess;
            if (_owner is not null) return LockBusy;
            // Until the first setcolor the LEDs keep showing what they showed (Prismatik freezes the last frame):
            // the persisted API colors, or the current normal output.
            byte[] current;
            if (_persisting && _override is not null) current = _override.GetColors();
            else
            {
                try { current = _host.GetColors(); }
                catch { current = Array.Empty<byte>(); }
            }
            _owner = s;
            _persisting = false;
            _persistOwner = null;
            _override = new ApiColorOverride(current);
            NotifyControl();
        }
        Log.Info($"Prismatik API: LEDs locked by {s.EndPoint}");
        RaiseStatus();
        return LockSuccess;
    }

    /// <summary>Under _gate.</summary>
    private void ReleaseLock(Session s, string why)
    {
        _owner = null;
        if (_persistOnUnlock && _override is not null)
        {
            _persisting = true;
            _persistOwner = s.Address;
            Log.Info($"Prismatik API: {s.EndPoint} released the LEDs ({why}); its colors persist until the lights or mode change");
        }
        else
        {
            _override = null;
            Log.Info($"Prismatik API: {s.EndPoint} released the LEDs ({why})");
        }
        NotifyControl();
    }

    /// <summary>
    /// Ends "persist on unlock" (the host calls this when the user switches the lights or the mode,
    /// like Prismatik's ApiPersist → Unlocked transition).
    /// </summary>
    public void ReleasePersistedColors()
    {
        lock (_gate)
        {
            if (!_persisting) return;
            _persisting = false;
            _persistOwner = null;
            if (_owner is null) _override = null;
            NotifyControl();
        }
        Log.Info("Prismatik API: persisted colors released");
        RaiseStatus();
    }

    private PrismatikControl BuildControl() =>
        _owner is null && !_persisting ? PrismatikControl.None
            : new PrismatikControl(_owner is not null, _persisting && _owner is null, _owner?.Address ?? _persistOwner, _override);

    /// <summary>Under _gate.</summary>
    private void NotifyControl()
    {
        try { _host.OnApiControlChanged(BuildControl()); }
        catch (Exception ex) { Log.Warn($"Prismatik API: host callback failed: {ex.Message}"); }
    }

    private void RaiseStatus()
    {
        var h = StatusChanged;
        if (h is null) return;
        try { h(Status); }
        catch (Exception ex) { Log.Warn($"Prismatik API: status listener failed: {ex.Message}"); }
    }
}
