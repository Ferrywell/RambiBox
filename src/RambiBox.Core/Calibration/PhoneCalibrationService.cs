using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RambiBox.Core.Engine;

namespace RambiBox.Core.Calibration;

/// <summary>
/// Phone-assisted LED color calibration.
/// <para>
/// Flow: <see cref="Start"/> runs a tiny HTTP server on the LAN and lights all LEDs neutral gray (through the current
/// white balance). The UI shows a QR code for <see cref="Url"/>; once the phone opens the page the QR disappears and the
/// measurement pattern (<see cref="Pattern"/>) is shown. Each photo of screen + glow is analyzed, a corrected white
/// balance is applied live via the <c>applyWhiteBalance</c> callback, and the user repeats until it converges.
/// Optionally a second "dark tones" photo fits per-channel gamma (via <c>applyChannelGamma</c>).
/// </para>
/// Thread-safety: all public members may be called from any thread; HTTP requests run on the thread pool;
/// <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class PhoneCalibrationService : IDisposable
{
    public const int FirstPort = 47800, LastPort = 47820;
    public const int MaxPhotoBytes = 25 * 1024 * 1024;
    /// <summary>Set to true to dump every received photo + analysis JSON to %LocalAppData%\RambiBox\calibration-debug.</summary>
    private static readonly bool DebugSavePhotos = false;

    private readonly LightEngine _engine;
    private readonly Func<(double R, double G, double B)> _currentWhiteBalance;
    private readonly Action<double, double, double> _applyWhiteBalance;
    private readonly Action<double, double, double>? _applyChannelGamma;
    private readonly Func<(double R, double G, double B)>? _currentChannelGamma;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _analysis = new(1, 1);
    private readonly CalibrationLedOverride _override = new();

    // ---- state guarded by _gate
    private MiniHttpServer? _server;
    private Timer? _autoStop;
    private string _token = "";
    private IPAddress? _client; // the phone this session is bound to (see Precheck)
    private Uri? _url;
    private CalibrationLayout _layout = new(16.0 / 9.0);
    private CalibrationPhase _phase = CalibrationPhase.WaitingForPhone;
    private string _message = "Not started.";
    private int _photos;
    private int _photosInStage;
    private double? _error;
    private Rgb3 _wb = new(1, 1, 1), _origWb = new(1, 1, 1);
    private Rgb3 _gamma = new(1, 1, 1), _origGamma = new(1, 1, 1);
    private bool _wbChanged, _gammaChanged;
    private bool _showQr = true, _darkStage, _finished, _whiteConverged;
    private double _residualWhiteR = 1, _residualWhiteB = 1;
    private LastPhoto? _last;
    private CalibrationPattern _pattern;
    private int _session;

    private sealed record LastPhoto(RgbImage Image, Rgb3 BaseWb, Rgb3 BaseGamma, bool Dark, double K,
        double ResidualWhiteR, double ResidualWhiteB, bool Counted, int Session);

    /// <param name="engine">Used to show the LED calibration pattern via <see cref="LightEngine.SetOverride"/>.</param>
    /// <param name="currentWhiteBalance">Returns the white balance (R,G,B 0..1) in effect when calibration starts.</param>
    /// <param name="applyWhiteBalance">Called (any thread) when a new white balance should be applied and persisted.</param>
    /// <param name="applyChannelGamma">Optional: called (any thread) to apply + persist per-channel gamma
    /// (ColorSettings.ChannelGammaR/G/B). Null disables the optional dark-tone stage.</param>
    /// <param name="currentChannelGamma">Optional: channel gamma in effect when calibration starts (default 1,1,1).</param>
    public PhoneCalibrationService(LightEngine engine,
        Func<(double R, double G, double B)> currentWhiteBalance,
        Action<double, double, double> applyWhiteBalance,
        Action<double, double, double>? applyChannelGamma = null,
        Func<(double R, double G, double B)>? currentChannelGamma = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _currentWhiteBalance = currentWhiteBalance ?? throw new ArgumentNullException(nameof(currentWhiteBalance));
        _applyWhiteBalance = applyWhiteBalance ?? throw new ArgumentNullException(nameof(applyWhiteBalance));
        _applyChannelGamma = applyChannelGamma;
        _currentChannelGamma = currentChannelGamma;
        _pattern = _layout.BuildPattern(showQr: true, darkStage: false);
    }

    // ------------------------------------------------------------------ public API

    /// <summary>URL to encode in the QR code (http://&lt;lan-ip&gt;:&lt;port&gt;/c/&lt;token&gt;). Null until started.</summary>
    public Uri? Url { get { lock (_gate) return _url; } }

    public CalibrationState State
    {
        get
        {
            lock (_gate) return new CalibrationState(_phase, _message, _photos, _error, _wb.R, _wb.G, _wb.B);
        }
    }

    /// <summary>What the calibration window should draw right now.</summary>
    public CalibrationPattern Pattern { get { lock (_gate) return _pattern; } }

    /// <summary>True after the user tapped Done on the phone: LEDs are back to normal; the UI should close the
    /// pattern window and call <see cref="Stop"/>(keepResult: true).</summary>
    public bool IsFinished { get { lock (_gate) return _finished; } }

    /// <summary>True while the optional dark-tone (channel gamma) measurement is active.</summary>
    public bool IsDarkStage { get { lock (_gate) return _darkStage; } }

    /// <summary>Whether the optional dark-tone stage is supported (an applyChannelGamma callback was given).</summary>
    public bool SupportsDarkStage => _applyChannelGamma is not null;

    /// <summary>Per-channel gamma currently applied by the calibration (1,1,1 unless the dark stage changed it).</summary>
    public (double R, double G, double B) ChannelGamma { get { lock (_gate) return (_gamma.R, _gamma.G, _gamma.B); } }

    /// <summary>Raised on any thread when <see cref="State"/> or <see cref="Pattern"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the local web server and the LED pattern. Returns false (State = Failed) if impossible.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_server is not null) return true;
            _session++;
            double aspect = 16.0 / 9.0;
            try
            {
                var mon = _engine.Sampler.CurrentMonitor;
                if (mon is { Width: > 0, Height: > 0 }) aspect = (double)mon.Width / mon.Height;
            }
            catch (Exception ex) { Log.Warn($"Calibration: monitor info unavailable: {ex.Message}"); }
            _layout = new CalibrationLayout(aspect);
            _photos = _photosInStage = 0;
            _error = null;
            _last = null;
            _showQr = true;
            _darkStage = _finished = _whiteConverged = false;
            _wbChanged = _gammaChanged = false;
            _residualWhiteR = _residualWhiteB = 1;
            try
            {
                var (r, g, b) = _currentWhiteBalance();
                _origWb = _wb = new Rgb3(r, g, b);
                var cg = _currentChannelGamma?.Invoke() ?? (1, 1, 1);
                _origGamma = _gamma = new Rgb3(cg.R, cg.G, cg.B);
            }
            catch (Exception ex)
            {
                Log.Error("Calibration: could not read current settings", ex);
                _origWb = _wb = new Rgb3(1, 1, 1);
                _origGamma = _gamma = new Rgb3(1, 1, 1);
            }
            _override.Level = (float)CalibrationMath.WhiteLevel;
            RebuildPattern();

            IPAddress? ip;
            try { ip = AddressProvider(); }
            catch { ip = null; }
            if (ip is null)
            {
                SetPhase(CalibrationPhase.Failed,
                    "No network connection found. Connect this PC to the same Wi-Fi router as your phone (cable or Wi-Fi) and try again.");
                Log.Warn("Calibration: no LAN address found");
                goto failed;
            }
            var server = new MiniHttpServer(HandleAsync, Precheck, MaxPhotoBytes)
            {
                ClientAddress = ClientAddressOverride ?? (ep => ep.Address),
            };
            if (!server.Start(FirstPort, LastPort))
            {
                SetPhase(CalibrationPhase.Failed, $"Could not open a network port ({FirstPort}–{LastPort} are all in use).");
                Log.Warn("Calibration: no free port");
                goto failed;
            }
            _server = server;
            _token = NewToken();
            _client = null;
            _url = new Uri($"http://{ip}:{server.Port}/c/{_token}");
            SetPhase(CalibrationPhase.WaitingForPhone, "Scan the QR code with your phone's camera (phone on the same Wi-Fi).");
            int session = _session;
            _autoStop = new Timer(_ => AutoStop(session), null, AutoStopAfter, Timeout.InfiniteTimeSpan);
            _engine.SetOverride(_override);
            Log.Info($"Calibration started: http://{ip}:{server.Port}/c/… (aspect {aspect:0.###}, WB {_wb}, gamma {_gamma})");
        }
        RaiseChanged();
        return true;

    failed:
        RaiseChanged();
        return false;
    }

    /// <summary>
    /// Stops server and LED override. <paramref name="keepResult"/> false restores the white balance from before Start.
    /// </summary>
    public void Stop(bool keepResult)
    {
        bool restoreWb, restoreGamma;
        Rgb3 wb, gamma;
        lock (_gate)
        {
            bool wasRunning = _server is not null;
            _server?.Stop();
            _server = null;
            _autoStop?.Dispose();
            _autoStop = null;
            _url = null;
            if (ReferenceEquals(_engine.Override, _override)) _engine.SetOverride(null);
            restoreWb = !keepResult && _wbChanged;
            restoreGamma = !keepResult && _gammaChanged && _applyChannelGamma is not null;
            wb = _origWb;
            gamma = _origGamma;
            if (restoreWb) { _wb = _origWb; _wbChanged = false; }
            if (restoreGamma) { _gamma = _origGamma; _gammaChanged = false; }
            if (!wasRunning && !restoreWb && !restoreGamma) return;
            if (!keepResult) _message = "Calibration cancelled — previous LED colors restored.";
            else if (!_finished && _phase != CalibrationPhase.Failed) _message = "Calibration closed.";
            Log.Info($"Calibration stopped (keepResult={keepResult}); WB {_wb}, gamma {_gamma}");
        }
        if (restoreWb) SafeApply(_applyWhiteBalance, wb);
        if (restoreGamma) SafeApply(_applyChannelGamma!, gamma);
        RaiseChanged();
    }

    public void Dispose() => Stop(keepResult: true);

    // ------------------------------------------------------------------ internals (tests)

    /// <summary>Address to advertise in the URL (tests substitute loopback).</summary>
    internal Func<IPAddress?> AddressProvider { get; set; } = LanAddress.Pick;
    internal TimeSpan AutoStopAfter { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Maps a connection to its client IP (tests simulate other LAN hosts over loopback). Read by Start.</summary>
    internal Func<IPEndPoint, IPAddress>? ClientAddressOverride { get; set; }
    internal int Port { get { lock (_gate) return _server?.Port ?? 0; } }
    internal string Token { get { lock (_gate) return _token; } }
    internal CalibrationLayout Layout { get { lock (_gate) return _layout; } }
    internal float LedLevel => _override.Level;

    private static string NewToken()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void AutoStop(int session)
    {
        lock (_gate)
        {
            if (session != _session || _server is null) return;
            if (_phase is not (CalibrationPhase.Adjusted or CalibrationPhase.Converged)) _phase = CalibrationPhase.Failed;
            _message = "The calibration session ended after 30 minutes. Start it again from the app if needed.";
            _finished = true;
        }
        Log.Info("Calibration: auto-stop after timeout");
        Stop(keepResult: true);
    }

    private void SetPhase(CalibrationPhase p, string msg)
    {
        _phase = p;
        _message = msg;
    }

    private void RebuildPattern() => _pattern = _layout.BuildPattern(_showQr, _darkStage);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Calibration Changed handler failed: {ex.Message}"); }
    }

    private static void SafeApply(Action<double, double, double> apply, Rgb3 v)
    {
        try { apply(v.R, v.G, v.B); }
        catch (Exception ex) { Log.Error("Calibration: applying settings failed", ex); }
    }

    // ------------------------------------------------------------------ HTTP

    private static readonly string[] Routes = ["", "/state", "/photo", "/corners", "/dark", "/finish"];

    /// <summary>Returns the route suffix ("" for the page) if the path carries the right token, else null.</summary>
    private string? Route(string path)
    {
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
        return Array.IndexOf(Routes, suffix) >= 0 ? suffix : null;
    }

    // Client binding. The page and API are plain HTTP on the LAN, so on an open Wi-Fi a sniffer could learn the token
    // from the phone's first request. The session is therefore bound to the IP address that first loaded the page
    // (GET /c/{token}); afterwards every route - page reloads, /state and the POST endpoints - answers only that IP
    // (403 for anyone else; /state and POSTs are also refused before any page load). The token travels only after the
    // phone scanned the QR code, so a sniffer cannot load the page before the phone does. Loopback is just another IP.
    // If the phone's address changes mid-session (rare: Wi-Fi roaming to another subnet / DHCP renewal), the phone is
    // locked out too; the user simply restarts calibration on the PC, which issues a new token and a fresh binding.

    /// <summary>Runs before the request body is read: unknown routes → 404, foreign clients → 403, otherwise null.</summary>
    private MiniHttpResponse? Precheck(string method, string path, IPAddress remote)
    {
        var r = Route(path);
        if (r is null) return MiniHttpResponse.Text(404, "Not found");
        bool methodOk = r is "" or "/state" ? method is "GET" or "HEAD" : method == "POST";
        if (!methodOk) return MiniHttpResponse.Text(404, "Not found");
        IPAddress? bound;
        lock (_gate) bound = _client;
        if (r == "") return bound is null || bound.Equals(remote) ? null : ForbiddenPage();
        return bound is not null && bound.Equals(remote) ? null : Forbidden();
    }

    /// <summary>Binds the session to <paramref name="remote"/> if unbound; true if it is (now) bound to it.</summary>
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
        var route = Route(req.Path);
        // Precheck ran before the body was read; re-check here because two hosts may race for the first page load.
        if (route == "" ? !TryBind(req.Remote) : route is not null && !IsBoundTo(req.Remote))
            return route == "" ? ForbiddenPage() : Forbidden();
        switch (route)
        {
            case "":
                OnPageLoaded();
                return new MiniHttpResponse(200, "text/html; charset=utf-8", PageBytes.Value, new Dictionary<string, string>
                {
                    ["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'",
                    ["X-Frame-Options"] = "DENY",
                });
            case "/state":
                return Json(200, WriteState);
            case "/photo":
                return await Task.Run(() => OnPhoto(req.Body), ct).ConfigureAwait(false);
            case "/corners":
                return await Task.Run(() => OnCorners(req.Body), ct).ConfigureAwait(false);
            case "/dark":
                return OnDark();
            case "/finish":
                return OnFinish();
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

    private void OnPageLoaded()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_phase == CalibrationPhase.WaitingForPhone && _server is not null)
            {
                _showQr = false;
                RebuildPattern();
                SetPhase(CalibrationPhase.WaitingForPhoto,
                    "Phone connected. Turn off the room lights if possible, then take a photo of the screen and the glow around it.");
                changed = true;
            }
        }
        if (changed)
        {
            Log.Info("Calibration: phone connected");
            RaiseChanged();
        }
    }

    private MiniHttpResponse OnDark()
    {
        lock (_gate)
        {
            if (_finished) return Error(409, "Calibration already finished.");
            if (_applyChannelGamma is null) return Error(409, "Dark-tone calibration is not available in this version.");
            if (_photos == 0) return Error(409, "Calibrate white first.");
            if (!_darkStage)
            {
                _darkStage = true;
                _photosInStage = 0;
                _override.Level = (float)CalibrationMath.DarkLevel;
                RebuildPattern();
                SetPhase(CalibrationPhase.WaitingForPhoto, "Dark-tone check: the LEDs are dimmed. Take one more photo the same way.");
            }
        }
        RaiseChanged();
        return Json(200, WriteState);
    }

    private MiniHttpResponse OnFinish()
    {
        lock (_gate)
        {
            if (!_finished)
            {
                _finished = true;
                if (ReferenceEquals(_engine.Override, _override)) _engine.SetOverride(null);
                if (_photos > 0)
                    SetPhase(CalibrationPhase.Converged, "Calibration finished — the new LED colors are saved.");
                else
                    _message = "Finished without changes.";
                Log.Info($"Calibration finished by user: WB {_wb}, gamma {_gamma}, photos {_photos}");
            }
        }
        RaiseChanged();
        return Json(200, WriteState);
    }

    private MiniHttpResponse OnPhoto(byte[] body)
    {
        lock (_gate)
            if (_finished || _server is null) return Error(409, "Calibration already finished. Start it again from the PC.");
        if (!_analysis.Wait(0)) return Error(409, "Still analyzing the previous photo — please wait a moment.");
        try
        {
            lock (_gate) SetPhase(CalibrationPhase.Analyzing, "Analyzing photo…");
            RaiseChanged();

            RgbImage img;
            try { img = ImageCodec.Decode(body); }
            catch (Exception ex)
            {
                Log.Warn($"Calibration: photo decode failed ({body.Length} bytes): {ex.Message}");
                lock (_gate) SetPhase(CalibrationPhase.PhotoRejected, "Could not read the photo. Please try again.");
                RaiseChanged();
                return Error(200, "Could not read the photo. Please try again.");
            }

            LastPhoto shot;
            lock (_gate)
            {
                double k = _photosInStage == 0 ? CalibrationMath.FirstPhotoDamping : CalibrationMath.LaterDamping;
                shot = new LastPhoto(img, _wb, _gamma, _darkStage, k, _residualWhiteR, _residualWhiteB, false, _session);
            }
            var analysis = Analyze(img, null);
            return Complete(shot, analysis, body);
        }
        finally { _analysis.Release(); }
    }

    private MiniHttpResponse OnCorners(byte[] body)
    {
        PointD[] corners;
        try { corners = ParseCorners(body); }
        catch (Exception) { return Error(400, "Invalid corner data."); }
        if (!_analysis.Wait(0)) return Error(409, "Still analyzing the previous photo — please wait a moment.");
        try
        {
            LastPhoto? shot;
            lock (_gate)
            {
                if (_finished || _server is null) return Error(409, "Calibration already finished.");
                shot = _last;
            }
            if (shot is null) return Error(409, "Take a photo first.");
            foreach (var c in corners)
                if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || Math.Abs(c.X) > shot.Image.Width * 3 || Math.Abs(c.Y) > shot.Image.Height * 3)
                    return Error(400, "Corner out of range.");
            lock (_gate) SetPhase(CalibrationPhase.Analyzing, "Analyzing photo with your corners…");
            RaiseChanged();
            var analysis = Analyze(shot.Image, corners);
            return Complete(shot, analysis, null);
        }
        finally { _analysis.Release(); }
    }

    private PhotoAnalysis Analyze(RgbImage img, IReadOnlyList<PointD>? corners)
    {
        CalibrationLayout layout;
        lock (_gate) layout = _layout;
        try { return CalibrationAnalyzer.Analyze(img, layout, corners); }
        catch (Exception ex)
        {
            Log.Error("Calibration: analysis crashed", ex);
            return new PhotoAnalysis(AnalysisFailure.InvalidImage, "Could not analyze the photo. Please try again.",
                img.Width, img.Height, null, [], 1, 1, 0);
        }
    }

    /// <summary>Turns an analysis into a correction (if usable), applies it, updates the state, returns the JSON result.</summary>
    private MiniHttpResponse Complete(LastPhoto shot, PhotoAnalysis a, byte[]? rawPhoto)
    {
        Rgb3? applyWb = null, applyGamma = null;
        lock (_gate)
        {
            if (shot.Session != _session || _server is null) return Error(409, "Calibration was stopped.");
            bool count = a.Ok && !shot.Counted;
            _last = shot with { Counted = shot.Counted || a.Ok };
            if (!a.Ok)
            {
                SetPhase(CalibrationPhase.PhotoRejected, a.Message);
                Log.Info($"Calibration photo rejected ({a.Failure}): {Describe(a)}");
            }
            else
            {
                if (count) { _photos++; _photosInStage++; }
                double err = a.ErrorPercent;
                _error = err;
                bool good = err < CalibrationMath.ConvergedErrorPercent;
                if (!shot.Dark)
                {
                    var (gains, resR, resB) = CalibrationMath.WhiteStep(shot.BaseWb, a.RhoR, a.RhoB, shot.K);
                    applyWb = gains;
                    _wb = gains;
                    _wbChanged = true;
                    _residualWhiteR = resR;
                    _residualWhiteB = resB;
                    if (good) _whiteConverged = true;
                    SetPhase(good ? CalibrationPhase.Converged : CalibrationPhase.Adjusted, good
                        ? $"Calibrated! The glow matches screen white within {err:0.0}%. Take another photo to verify, or tap Done."
                        : $"Color difference {err:0.0}% — corrected. Take another photo to verify.");
                    Log.Info($"Calibration photo #{_photos} (white, k={shot.K}): {Describe(a)} → WB {shot.BaseWb} → {gains}");
                }
                else
                {
                    var (gains, gamma) = CalibrationMath.DarkStep(shot.BaseWb, shot.BaseGamma, a.RhoR, a.RhoB,
                        shot.ResidualWhiteR, shot.ResidualWhiteB);
                    applyWb = gains;
                    applyGamma = gamma;
                    _wb = gains;
                    _gamma = gamma;
                    _wbChanged = _gammaChanged = true;
                    _residualWhiteR = _residualWhiteB = 1;
                    SetPhase(good ? CalibrationPhase.Converged : CalibrationPhase.Adjusted, good
                        ? $"Dark tones match within {err:0.0}%. Tap Done, or take another photo to verify."
                        : $"Dark tones off by {err:0.0}% — corrected. Take another photo to verify.");
                    Log.Info($"Calibration photo #{_photos} (dark): {Describe(a)} → WB {shot.BaseWb} → {gains}, gamma {shot.BaseGamma} → {gamma}");
                }
            }
        }
        if (applyWb is { } w) SafeApply(_applyWhiteBalance, w);
        if (applyGamma is { } g && _applyChannelGamma is not null) SafeApply(_applyChannelGamma, g);
        RaiseChanged();

        var resp = Json(200, wr => WriteResult(wr, a));
        if (DebugSavePhotos) DebugSave(shot, rawPhoto, resp.Body);
        return resp;
    }

    private static string Describe(PhotoAnalysis a)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{a.PhotoWidth}x{a.PhotoHeight}");
        foreach (var s in a.Sides)
            sb.Append(CultureInfo.InvariantCulture,
                $" {s.Side}:{(s.Valid ? $"ρR={s.RhoR:0.000} ρB={s.RhoB:0.000}" : $"{s.Screen.Status}/{s.Glow.Status}")}");
        if (a.Ok) sb.Append(CultureInfo.InvariantCulture, $" → ρR={a.RhoR:0.000} ρB={a.RhoB:0.000} err={a.ErrorPercent:0.0}%");
        return sb.ToString();
    }

    private static void DebugSave(LastPhoto shot, byte[]? raw, byte[] json)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RambiBox", "calibration-debug");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            File.WriteAllBytes(Path.Combine(dir, stamp + ".jpg"), raw ?? ImageCodec.EncodeJpeg(shot.Image));
            File.WriteAllBytes(Path.Combine(dir, stamp + ".json"), json);
        }
        catch (Exception ex) { Log.Warn($"Calibration debug save failed: {ex.Message}"); }
    }

    internal static PointD[] ParseCorners(byte[] body)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var arr = doc.RootElement.GetProperty("corners");
        if (arr.GetArrayLength() != 4) throw new FormatException("need 4 corners");
        var res = new PointD[4];
        int i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Array) res[i++] = new PointD(e[0].GetDouble(), e[1].GetDouble());
            else res[i++] = new PointD(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble());
        }
        return res;
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
        WriteStateFields(w);
    });

    private void WriteState(Utf8JsonWriter w)
    {
        w.WriteBoolean("ok", true);
        WriteStateFields(w);
    }

    private void WriteStateFields(Utf8JsonWriter w)
    {
        lock (_gate)
        {
            w.WriteString("phase", _phase.ToString());
            w.WriteString("status", _message);
            w.WriteNumber("photosAnalyzed", _photos);
            if (_error is { } e) w.WriteNumber("errorPercent", Math.Round(e, 2));
            else w.WriteNull("errorPercent");
            w.WriteNumber("whiteR", Math.Round(_wb.R, 4));
            w.WriteNumber("whiteG", Math.Round(_wb.G, 4));
            w.WriteNumber("whiteB", Math.Round(_wb.B, 4));
            w.WriteNumber("gammaR", Math.Round(_gamma.R, 4));
            w.WriteNumber("gammaG", Math.Round(_gamma.G, 4));
            w.WriteNumber("gammaB", Math.Round(_gamma.B, 4));
            w.WriteString("stage", _darkStage ? "dark" : "white");
            w.WriteBoolean("whiteConverged", _whiteConverged);
            w.WriteBoolean("darkAvailable", _applyChannelGamma is not null && _photos > 0 && !_darkStage);
            w.WriteBoolean("finished", _finished || _server is null);
            w.WriteNumber("convergedBelowPercent", CalibrationMath.ConvergedErrorPercent);
        }
    }

    private void WriteResult(Utf8JsonWriter w, PhotoAnalysis a)
    {
        w.WriteBoolean("ok", a.Ok);
        w.WriteString("message", a.Ok ? State.Message : a.Message);
        w.WriteString("failure", a.Failure.ToString());
        w.WriteBoolean("canAdjustCorners", a.Failure is AnalysisFailure.ScreenNotFound or AnalysisFailure.BadCorners || a.Corners is not null);
        if (a.Ok) w.WriteNumber("measuredErrorPercent", Math.Round(a.ErrorPercent, 2));
        w.WriteNumber("imageWidth", a.PhotoWidth);
        w.WriteNumber("imageHeight", a.PhotoHeight);
        w.WritePropertyName("corners");
        if (a.Corners is null) w.WriteNullValue();
        else WritePoints(w, a.Corners);
        w.WriteStartArray("sampleRegions");
        foreach (var s in a.Sides)
        {
            foreach (var r in new[] { s.Screen, s.Glow })
            {
                if (r.Polygon.Length != 4) continue;
                w.WriteStartObject();
                w.WriteString("side", s.Side.ToString().ToLowerInvariant());
                w.WriteString("kind", r.IsGlow ? "glow" : "screen");
                w.WriteBoolean("ok", s.Valid);
                w.WriteString("status", r.Status.ToString());
                w.WritePropertyName("points");
                WritePoints(w, r.Polygon);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        WriteStateFields(w);
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

/// <summary>All LEDs one neutral gray, through the engine's white balance (and channel gamma).</summary>
internal sealed class CalibrationLedOverride : ILedOverride
{
    private volatile float _level = 0.6f;

    public float Level
    {
        get => _level;
        set => _level = Math.Clamp(value, 0f, 1f);
    }

    public bool ApplyWhiteBalance => true;

    public void Render(Span<float> rgb, int ledCount, double timeSeconds) => rgb[..Math.Min(rgb.Length, ledCount * 3)].Fill(_level);
}
