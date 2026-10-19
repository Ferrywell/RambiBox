using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Capture;

/// <summary>A sampler whose capture API can be chosen at runtime (see <see cref="CaptureSettings.Method"/>).</summary>
public interface ICaptureMethodSelector
{
    /// <summary>Requests a capture method. Any thread; takes effect on the engine thread at the next Configure/Sample.</summary>
    void SetCaptureMethod(CaptureMethod method);

    /// <summary>The API actually capturing (<see cref="CaptureMethod.WindowsGraphicsCapture"/> or
    /// <see cref="CaptureMethod.DesktopDuplication"/>); null before the engine configured the sampler.</summary>
    CaptureMethod? EffectiveMethod { get; }

    /// <summary>True when Windows Graphics Capture was wanted but Desktop Duplication is used (unsupported / failing).</summary>
    bool IsFallback { get; }
}

/// <summary>
/// Owns the active capture backend (<see cref="WgcScreenSampler"/> or <see cref="DxgiScreenSampler"/>) and switches live
/// when the capture method setting changes. The switch (and disposal of the old backend) happens on the engine thread
/// inside <see cref="Configure"/> / <see cref="Sample"/>; <see cref="GetMonitors"/>, <see cref="TryGetPreview"/>,
/// <see cref="BlackBars"/> and <see cref="CurrentMonitor"/> stay thread-safe.
/// <para>Automatic fallback: WGC unsupported, or failing to start <see cref="MaxWgcInitFailures"/> times in a row →
/// Desktop Duplication (logged, shown in the UI) until the setting changes or the app restarts.</para>
/// <para>Also logs (rate-limited) every Sample() call that blocks much longer than its timeout, for both backends.</para>
/// </summary>
public sealed class SwitchingScreenSampler : IScreenSampler, ICaptureMethodSelector
{
    internal const int MaxWgcInitFailures = 5;
    /// <summary>A Sample() call taking this much longer than its timeout is logged as a capture stall.</summary>
    internal const double SlowSampleMarginMs = 500;
    private const int MinUnavailableSleepMs = 16;

    private readonly Func<CaptureMethod, IScreenSampler> _create;
    private readonly Func<bool> _wgcSupported;
    private readonly Func<IReadOnlyList<MonitorInfo>> _getMonitors;
    private readonly object _engineLock = new(); // Configure / Sample / switching
    private readonly object _switchLock = new(); // swapping _current vs. the UI-thread getters
    private readonly RateLimitedLog _slowSampleLog = new(TimeSpan.FromMinutes(1));

    private volatile IScreenSampler? _current;
    private CaptureMethod _currentKind;
    private volatile int _requested = (int)CaptureMethod.Auto;
    private CaptureMethod _applied;
    private bool _hasApplied, _wgcFailed, _disposed;
    private volatile int _effective = -1;
    private volatile bool _isFallback;

    private string _monitorId = "";
    private IReadOnlyList<LedZone> _zones = [];
    private bool _blackBarDetection, _configured;

    public SwitchingScreenSampler() : this(CreateDefault, WgcScreenSampler.IsSupported, () => DisplayTopology.GetMonitors()) { }

    internal SwitchingScreenSampler(Func<CaptureMethod, IScreenSampler> create, Func<bool> wgcSupported,
        Func<IReadOnlyList<MonitorInfo>> getMonitors)
    {
        _create = create;
        _wgcSupported = wgcSupported;
        _getMonitors = getMonitors;
    }

    private static IScreenSampler CreateDefault(CaptureMethod method) =>
        method == CaptureMethod.WindowsGraphicsCapture ? new WgcScreenSampler() : new DxgiScreenSampler();

    /// <summary>
    /// Which API to use: Desktop Duplication when asked for; otherwise WGC when supported and not failing, else
    /// Desktop Duplication as a fallback.
    /// </summary>
    internal static (CaptureMethod Method, bool Fallback) Resolve(CaptureMethod requested, bool wgcSupported, bool wgcFailed)
    {
        if (requested == CaptureMethod.DesktopDuplication) return (CaptureMethod.DesktopDuplication, false);
        if (wgcSupported && !wgcFailed) return (CaptureMethod.WindowsGraphicsCapture, false);
        return (CaptureMethod.DesktopDuplication, true);
    }

    // =====================================================================================================
    // ICaptureMethodSelector
    // =====================================================================================================

    public void SetCaptureMethod(CaptureMethod method) => _requested = (int)method;

    public CaptureMethod RequestedMethod => (CaptureMethod)_requested;

    public CaptureMethod? EffectiveMethod => _effective < 0 ? null : (CaptureMethod)_effective;

    public bool IsFallback => _isFallback;

    /// <summary>The active backend (tests).</summary>
    internal IScreenSampler? Current => _current;

    // =====================================================================================================
    // IScreenSampler
    // =====================================================================================================

    public IReadOnlyList<MonitorInfo> GetMonitors() => _getMonitors();

    public MonitorInfo? CurrentMonitor => _current?.CurrentMonitor;

    public (float Top, float Bottom, float Left, float Right) BlackBars
    {
        get { lock (_switchLock) return _current?.BlackBars ?? default; }
    }

    public bool TryGetPreview(out byte[] bgra, out int width, out int height)
    {
        lock (_switchLock)
        {
            if (_current is { } c) return c.TryGetPreview(out bgra, out width, out height);
        }
        bgra = [];
        width = height = 0;
        return false;
    }

    public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection)
    {
        lock (_engineLock)
        {
            if (_disposed) return;
            _monitorId = monitorId ?? "";
            _zones = zones.ToArray();
            _blackBarDetection = blackBarDetection;
            _configured = true;
            if (!EnsureBackend()) // a fresh backend was configured already
                _current?.Configure(_monitorId, _zones, _blackBarDetection);
        }
    }

    public SampleResult Sample(Span<float> destRgb, int timeoutMs)
    {
        IScreenSampler? cur;
        lock (_engineLock)
        {
            if (!_disposed) EnsureBackend();
            cur = _disposed ? null : _current;
        }
        if (cur is null)
        {
            Thread.Sleep(Math.Max(timeoutMs, MinUnavailableSleepMs));
            return SampleResult.Unavailable;
        }

        long t0 = Stopwatch.GetTimestamp();
        SampleResult r;
        try
        {
            r = cur.Sample(destRgb, timeoutMs);
        }
        catch (Exception ex) // backends never throw; belt and braces for the engine
        {
            if (_slowSampleLog.ShouldLog(out _)) Log.Error("Screen capture: unexpected sampler exception", ex);
            Thread.Sleep(Math.Max(timeoutMs, MinUnavailableSleepMs));
            r = SampleResult.Unavailable;
        }
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > Math.Max(0, timeoutMs) + SlowSampleMarginMs && _slowSampleLog.ShouldLog(out int suppressed))
            Log.Warn($"Screen capture: Sample() blocked {ms:0} ms (timeout {timeoutMs} ms, {Describe(_currentKind)}, result {r})" +
                     (suppressed > 0 ? $"; {suppressed} more slow calls since the last report" : ""));

        if (_currentKind == CaptureMethod.WindowsGraphicsCapture && cur is ICaptureBackendHealth h
            && h.ConsecutiveInitFailures >= MaxWgcInitFailures)
        {
            lock (_engineLock)
            {
                if (!_wgcFailed && !_disposed && ReferenceEquals(cur, _current))
                {
                    _wgcFailed = true;
                    Log.Warn($"Screen capture: Windows Graphics Capture failed to start {h.ConsecutiveInitFailures} times in a row; " +
                             "falling back to Desktop Duplication");
                    EnsureBackend();
                }
            }
        }
        return r;
    }

    public void Dispose()
    {
        IScreenSampler? old;
        lock (_engineLock)
        {
            if (_disposed) return;
            _disposed = true;
            lock (_switchLock)
            {
                old = _current;
                _current = null;
            }
        }
        try { old?.Dispose(); } catch (Exception ex) { Log.Warn($"Screen capture: dispose failed: {ex.Message}"); }
    }

    // =====================================================================================================
    // Switching (engine thread, under _engineLock)
    // =====================================================================================================

    /// <summary>Creates / replaces the backend if needed. Returns true when a new backend was created (and configured).</summary>
    private bool EnsureBackend()
    {
        var requested = (CaptureMethod)_requested;
        if (!_hasApplied || requested != _applied)
        {
            if (_hasApplied) Log.Info($"Screen capture: method set to {requested}");
            _applied = requested;
            _hasApplied = true;
            _wgcFailed = false; // a new choice gives WGC another chance
        }
        bool supported = requested != CaptureMethod.DesktopDuplication && SafeWgcSupported();
        var (method, fallback) = Resolve(requested, supported, _wgcFailed);
        _isFallback = fallback;
        if (_current is not null && method == _currentKind) return false;

        if (fallback && !supported)
            Log.Warn("Screen capture: Windows Graphics Capture is not supported on this system; using Desktop Duplication");

        IScreenSampler next;
        try
        {
            next = _create(method);
        }
        catch (Exception ex) when (method == CaptureMethod.WindowsGraphicsCapture)
        {
            Log.Warn($"Screen capture: could not create the Windows Graphics Capture sampler ({ex.Message}); using Desktop Duplication");
            _wgcFailed = true;
            _isFallback = true;
            method = CaptureMethod.DesktopDuplication;
            if (_current is not null && _currentKind == method) return false;
            next = _create(method);
        }
        if (_configured) next.Configure(_monitorId, _zones, _blackBarDetection);

        IScreenSampler? old;
        lock (_switchLock)
        {
            old = _current;
            _current = next;
        }
        _currentKind = method;
        _effective = (int)method;
        Log.Info($"Screen capture method: {Describe(method)}{(_isFallback ? " (fallback)" : "")}" +
                 (old is null ? "" : " — switched live"));
        try { old?.Dispose(); } catch (Exception ex) { Log.Warn($"Screen capture: disposing the previous sampler failed: {ex.Message}"); }
        return true;
    }

    private bool SafeWgcSupported()
    {
        try { return _wgcSupported(); }
        catch { return false; }
    }

    internal static string Describe(CaptureMethod m) => m switch
    {
        CaptureMethod.WindowsGraphicsCapture => "Windows Graphics Capture",
        CaptureMethod.DesktopDuplication => "Desktop Duplication",
        _ => "Automatic",
    };
}
