using System.Diagnostics;
using RambiBox.Core.Config;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace RambiBox.Core.Capture;

/// <summary>
/// Screen sampler based on DXGI Desktop Duplication.
/// <para>Per frame: AcquireNextFrame → copy into an own mip-mapped texture → ReleaseFrame immediately → the shared
/// <see cref="GpuFrameReducer"/> pipeline (GenerateMips in linear light → small mip → staging → fence → Map → linear
/// float image in desktop orientation, HDR tone-mapped → black bars → exact area-weighted zone averages).
/// Steady state is allocation free.</para>
/// <para>Failures (mode switches, secure desktop, driver resets, unplugged monitors) never throw: <see cref="Sample"/>
/// returns <see cref="SampleResult.Unavailable"/>, sleeps instead of spinning, and re-initializes with backoff.</para>
/// <para>An active Desktop Duplication session can make Chromium's DirectComposition presentation stall on some
/// systems, so <see cref="WgcScreenSampler"/> is preferred where available (see <see cref="SwitchingScreenSampler"/>).</para>
/// </summary>
public sealed class DxgiScreenSampler : IScreenSampler
{
    private const int MinUnavailableSleepMs = 16;
    private const int FirstBackoffMs = 250, MaxBackoffMs = 2000;
    private const double SlowCheckIntervalSeconds = 2.0;

    private static readonly Format[] DuplicationFormats = [GpuFrameReducer.FormatBgra, GpuFrameReducer.FormatFp16];

    private readonly object _sync = new();
    private bool _disposed;

    // ---- Configuration
    private string _monitorId = "";

    // ---- Device (owned by the reducer) / duplication
    private readonly GpuFrameReducer _reducer = new("Capture (Desktop Duplication): ");
    private IDXGIOutputDuplication? _dupl;
    private IntPtr _duplPtr;
    private DisplayTarget? _target;
    private bool _targetExact;
    private volatile MonitorInfo? _currentMonitor;

    // ---- Retry / logging
    private long _nextInitTs;
    private int _backoffMs;
    private bool _immediateRetryUsed;
    private string? _lastFailure;
    private bool _everCaptured;
    private long _nextSlowCheckTs;

    // ---- Diagnostics (tests / benchmark)
    internal double LastWorkMs { get; private set; }
    internal double LastMapWaitMs => _reducer.LastMapWaitMs;
    internal long FramesCaptured { get; private set; }
    internal bool IsFp16 => _reducer.IsFp16;
    internal float SdrWhiteNits => _reducer.SdrWhiteNits;
    internal (int Width, int Height) SmallImageSize => _reducer.SmallImageSize;
    internal int MipLevel => _reducer.MipLevel;

    public DxgiScreenSampler() { }

    // =====================================================================================================
    // Public API
    // =====================================================================================================

    public IReadOnlyList<MonitorInfo> GetMonitors() => DisplayTopology.GetMonitors();

    public MonitorInfo? CurrentMonitor => _currentMonitor;

    public (float Top, float Bottom, float Left, float Right) BlackBars => _reducer.BlackBars;

    public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection)
    {
        lock (_sync)
        {
            if (_disposed) return;
            monitorId ??= "";
            if (!string.Equals(monitorId, _monitorId, StringComparison.OrdinalIgnoreCase))
            {
                _monitorId = monitorId;
                if (_target is not null)
                {
                    var t = DisplayTopology.Resolve(DisplayTopology.GetTargets(), monitorId, out bool exact);
                    if (t is null || !string.Equals(t.Info.Id, _target.Info.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        TeardownDuplication();
                        _nextInitTs = 0; // switch right away
                    }
                    else _targetExact = exact || monitorId.Length == 0;
                }
            }
            _reducer.Configure(zones, blackBarDetection);
        }
    }

    public SampleResult Sample(Span<float> destRgb, int timeoutMs)
    {
        if (timeoutMs < 0) timeoutMs = 0;
        lock (_sync)
        {
            if (_disposed)
            {
                SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
            try
            {
                return SampleCore(destRgb, timeoutMs);
            }
            catch (Exception ex)
            {
                int hr = ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
                ReportFailure($"Capture error: {ex.GetType().Name}: {ex.Message}");
                if (IsDeviceLost(hr) || _reducer.Device is null) TeardownAll(); else TeardownDuplication();
                ScheduleRetry(immediate: false);
                SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
        }
    }

    public bool TryGetPreview(out byte[] bgra, out int width, out int height) => _reducer.TryGetPreview(out bgra, out width, out height);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            TeardownAll();
            _reducer.Dispose();
            _currentMonitor = null;
        }
    }

    // =====================================================================================================
    // Capture
    // =====================================================================================================

    private SampleResult SampleCore(Span<float> destRgb, int timeoutMs)
    {
        long callStart = Stopwatch.GetTimestamp();

        if (_dupl is null)
        {
            if (callStart < _nextInitTs || !TryInit())
            {
                SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
        }

        if (callStart >= _nextSlowCheckTs)
        {
            _nextSlowCheckTs = callStart + (long)(SlowCheckIntervalSeconds * Stopwatch.Frequency);
            if (SlowChecks()) return SampleResult.Unavailable; // switching monitors; next call re-inits immediately
        }

        // ---- Acquire (pointer-only updates are skipped until the timeout expires)
        int hr;
        IntPtr resource;
        uint wait = (uint)timeoutMs;
        while (true)
        {
            hr = NativeCom.AcquireNextFrame(_duplPtr, wait, out var info, out resource);
            if (hr == NativeCom.DXGI_ERROR_WAIT_TIMEOUT) return SampleResult.NoChange;
            if (hr < 0)
            {
                HandleHresult(hr, "AcquireNextFrame");
                if (_dupl is null && _nextInitTs > Stopwatch.GetTimestamp()) SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
            if (info.LastPresentTime != 0 || !_everCaptured) break;

            // Only the mouse moved (cursor is not part of the image) → not a new frame.
            NativeCom.Release(ref resource);
            NativeCom.ReleaseFrame(_duplPtr);
            double elapsed = Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
            if (elapsed >= timeoutMs) return SampleResult.NoChange;
            wait = (uint)Math.Max(0, timeoutMs - (int)elapsed);
        }

        long workStart = Stopwatch.GetTimestamp();

        // ---- Copy the desktop into our own texture and give the frame back to DWM right away
        IntPtr tex = IntPtr.Zero;
        bool ok = false;
        try
        {
            hr = NativeCom.QueryInterface(resource, NativeCom.IID_ID3D11Texture2D, out tex);
            if (hr >= 0)
            {
                NativeCom.GetTextureDesc(tex, out var desc);
                if (_reducer.EnsureResources(in desc, out var formatError))
                {
                    _reducer.CopyIn(tex);
                    ok = true;
                }
                else ReportFailure(formatError!);
            }
        }
        finally
        {
            NativeCom.Release(ref tex);
            NativeCom.Release(ref resource);
            NativeCom.ReleaseFrame(_duplPtr);
        }
        if (!ok)
        {
            // Teardown only after the frame was released (the duplication pointer must stay valid until then).
            if (hr < 0) HandleHresult(hr, "QueryInterface(ID3D11Texture2D)");
            else
            {
                TeardownDuplication(); // unsupported desktop format (reported above)
                ScheduleRetry(immediate: false);
            }
            SleepUnavailable(timeoutMs);
            return SampleResult.Unavailable;
        }

        // ---- GPU downscale, read back the small mip level, zone averages
        int mapHr = _reducer.Reduce(destRgb);
        if (mapHr < 0)
        {
            HandleHresult(mapHr, "Map");
            SleepUnavailable(timeoutMs);
            return SampleResult.Unavailable;
        }

        FramesCaptured++;
        _everCaptured = true;
        _backoffMs = 0;
        _immediateRetryUsed = false;
        if (_lastFailure is not null)
        {
            Log.Info($"Screen capture resumed on {_currentMonitor?.FriendlyName}");
            _lastFailure = null;
        }
        LastWorkMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        return SampleResult.NewFrame;
    }

    /// <summary>Periodic work (every 2 s): refresh SDR white level; switch to the configured monitor when it reappears.</summary>
    private bool SlowChecks()
    {
        if (_reducer.IsFp16) _reducer.RefreshSdrWhite();
        if (!_targetExact && _monitorId.Length > 0 && _target is not null)
        {
            var t = DisplayTopology.Resolve(DisplayTopology.GetTargets(1000), _monitorId, out bool exact);
            if (t is not null && exact && !string.Equals(t.Info.Id, _target.Info.Id, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Configured monitor {t.Info.FriendlyName} is available again; switching capture");
                TeardownDuplication();
                _nextInitTs = 0;
                return true;
            }
        }
        return false;
    }

    // =====================================================================================================
    // Initialization
    // =====================================================================================================

    private bool TryInit()
    {
        var targets = DisplayTopology.GetTargets(maxAgeMs: 0);
        var target = DisplayTopology.Resolve(targets, _monitorId, out bool exact);
        if (target is null)
        {
            ReportFailure("No monitor is attached to the desktop");
            ScheduleRetry(immediate: false);
            return false;
        }

        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput? output = null;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            (adapter, output) = DxgiOutputs.Find(factory, target.Info.GdiDeviceName);
            if (output is null || adapter is null)
            {
                DisplayTopology.Invalidate();
                ReportFailure($"No DXGI output for {target.Info.GdiDeviceName} ({target.Info.FriendlyName})");
                ScheduleRetry(immediate: false);
                return false;
            }

            if (_reducer.NeedsDevice(adapter.Description1.Luid))
            {
                TeardownAll();
                _reducer.CreateDevice(adapter);
            }

            int hr = Duplicate(output, out var dupl);
            if (hr < 0 || dupl is null)
            {
                HandleHresult(hr, "DuplicateOutput");
                return false;
            }
            _dupl = dupl;
            _duplPtr = dupl.NativePointer;
            var dd = dupl.Description;
            _reducer.Rotation = RotationMapper.FromDxgi((int)dd.Rotation);

            var info = DxgiOutputs.Describe(output, target.Info);
            bool hdr = info.IsHdr;

            bool changedTarget = _target is null || !string.Equals(_target.Info.Id, target.Info.Id, StringComparison.OrdinalIgnoreCase);
            _target = target;
            _reducer.Target = target;
            _targetExact = exact || _monitorId.Length == 0;
            _currentMonitor = info;
            _everCaptured = false;
            _nextSlowCheckTs = Stopwatch.GetTimestamp() + (long)(SlowCheckIntervalSeconds * Stopwatch.Frequency);
            if (changedTarget || _lastFailure is not null)
                Log.Info($"Capturing {info.FriendlyName} [{info.Id}] on {_reducer.AdapterName} with Desktop Duplication, {dd.ModeDescription.Format}" +
                         $"{(hdr ? " HDR" : "")}, rotation {_reducer.Rotation}{(_targetExact ? "" : " (configured monitor not found, using fallback)")}");
            return true;
        }
        catch (Exception ex)
        {
            int hr = ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
            HandleHresult(hr, $"Capture init ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
        finally
        {
            output?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
        }
    }

    private int Duplicate(IDXGIOutput output, out IDXGIOutputDuplication? dupl)
    {
        dupl = null;
        using (var o5 = output.QueryInterfaceOrNull<IDXGIOutput5>())
        {
            if (o5 is not null)
            {
                // DuplicateOutput1 requires per-monitor-v2 DPI awareness (at least for the calling thread).
                var prev = DisplayConfigNative.SetThreadDpiAwarenessContext(DisplayConfigNative.DpiPerMonitorV2);
                try
                {
                    dupl = o5.DuplicateOutput1(_reducer.Device!, DuplicationFormats);
                    return 0;
                }
                catch (SharpGenException ex) when (ex.ResultCode.Code is NativeCom.DXGI_ERROR_UNSUPPORTED or NativeCom.E_INVALIDARG
                                                       or unchecked((int)0x80004002) /* E_NOINTERFACE */)
                {
                    // Old driver / process not DPI aware enough: legacy duplication still works (SDR only).
                    LogOnce($"DuplicateOutput1 failed ({NativeCom.Describe(ex.ResultCode.Code)}), falling back to DuplicateOutput (no HDR)");
                }
                catch (SharpGenException ex)
                {
                    return ex.ResultCode.Code;
                }
                finally
                {
                    if (prev != IntPtr.Zero) DisplayConfigNative.SetThreadDpiAwarenessContext(prev);
                }
            }
        }
        using var o1 = output.QueryInterface<IDXGIOutput1>();
        try
        {
            dupl = o1.DuplicateOutput(_reducer.Device!);
            return 0;
        }
        catch (SharpGenException ex)
        {
            return ex.ResultCode.Code;
        }
    }

    // =====================================================================================================
    // Failure handling
    // =====================================================================================================

    internal static bool IsDeviceLost(int hr) => hr is NativeCom.DXGI_ERROR_DEVICE_REMOVED or NativeCom.DXGI_ERROR_DEVICE_RESET
        or NativeCom.DXGI_ERROR_DEVICE_HUNG or NativeCom.DXGI_ERROR_DRIVER_INTERNAL_ERROR;

    private void HandleHresult(int hr, string where)
    {
        switch (hr)
        {
            case NativeCom.DXGI_ERROR_ACCESS_LOST:
            case NativeCom.DXGI_ERROR_MODE_CHANGE_IN_PROGRESS:
            case NativeCom.DXGI_ERROR_INVALID_CALL:
                // Mode change, fullscreen transition, desktop switch: recreate the duplication right away.
                ReportFailure($"{where}: {NativeCom.Describe(hr)} (display mode / desktop changed)");
                TeardownDuplication();
                DisplayTopology.Invalidate();
                ScheduleRetry(immediate: true);
                break;
            case NativeCom.E_ACCESSDENIED:
            case NativeCom.DXGI_ERROR_ACCESS_DENIED:
            case NativeCom.DXGI_ERROR_SESSION_DISCONNECTED:
            case NativeCom.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE:
                // Secure desktop (UAC / lock screen / Ctrl+Alt+Del), remote session, or too many duplications.
                ReportFailure($"{where}: {NativeCom.Describe(hr)} (secure desktop or capture not permitted right now)");
                TeardownDuplication();
                ScheduleRetry(immediate: false);
                break;
            default:
                if (IsDeviceLost(hr) && _reducer.Device is { } device)
                {
                    var reason = device.DeviceRemovedReason;
                    ReportFailure($"{where}: {NativeCom.Describe(hr)} (GPU device lost, reason {NativeCom.Describe(reason.Code)})");
                }
                else ReportFailure($"{where}: {NativeCom.Describe(hr)}");
                TeardownAll();
                DisplayTopology.Invalidate();
                ScheduleRetry(immediate: false);
                break;
        }
    }

    private void ScheduleRetry(bool immediate)
    {
        long now = Stopwatch.GetTimestamp();
        if (immediate && !_immediateRetryUsed)
        {
            // One immediate retry after each successful stretch (a mode switch usually completes instantly);
            // repeated failures back off 250 ms → 500 → 1000 → 2000 ms.
            _immediateRetryUsed = true;
            _nextInitTs = now;
            return;
        }
        _backoffMs = _backoffMs == 0 ? FirstBackoffMs : Math.Min(_backoffMs * 2, MaxBackoffMs);
        _nextInitTs = now + _backoffMs * Stopwatch.Frequency / 1000;
    }

    /// <summary>Logs a failure only when it differs from the previous one (never every frame).</summary>
    private void ReportFailure(string message)
    {
        if (message == _lastFailure) return;
        _lastFailure = message;
        Log.Warn("Screen capture: " + message);
    }

    private string? _lastOnce;
    private void LogOnce(string message)
    {
        if (message == _lastOnce) return;
        _lastOnce = message;
        Log.Warn("Screen capture: " + message);
    }

    /// <summary>Never spin while capture is impossible: sleep for the timeout (at least a few ms).</summary>
    private static void SleepUnavailable(int timeoutMs) => Thread.Sleep(Math.Max(timeoutMs, MinUnavailableSleepMs));

    // =====================================================================================================
    // Teardown
    // =====================================================================================================

    private void TeardownDuplication()
    {
        _duplPtr = IntPtr.Zero;
        try { _dupl?.Dispose(); } catch { /* already gone */ }
        _dupl = null;
    }

    private void TeardownAll()
    {
        TeardownDuplication();
        _reducer.ReleaseDevice();
    }
}

/// <summary>DXGI output lookup shared by the samplers.</summary>
internal static class DxgiOutputs
{
    /// <summary>Finds the adapter + output that drives the GDI display <paramref name="gdiDeviceName"/> (both null if none).</summary>
    public static (IDXGIAdapter1? Adapter, IDXGIOutput? Output) Find(IDXGIFactory1 factory, string gdiDeviceName)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var ad).Success; a++)
        {
            for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
            {
                var d = op.Description;
                if (d.AttachedToDesktop && string.Equals(d.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    return (ad, op);
                op.Dispose();
            }
            ad.Dispose();
        }
        return (null, null);
    }

    /// <summary>Monitor info with the output's current desktop rectangle and HDR state (IDXGIOutput6 color space).</summary>
    public static MonitorInfo Describe(IDXGIOutput output, MonitorInfo fromTopology)
    {
        bool hdr = fromTopology.IsHdr;
        using (var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
            if (o6 is not null) hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        var bounds = output.Description.DesktopCoordinates;
        return fromTopology with
        {
            X = bounds.Left, Y = bounds.Top, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top,
            IsHdr = hdr,
        };
    }
}
