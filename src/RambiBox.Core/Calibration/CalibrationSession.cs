using System.Globalization;
using RambiBox.Core.Engine;

namespace RambiBox.Core.Calibration;

internal enum LoopStepKind { Ambient, White, Gray, WhiteRef, Primary }

/// <summary>One step of the color loop: LED drive + matching screen reference.</summary>
internal sealed record LoopStep(LoopStepKind Kind, double Level, int Channel, string Id, string Title);

internal enum SessionMode { Waiting, Choosing, Live, Video, VideoAnalyzing, Eye, Finished }

internal enum EyeStage { White, Dark, Colors }

/// <summary>Per-side camera colors of one frame, normalized by the frame's neutral reference patch.</summary>
internal sealed record SideColors(Side Side, Rgb3 Glow, Rgb3 Band);

/// <summary>Normalized colors of one frame + the LED settings it was taken with.</summary>
internal sealed record ColorSample(IReadOnlyList<SideColors> Sides, Rgb3 Wb, Rgb3 Gamma);

/// <summary>What happened to one live frame (for the phone's response).</summary>
internal sealed record LiveFrameResult(FrameMeasurement? Measurement, bool Used, string Note, IReadOnlyList<string> Hints, int DelayMs);

/// <summary>A color shown during the eye A/B check.</summary>
internal sealed record EyeColor(string Name, int Rgb);

/// <summary>
/// The calibration logic without networking: patterns, LED drive, the camera color loop (live, closed loop, or video,
/// open loop), the fits, and the eye fine-tune. All results are applied through <see cref="ICalibrationTarget"/>.
/// <para>
/// Live loop: WHITE (closed loop on the white balance until the glow matches the screen reference within 2 % for 3
/// frames) → GRAY RAMP (0.35 / 0.2 / 0.12 → per-channel gamma fit, white re-solved) → WHITE again (re-converge + white
/// reference for the matrix) → PRIMARIES R, G, B (LED matrix fit, not applied; the eye A/B check decides) → EYE.
/// Every pattern carries a visual step code; a frame is used only when its code matches the current pattern and it
/// arrived at least <see cref="SettleSeconds"/> after the last change.
/// </para>
/// Thread-safe: all members may be called from any thread. Target callbacks are invoked under the lock and must not
/// block (they post to the UI thread).
/// </summary>
internal sealed class CalibrationSession
{
    public static readonly LoopStep[] LivePlan =
    [
        new(LoopStepKind.Ambient, 0, -1, "ambient", "Room light (LEDs off)"),
        new(LoopStepKind.White, CalibrationMath.WhiteLevel, -1, "white", "White"),
        new(LoopStepKind.Gray, 0.35, -1, "gray35", "Dark tones 35 %"),
        new(LoopStepKind.Gray, 0.20, -1, "gray20", "Dark tones 20 %"),
        new(LoopStepKind.Gray, 0.12, -1, "gray12", "Dark tones 12 %"),
        new(LoopStepKind.WhiteRef, CalibrationMath.WhiteLevel, -1, "white2", "White check"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 0, "red", "Red"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 1, "green", "Green"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 2, "blue", "Blue"),
    ];

    public static readonly LoopStep[] VideoPlan =
    [
        new(LoopStepKind.Ambient, 0, -1, "ambient", "Room light (LEDs off)"),
        new(LoopStepKind.White, CalibrationMath.WhiteLevel, -1, "white", "White"),
        new(LoopStepKind.Gray, 0.35, -1, "gray35", "Dark tones 35 %"),
        new(LoopStepKind.Gray, 0.20, -1, "gray20", "Dark tones 20 %"),
        new(LoopStepKind.Gray, 0.12, -1, "gray12", "Dark tones 12 %"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 0, "red", "Red"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 1, "green", "Green"),
        new(LoopStepKind.Primary, CalibrationMath.WhiteLevel, 2, "blue", "Blue"),
    ];

    public static readonly EyeColor[] EyeColors =
    [
        new("red", 0xFF0000), new("green", 0x00FF00), new("blue", 0x0000FF), new("orange", 0xFF8A30), new("cyan", 0x00FFFF),
    ];

    public const int VideoCodeBase = 48;
    public const double VideoStepSeconds = 2.5;
    public const double EyeDarkLevel = 0.2;   // LED drive; the screen shows the same value (0x33), like the ambilight does
    public const double LiveTargetPercent = 2.0;

    private readonly object _gate = new();
    private readonly ICalibrationTarget _target;
    private readonly CalibrationLedOverride _led;
    private readonly Func<double> _clock;
    private readonly CalibrationValues _orig;

    // ---- current values
    private Rgb3 _wb, _gamma;
    private double[]? _matrix;
    private readonly double _brightness;

    // ---- pattern / sync
    private CalibrationLayout _layout;
    private CalibrationPattern _pattern;
    private int _epoch;
    private int _code;
    private double _switchTime;
    private bool _showQr = true;

    // ---- mode / loop
    private SessionMode _mode = SessionMode.Waiting;
    private int _step;
    private int _stepFrames, _whiteGood;
    private bool _whiteFirst = true;
    private (double R, double B) _whitePoint = (1, 1);
    private readonly List<ColorSample> _whiteRef = new();
    private readonly List<(double R, double B)> _graySamples = new();
    private readonly List<GrayPoint> _grayPoints = new();
    private readonly List<ColorSample>[] _primary = [new(), new(), new()];
    private double? _error, _initialError;
    private string _note = "";
    private PointD[]? _prevCorners;
    private string _summary = "";

    // ---- video
    private double _videoStart;
    private int _videoIndex = -1;
    private readonly List<VideoSample> _video = new();
    private int _videoReceived;

    private sealed record VideoSample(double T, int Code, int RhoSides, double RhoR, double RhoB, ColorSample? Colors, ColorSample? Ambient);

    /// <summary>Wall light with the LEDs off, per side (camera, normalized by the reference patch); subtracted from every glow.</summary>
    private readonly Dictionary<Side, Rgb3> _ambient = new();
    private readonly List<ColorSample> _ambientSamples = new();

    // ---- results
    private double[]? _matrixFull;
    private int _strengthIdx = Array.IndexOf(ColorFit.StrengthSteps, ColorFit.DefaultMatrixStrength);

    // ---- eye
    private EyeStage _eyeStage = EyeStage.White;
    private Rgb3 _eyeBaseWb, _eyeBaseGamma;
    private double _eyeT, _eyeM, _eyeTd, _eyeMd;
    private int _eyeColor;
    private bool _eyeShowA = true;
    private int _votesA, _votesB, _votesSame;
    private readonly Dictionary<int, string> _votes = new();

    public CalibrationSession(ICalibrationTarget target, CalibrationLedOverride led, CalibrationLayout layout, Func<double> clock)
    {
        _target = target;
        _led = led;
        _layout = layout;
        _clock = clock;
        CalibrationValues v;
        try { v = target.Read(); }
        catch (Exception ex)
        {
            Log.Error("Calibration: could not read current settings", ex);
            v = new CalibrationValues((1, 1, 1), (1, 1, 1), null);
        }
        _orig = v;
        _wb = new Rgb3(v.WhiteBalance.R, v.WhiteBalance.G, v.WhiteBalance.B);
        _gamma = new Rgb3(v.ChannelGamma.R, v.ChannelGamma.G, v.ChannelGamma.B);
        _matrix = v.ColorMatrix is null ? null : (double[])v.ColorMatrix.Clone();
        _brightness = double.IsFinite(v.Brightness) && v.Brightness > 0 ? v.Brightness : 1;
        _eyeBaseWb = _wb;
        _eyeBaseGamma = _gamma;
        _pattern = _layout.BuildMeasurePattern(CalibrationLayout.WhiteBandRgb, 0, showQr: true);
        lock (_gate) ShowLoopStep(LivePlan[0]);
    }

    // ------------------------------------------------------------------ tunables (tests)

    public double SettleSeconds { get; init; } = 0.4;
    public int FramesPerStep { get; init; } = 4;
    public int WhiteGoodFrames { get; init; } = 3;
    public int MaxWhiteFrames { get; init; } = 30;
    public int MaxStepFrames { get; init; } = 40;

    // ------------------------------------------------------------------ state

    public CalibrationValues Original => _orig;
    public CalibrationPattern Pattern { get { lock (_gate) return _pattern; } }
    public SessionMode Mode { get { lock (_gate) return _mode; } }
    public CalibrationLayout Layout { get { lock (_gate) return _layout; } }
    public int ExpectedCode { get { lock (_gate) return _code; } }
    public Rgb3 WhiteBalance { get { lock (_gate) return _wb; } }
    public Rgb3 ChannelGamma { get { lock (_gate) return _gamma; } }
    public double[]? Matrix { get { lock (_gate) return _matrix; } }
    public double[]? MatrixCandidate { get { lock (_gate) return Candidate(); } }
    public double[]? MatrixFull { get { lock (_gate) return _matrixFull; } }
    public LoopStep? CurrentStep { get { lock (_gate) return CurrentStepLocked(); } }
    public EyeStage EyeStage { get { lock (_gate) return _eyeStage; } }
    internal IReadOnlyList<GrayPoint> GrayPoints { get { lock (_gate) return _grayPoints.ToArray(); } }

    private LoopStep? CurrentStepLocked() => _mode switch
    {
        SessionMode.Live when _step < LivePlan.Length => LivePlan[_step],
        SessionMode.Video when _videoIndex >= 0 => VideoPlan[_videoIndex],
        _ => null,
    };

    private double[]? Candidate() => _matrixFull is null ? null : ColorFit.Blend(_matrixFull, ColorFit.StrengthSteps[_strengthIdx]);

    // ------------------------------------------------------------------ phone connection / mode changes

    /// <summary>The phone opened the page: hide the QR code.</summary>
    public bool PhoneConnected()
    {
        lock (_gate)
        {
            if (_mode != SessionMode.Waiting) return false;
            _mode = SessionMode.Choosing;
            _showQr = false;
            ShowLoopStep(LivePlan[0]);
            return true;
        }
    }

    /// <summary>Starts (or restarts) the live camera loop at the white step.</summary>
    public void StartLive()
    {
        lock (_gate)
        {
            if (_mode == SessionMode.Finished) return;
            ResetLoop();
            _mode = SessionMode.Live;
            _showQr = false;
            _step = 0;
            ShowLoopStep(LivePlan[0]);
            _note = "Point the camera at the screen.";
            Log.Info("Calibration: live camera loop started");
        }
    }

    private void ResetLoop()
    {
        _ambient.Clear();
        _ambientSamples.Clear();
        _stepFrames = _whiteGood = 0;
        _whiteFirst = true;
        _whitePoint = (1, 1);
        _whiteRef.Clear();
        _graySamples.Clear();
        _grayPoints.Clear();
        foreach (var p in _primary) p.Clear();
        _error = _initialError = null;
        _prevCorners = null;
        _matrixFull = null;
    }

    /// <summary>Video fallback: starts the fixed, repeating color loop on screen + LEDs (open loop, WB fixed).</summary>
    public void StartVideoLoop()
    {
        lock (_gate)
        {
            if (_mode == SessionMode.Finished) return;
            if (_mode != SessionMode.Video)
            {
                ResetLoop();
                _video.Clear();
                _videoReceived = 0;
                _mode = SessionMode.Video;
                _showQr = false;
                _videoStart = _clock();
                _videoIndex = -1;
                Log.Info("Calibration: video color loop started");
            }
            TickLocked();
        }
    }

    /// <summary>Advances the video loop timeline. Returns true when the pattern changed.</summary>
    public bool Tick()
    {
        lock (_gate) return TickLocked();
    }

    private bool TickLocked()
    {
        if (_mode != SessionMode.Video) return false;
        int idx = (int)Math.Floor((_clock() - _videoStart) / VideoStepSeconds) % VideoPlan.Length;
        if (idx < 0) idx = 0;
        if (idx == _videoIndex) return false;
        _videoIndex = idx;
        var s = VideoPlan[idx];
        SetLed(s);
        _code = VideoCodeBase + idx;
        _pattern = _layout.BuildMeasurePattern(BandFor(s), _code, showQr: false);
        _switchTime = _clock();
        return true;
    }

    /// <summary>Seconds of one full video loop.</summary>
    public static double VideoLoopSeconds => VideoPlan.Length * VideoStepSeconds;

    /// <summary>Skips the camera part: straight to the eye fine-tune.</summary>
    public void SkipCamera()
    {
        lock (_gate)
        {
            if (_mode is SessionMode.Finished or SessionMode.Eye) return;
            EnterEye("Camera part skipped.");
        }
    }

    // ------------------------------------------------------------------ patterns / LEDs

    private static int BandFor(LoopStep s) => s.Kind switch
    {
        LoopStepKind.Primary => CalibrationLayout.PrimaryBandRgb(s.Channel),
        LoopStepKind.Ambient => CalibrationLayout.WhiteBandRgb, // the bands must stay visible for detection
        _ => CalibrationLayout.GrayBandRgb(s.Level),
    };

    private void SetLed(LoopStep s)
    {
        if (s.Kind == LoopStepKind.Primary)
            _led.Set(s.Channel == 0 ? s.Level : 0, s.Channel == 1 ? s.Level : 0, s.Channel == 2 ? s.Level : 0, OverrideColorPath.WhiteBalanceNoMatrix);
        else
            _led.Set(s.Level, s.Level, s.Level, OverrideColorPath.WhiteBalanceNoMatrix);
    }

    /// <summary>Shows a loop step (new epoch → new code; frames before the switch are ignored).</summary>
    private void ShowLoopStep(LoopStep s)
    {
        SetLed(s);
        BumpEpoch(BandFor(s));
    }

    private void BumpEpoch(int bandRgb)
    {
        _epoch++;
        _code = 1 + _epoch % 39; // 1..39 (video codes start at 48)
        _pattern = _layout.BuildMeasurePattern(bandRgb, _code, _showQr);
        _switchTime = _clock();
    }

    // ------------------------------------------------------------------ live frames

    /// <summary>Analyzes one live camera frame and advances the loop. Heavy work runs outside the lock.</summary>
    public LiveFrameResult ProcessLiveFrame(RgbImage img)
    {
        CalibrationLayout layout;
        lock (_gate)
        {
            if (_mode != SessionMode.Live)
                return new LiveFrameResult(null, false, _mode == SessionMode.Eye ? "Camera part finished." : "Not measuring right now.", [], 0);
            layout = _layout;
        }
        var m = FrameAnalyzer.Measure(img, layout);
        lock (_gate)
        {
            if (_mode != SessionMode.Live) return new LiveFrameResult(m, false, "Camera part finished.", [], 0);
            var hints = Hints(m, _prevCorners);
            _prevCorners = m.Corners;
            double now = _clock();
            if (!m.Found) return Result(m, false, "Looking for the screen…", hints);
            if (m.Code is null) return Result(m, false, "Reading the step code…", hints);
            if (m.Code != _code) return Result(m, false, "Waiting for the new pattern…", hints);
            if (now - _switchTime < SettleSeconds) return Result(m, false, "Waiting for the LEDs to settle…", hints);
            var step = LivePlan[_step];
            bool used = step.Kind switch
            {
                LoopStepKind.White or LoopStepKind.WhiteRef => LiveWhite(m, step),
                LoopStepKind.Gray => LiveGray(m, step),
                LoopStepKind.Ambient => LiveAmbient(m),
                _ => LivePrimary(m, step),
            };
            return Result(m, used, _note, hints);
        }
    }

    private LiveFrameResult Result(FrameMeasurement m, bool used, string note, List<string> hints)
    {
        int delay = (int)Math.Max(0, Math.Ceiling((SettleSeconds - (_clock() - _switchTime)) * 1000));
        return new LiveFrameResult(m, used, note, hints, delay);
    }

    private bool LiveAmbient(FrameMeasurement m)
    {
        var a = SampleAmbient(m);
        if (a is null)
        {
            _note = m.RefOk ? "Can't see the wall around the screen on at least 2 sides." : "Reference squares not readable — hold still.";
            return false;
        }
        _ambientSamples.Add(a);
        _stepFrames++;
        _note = $"Measuring the room light: {_ambientSamples.Count}/{Math.Max(2, FramesPerStep - 1)}";
        if (_ambientSamples.Count >= Math.Max(2, FramesPerStep - 1))
        {
            SetAmbient(_ambientSamples);
            Advance();
        }
        return true;
    }

    /// <summary>Wall colors with the LEDs off (dark pixels included).</summary>
    private ColorSample? SampleAmbient(FrameMeasurement m)
    {
        if (!m.RefOk) return null;
        var r = m.Ref!.Mean;
        var sides = m.Sides.Where(s => s.Screen.Status == RegionStatus.Ok
                                       && s.Glow.Status is RegionStatus.Ok or RegionStatus.Weak or RegionStatus.TooDark
                                       && s.Glow.Samples >= 20)
            .Select(s => new SideColors(s.Side, FrameAnalyzer.Div(s.Glow.Mean, r), FrameAnalyzer.Div(s.Screen.Mean, r)))
            .ToList();
        return sides.Count < 2 ? null : new ColorSample(sides, _wb, _gamma);
    }

    private void SetAmbient(IReadOnlyList<ColorSample> samples)
    {
        _ambient.Clear();
        foreach (var side in Enum.GetValues<Side>())
            if (MedianOf(samples, side, c => c.Glow) is { } a) _ambient[side] = a;
        Log.Info("Calibration: ambient (LEDs off) " + string.Join(" ", _ambient.Select(kv => $"{kv.Key}:{kv.Value}")));
    }

    /// <summary>Glow minus the wall's light with the LEDs off (normalized units).</summary>
    private Rgb3 GlowOf(SideColors c) => _ambient.TryGetValue(c.Side, out var a) ? new Rgb3(c.Glow.R - a.R, c.Glow.G - a.G, c.Glow.B - a.B) : c.Glow;

    /// <summary>ρ of a neutral step with the ambient light removed (falls back to the plain frame ratio without a reference patch).</summary>
    private (int Sides, double RhoR, double RhoB) RhoOf(FrameMeasurement m)
    {
        var cs = SampleColors(m);
        return cs is null ? m.Rho() : RhoOf(cs);
    }

    private (int Sides, double RhoR, double RhoB) RhoOf(ColorSample cs)
    {
        var rr = new List<double>(4);
        var rb = new List<double>(4);
        foreach (var c in cs.Sides)
        {
            var g = GlowOf(c);
            var b = c.Band;
            double floor = 0.02 * Math.Max(g.R, Math.Max(g.G, g.B));
            if (g.R <= floor || g.G <= floor || g.B <= floor || b.R <= 0 || b.G <= 0 || b.B <= 0) continue;
            double r = (g.R / g.G) / (b.R / b.G), bl = (g.B / g.G) / (b.B / b.G);
            if (!double.IsFinite(r) || !double.IsFinite(bl)) continue;
            rr.Add(r);
            rb.Add(bl);
        }
        if (rr.Count == 0) return (0, 1, 1);
        return (rr.Count, CalibrationAnalyzer.Median(rr), CalibrationAnalyzer.Median(rb));
    }

    private bool LiveWhite(FrameMeasurement m, LoopStep step)
    {
        var (n, rr, rb) = RhoOf(m);
        if (n < 2)
        {
            _note = ExplainUnusable(m);
            return false;
        }
        (rr, rb) = ColorFit.CorrectQuantization(rr, rb, step.Level, _wb, _gamma, _brightness);
        double err = CalibrationMath.ErrorPercent(rr, rb);
        _error = err;
        _initialError ??= err;
        _stepFrames++;
        bool good = err < LiveTargetPercent;
        if (good)
        {
            _whiteGood++;
            if (step.Kind == LoopStepKind.WhiteRef && SampleColors(m) is { } cs) _whiteRef.Add(cs);
        }
        else
        {
            _whiteGood = 0;
            if (step.Kind == LoopStepKind.WhiteRef) _whiteRef.Clear();
        }
        _whitePoint = (rr, rb);

        if (_whiteGood >= WhiteGoodFrames || _stepFrames >= MaxWhiteFrames)
        {
            if (_whiteGood < WhiteGoodFrames)
                Log.Info($"Calibration: white did not settle below {LiveTargetPercent}% after {_stepFrames} frames (last {err:0.0}%); continuing");
            if (step.Kind == LoopStepKind.WhiteRef && _whiteRef.Count == 0 && SampleColors(m) is { } last) _whiteRef.Add(last);
            Log.Info($"Calibration: {step.Id} done, error {err:0.0}%, WB {_wb}");
            Advance();
            return true;
        }

        if (err >= 1.0)
        {
            double k = _whiteFirst ? 1.0 : good ? 0.5 : 0.7;
            _whiteFirst = false;
            var (gains, resR, resB) = CalibrationMath.WhiteStep(_wb, rr, rb, k);
            ApplyWb(gains);
            _whitePoint = (resR, resB);
            BumpEpoch(BandFor(step)); // the frames that follow must show the new white
            _note = good ? $"Fine-tuning white… {err:0.0} %" : $"Matching white… {err:0.0} %";
        }
        else _note = $"White matches within {err:0.0} % — hold still…";
        return true;
    }

    private bool LiveGray(FrameMeasurement m, LoopStep step)
    {
        var (n, rr, rb) = RhoOf(m);
        if (n < 2)
        {
            _note = ExplainUnusable(m);
            return false;
        }
        (rr, rb) = ColorFit.CorrectQuantization(rr, rb, step.Level, _wb, _gamma, _brightness);
        _graySamples.Add((rr, rb));
        _stepFrames++;
        _error = CalibrationMath.ErrorPercent(rr, rb);
        _note = $"{step.Title}: {_graySamples.Count}/{FramesPerStep}";
        if (_graySamples.Count >= FramesPerStep)
        {
            _grayPoints.Add(new GrayPoint(step.Level, CalibrationAnalyzer.Median(_graySamples.Select(s => s.R)),
                CalibrationAnalyzer.Median(_graySamples.Select(s => s.B))));
            Log.Info(FormattableString.Invariant($"Calibration: {step.Id} ρR={_grayPoints[^1].RhoR:0.000} ρB={_grayPoints[^1].RhoB:0.000}"));
            Advance();
        }
        else if (_stepFrames >= MaxStepFrames) Advance();
        return true;
    }

    private bool LivePrimary(FrameMeasurement m, LoopStep step)
    {
        var cs = SampleColors(m);
        if (cs is null)
        {
            _note = m.RefOk ? ExplainUnusable(m) : "Reference squares not readable — hold still, avoid reflections on the screen.";
            return false;
        }
        var list = _primary[step.Channel];
        list.Add(cs);
        _stepFrames++;
        _note = $"{step.Title}: {list.Count}/{FramesPerStep}";
        if (list.Count >= FramesPerStep || _stepFrames >= MaxStepFrames) Advance();
        return true;
    }

    /// <summary>Per-side colors normalized by the reference patch (null if fewer than 2 usable sides or no reference).</summary>
    private ColorSample? SampleColors(FrameMeasurement m)
    {
        if (!m.RefOk) return null;
        var r = m.Ref!.Mean;
        var sides = m.Sides.Where(s => s.Usable)
            .Select(s => new SideColors(s.Side, FrameAnalyzer.Div(s.Glow.Mean, r), FrameAnalyzer.Div(s.Screen.Mean, r)))
            .ToList();
        return sides.Count < 2 ? null : new ColorSample(sides, _wb, _gamma);
    }

    private void Advance()
    {
        var finished = LivePlan[_step];
        if (finished.Kind == LoopStepKind.Gray && (_step + 1 >= LivePlan.Length || LivePlan[_step + 1].Kind != LoopStepKind.Gray))
            FitGamma(_wb, _gamma);
        if (finished.Kind == LoopStepKind.Primary && finished.Channel == 2) FitMatrixLocked(_whiteRef, _primary);
        _step++;
        _stepFrames = 0;
        _whiteGood = 0;
        _graySamples.Clear();
        if (_step >= LivePlan.Length)
        {
            EnterEye("Camera part done.");
            return;
        }
        ShowLoopStep(LivePlan[_step]);
        _note = LivePlan[_step].Title + "…";
    }

    /// <summary>Gray-ramp fit with the white point; applies channel gamma + re-solved white balance.</summary>
    private void FitGamma(Rgb3 measuredWb, Rgb3 measuredGamma)
    {
        var pts = new List<GrayPoint> { new(CalibrationMath.WhiteLevel, _whitePoint.R, _whitePoint.B, 2) };
        pts.AddRange(_grayPoints);
        var fit = ColorFit.FitGamma(pts);
        if (fit is not { } f)
        {
            Log.Info("Calibration: not enough gray levels for a gamma fit");
            return;
        }
        var (wb, gamma) = ColorFit.ApplyGammaFit(measuredWb, measuredGamma, f);
        Log.Info(FormattableString.Invariant(
            $"Calibration: gamma fit δR={f.DeltaR:0.000} δB={f.DeltaB:0.000} (rms {f.RmsR:0.000}/{f.RmsB:0.000}) → gamma {measuredGamma} → {gamma}, WB {measuredWb} → {wb}"));
        ApplyGamma(gamma);
        ApplyWb(wb);
    }

    /// <summary>LED matrix candidate from the white reference + primaries (not applied; see the eye A/B check).</summary>
    private void FitMatrixLocked(IReadOnlyList<ColorSample> white, IReadOnlyList<ColorSample>[] prim)
    {
        _matrixFull = null;
        if (white.Count == 0 || prim.Any(p => p.Count == 0))
        {
            Log.Info("Calibration: no matrix (missing white reference or primary measurements)");
            return;
        }
        var led = new List<Rgb3>[3];
        var scr = new List<Rgb3>[3];
        var scrWhite = new List<Rgb3>();
        for (int k = 0; k < 3; k++) { led[k] = new(); scr[k] = new(); }
        foreach (var side in Enum.GetValues<Side>())
        {
            var wGlow = MedianOf(white, side, GlowOf);
            var wBand = MedianOf(white, side, c => c.Band);
            if (wGlow is not { } wg || wBand is not { } wb) continue;
            double lum = 0.2126 * wg.R + 0.7152 * wg.G + 0.0722 * wg.B;
            if (!(lum > 1e-9)) continue;
            var cols = new Rgb3[3];
            var bands = new Rgb3[3];
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++)
            {
                // Each primary was measured with the WB/gamma of its time; rescale its column to the final settings.
                var g = MedianOf(prim[k], side, GlowOf, s => ColumnScale(s, k));
                var b = MedianOf(prim[k], side, c => c.Band);
                if (g is null || b is null) { ok = false; break; }
                cols[k] = new Rgb3(g.Value.R / lum, g.Value.G / lum, g.Value.B / lum);
                bands[k] = b.Value;
            }
            if (!ok) continue;
            for (int k = 0; k < 3; k++) { led[k].Add(cols[k]); scr[k].Add(bands[k]); }
            scrWhite.Add(wb);
        }
        if (scrWhite.Count < 2)
        {
            Log.Info("Calibration: no matrix (fewer than 2 sides measured in all colors)");
            return;
        }
        var P = led.Select(MedianRgb).ToArray();
        var S = scr.Select(MedianRgb).ToArray();
        var W = MedianRgb(scrWhite);
        var m = ColorFit.FitMatrix(P, S, W);
        if (m is null)
        {
            Log.Info($"Calibration: matrix fit failed (P {string.Join(" ", P)}; S {string.Join(" ", S)}; W {W})");
            return;
        }
        _matrixFull = m;
        Log.Info("Calibration: LED matrix candidate " + FormatMatrix(m) + $" (P {string.Join(" ", P)}; S {string.Join(" ", S)}; W {W})");

        double ColumnScale(ColorSample s, int k)
        {
            double wOld = Pick(s.Wb, k), wNew = Pick(_wb, k), gOld = Pick(s.Gamma, k), gNew = Pick(_gamma, k);
            return (wNew / Math.Max(1e-6, wOld)) * Math.Pow(CalibrationMath.WhiteLevel, gNew - gOld);
        }
    }

    private static double Pick(Rgb3 v, int k) => k switch { 0 => v.R, 1 => v.G, _ => v.B };

    private static Rgb3? MedianOf(IReadOnlyList<ColorSample> samples, Side side, Func<SideColors, Rgb3> sel, Func<ColorSample, double>? scale = null)
    {
        var vals = new List<Rgb3>();
        foreach (var s in samples)
            foreach (var c in s.Sides)
                if (c.Side == side)
                {
                    var v = sel(c);
                    double f = scale?.Invoke(s) ?? 1;
                    vals.Add(new Rgb3(v.R * f, v.G * f, v.B * f));
                }
        return vals.Count == 0 ? null : MedianRgb(vals);
    }

    private static Rgb3 MedianRgb(IEnumerable<Rgb3> v)
    {
        var list = v.ToList();
        return new Rgb3(CalibrationAnalyzer.Median(list.Select(x => x.R)), CalibrationAnalyzer.Median(list.Select(x => x.G)),
            CalibrationAnalyzer.Median(list.Select(x => x.B)));
    }

    internal static string FormatMatrix(double[]? m) => m is null ? "identity" : "[" + string.Join(", ",
        Enumerable.Range(0, 3).Select(r => string.Join(" ", Enumerable.Range(0, 3).Select(c => m[r * 3 + c].ToString("0.000", CultureInfo.InvariantCulture))))) + "]";

    // ------------------------------------------------------------------ hints

    internal static List<string> Hints(FrameMeasurement m, PointD[]? prev)
    {
        var h = new List<string>();
        if (!m.Found)
        {
            h.Add("Point the camera at the screen — all 4 white corner squares must be visible.");
            return h;
        }
        if (m.ScreenFraction < 0.3) h.Add("Move closer — the screen should fill about half of the picture.");
        if (m.OutOfFrameSides >= 2) h.Add("Screen not fully visible — keep the glow on all sides in the picture.");
        if (m.ClippedSides >= 2) h.Add("Overexposed — tap the brightest area on screen to lower exposure.");
        else if (m.WeakSides >= 2) h.Add("The glow is too weak — dim the room lights or raise the LED brightness.");
        if (prev is { Length: 4 } && m.Corners is { Length: 4 } c)
        {
            double diag = (c[2] - c[0]).Length, moved = 0;
            for (int i = 0; i < 4; i++) moved = Math.Max(moved, (c[i] - prev[i]).Length);
            if (diag > 0 && moved / diag > 0.03) h.Add("Hold still…");
        }
        if (m.Code is null && h.Count == 0) h.Add("Hold still — reading the step code…");
        return h;
    }

    private static string ExplainUnusable(FrameMeasurement m)
    {
        if (m.ClippedSides >= 2) return "Overexposed — can't measure the glow.";
        if (m.OutOfFrameSides >= 2) return "The glow around the screen is cut off.";
        if (m.WeakSides >= 2) return "The glow is too weak to measure.";
        return "Could not measure the glow on at least 2 sides.";
    }

    // ------------------------------------------------------------------ video

    /// <summary>Analyzes one frame extracted from the phone's video (time <paramref name="t"/> in seconds).</summary>
    public (int? Code, bool Usable) ProcessVideoFrame(RgbImage img, double t)
    {
        CalibrationLayout layout;
        lock (_gate)
        {
            if (_mode is not (SessionMode.Video or SessionMode.VideoAnalyzing)) return (null, false);
            _mode = SessionMode.VideoAnalyzing;
            layout = _layout;
        }
        var m = FrameAnalyzer.Measure(img, layout);
        lock (_gate)
        {
            _videoReceived++;
            if (!m.Found || m.Code is not { } code || code < VideoCodeBase || code >= VideoCodeBase + VideoPlan.Length) return (m.Code, false);
            var (n, rr, rb) = m.Rho();
            var cs = SampleColors(m);
            var amb = VideoPlan[code - VideoCodeBase].Kind == LoopStepKind.Ambient ? SampleAmbient(m) : null;
            _video.Add(new VideoSample(t, code, n, rr, rb, cs, amb));
            return (code, n >= 2 || cs is not null || amb is not null);
        }
    }

    public (int Received, int Recognized) VideoCounts { get { lock (_gate) return (_videoReceived, _video.Count); } }

    /// <summary>A new upload begins: forget frames of an earlier attempt.</summary>
    public void ClearVideoFrames()
    {
        lock (_gate)
        {
            _video.Clear();
            _videoReceived = 0;
        }
    }

    /// <summary>
    /// Computes WB, channel gamma and the matrix candidate from the uploaded video frames (open loop: everything was
    /// measured with the settings in effect while recording) and moves on to the eye fine-tune.
    /// Returns null on success, else a message for the user (the video loop keeps running).
    /// </summary>
    public string? FinishVideo()
    {
        lock (_gate)
        {
            if (_mode is not (SessionMode.Video or SessionMode.VideoAnalyzing)) return "The video loop is not running.";
            // Frames in time order; drop the first frame of every run of the same code (it may straddle the switch).
            var frames = _video.OrderBy(v => v.T).ToList();
            var used = new List<VideoSample>();
            int lastCode = -1;
            double runStart = double.NegativeInfinity;
            foreach (var f in frames)
            {
                if (f.Code != lastCode) { lastCode = f.Code; runStart = f.T; }
                if (f.T - runStart < 0.4 - 1e-9) continue;
                used.Add(f);
            }
            var wbRec = _wb;
            var gammaRec = _gamma;
            var byStep = Enumerable.Range(0, VideoPlan.Length).Select(i => used.Where(u => u.Code == VideoCodeBase + i).ToList()).ToArray();
            int whiteIdx = Array.FindIndex(VideoPlan, p => p.Kind == LoopStepKind.White);
            int ambIdx = Array.FindIndex(VideoPlan, p => p.Kind == LoopStepKind.Ambient);
            var white = byStep[whiteIdx].Where(u => u.RhoSides >= 2 || u.Colors is not null).ToList();
            if (white.Count == 0)
            {
                _mode = SessionMode.Video;
                Log.Info($"Calibration video: no usable white frames ({_videoReceived} frames received, {_video.Count} recognized)");
                return _video.Count == 0
                    ? "The color loop was not found in the video. Keep the whole screen and the glow around it in view and record again."
                    : "The white step could not be measured in the video. Keep the whole screen and the glow in view and record at least one full loop.";
            }
            ResetLoop();
            var ambient = byStep[ambIdx].Where(u => u.Ambient is not null).Select(u => u.Ambient!).ToList();
            if (ambient.Count > 0) SetAmbient(ambient);
            // ρ per frame: from the reference-normalized colors with the room light removed, else the plain ratio.
            List<(double R, double B)> Rhos(List<VideoSample> list) => list
                .Select(u => u.Colors is { } c && RhoOf(c) is { Sides: >= 2 } x ? (x.RhoR, x.RhoB) : u.RhoSides >= 2 ? (u.RhoR, u.RhoB) : (double.NaN, double.NaN))
                .Where(x => double.IsFinite(x.Item1)).ToList();
            var wRhos = Rhos(white);
            if (wRhos.Count == 0) wRhos.Add((1, 1));
            var (wr, wb) = (CalibrationAnalyzer.Median(wRhos.Select(w => w.R)), CalibrationAnalyzer.Median(wRhos.Select(w => w.B)));
            (wr, wb) = ColorFit.CorrectQuantization(wr, wb, CalibrationMath.WhiteLevel, wbRec, gammaRec, _brightness);
            _initialError = _error = CalibrationMath.ErrorPercent(wr, wb);
            _whitePoint = (wr, wb);
            for (int i = 0; i < VideoPlan.Length; i++)
            {
                var s = VideoPlan[i];
                if (s.Kind != LoopStepKind.Gray) continue;
                var g = Rhos(byStep[i]);
                if (g.Count == 0) continue;
                var (r, b) = ColorFit.CorrectQuantization(CalibrationAnalyzer.Median(g.Select(x => x.R)),
                    CalibrationAnalyzer.Median(g.Select(x => x.B)), s.Level, wbRec, gammaRec, _brightness);
                _grayPoints.Add(new GrayPoint(s.Level, r, b));
            }
            if (_grayPoints.Count > 0) FitGamma(wbRec, gammaRec);
            else
            {
                var (gains, _, _) = CalibrationMath.WhiteStep(wbRec, wr, wb, 1.0);
                ApplyWb(gains);
            }
            var whiteColors = byStep[whiteIdx].Where(u => u.Colors is not null).Select(u => u.Colors!).ToList();
            var prim = new List<ColorSample>[3];
            for (int k = 0; k < 3; k++)
            {
                int idx = Array.FindIndex(VideoPlan, p => p.Kind == LoopStepKind.Primary && p.Channel == k);
                prim[k] = byStep[idx].Where(u => u.Colors is not null).Select(u => u.Colors!).ToList();
            }
            FitMatrixLocked(whiteColors, prim);
            _summary = $"Video: {_videoReceived} frames, {used.Count} used. White was off by {_initialError:0.0} %"
                       + (_grayPoints.Count > 0 ? $", {_grayPoints.Count} dark levels measured" : "")
                       + (_matrixFull is not null ? ", color correction measured." : ".");
            Log.Info("Calibration " + _summary + $" → WB {_wb}, gamma {_gamma}");
            EnterEye("Video analyzed.");
            return null;
        }
    }

    // ------------------------------------------------------------------ eye fine-tune

    private void EnterEye(string why)
    {
        _mode = SessionMode.Eye;
        _eyeBaseWb = _wb;
        _eyeBaseGamma = _gamma;
        _eyeT = _eyeM = _eyeTd = _eyeMd = 0;
        _eyeColor = 0;
        _eyeShowA = true;
        _votesA = _votesB = _votesSame = 0;
        _votes.Clear();
        _note = why;
        SetEyeStage(EyeStage.White);
        Log.Info($"Calibration: eye fine-tune ({why}) WB {_wb}, gamma {_gamma}, matrix candidate {FormatMatrix(Candidate())}");
    }

    private void SetEyeStage(EyeStage stage)
    {
        if (stage == EyeStage.Colors && _matrixFull is null) stage = EyeStage.Dark;
        _eyeStage = stage;
        switch (stage)
        {
            case EyeStage.White:
                ApplyMatrix(_orig.ColorMatrix);
                _led.Set(1, 1, 1, OverrideColorPath.LedCorrection);
                _pattern = _layout.BuildEyePattern(0xFFFFFF);
                break;
            case EyeStage.Dark:
                ApplyMatrix(_orig.ColorMatrix);
                _led.Set(EyeDarkLevel, EyeDarkLevel, EyeDarkLevel, OverrideColorPath.LedCorrection);
                int c = (int)Math.Round(EyeDarkLevel * 255);
                _pattern = _layout.BuildEyePattern((c << 16) | (c << 8) | c);
                break;
            default:
                ShowEyeColor();
                break;
        }
        _switchTime = _clock();
    }

    private void ShowEyeColor()
    {
        var col = EyeColors[_eyeColor];
        ApplyMatrix(_eyeShowA ? Candidate() : null);
        _led.Set(((col.Rgb >> 16) & 0xFF) / 255.0, ((col.Rgb >> 8) & 0xFF) / 255.0, (col.Rgb & 0xFF) / 255.0, OverrideColorPath.LedCorrection);
        _pattern = _layout.BuildEyePattern(col.Rgb);
    }

    /// <summary>Handles one eye command. Returns an error message or null.</summary>
    public string? Eye(EyeCommand cmd)
    {
        lock (_gate)
        {
            if (_mode != SessionMode.Eye) return "Fine-tuning is not active.";
            switch (cmd.Action)
            {
                case "stage":
                    if (cmd.Stage is not { } st) return "Missing stage.";
                    if (st == EyeStage.Colors && _matrixFull is null) return "No color correction was measured.";
                    SetEyeStage(st);
                    return null;
                case "pad":
                    double t = Clamp1(cmd.T), m = Clamp1(cmd.M);
                    if (_eyeStage == EyeStage.White)
                    {
                        _eyeT = t; _eyeM = m;
                        ApplyWb(ColorFit.WhiteFromPad(_eyeBaseWb, t, m));
                    }
                    else if (_eyeStage == EyeStage.Dark)
                    {
                        _eyeTd = t; _eyeMd = m;
                        ApplyGamma(ColorFit.GammaFromPad(_eyeBaseGamma, t, m));
                    }
                    else return "The pad is not used in this step.";
                    return null;
                case "reset":
                    if (_eyeStage == EyeStage.White) { _eyeT = _eyeM = 0; ApplyWb(_eyeBaseWb); }
                    else if (_eyeStage == EyeStage.Dark) { _eyeTd = _eyeMd = 0; ApplyGamma(_eyeBaseGamma); }
                    else { _strengthIdx = Array.IndexOf(ColorFit.StrengthSteps, ColorFit.DefaultMatrixStrength); ShowEyeColor(); }
                    return null;
                case "show":
                    if (_eyeStage != EyeStage.Colors) return "Not in the color check.";
                    if (cmd.Color is { } ci) _eyeColor = Math.Clamp(ci, 0, EyeColors.Length - 1);
                    if (cmd.Variant is { } v) _eyeShowA = v == "A";
                    ShowEyeColor();
                    return null;
                case "vote":
                    if (_eyeStage != EyeStage.Colors) return "Not in the color check.";
                    if (cmd.Variant is not ("A" or "B" or "same")) return "Vote A, B or same.";
                    int color = cmd.Color is { } vc ? Math.Clamp(vc, 0, EyeColors.Length - 1) : _eyeColor;
                    _votes[color] = cmd.Variant;
                    _votesA = _votes.Values.Count(x => x == "A");
                    _votesB = _votes.Values.Count(x => x == "B");
                    _votesSame = _votes.Values.Count(x => x == "same");
                    // next color not voted yet, starting with A
                    for (int i = 1; i <= EyeColors.Length; i++)
                    {
                        int n = (color + i) % EyeColors.Length;
                        if (!_votes.ContainsKey(n)) { _eyeColor = n; break; }
                    }
                    _eyeShowA = true;
                    ShowEyeColor();
                    return null;
                case "strength":
                    if (_matrixFull is null) return "No color correction was measured.";
                    _strengthIdx = Math.Clamp(_strengthIdx + Math.Sign(cmd.Delta), 0, ColorFit.StrengthSteps.Length - 1);
                    if (_eyeStage == EyeStage.Colors) ShowEyeColor();
                    return null;
                default:
                    return "Unknown action.";
            }
        }
    }

    private static double Clamp1(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;

    /// <summary>The matrix decided by the A/B votes: candidate if A won, off if B won, original if no votes.</summary>
    private double[]? DecidedMatrix()
    {
        if (_votesA + _votesB == 0) return _orig.ColorMatrix;
        return _votesA > _votesB ? Candidate() : null;
    }

    /// <summary>Done: applies the final values (all were applied live already; the matrix follows the A/B votes).</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_mode == SessionMode.Finished) return;
            var m = DecidedMatrix();
            if (_mode != SessionMode.Eye) m = _orig.ColorMatrix;
            ApplyMatrix(m);
            _mode = SessionMode.Finished;
            Log.Info($"Calibration finished: WB {_wb}, gamma {_gamma}, matrix {FormatMatrix(_matrix)} (votes A {_votesA} / B {_votesB} / same {_votesSame})");
        }
    }

    /// <summary>Cancel: puts white balance, channel gamma and matrix back to what they were before the calibration.</summary>
    public void RestoreOriginal()
    {
        lock (_gate)
        {
            var o = _orig;
            if (!Same(_wb, o.WhiteBalance)) ApplyWb(new Rgb3(o.WhiteBalance.R, o.WhiteBalance.G, o.WhiteBalance.B));
            if (!Same(_gamma, o.ChannelGamma)) ApplyGamma(new Rgb3(o.ChannelGamma.R, o.ChannelGamma.G, o.ChannelGamma.B));
            if (!SameMatrix(_matrix, o.ColorMatrix)) ApplyMatrix(o.ColorMatrix);
        }
    }

    private static bool Same(Rgb3 a, (double R, double G, double B) b) =>
        Math.Abs(a.R - b.R) < 1e-9 && Math.Abs(a.G - b.G) < 1e-9 && Math.Abs(a.B - b.B) < 1e-9;

    internal static bool SameMatrix(double[]? a, double[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        for (int i = 0; i < 9; i++) if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
        return true;
    }

    // ------------------------------------------------------------------ applying

    private void ApplyWb(Rgb3 w)
    {
        _wb = w;
        try { _target.ApplyWhiteBalance(w.R, w.G, w.B); }
        catch (Exception ex) { Log.Error("Calibration: applying the white balance failed", ex); }
    }

    private void ApplyGamma(Rgb3 g)
    {
        _gamma = g;
        try { _target.ApplyChannelGamma(g.R, g.G, g.B); }
        catch (Exception ex) { Log.Error("Calibration: applying the channel gamma failed", ex); }
    }

    private void ApplyMatrix(double[]? m)
    {
        if (SameMatrix(_matrix, m)) return;
        _matrix = m is null ? null : (double[])m.Clone();
        try { _target.ApplyColorMatrix(_matrix is null ? null : (double[])_matrix.Clone()); }
        catch (Exception ex) { Log.Error("Calibration: applying the color matrix failed", ex); }
    }

    // ------------------------------------------------------------------ snapshot

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            var step = CurrentStepLocked();
            int stepCount = _mode == SessionMode.Video || _mode == SessionMode.VideoAnalyzing ? VideoPlan.Length : LivePlan.Length;
            int stepIndex = _mode == SessionMode.Live ? _step : _mode is SessionMode.Video or SessionMode.VideoAnalyzing ? Math.Max(0, _videoIndex) : 0;
            double? progress = _mode switch
            {
                SessionMode.Live => Math.Min(1, (_step + StepProgress()) / LivePlan.Length),
                SessionMode.Eye or SessionMode.Finished => 1,
                _ => null,
            };
            double videoPos = _mode is SessionMode.Video ? (_clock() - _videoStart) % VideoLoopSeconds : 0;
            return new SessionSnapshot(_mode, step?.Id, step?.Title, stepIndex, stepCount, progress, _error, _initialError, _note,
                _wb, _gamma, _matrix is not null, _matrixFull is not null, ColorFit.StrengthSteps[_strengthIdx],
                _eyeStage, _eyeT, _eyeM, _eyeTd, _eyeMd, _eyeColor, _eyeShowA, _votesA, _votesB, _votesSame,
                _videoReceived, _video.Count, videoPos, _summary, _code);
        }
    }

    private double StepProgress()
    {
        if (_step >= LivePlan.Length) return 0;
        var s = LivePlan[_step];
        return s.Kind switch
        {
            LoopStepKind.White or LoopStepKind.WhiteRef => Math.Min(0.95, (double)_whiteGood / WhiteGoodFrames * 0.6 + (_error is { } e ? Math.Max(0, 0.4 - e / 25) : 0)),
            LoopStepKind.Gray => (double)_graySamples.Count / FramesPerStep,
            LoopStepKind.Ambient => (double)_ambientSamples.Count / Math.Max(2, FramesPerStep - 1),
            _ => (double)_primary[s.Channel].Count / FramesPerStep,
        };
    }
}

/// <summary>A command from the phone's eye fine-tune UI.</summary>
internal sealed record EyeCommand(string Action, EyeStage? Stage = null, double T = 0, double M = 0, string? Variant = null,
    int? Color = null, int Delta = 0);

internal sealed record SessionSnapshot(
    SessionMode Mode, string? StepId, string? StepTitle, int StepIndex, int StepCount, double? Progress,
    double? ErrorPercent, double? InitialErrorPercent, string Note,
    Rgb3 Wb, Rgb3 Gamma, bool MatrixOn, bool HasMatrixCandidate, double MatrixStrength,
    EyeStage EyeStage, double EyeT, double EyeM, double EyeTd, double EyeMd, int EyeColor, bool EyeShowA,
    int VotesA, int VotesB, int VotesSame,
    int VideoFramesReceived, int VideoFramesRecognized, double VideoPosition, string Summary, int Code);

/// <summary>The calibration's LED pattern: one color on all LEDs through the chosen color path.</summary>
internal sealed class CalibrationLedOverride : ILedOverride
{
    private sealed record Drive(float R, float G, float B, OverrideColorPath Path);

    private volatile Drive _d = new(0.6f, 0.6f, 0.6f, OverrideColorPath.WhiteBalanceNoMatrix);

    public void Set(double r, double g, double b, OverrideColorPath path) =>
        _d = new Drive((float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1), path);

    public (float R, float G, float B, OverrideColorPath Path) Current
    {
        get
        {
            var d = _d;
            return (d.R, d.G, d.B, d.Path);
        }
    }

    public bool ApplyWhiteBalance => _d.Path != OverrideColorPath.Raw;

    public OverrideColorPath ColorPath => _d.Path;

    public void Render(Span<float> rgb, int ledCount, double timeSeconds)
    {
        var d = _d;
        int n = Math.Min(rgb.Length / 3, ledCount);
        for (int i = 0; i < n; i++)
        {
            rgb[3 * i] = d.R;
            rgb[3 * i + 1] = d.G;
            rgb[3 * i + 2] = d.B;
        }
    }
}
