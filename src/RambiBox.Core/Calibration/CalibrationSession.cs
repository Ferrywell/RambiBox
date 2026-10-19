using System.Globalization;
using RambiBox.Core.Engine;

namespace RambiBox.Core.Calibration;

internal enum LoopStepKind { Ambient, White, Gray, WhiteRef, Primary }

/// <summary>One step of the color loop: LED drive (relative level, see <see cref="CalibrationSession"/>) + screen reference.</summary>
internal sealed record LoopStep(LoopStepKind Kind, double Level, int Channel, string Id, string Title);

internal enum SessionMode { Waiting, Choosing, Live, Video, VideoAnalyzing, Eye, Finished }

internal enum EyeStage { White, Dark, Colors }

/// <summary>
/// Per-side camera colors of one frame, normalized by the frame's neutral reference square: units are "camera response
/// relative to the screen's displayed linear level", so frames shown at different screen gains are comparable.
/// <see cref="Band"/> has the screen's black level removed.
/// </summary>
internal sealed record SideColors(Side Side, Rgb3 Glow, Rgb3 Band);

/// <summary>Normalized colors of one frame + the LED settings (and actual LED drive level) it was taken with.</summary>
internal sealed record ColorSample(IReadOnlyList<SideColors> Sides, Rgb3 Wb, Rgb3 Gamma, double Level);

/// <summary>What happened to one live frame (for the phone's response).</summary>
internal sealed record LiveFrameResult(FrameMeasurement? Measurement, bool Used, string Note, IReadOnlyList<string> Hints, int DelayMs);

/// <summary>A color shown during the eye A/B check.</summary>
internal sealed record EyeColor(string Name, int Rgb);

/// <summary>
/// The calibration logic without networking: patterns, LED drive, the camera color loop (live, closed loop, or video,
/// open loop), the fits, and the eye fine-tune. All results are applied through <see cref="ICalibrationTarget"/>.
/// <para>
/// Live loop: ROOM LIGHT (LEDs off) → WHITE (closed loop on the white balance until the glow matches the screen reference
/// within 2 % for 3 frames) → GRAY RAMP (50 / 25 / 12 % → per-channel gamma fit, white re-solved) → WHITE again
/// (re-converge + white reference for the matrix) → PRIMARIES R, G, B (LED matrix fit, not applied; the eye A/B check
/// decides) → EYE. Every pattern carries a visual step code; a frame is used only when its code matches the current
/// pattern and it arrived at least <see cref="SettleSeconds"/> after the last change.
/// </para>
/// <para>
/// Exposure: the LEDs are driven as bright as possible (white and primaries at 1.0, the gray ramp relative to that;
/// the engine still applies the user's brightness) and the photo's exposure is balanced with the SCREEN instead
/// (<see cref="ScreenGainControl"/>): every analyzed frame may dim or brighten all measured screen elements, per step,
/// until the camera sees them unclipped. Only when the glow itself clips are the LEDs dimmed a little.
/// </para>
/// <para>
/// Never hangs: every step has a timeout (frame time, i.e. only while the phone is sending frames, counted from the
/// first time the screen was detected): room light 8 s (then a dark room is assumed), white 12 s without any usable
/// frame or 25 s in total (then the best result so far is kept), other steps 12 s (then what was measured is used, or
/// the step is skipped with a note). The phone can skip the current step at any time.
/// </para>
/// Thread-safe: all members may be called from any thread. Target callbacks are invoked under the lock and must not
/// block (they post to the UI thread).
/// </summary>
internal sealed class CalibrationSession
{
    /// <summary>LED levels are relative to full drive (× <see cref="LedScale"/>, normally 1).</summary>
    public static readonly LoopStep[] LivePlan =
    [
        new(LoopStepKind.Ambient, 0, -1, "ambient", "Room light (LEDs off)"),
        new(LoopStepKind.White, 1.0, -1, "white", "White"),
        new(LoopStepKind.Gray, 0.50, -1, "gray50", "Dark tones 50 %"),
        new(LoopStepKind.Gray, 0.25, -1, "gray25", "Dark tones 25 %"),
        new(LoopStepKind.Gray, 0.12, -1, "gray12", "Dark tones 12 %"),
        new(LoopStepKind.WhiteRef, 1.0, -1, "white2", "White check"),
        new(LoopStepKind.Primary, 1.0, 0, "red", "Red"),
        new(LoopStepKind.Primary, 1.0, 1, "green", "Green"),
        new(LoopStepKind.Primary, 1.0, 2, "blue", "Blue"),
    ];

    public static readonly LoopStep[] VideoPlan =
    [
        new(LoopStepKind.Ambient, 0, -1, "ambient", "Room light (LEDs off)"),
        new(LoopStepKind.White, 1.0, -1, "white", "White"),
        new(LoopStepKind.Gray, 0.50, -1, "gray50", "Dark tones 50 %"),
        new(LoopStepKind.Gray, 0.25, -1, "gray25", "Dark tones 25 %"),
        new(LoopStepKind.Gray, 0.12, -1, "gray12", "Dark tones 12 %"),
        new(LoopStepKind.Primary, 1.0, 0, "red", "Red"),
        new(LoopStepKind.Primary, 1.0, 1, "green", "Green"),
        new(LoopStepKind.Primary, 1.0, 2, "blue", "Blue"),
    ];

    public static readonly EyeColor[] EyeColors =
    [
        new("red", 0xFF0000), new("green", 0x00FF00), new("blue", 0x0000FF), new("orange", 0xFF8A30), new("cyan", 0x00FFFF),
    ];

    public const int VideoCodeBase = 48;
    public const double VideoStepSeconds = 2.5;
    /// <summary>Video is open loop (no feedback while recording): a fixed, moderate screen gain; the three reference
    /// squares still give a usable normalization when part of the pattern clips.</summary>
    public const double VideoScreenGain = 0.35;
    public const double EyeDarkLevel = 0.2;   // LED drive; the screen shows the same value (0x33), like the ambilight does
    public const double LiveTargetPercent = 2.0;
    /// <summary>Screen gain of the first live step (the gain loop takes over from there).</summary>
    public const double InitialScreenGain = 0.45;
    public const double MinLedScale = 0.35;

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
    private ScreenLevels _levels;
    private int _epoch;
    private int _code;
    private double _switchTime;
    private bool _showQr = true;

    // ---- exposure control
    private double _gain = InitialScreenGain;
    private readonly Dictionary<string, double> _stepGain = new();
    private double _ledScale = 1.0;
    private int _notFound, _glowClip;
    private string? _gainHint;
    private bool _roomLightHint, _glowWeakHint;

    // ---- mode / loop
    private SessionMode _mode = SessionMode.Waiting;
    private int _step;
    private int _stepFrames, _whiteGood, _stepUsable;
    private double _stepTime, _lastFrameTime = double.NaN;
    private bool _seenScreen;
    private bool _whiteFirst = true;
    private (double R, double B) _whitePoint = (1, 1);
    private double _whiteLevel = 1.0;
    private double _bestErr = double.PositiveInfinity, _lastErr = double.PositiveInfinity;
    private Rgb3 _bestWb;
    private (double R, double B) _bestPoint = (1, 1);
    private readonly List<ColorSample> _whiteRef = new();
    private ColorSample? _lastWhiteRefAny;
    private readonly List<(double R, double B)> _graySamples = new();
    private readonly List<GrayPoint> _grayPoints = new();
    private readonly List<ColorSample>[] _primary = [new(), new(), new()];
    private readonly List<string> _stepNotes = new();
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

    /// <summary>Wall light with the LEDs off, per side (normalized units, see <see cref="SideColors"/>); subtracted from every glow.</summary>
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
        _bestWb = _wb;
        _levels = ScreenLevels.ForGain(_gain);
        _pattern = _layout.BuildMeasurePattern(_levels, 0, showQr: true);
        lock (_gate) ShowLoopStep(LivePlan[0]);
    }

    // ------------------------------------------------------------------ tunables (tests)

    public double SettleSeconds { get; init; } = 0.4;
    public int FramesPerStep { get; init; } = 4;
    public int WhiteGoodFrames { get; init; } = 3;
    public int MaxWhiteFrames { get; init; } = 30;
    public int MaxStepFrames { get; init; } = 40;
    public double StepTimeoutSeconds { get; init; } = 12;
    public double AmbientTimeoutSeconds { get; init; } = 8;
    public double WhiteTimeoutSeconds { get; init; } = 25;

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
    /// <summary>Current screen gain (linear band level relative to full white).</summary>
    public double ScreenGain { get { lock (_gate) return _levels.Gain; } }
    /// <summary>Screen intensities of the pattern currently shown.</summary>
    internal ScreenLevels Levels { get { lock (_gate) return _levels; } }
    /// <summary>LED drive scale (1 = full; lowered only when the glow clips in the camera).</summary>
    public double LedScale { get { lock (_gate) return _ledScale; } }
    /// <summary>Steps that timed out or were skipped (user-visible notes).</summary>
    public IReadOnlyList<string> StepNotes { get { lock (_gate) return _stepNotes.ToArray(); } }

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

    /// <summary>Starts (or restarts) the live camera loop at the first step.</summary>
    public void StartLive()
    {
        lock (_gate)
        {
            if (_mode == SessionMode.Finished) return;
            ResetLoop();
            _mode = SessionMode.Live;
            _showQr = false;
            _step = 0;
            _gain = InitialScreenGain;
            _stepGain.Clear();
            _ledScale = 1.0;
            _seenScreen = false;
            ShowLoopStep(LivePlan[0]);
            _note = "Point the camera at the screen.";
            Log.Info("Calibration: live camera loop started");
        }
    }

    private void ResetLoop()
    {
        _ambient.Clear();
        _ambientSamples.Clear();
        ResetStepState();
        _whiteFirst = true;
        _whitePoint = (1, 1);
        _whiteLevel = 1.0;
        _whiteRef.Clear();
        _grayPoints.Clear();
        foreach (var p in _primary) p.Clear();
        _stepNotes.Clear();
        _error = _initialError = null;
        _prevCorners = null;
        _matrixFull = null;
        _summary = "";
        _roomLightHint = _glowWeakHint = false;
    }

    private void ResetStepState()
    {
        _stepFrames = _whiteGood = _stepUsable = 0;
        _stepTime = 0;
        _notFound = _glowClip = 0;
        _bestErr = _lastErr = double.PositiveInfinity;
        _bestWb = _wb;
        _lastWhiteRefAny = null;
        _graySamples.Clear();
        _gainHint = null;
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
                _ledScale = 1.0;
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
        _levels = VideoLevels(idx);
        _pattern = _layout.BuildMeasurePattern(_levels, _code, showQr: false);
        _switchTime = _clock();
        return true;
    }

    /// <summary>Screen intensities of video step <paramref name="index"/>.</summary>
    internal static ScreenLevels VideoLevels(int index)
    {
        var s = VideoPlan[index];
        return ScreenLevels.ForGain(VideoScreenGain, s.Kind == LoopStepKind.Primary ? s.Channel : -1);
    }

    /// <summary>The pattern the video loop shows during step <paramref name="index"/>.</summary>
    internal static CalibrationPattern VideoPattern(CalibrationLayout layout, int index) =>
        layout.BuildMeasurePattern(VideoLevels(index), VideoCodeBase + index, showQr: false);

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

    /// <summary>
    /// The phone's "Skip this step" button: ends the current live step like a timeout does (uses what was measured,
    /// assumes no room light, keeps the best white so far). Returns false when no live step is running.
    /// </summary>
    public bool SkipStep()
    {
        lock (_gate)
        {
            if (_mode != SessionMode.Live || _step >= LivePlan.Length) return false;
            EndStepEarly("skipped");
            return true;
        }
    }

    // ------------------------------------------------------------------ patterns / LEDs

    private ScreenLevels LevelsFor(LoopStep s) => ScreenLevels.ForGain(_gain, s.Kind == LoopStepKind.Primary ? s.Channel : -1);

    private double LedLevel(LoopStep s) => s.Level * _ledScale;

    private void SetLed(LoopStep s)
    {
        double l = _mode == SessionMode.Video ? s.Level : LedLevel(s);
        if (s.Kind == LoopStepKind.Primary)
            _led.Set(s.Channel == 0 ? l : 0, s.Channel == 1 ? l : 0, s.Channel == 2 ? l : 0, OverrideColorPath.WhiteBalanceNoMatrix);
        else
            _led.Set(l, l, l, OverrideColorPath.WhiteBalanceNoMatrix);
    }

    /// <summary>
    /// Screen gains are remembered per LED drive (the phone's auto exposure mostly follows the glow), so e.g. the white
    /// check starts with the gain the white step ended with.
    /// </summary>
    private static string GainKey(LoopStep s) => s.Kind == LoopStepKind.Primary
        ? "p" + s.Channel.ToString(CultureInfo.InvariantCulture)
        : "n" + s.Level.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Shows a loop step (new epoch → new code; frames before the switch are ignored).</summary>
    private void ShowLoopStep(LoopStep s)
    {
        if (_stepGain.TryGetValue(GainKey(s), out var g)) _gain = g;
        SetLed(s);
        BumpEpoch(LevelsFor(s));
    }

    private void BumpEpoch(ScreenLevels levels)
    {
        _levels = levels;
        _epoch++;
        _code = 1 + _epoch % 39; // 1..39 (video codes start at 48)
        _pattern = _layout.BuildMeasurePattern(levels, _code, _showQr);
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
            double now = _clock();
            // Step clock: frame time only (a paused phone doesn't time steps out), and only once the screen was seen
            // at all (aiming at the start doesn't cost the first step).
            _seenScreen |= m.Found;
            if (double.IsFinite(_lastFrameTime) && _seenScreen) _stepTime += Math.Clamp(now - _lastFrameTime, 0, 1.5);
            _lastFrameTime = now;
            int stepBefore = _step;
            var step = LivePlan[_step];
            var hints = Hints(m, _prevCorners, step.Kind == LoopStepKind.Ambient);
            _prevCorners = m.Corners;
            var (used, note) = Handle(m, step, now);
            if (_mode == SessionMode.Live && _step == stepBefore && TimedOut(step))
            {
                EndStepEarly("timed out");
                note = _note;
            }
            if (_mode == SessionMode.Live && _step == stepBefore)
            {
                if (_gainHint is { } gh) hints.Add(gh);
                if (_glowWeakHint && step.Kind != LoopStepKind.Ambient) AddOnce(hints, WeakGlowHint);
                if (_roomLightHint) AddOnce(hints, "Too much room light — dim the lights for a more accurate result.");
            }
            return Result(m, used, note, hints);
        }
    }

    private (bool Used, string Note) Handle(FrameMeasurement m, LoopStep step, double now)
    {
        bool settled = now - _switchTime >= SettleSeconds;
        if (!m.Found)
        {
            // Not found for a while although the pattern is steady: maybe the markers vanish next to a bright glow
            // (gain very low) or bloom (gain high) — scan the screen gain until the camera picks the screen up again.
            if (settled && ++_notFound >= 2)
            {
                _notFound = 0;
                _gain = _gain < 0.1 ? 0.6 : _gain * 0.45;
                BumpEpoch(LevelsFor(step));
            }
            return (false, "Looking for the screen…");
        }
        _notFound = 0;
        if (m.Code is null)
        {
            if (settled && AdjustGain(m, step)) return (false, AdjustingNote);
            return (false, "Reading the step code — hold still…");
        }
        if (m.Code != _code) return (false, "Waiting for the new pattern…");
        if (!settled) return (false, "Waiting for the LEDs to settle…");

        if (AdjustGain(m, step)) return (false, AdjustingNote);

        // The glow itself clips (very bright LEDs / close wall): the only lever left is the LED level.
        if (step.Kind != LoopStepKind.Ambient && m.GlowClippedSides >= 2)
        {
            if (++_glowClip >= 2 && _ledScale > MinLedScale + 1e-9)
            {
                _glowClip = 0;
                _ledScale = Math.Max(MinLedScale, _ledScale * 0.75);
                Log.Info(FormattableString.Invariant($"Calibration: glow clipped, LED scale → {_ledScale:0.00}"));
                SetLed(step);
                BumpEpoch(_levels);
                _note = "The glow is overexposed — dimming the LEDs a little…";
                return (false, _note);
            }
        }
        else _glowClip = 0;
        _glowWeakHint = m.WeakSides >= 2 && m.UsableSides < 2;

        bool used = step.Kind switch
        {
            LoopStepKind.White or LoopStepKind.WhiteRef => LiveWhite(m, step),
            LoopStepKind.Gray => LiveGray(m, step),
            LoopStepKind.Ambient => LiveAmbient(m),
            _ => LivePrimary(m, step),
        };
        if (used) _stepUsable++;
        return (used, _note);
    }

    private const string AdjustingNote = "Adjusting the screen brightness for your camera…";
    private const string WeakGlowHint = "The LED glow is too weak — turn the room lights off, or move closer to the screen.";

    /// <summary>Runs the screen-gain loop on a frame of the current pattern. True when the pattern changed.</summary>
    private bool AdjustGain(FrameMeasurement m, LoopStep step)
    {
        var d = ScreenGainControl.Decide(m, _gain);
        if (d.AtLimit)
            _gainHint = d.Level is { } l && l < ScreenGainControl.Low
                ? "The screen looks too dark to the camera — turn the monitor brightness up, or move closer."
                : "The screen is too bright for the camera — turn a small light on in the room, or move a bit further away.";
        else _gainHint = null;
        if (d.Action == GainAction.Hold) return false;
        var levels = ScreenLevels.ForGain(d.Gain, step.Kind == LoopStepKind.Primary ? step.Channel : -1);
        _gain = d.Gain;
        _stepGain[GainKey(step)] = _gain;
        if (levels.SameAs(_levels)) return false;
        Log.Info(FormattableString.Invariant($"Calibration: {step.Id} screen gain → {_gain:0.000} ({d.Reason}, level {d.Level:0.000})"));
        BumpEpoch(levels);
        _note = AdjustingNote;
        return true;
    }

    private bool TimedOut(LoopStep s) => s.Kind switch
    {
        LoopStepKind.Ambient => _stepTime >= AmbientTimeoutSeconds,
        LoopStepKind.White => _stepTime >= WhiteTimeoutSeconds || (_stepUsable == 0 && _stepTime >= StepTimeoutSeconds),
        _ => _stepTime >= StepTimeoutSeconds,
    };

    private static void AddOnce(List<string> list, string s)
    {
        if (!list.Contains(s)) list.Add(s);
    }

    private LiveFrameResult Result(FrameMeasurement m, bool used, string note, List<string> hints)
    {
        int delay = (int)Math.Max(0, Math.Ceiling((SettleSeconds - (_clock() - _switchTime)) * 1000));
        return new LiveFrameResult(m, used, note, hints, delay);
    }

    /// <summary>
    /// Ends the current step before it collected all frames (timeout or the phone's skip button) — never leaves the loop
    /// stuck: room light → assume none if nothing was measured; white → keep the best result so far; other steps → use
    /// what they have (≥ 1 good frame) or skip with a note.
    /// </summary>
    private void EndStepEarly(string reason)
    {
        var step = LivePlan[_step];
        string what;
        switch (step.Kind)
        {
            case LoopStepKind.Ambient:
                if (_ambientSamples.Count > 0)
                {
                    SetAmbient(_ambientSamples);
                    what = $"measured from {_ambientSamples.Count} frame(s)";
                }
                else
                {
                    _ambient.Clear();
                    what = "not measured — assuming a dark room";
                }
                break;
            case LoopStepKind.White:
                if (double.IsFinite(_bestErr))
                {
                    if (_lastErr > _bestErr * 1.5 + 1)
                    {
                        ApplyWb(_bestWb);
                        _whitePoint = _bestPoint;
                    }
                    what = FormattableString.Invariant($"kept the best result ({_bestErr:0.0} % off)");
                }
                else what = "could not be measured — kept the current white balance";
                break;
            case LoopStepKind.WhiteRef:
                if (_whiteRef.Count == 0 && _lastWhiteRefAny is { } last) _whiteRef.Add(last);
                what = _whiteRef.Count > 0 ? "used what was measured" : "skipped (nothing measured)";
                break;
            case LoopStepKind.Gray:
                if (_graySamples.Count > 0)
                {
                    AddGrayPoint(step);
                    what = $"used {_graySamples.Count} frame(s)";
                }
                else what = "skipped (nothing measured)";
                break;
            default:
                what = _primary[step.Channel].Count > 0 ? $"used {_primary[step.Channel].Count} frame(s)" : "skipped (nothing measured)";
                break;
        }
        string line = $"{step.Title}: {what} ({reason}).";
        _stepNotes.Add(line);
        Log.Info("Calibration: " + line);
        Advance();
        if (_mode == SessionMode.Live) _note = line + " Next: " + LivePlan[_step].Title + "…";
    }

    private bool LiveAmbient(FrameMeasurement m)
    {
        var a = SampleAmbient(m, _levels, 0);
        if (a is null)
        {
            _note = !m.RefOk ? "The small gray squares on the screen are not readable yet — hold still."
                : "Can't see the wall around the screen on at least 2 sides — keep some wall in the picture.";
            return false;
        }
        _ambientSamples.Add(a);
        _stepFrames++;
        int need = Math.Max(2, FramesPerStep - 1);
        _note = $"Measuring the room light: {_ambientSamples.Count}/{need}";
        if (_ambientSamples.Count >= need)
        {
            SetAmbient(_ambientSamples);
            Advance();
        }
        return true;
    }

    /// <summary>
    /// Camera → normalized units: the chosen reference square minus the screen black, divided by its displayed linear
    /// value (decoded from the code actually drawn), so frames at different screen gains are comparable.
    /// </summary>
    private static Rgb RefScale(FrameMeasurement m, ScreenLevels levels)
    {
        var n = m.RefNet;
        double lin = Math.Max(1e-6, levels.RefLinear(m.RefIndex));
        return new Rgb(n.R / lin, n.G / lin, n.B / lin);
    }

    /// <summary>Wall colors with the LEDs off (dark pixels included).</summary>
    private ColorSample? SampleAmbient(FrameMeasurement m, ScreenLevels levels, double level)
    {
        if (!m.RefOk) return null;
        var r = RefScale(m, levels);
        var sides = m.Sides.Where(s => s.Screen.Status == RegionStatus.Ok
                                       && s.Glow.Status is RegionStatus.Ok or RegionStatus.Weak or RegionStatus.TooDark
                                       && s.Glow.Samples >= 20)
            .Select(s => new SideColors(s.Side, FrameAnalyzer.Div(s.Glow.Mean, r), FrameAnalyzer.Div(FrameAnalyzer.Net(s.Screen.Mean, m.BlackRgb), r)))
            .ToList();
        return sides.Count < 2 ? null : new ColorSample(sides, _wb, _gamma, level);
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

    /// <summary>Share of the room light in the measured glow (median over sides, green channel), null without ambient data.</summary>
    private double? AmbientShare(ColorSample cs)
    {
        var shares = new List<double>(4);
        foreach (var c in cs.Sides)
            if (_ambient.TryGetValue(c.Side, out var a) && c.Glow.G > 1e-9) shares.Add(a.G / c.Glow.G);
        return shares.Count == 0 ? null : CalibrationAnalyzer.Median(shares);
    }

    private bool LiveWhite(FrameMeasurement m, LoopStep step)
    {
        double level = LedLevel(step);
        var cs = SampleColors(m, _levels, level);
        // ρ needs no reference square (both colors are in the same frame); without one only the room light can't be removed.
        var (n, rr, rb) = cs is null ? m.Rho() : RhoOf(cs);
        if (n < 2)
        {
            _note = ExplainUnusable(m);
            return false;
        }
        if (cs is not null) _roomLightHint = AmbientShare(cs) is > 0.5;
        (rr, rb) = ColorFit.CorrectQuantization(rr, rb, level, _wb, _gamma, _brightness);
        double err = CalibrationMath.ErrorPercent(rr, rb);
        _error = err;
        _initialError ??= err;
        _lastErr = err;
        if (err < _bestErr)
        {
            _bestErr = err;
            _bestWb = _wb; // the settings this frame was taken with
            _bestPoint = (rr, rb);
        }
        _stepFrames++;
        _whiteLevel = level;
        bool good = err < LiveTargetPercent;
        if (step.Kind == LoopStepKind.WhiteRef && cs is not null) _lastWhiteRefAny = cs;
        if (good)
        {
            _whiteGood++;
            if (step.Kind == LoopStepKind.WhiteRef && cs is not null) _whiteRef.Add(cs);
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
            if (step.Kind == LoopStepKind.WhiteRef && _whiteRef.Count == 0 && (cs ?? _lastWhiteRefAny) is { } last) _whiteRef.Add(last);
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
            BumpEpoch(_levels); // the frames that follow must show the new white
            _note = good ? $"Fine-tuning white… {err:0.0} %" : $"Matching white… {err:0.0} %";
        }
        else _note = $"White matches within {err:0.0} % — hold still…";
        return true;
    }

    private bool LiveGray(FrameMeasurement m, LoopStep step)
    {
        double level = LedLevel(step);
        var cs = SampleColors(m, _levels, level);
        var (n, rr, rb) = cs is null ? m.Rho() : RhoOf(cs);
        if (n < 2)
        {
            _note = ExplainUnusable(m);
            return false;
        }
        (rr, rb) = ColorFit.CorrectQuantization(rr, rb, level, _wb, _gamma, _brightness);
        _graySamples.Add((rr, rb));
        _stepFrames++;
        _error = CalibrationMath.ErrorPercent(rr, rb);
        _note = $"{step.Title}: {_graySamples.Count}/{FramesPerStep}";
        if (_graySamples.Count >= FramesPerStep || _stepFrames >= MaxStepFrames)
        {
            AddGrayPoint(step);
            Advance();
        }
        return true;
    }

    private void AddGrayPoint(LoopStep step)
    {
        _grayPoints.Add(new GrayPoint(LedLevel(step), CalibrationAnalyzer.Median(_graySamples.Select(s => s.R)),
            CalibrationAnalyzer.Median(_graySamples.Select(s => s.B))));
        Log.Info(FormattableString.Invariant($"Calibration: {step.Id} ρR={_grayPoints[^1].RhoR:0.000} ρB={_grayPoints[^1].RhoB:0.000} ({_graySamples.Count} frames)"));
    }

    private bool LivePrimary(FrameMeasurement m, LoopStep step)
    {
        var cs = SampleColors(m, _levels, LedLevel(step));
        if (cs is null)
        {
            _note = ExplainUnusable(m);
            return false;
        }
        var list = _primary[step.Channel];
        list.Add(cs);
        _stepFrames++;
        _note = $"{step.Title}: {list.Count}/{FramesPerStep}";
        if (list.Count >= FramesPerStep || _stepFrames >= MaxStepFrames) Advance();
        return true;
    }

    /// <summary>Per-side colors in normalized units (null if fewer than 2 usable sides or no usable reference square).</summary>
    private ColorSample? SampleColors(FrameMeasurement m, ScreenLevels levels, double level)
    {
        if (!m.RefOk) return null;
        var r = RefScale(m, levels);
        var sides = m.Sides.Where(s => s.Usable)
            .Select(s => new SideColors(s.Side, FrameAnalyzer.Div(s.Glow.Mean, r), FrameAnalyzer.Div(FrameAnalyzer.Net(s.Screen.Mean, m.BlackRgb), r)))
            .ToList();
        return sides.Count < 2 ? null : new ColorSample(sides, _wb, _gamma, level);
    }

    private void Advance()
    {
        var finished = LivePlan[_step];
        if (finished.Kind == LoopStepKind.Gray && (_step + 1 >= LivePlan.Length || LivePlan[_step + 1].Kind != LoopStepKind.Gray))
            FitGamma(_wb, _gamma);
        if (finished.Kind == LoopStepKind.Primary && finished.Channel == 2) FitMatrixLocked(_whiteRef, _primary);
        _stepGain[GainKey(finished)] = _gain;
        _step++;
        ResetStepState();
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
        var pts = new List<GrayPoint> { new(_whiteLevel, _whitePoint.R, _whitePoint.B, 2) };
        pts.AddRange(_grayPoints);
        var fit = ColorFit.FitGamma(pts);
        if (fit is not { } f)
        {
            Log.Info("Calibration: not enough gray levels for a gamma fit");
            return;
        }
        var (wb, gamma) = ColorFit.ApplyGammaFit(measuredWb, measuredGamma, f, _whiteLevel);
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
            double level = s.Level > 0 ? s.Level : 1;
            return (wNew / Math.Max(1e-6, wOld)) * Math.Pow(level, gNew - gOld);
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

    /// <summary>
    /// Things the user can act on. (No "tap to lower the exposure": web pages cannot steer the exposure on iOS; the
    /// screen gain loop handles over/under-exposure automatically.)
    /// </summary>
    internal static List<string> Hints(FrameMeasurement m, PointD[]? prev, bool ambientStep = false)
    {
        var h = new List<string>();
        const string frame = "Hold the phone so the whole screen and some wall around it are visible.";
        if (!m.Found)
        {
            h.Add(frame);
            return h;
        }
        if (m.ScreenFraction < 0.25) h.Add("Move a bit closer.");
        if (m.OutOfFrameSides >= 2) AddOnce(h, frame);
        if (!ambientStep && m.WeakSides >= 2 && m.UsableSides < 2) AddOnce(h, WeakGlowHint);
        if (prev is { Length: 4 } && m.Corners is { Length: 4 } c)
        {
            double diag = (c[2] - c[0]).Length, moved = 0;
            for (int i = 0; i < 4; i++) moved = Math.Max(moved, (c[i] - prev[i]).Length);
            if (diag > 0 && moved / diag > 0.03) h.Add("Hold still.");
        }
        if (m.Code is null && h.Count == 0) h.Add("Hold still.");
        return h;
    }

    private static string ExplainUnusable(FrameMeasurement m)
    {
        if (m.GlowClippedSides >= 2) return "The glow is overexposed — dimming the LEDs a little…";
        if (m.Sides.Count(s => s.Screen.Status == RegionStatus.Clipped) >= 2) return AdjustingNote;
        if (m.OutOfFrameSides >= 2) return "The glow around the screen is cut off — keep some wall around the screen in the picture.";
        if (m.WeakSides >= 2) return "The glow is too weak to measure — turn the room lights off, or move closer to the screen.";
        if (!m.RefOk) return "The small gray squares on the screen are not readable — avoid reflections on the screen and hold still.";
        return "Could not measure the glow on at least 2 sides — keep the wall around the screen in the picture.";
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
            int idx = code - VideoCodeBase;
            var step = VideoPlan[idx];
            var levels = VideoLevels(idx);
            var (n, rr, rb) = m.Rho();
            var cs = SampleColors(m, levels, step.Level);
            var amb = step.Kind == LoopStepKind.Ambient ? SampleAmbient(m, levels, 0) : null;
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
            double whiteLevel = VideoPlan[whiteIdx].Level;
            var wRhos = Rhos(white);
            if (wRhos.Count == 0) wRhos.Add((1, 1));
            var (wr, wb) = (CalibrationAnalyzer.Median(wRhos.Select(w => w.R)), CalibrationAnalyzer.Median(wRhos.Select(w => w.B)));
            (wr, wb) = ColorFit.CorrectQuantization(wr, wb, whiteLevel, wbRec, gammaRec, _brightness);
            _initialError = _error = CalibrationMath.ErrorPercent(wr, wb);
            _whitePoint = (wr, wb);
            _whiteLevel = whiteLevel;
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
        if (_mode == SessionMode.Live)
        {
            _summary = (_initialError is { } ie ? FormattableString.Invariant($"Camera: white was off by {ie:0.0} % before. ") : "Camera part done. ")
                       + string.Join(" ", _stepNotes);
            _summary = _summary.Trim();
        }
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
            double timeout = step is null || _mode != SessionMode.Live ? 0 : step.Kind switch
            {
                LoopStepKind.Ambient => AmbientTimeoutSeconds,
                LoopStepKind.White => _stepUsable == 0 ? StepTimeoutSeconds : WhiteTimeoutSeconds,
                _ => StepTimeoutSeconds,
            };
            return new SessionSnapshot(_mode, step?.Id, step?.Title, stepIndex, stepCount, progress, _error, _initialError, _note,
                _wb, _gamma, _matrix is not null, _matrixFull is not null, ColorFit.StrengthSteps[_strengthIdx],
                _eyeStage, _eyeT, _eyeM, _eyeTd, _eyeMd, _eyeColor, _eyeShowA, _votesA, _votesB, _votesSame,
                _videoReceived, _video.Count, videoPos, _summary, _code)
            {
                ScreenGain = _levels.Gain,
                LedScale = _ledScale,
                StepSeconds = _mode == SessionMode.Live ? _stepTime : 0,
                StepTimeoutSeconds = timeout,
                Notes = _stepNotes.ToArray(),
            };
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
    int VideoFramesReceived, int VideoFramesRecognized, double VideoPosition, string Summary, int Code)
{
    /// <summary>Screen gain of the current pattern (linear band level relative to full white).</summary>
    public double ScreenGain { get; init; }
    public double LedScale { get; init; } = 1;
    /// <summary>Frame time spent in the current live step and its timeout (0 = none).</summary>
    public double StepSeconds { get; init; }
    public double StepTimeoutSeconds { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The calibration's LED pattern: one color on all LEDs through the chosen color path.</summary>
internal sealed class CalibrationLedOverride : ILedOverride
{
    private sealed record Drive(float R, float G, float B, OverrideColorPath Path);

    private volatile Drive _d = new(1f, 1f, 1f, OverrideColorPath.WhiteBalanceNoMatrix);

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
