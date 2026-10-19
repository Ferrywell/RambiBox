using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RambiBox.Core.Engine;

namespace RambiBox.Core.Calibration;

/// <summary>
/// Phone-assisted LED color calibration (v2): live camera color loop over HTTPS with an automatic fallback to a
/// recorded video over HTTP, followed by fine-tuning by eye with the phone as a remote control.
/// <para>
/// <see cref="Start"/> runs two tiny web servers on the LAN: plain HTTP (the QR code's URL; landing page, video
/// fallback, eye fine-tune, iOS profile download) and HTTPS with a certificate from the PC's own name-constrained
/// <see cref="LocalCertificateAuthority"/> (live camera: getUserMedia needs a secure context). The landing page pings
/// the HTTPS port; if the phone trusts the CA it switches to the live page, otherwise it offers the one-time setup or
/// the video fallback. All logic lives in <see cref="CalibrationSession"/>.
/// </para>
/// Security: random per-session token in every URL, private-network clients only, per-IP connection caps, the session
/// is bound to the IP that first loaded the page (both ports), bounded upload sizes and timeouts; the CA key never
/// leaves the PC.
/// Thread-safety: all public members may be called from any thread; <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class PhoneCalibrationService : IDisposable
{
    public const int FirstPort = 47800, LastPort = 47820;
    public const int MaxUploadBytes = 40 * 1024 * 1024;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxVideoFramesPerBatch = 16;

    private readonly LightEngine _engine;
    private readonly ICalibrationTarget _target;
    private readonly LocalCertificateAuthority? _ca;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CalibrationLedOverride _override = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // ---- state guarded by _gate
    private MiniHttpServer? _server, _tls;
    private X509Certificate2? _leaf;
    private bool _tlsPending;
    private string? _tlsProblem;
    private Timer? _autoStop, _ticker;
    private string _token = "";
    private IPAddress? _client;
    private IPAddress? _ip;
    private Uri? _url;
    private CalibrationSession? _session;
    private CalibrationLayout _layout = new(16.0 / 9.0);
    private bool _finished, _cancelRequested, _failed;
    private string _failMessage = "";
    private int _sessionNo;

    public PhoneCalibrationService(LightEngine engine, ICalibrationTarget target, LocalCertificateAuthority? ca = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _ca = ca;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>URL to encode in the QR code (http://&lt;lan-ip&gt;:&lt;port&gt;/c/&lt;token&gt;). Null until started.</summary>
    public Uri? Url { get { lock (_gate) return _url; } }

    /// <summary>HTTPS URL of the live camera page (null while the TLS server isn't running).</summary>
    public Uri? SecureUrl { get { lock (_gate) return _tls is null || _ip is null ? null : new Uri($"https://{_ip}:{_tls.Port}/c/{_token}"); } }

    /// <summary>What the calibration window should draw right now.</summary>
    public CalibrationPattern Pattern
    {
        get
        {
            CalibrationSession? s;
            lock (_gate) s = _session;
            return s?.Pattern ?? _layout.BuildMeasurePattern(CalibrationLayout.WhiteBandRgb, 0, showQr: true);
        }
    }

    /// <summary>True after the user tapped Done on the phone: the UI should close the window and call Stop(keepResult: true).</summary>
    public bool IsFinished { get { lock (_gate) return _finished; } }

    /// <summary>True after the user tapped Cancel on the phone: the UI should call Stop(keepResult: false).</summary>
    public bool IsCancelRequested { get { lock (_gate) return _cancelRequested; } }

    public CalibrationState State
    {
        get
        {
            CalibrationSession? s;
            lock (_gate)
            {
                s = _session;
                if (_failed || s is null) return new CalibrationState(CalibrationPhase.Failed, _failed ? _failMessage : "Not started.", "", null, null, 1, 1, 1);
            }
            return Describe(s.Snapshot());
        }
    }

    /// <summary>Raised on any thread when <see cref="State"/> or <see cref="Pattern"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the web servers and the LED pattern. Returns false (State = Failed) if impossible.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_server is not null) return true;
            _sessionNo++;
            _finished = _cancelRequested = _failed = false;
            double aspect = 16.0 / 9.0;
            try
            {
                var mon = _engine.Sampler.CurrentMonitor;
                if (mon is { Width: > 0, Height: > 0 }) aspect = (double)mon.Width / mon.Height;
            }
            catch (Exception ex) { Log.Warn($"Calibration: monitor info unavailable: {ex.Message}"); }
            _layout = new CalibrationLayout(aspect);

            IPAddress? ip;
            try { ip = AddressProvider(); }
            catch { ip = null; }
            if (ip is null)
            {
                Fail("No network connection found. Connect this PC to the same Wi-Fi router as your phone (cable or Wi-Fi) and try again.");
                Log.Warn("Calibration: no LAN address found");
                goto failed;
            }
            var server = new MiniHttpServer(HandleAsync, Precheck, MaxUploadBytes)
            {
                ClientAddress = ClientAddressOverride ?? (ep => ep.Address),
            };
            if (!server.Start(FirstPort, LastPort))
            {
                Fail($"Could not open a network port ({FirstPort}–{LastPort} are all in use).");
                Log.Warn("Calibration: no free port");
                goto failed;
            }
            _server = server;
            _ip = ip;
            _token = NewToken();
            _client = null;
            _tlsFailures = _tlsRequests = 0;
            _tlsLastError = null;
            _url = new Uri($"http://{ip}:{server.Port}/c/{_token}");
            _session = new CalibrationSession(_target, _override, _layout, () => _clock.Elapsed.TotalSeconds)
            {
                SettleSeconds = SettleSecondsOverride ?? 0.4,
            };
            _engine.SetOverride(_override);
            int no = _sessionNo;
            _autoStop = new Timer(_ => AutoStop(no), null, AutoStopAfter, Timeout.InfiniteTimeSpan);
            _ticker = new Timer(_ => OnTick(no), null, 100, 100);
            StartTlsInBackground(ip, no);
            Log.Info($"Calibration started: http://{ip}:{server.Port}/c/… (aspect {aspect:0.###}, {CalibrationSession.FormatMatrix(_session.Matrix)} matrix, WB {_session.WhiteBalance}, gamma {_session.ChannelGamma})");
        }
        RaiseChanged();
        return true;

    failed:
        RaiseChanged();
        return false;
    }

    private void Fail(string message)
    {
        _failed = true;
        _failMessage = message;
    }

    /// <summary>
    /// The HTTPS server needs a certificate; creating the CA (first run) and the RSA key of the leaf takes a moment, so
    /// it happens in the background. The landing page waits for it (state.tls) before pinging.
    /// </summary>
    private void StartTlsInBackground(IPAddress ip, int no)
    {
        if (_ca is null)
        {
            _tlsProblem = "Live camera is not available in this build.";
            return;
        }
        if (!LocalCertificateAuthority.IsPermittedAddress(ip) && !AllowTlsForAnyAddress)
        {
            _tlsProblem = $"Live camera needs a private network address (this PC has {ip}).";
            Log.Info($"Calibration: no HTTPS ({ip} is not a private address)");
            return;
        }
        _tlsPending = true;
        int httpPort = _server!.Port;
        Task.Run(() =>
        {
            X509Certificate2? leaf = null;
            MiniHttpServer? tls = null;
            string? problem = null;
            try
            {
                leaf = _ca.IssueServerCertificate(ip);
                tls = new MiniHttpServer(HandleAsync, Precheck, MaxUploadBytes)
                {
                    TlsHandshakeFailed = OnTlsHandshakeFailed,
                    ClientAddress = ClientAddressOverride ?? (ep => ep.Address),
                    ServerCertificate = leaf,
                };
                if (!tls.Start(Math.Min(httpPort + 1, LastPort), LastPort) && !tls.Start(FirstPort, LastPort))
                {
                    problem = "No free network port for the secure (HTTPS) page.";
                    tls = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Calibration: HTTPS server could not start", ex);
                problem = "The secure (HTTPS) page could not start: " + ex.Message;
                tls?.Stop();
                tls = null;
            }
            bool keep;
            lock (_gate)
            {
                keep = no == _sessionNo && _server is not null;
                if (keep)
                {
                    _tls = tls;
                    _leaf = tls is null ? null : leaf;
                    _tlsProblem = problem;
                    _tlsPending = false;
                }
            }
            if (!keep || tls is null)
            {
                tls?.Stop(); // session ended meanwhile, or the server could not start
                leaf?.Dispose();
            }
            else Log.Info($"Calibration: HTTPS on port {tls.Port}");
            RaiseChanged();
        });
    }

    /// <summary>Stops the servers and the LED override. <paramref name="keepResult"/> false restores the previous colors.</summary>
    public void Stop(bool keepResult)
    {
        CalibrationSession? session;
        lock (_gate)
        {
            bool wasRunning = _server is not null;
            _server?.Stop();
            _server = null;
            _tls?.Stop();
            _tls = null;
            _leaf?.Dispose();
            _leaf = null;
            _tlsPending = false;
            _autoStop?.Dispose();
            _autoStop = null;
            _ticker?.Dispose();
            _ticker = null;
            _url = null;
            _sessionNo++;
            if (ReferenceEquals(_engine.Override, _override)) _engine.SetOverride(null);
            session = _session;
            if (!wasRunning) session = null;
        }
        if (session is not null)
        {
            if (keepResult) session.Finish();
            else session.RestoreOriginal();
            Log.Info($"Calibration stopped (keepResult={keepResult})");
        }
        RaiseChanged();
    }

    public void Dispose() => Stop(keepResult: true);

    // ------------------------------------------------------------------ internals (tests)

    /// <summary>Address to advertise in the URL (tests substitute loopback).</summary>
    internal Func<IPAddress?> AddressProvider { get; set; } = LanAddress.Pick;
    internal TimeSpan AutoStopAfter { get; set; } = TimeSpan.FromMinutes(45);
    /// <summary>Maps a connection to its client IP (tests simulate other LAN hosts over loopback). Read by Start.</summary>
    internal Func<IPEndPoint, IPAddress>? ClientAddressOverride { get; set; }
    /// <summary>Tests: run HTTPS even on loopback.</summary>
    internal bool AllowTlsForAnyAddress { get; set; }
    internal double? SettleSecondsOverride { get; set; }
    internal int Port { get { lock (_gate) return _server?.Port ?? 0; } }
    internal int TlsPort { get { lock (_gate) return _tls?.Port ?? 0; } }
    internal bool TlsPending { get { lock (_gate) return _tlsPending; } }
    internal string Token { get { lock (_gate) return _token; } }
    internal CalibrationSession? Session { get { lock (_gate) return _session; } }
    internal CalibrationLedOverride LedOverride => _override;

    private static string NewToken()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void AutoStop(int no)
    {
        lock (_gate)
        {
            if (no != _sessionNo || _server is null) return;
            _finished = true;
        }
        Log.Info("Calibration: auto-stop after timeout");
        RaiseChanged();
    }

    private void OnTick(int no)
    {
        CalibrationSession? s;
        lock (_gate)
        {
            if (no != _sessionNo) return;
            s = _session;
        }
        if (s?.Tick() == true) RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Calibration Changed handler failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ PC window text

    private CalibrationState Describe(SessionSnapshot s)
    {
        string msg, detail = "";
        CalibrationPhase phase;
        var wb = s.Wb;
        string wbText = FormattableString.Invariant($"white balance R {wb.R:P0} · G {wb.G:P0} · B {wb.B:P0}");
        switch (s.Mode)
        {
            case SessionMode.Waiting:
                phase = CalibrationPhase.WaitingForPhone;
                msg = "Scan the QR code with your phone's camera (phone on the same Wi-Fi).";
                break;
            case SessionMode.Choosing:
                phase = CalibrationPhase.PhoneConnected;
                msg = "Phone connected — follow the steps on your phone.";
                detail = s.Summary.Length > 0 ? s.Summary : "Turn off the room lights if possible.";
                break;
            case SessionMode.Live:
                phase = CalibrationPhase.LiveCamera;
                msg = $"{s.StepTitle} — {s.Note}";
                detail = $"Step {s.StepIndex + 1} of {s.StepCount}"
                         + (s.ErrorPercent is { } e ? FormattableString.Invariant($"   ·   color difference {e:0.0} %") : "")
                         + "   ·   " + wbText;
                break;
            case SessionMode.Video:
                phase = CalibrationPhase.VideoLoop;
                msg = "Video color loop — record the screen and the glow around it for at least "
                      + $"{Math.Ceiling(CalibrationSession.VideoLoopSeconds + 4):0} seconds.";
                detail = FormattableString.Invariant(
                    $"Step {s.StepIndex + 1} of {s.StepCount}: {s.StepTitle}   ·   loop {s.VideoPosition:0} / {CalibrationSession.VideoLoopSeconds:0} s");
                break;
            case SessionMode.VideoAnalyzing:
                phase = CalibrationPhase.VideoAnalyzing;
                msg = "Analyzing the video from your phone…";
                detail = $"{s.VideoFramesReceived} frames received, {s.VideoFramesRecognized} with the color loop";
                break;
            case SessionMode.Eye:
                phase = CalibrationPhase.EyeTune;
                msg = s.EyeStage switch
                {
                    EyeStage.White => "White by eye: compare the LED glow with the white screen edge.",
                    EyeStage.Dark => "Dark tones by eye: compare the dim glow with the gray screen edge.",
                    _ => $"Colors A/B: {CalibrationSession.EyeColors[s.EyeColor].Name} — showing {(s.EyeShowA ? "A (with color correction)" : "B (without)")}.",
                };
                detail = "Use your phone to adjust.   ·   " + wbText
                         + FormattableString.Invariant($"   ·   gamma R {s.Gamma.R:0.00} G {s.Gamma.G:0.00} B {s.Gamma.B:0.00}");
                break;
            default:
                phase = CalibrationPhase.Finished;
                msg = "Calibration finished — the new LED colors are saved.";
                break;
        }
        return new CalibrationState(phase, msg, detail, s.Progress, s.ErrorPercent, wb.R, wb.G, wb.B);
    }

    // ------------------------------------------------------------------ HTTP routing

    private static readonly string[] GetRoutes = ["", "/state", "/ping", "/rambibox-ca.mobileconfig"];

    /// <summary>
    /// Short, token-less address for the certificate profile ("http://&lt;ip&gt;:&lt;port&gt;/ca") so it can be typed into
    /// Safari. Only the CA's public certificate is served there (nothing secret), and only while calibration runs.
    /// </summary>
    private const string ShortProfilePath = "/ca";

    // ---- TLS diagnostics (guarded by _gate): did the phone reach the secure port, and why was it rejected?
    private int _tlsFailures, _tlsRequests;
    private string? _tlsLastError;

    private void OnTlsHandshakeFailed(IPAddress remote, string reason)
    {
        int n;
        lock (_gate)
        {
            n = ++_tlsFailures;
            _tlsLastError = reason;
        }
        if (n == 1 || n % 10 == 0)
            Log.Info($"Calibration: secure connection from the phone rejected (#{n}): {reason}");
    }
    private static readonly string[] PostRoutes = ["/live", "/frame", "/video/start", "/video/frames", "/video/done", "/skip", "/eye", "/finish", "/cancel"];

    /// <summary>Returns the route suffix ("" for the page) if the path carries the right token, else null.</summary>
    private string? Route(string path, out bool isPost)
    {
        isPost = false;
        if (path == ShortProfilePath) return ShortProfilePath;
        string token;
        lock (_gate) token = _token;
        if (token.Length == 0 || !path.StartsWith("/c/", StringComparison.Ordinal)) return null;
        string rest = path[3..];
        int slash = rest.IndexOf('/');
        string t = slash < 0 ? rest : rest[..slash];
        string suffix = slash < 0 ? "" : rest[slash..];
        if (suffix == "/") suffix = "";
        var a = Encoding.ASCII.GetBytes(t);
        var b = Encoding.ASCII.GetBytes(token);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return null;
        if (Array.IndexOf(GetRoutes, suffix) >= 0) return suffix;
        if (Array.IndexOf(PostRoutes, suffix) >= 0) { isPost = true; return suffix; }
        return null;
    }

    // Client binding. The token travels in the URL over plain HTTP on the LAN, so a sniffer on an open Wi-Fi could learn
    // it from the phone's first request. The session is therefore bound to the IP address that first loaded the page
    // (GET /c/{token} on either port); afterwards every route answers only that IP (403 for anyone else; API routes are
    // also refused before any page load). Loopback is just another IP. If the phone's address changes mid-session the
    // user restarts the calibration on the PC, which issues a new token and a fresh binding.

    /// <summary>Runs before the request body is read: unknown routes → 404, foreign clients → 403, otherwise null.</summary>
    private MiniHttpResponse? Precheck(string method, string path, IPAddress remote)
    {
        var r = Route(path, out bool isPost);
        if (r is null) return MiniHttpResponse.Text(404, "Not found");
        bool methodOk = isPost ? method == "POST" : method is "GET" or "HEAD";
        if (!methodOk) return MiniHttpResponse.Text(404, "Not found");
        if (r == ShortProfilePath) return null; // public certificate only; no session binding needed
        IPAddress? bound;
        lock (_gate) bound = _client;
        if (r == "") return bound is null || bound.Equals(remote) ? null : ForbiddenPage();
        return bound is not null && bound.Equals(remote) ? null : Forbidden();
    }

    private bool TryBind(IPAddress remote)
    {
        lock (_gate)
        {
            if (_client is not null) return _client.Equals(remote);
            _client = remote;
        }
        Log.Info("Calibration: session bound to the device that opened the page");
        return true;
    }

    private bool IsBoundTo(IPAddress remote)
    {
        lock (_gate) return _client is not null && _client.Equals(remote);
    }

    private const string ForbiddenMessage =
        "This calibration session is already in use by another device. To use this phone, start the calibration again on the PC.";

    private static MiniHttpResponse Forbidden() => Json(403, w =>
    {
        w.WriteBoolean("ok", false);
        w.WriteString("message", ForbiddenMessage);
    });

    private static MiniHttpResponse ForbiddenPage() => MiniHttpResponse.Text(403, ForbiddenMessage);

    private async Task<MiniHttpResponse> HandleAsync(MiniHttpRequest req, CancellationToken ct)
    {
        var route = Route(req.Path, out _);
        if (route == ShortProfilePath) return MobileConfig();
        if (req.IsTls) lock (_gate) _tlsRequests++;
        // Precheck ran before the body was read; re-check here because two hosts may race for the first page load.
        if (route == "" ? !TryBind(req.Remote) : route is not null && !IsBoundTo(req.Remote))
            return route == "" ? ForbiddenPage() : Forbidden();
        CalibrationSession? session;
        lock (_gate)
        {
            session = _session;
            if (_server is null || session is null) return Error(409, "Calibration was stopped. Start it again on the PC.");
        }
        if (req.IsTls && route is "" or "/ping") _ca?.MarkPhoneTrusted();
        switch (route)
        {
            case "":
                if (session.PhoneConnected()) { Log.Info("Calibration: phone connected"); RaiseChanged(); }
                return Page();
            case "/state":
                return Json(200, w => WriteState(w, session));
            case "/ping":
                return Ping(req);
            case "/rambibox-ca.mobileconfig":
                return MobileConfig();
            case "/live":
                session.StartLive();
                RaiseChanged();
                return Json(200, w => WriteState(w, session));
            case "/frame":
                return await Task.Run(() => OnFrame(session, req.Body), ct).ConfigureAwait(false);
            case "/video/start":
                session.StartVideoLoop();
                RaiseChanged();
                return Json(200, w => WriteState(w, session));
            case "/video/frames":
                return await Task.Run(() => OnVideoFrames(session, req), ct).ConfigureAwait(false);
            case "/video/done":
                return await Task.Run(() => OnVideoDone(session), ct).ConfigureAwait(false);
            case "/skip":
                session.SkipCamera();
                RaiseChanged();
                return Json(200, w => WriteState(w, session));
            case "/eye":
                return OnEye(session, req.Body);
            case "/finish":
                session.Finish();
                lock (_gate) _finished = true;
                if (ReferenceEquals(_engine.Override, _override)) _engine.SetOverride(null);
                RaiseChanged();
                return Json(200, w => WriteState(w, session));
            case "/cancel":
                lock (_gate) _cancelRequested = true;
                RaiseChanged();
                return Json(200, w => WriteState(w, session));
            default:
                return MiniHttpResponse.Text(404, "Not found");
        }
    }

    private static readonly Lazy<byte[]> PageBytes = new(() =>
    {
        using var s = typeof(PhoneCalibrationService).Assembly.GetManifestResourceStream("RambiBox.Calibration.PhonePage.html")
                      ?? throw new InvalidOperationException("PhonePage.html resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    private MiniHttpResponse Page()
    {
        string connect = "'self'";
        lock (_gate)
            if (_ip is not null) connect += $" https://{_ip}:{_tls?.Port.ToString(CultureInfo.InvariantCulture) ?? "*"}";
        return new MiniHttpResponse(200, "text/html; charset=utf-8", PageBytes.Value, new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                                          $"img-src 'self' blob: data:; media-src 'self' blob:; connect-src {connect}",
            ["X-Frame-Options"] = "DENY",
            ["Permissions-Policy"] = "camera=(self)",
        });
    }

    /// <summary>The landing page (http) fetches this over https: success = the phone trusts our CA.</summary>
    private MiniHttpResponse Ping(MiniHttpRequest req)
    {
        string origin;
        lock (_gate) origin = _url is null ? "" : $"http://{_url.Authority}";
        var resp = Json(200, w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteBoolean("secure", req.IsTls);
        });
        return resp with { Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = origin, ["Vary"] = "Origin" } };
    }

    private MiniHttpResponse MobileConfig()
    {
        if (_ca is null) return MiniHttpResponse.Text(404, "Not available");
        try
        {
            return new MiniHttpResponse(200, "application/x-apple-aspen-config", _ca.BuildMobileConfig(), new Dictionary<string, string>
            {
                ["Content-Disposition"] = "attachment; filename=\"RambiBox-Local-CA.mobileconfig\"",
            });
        }
        catch (Exception ex)
        {
            Log.Error("Calibration: building the profile failed", ex);
            return MiniHttpResponse.Text(500, "Could not create the certificate profile.");
        }
    }

    // ------------------------------------------------------------------ frames

    private MiniHttpResponse OnFrame(CalibrationSession session, byte[] body)
    {
        if (body.Length > MaxFrameBytes) return Error(413, "Frame too large.");
        if (!_work.Wait(0)) return Error(429, "Still analyzing the previous frame.");
        try
        {
            RgbImage img;
            try { img = ImageCodec.Decode(body, 1920); }
            catch (Exception ex)
            {
                Log.Warn($"Calibration: frame decode failed ({body.Length} bytes): {ex.Message}");
                return Error(200, "Could not read the camera frame.");
            }
            var sw = Stopwatch.StartNew();
            LiveFrameResult r;
            try { r = session.ProcessLiveFrame(img); }
            catch (Exception ex)
            {
                Log.Error("Calibration: frame analysis crashed", ex);
                return Error(200, "Could not analyze the frame.");
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            RaiseChanged();
            return Json(200, w =>
            {
                w.WriteBoolean("ok", true);
                w.WriteBoolean("used", r.Used);
                w.WriteString("note", r.Note);
                w.WriteNumber("delayMs", r.DelayMs);
                w.WriteNumber("analysisMs", Math.Round(ms));
                w.WriteStartArray("hints");
                foreach (var h in r.Hints) w.WriteStringValue(h);
                w.WriteEndArray();
                WriteOverlay(w, r.Measurement);
                WriteState(w, session);
            });
        }
        finally { _work.Release(); }
    }

    private void WriteOverlay(Utf8JsonWriter w, FrameMeasurement? m)
    {
        w.WritePropertyName("overlay");
        if (m is null)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteNumber("width", m.Width);
        w.WriteNumber("height", m.Height);
        w.WritePropertyName("corners");
        if (m.Corners is null) w.WriteNullValue();
        else WritePoints(w, m.Corners);
        if (m.Code is { } code) w.WriteNumber("code", code);
        w.WriteStartArray("regions");
        foreach (var s in m.Sides)
        {
            foreach (var r in new[] { s.Screen, s.Glow })
            {
                if (r.Polygon.Length != 4) continue;
                w.WriteStartObject();
                w.WriteString("kind", r.IsGlow ? "glow" : "screen");
                w.WriteBoolean("ok", s.Usable);
                w.WriteString("status", r.Status.ToString());
                w.WritePropertyName("points");
                WritePoints(w, r.Polygon);
                w.WriteEndObject();
            }
        }
        if (m.Ref is { Polygon.Length: 4 } rf)
        {
            w.WriteStartObject();
            w.WriteString("kind", "ref");
            w.WriteBoolean("ok", m.RefOk);
            w.WriteString("status", rf.Status.ToString());
            w.WritePropertyName("points");
            WritePoints(w, rf.Polygon);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// Video fallback: a batch of frames extracted by the phone. Body: "RBV1" then per frame: float64 LE time (s),
    /// int32 LE length, JPEG bytes. Query "first=1" starts a new upload (drops frames of an earlier attempt).
    /// </summary>
    private MiniHttpResponse OnVideoFrames(CalibrationSession session, MiniHttpRequest req)
    {
        List<(double T, byte[] Jpeg)> frames;
        try { frames = ParseVideoBatch(req.Body); }
        catch (FormatException ex) { return Error(400, ex.Message); }
        if (!_work.Wait(TimeSpan.FromSeconds(30))) return Error(429, "Still analyzing — try again.");
        try
        {
            if (req.Query.Split('&').Contains("first=1")) session.ClearVideoFrames();
            var codes = new List<int?>();
            foreach (var (t, jpeg) in frames)
            {
                try
                {
                    var img = ImageCodec.Decode(jpeg, 1920);
                    codes.Add(session.ProcessVideoFrame(img, t).Code);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Calibration: video frame at {t:0.0}s unusable: {ex.Message}");
                    codes.Add(null);
                }
            }
            RaiseChanged();
            var (received, recognized) = session.VideoCounts;
            return Json(200, w =>
            {
                w.WriteBoolean("ok", true);
                w.WriteNumber("received", received);
                w.WriteNumber("recognized", recognized);
                w.WriteStartArray("codes");
                foreach (var c in codes)
                    if (c is { } v) w.WriteNumberValue(v);
                    else w.WriteNullValue();
                w.WriteEndArray();
                WriteState(w, session);
            });
        }
        finally { _work.Release(); }
    }

    internal static List<(double T, byte[] Jpeg)> ParseVideoBatch(byte[] body)
    {
        var res = new List<(double, byte[])>();
        if (body.Length < 4 || body[0] != 'R' || body[1] != 'B' || body[2] != 'V' || body[3] != '1') throw new FormatException("Bad batch format.");
        int pos = 4;
        while (pos < body.Length)
        {
            if (body.Length - pos < 12) throw new FormatException("Truncated batch.");
            double t = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(pos));
            int len = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 8));
            pos += 12;
            if (len <= 0 || len > MaxFrameBytes || len > body.Length - pos || !double.IsFinite(t) || t < 0 || t > 3600)
                throw new FormatException("Bad frame in batch.");
            res.Add((t, body.AsSpan(pos, len).ToArray()));
            pos += len;
            if (res.Count > MaxVideoFramesPerBatch) throw new FormatException("Too many frames in one batch.");
        }
        return res;
    }

    private MiniHttpResponse OnVideoDone(CalibrationSession session)
    {
        if (!_work.Wait(TimeSpan.FromSeconds(30))) return Error(429, "Still analyzing — try again.");
        try
        {
            var problem = session.FinishVideo();
            RaiseChanged();
            return Json(200, w =>
            {
                w.WriteBoolean("ok", problem is null);
                if (problem is not null) w.WriteString("message", problem);
                WriteState(w, session);
            });
        }
        finally { _work.Release(); }
    }

    private MiniHttpResponse OnEye(CalibrationSession session, byte[] body)
    {
        EyeCommand cmd;
        try { cmd = ParseEye(body); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Error(400, "Invalid command.");
        }
        var problem = session.Eye(cmd);
        RaiseChanged();
        return Json(200, w =>
        {
            w.WriteBoolean("ok", problem is null);
            if (problem is not null) w.WriteString("message", problem);
            WriteState(w, session);
        });
    }

    internal static EyeCommand ParseEye(byte[] body)
    {
        if (body.Length > 4096) throw new FormatException("too long");
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        string action = root.GetProperty("action").GetString() ?? throw new FormatException("action");
        EyeStage? stage = null;
        if (root.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.String)
            stage = st.GetString() switch
            {
                "white" => EyeStage.White,
                "dark" => EyeStage.Dark,
                "colors" => EyeStage.Colors,
                _ => throw new FormatException("stage"),
            };
        double Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        int? color = root.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
        string? variant = root.TryGetProperty("variant", out var va) && va.ValueKind == JsonValueKind.String ? va.GetString() : null;
        return new EyeCommand(action, stage, Num("t"), Num("m"), variant, color, (int)Math.Round(Num("delta")));
    }

    // ------------------------------------------------------------------ JSON

    private static MiniHttpResponse Json(int status, Action<Utf8JsonWriter> write)
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return new MiniHttpResponse(status, "application/json; charset=utf-8", buf.WrittenSpan.ToArray());
    }

    private MiniHttpResponse Error(int status, string message) => Json(status, w =>
    {
        w.WriteBoolean("ok", false);
        w.WriteString("message", message);
        CalibrationSession? s;
        lock (_gate) s = _session;
        if (s is not null) WriteState(w, s);
    });

    private void WriteState(Utf8JsonWriter w, CalibrationSession session)
    {
        var s = session.Snapshot();
        string? httpUrl, httpsUrl, tlsProblem;
        bool tlsPending, finished, cancelled;
        lock (_gate)
        {
            httpUrl = _url?.ToString();
            httpsUrl = _tls is null || _ip is null ? null : $"https://{_ip}:{_tls.Port}/c/{_token}";
            tlsPending = _tlsPending;
            tlsProblem = _tlsProblem;
            finished = _finished || _server is null;
            cancelled = _cancelRequested;
        }
        w.WriteStartObject("state");
        w.WriteString("mode", s.Mode switch
        {
            SessionMode.Waiting or SessionMode.Choosing => "choose",
            SessionMode.Live => "live",
            SessionMode.Video => "video",
            SessionMode.VideoAnalyzing => "video",
            SessionMode.Eye => "eye",
            _ => "finished",
        });
        w.WriteBoolean("finished", finished || s.Mode == SessionMode.Finished);
        w.WriteBoolean("cancelled", cancelled);
        if (s.StepId is not null) w.WriteString("step", s.StepId);
        if (s.StepTitle is not null) w.WriteString("stepTitle", s.StepTitle);
        w.WriteNumber("stepIndex", s.StepIndex);
        w.WriteNumber("stepCount", s.StepCount);
        if (s.Progress is { } p) w.WriteNumber("progress", Math.Round(p, 3));
        if (s.ErrorPercent is { } e) w.WriteNumber("errorPercent", Math.Round(e, 2));
        if (s.InitialErrorPercent is { } ie) w.WriteNumber("initialErrorPercent", Math.Round(ie, 2));
        w.WriteNumber("targetPercent", CalibrationSession.LiveTargetPercent);
        w.WriteString("instruction", s.Note);
        w.WriteString("summary", s.Summary);
        WriteTriple(w, "wb", s.Wb);
        WriteTriple(w, "gamma", s.Gamma);
        w.WriteBoolean("matrixOn", s.MatrixOn);
        w.WriteBoolean("hasMatrix", s.HasMatrixCandidate);
        w.WriteNumber("matrixStrength", s.MatrixStrength);

        w.WriteStartObject("tls");
        w.WriteBoolean("pending", tlsPending);
        if (httpsUrl is not null)
        {
            w.WriteString("url", httpsUrl);
            w.WriteString("ping", httpsUrl + "/ping");
        }
        if (tlsProblem is not null) w.WriteString("problem", tlsProblem);
        int tlsFailures, tlsRequests;
        string? tlsLastError;
        lock (_gate) { tlsFailures = _tlsFailures; tlsRequests = _tlsRequests; tlsLastError = _tlsLastError; }
        w.WriteNumber("failures", tlsFailures);
        w.WriteBoolean("reached", tlsRequests > 0);
        if (tlsLastError is not null) w.WriteString("lastError", tlsLastError);
        w.WriteEndObject();
        if (httpUrl is not null)
        {
            w.WriteString("httpUrl", httpUrl);
            w.WriteString("profileUrl", httpUrl + "/rambibox-ca.mobileconfig");
            var u = new Uri(httpUrl);
            w.WriteString("shortProfileUrl", $"{u.Host}:{u.Port}{ShortProfilePath}");
        }
        if (_ca is not null)
        {
            w.WriteString("caName", _ca.CaDisplayName);
            w.WriteString("pcName", _ca.PcName);
        }

        w.WriteStartObject("video");
        w.WriteNumber("loopSeconds", CalibrationSession.VideoLoopSeconds);
        w.WriteNumber("stepSeconds", CalibrationSession.VideoStepSeconds);
        w.WriteNumber("position", Math.Round(s.VideoPosition, 1));
        w.WriteNumber("received", s.VideoFramesReceived);
        w.WriteNumber("recognized", s.VideoFramesRecognized);
        w.WriteEndObject();

        w.WriteStartObject("eye");
        w.WriteString("stage", s.EyeStage.ToString().ToLowerInvariant());
        w.WriteNumber("t", Math.Round(s.EyeT, 3));
        w.WriteNumber("m", Math.Round(s.EyeM, 3));
        w.WriteNumber("td", Math.Round(s.EyeTd, 3));
        w.WriteNumber("md", Math.Round(s.EyeMd, 3));
        w.WriteNumber("color", s.EyeColor);
        w.WriteString("variant", s.EyeShowA ? "A" : "B");
        w.WriteNumber("votesA", s.VotesA);
        w.WriteNumber("votesB", s.VotesB);
        w.WriteNumber("votesSame", s.VotesSame);
        w.WriteStartArray("colors");
        foreach (var c in CalibrationSession.EyeColors)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("css", "#" + c.Rgb.ToString("X6", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteTriple(Utf8JsonWriter w, string name, Rgb3 v)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(Math.Round(v.R, 4));
        w.WriteNumberValue(Math.Round(v.G, 4));
        w.WriteNumberValue(Math.Round(v.B, 4));
        w.WriteEndArray();
    }

    private static void WritePoints(Utf8JsonWriter w, PointD[] pts)
    {
        w.WriteStartArray();
        foreach (var p in pts)
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(p.X, 1));
            w.WriteNumberValue(Math.Round(p.Y, 1));
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }
}
