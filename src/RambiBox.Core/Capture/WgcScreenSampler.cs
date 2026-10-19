using System.Diagnostics;
using System.Runtime.InteropServices;
using RambiBox.Core.Config;
using SharpGen.Runtime;
using Vortice.DXGI;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RambiBox.Core.Capture;

/// <summary>Lets <see cref="SwitchingScreenSampler"/> see whether a backend keeps failing to start.</summary>
internal interface ICaptureBackendHealth
{
    /// <summary>Back-to-back initialization failures specific to this capture method (reset by the first frame).</summary>
    int ConsecutiveInitFailures { get; }
}

/// <summary>
/// Screen sampler based on Windows.Graphics.Capture (the API behind the Snipping Tool / Teams screen sharing).
/// <para>Why: an active DXGI Desktop Duplication session (AmbiBox, older RambiBox) makes Chromium browsers stall in their
/// DirectComposition presentation path on some systems. WGC hands frames out through DWM's own capture path instead.</para>
/// <para>Per frame: FrameArrived (free-threaded pool) signals an event → <see cref="Sample"/> takes the newest queued
/// frame (older ones are returned at once) → copies the surface into the shared <see cref="GpuFrameReducer"/> pipeline →
/// returns the frame to the pool immediately → GenerateMips → small mip → fence → Map → zones (identical math to
/// <see cref="DxgiScreenSampler"/>). The per-frame WinRT calls go through the ABI v-tables (<see cref="WgcInterop"/>), so
/// steady state allocates nothing on the engine thread.</para>
/// <para>Session: cursor excluded, no yellow capture border (where Windows allows it), MinUpdateInterval on 24H2+.
/// HDR monitors are captured as FP16 scRGB and tone-mapped like the DXGI path; SDR as BGRA8.</para>
/// <para>Failures never throw out of <see cref="Sample"/>: device loss, monitor removal (item Closed), resolution / HDR /
/// rotation changes and the secure desktop are handled with re-initialization and 250 ms → 2 s backoff.</para>
/// </summary>
public sealed class WgcScreenSampler : IScreenSampler, ICaptureBackendHealth
{
    private const int MinUnavailableSleepMs = 16;
    private const int FirstBackoffMs = 250, MaxBackoffMs = 2000;
    private const double SlowCheckIntervalSeconds = 2.0;
    /// <summary>WGC delivers the current picture right after StartCapture; no first frame by then = broken session.</summary>
    internal const int FirstFrameTimeoutMs = 3000;
    /// <summary>Without frames for this long, check (at most once per second) whether the secure desktop is up.</summary>
    private const int SecureDesktopCheckMs = 1000;
    private const int AccessRequestTimeoutMs = 2000;
    private static readonly TimeSpan GapSummaryInterval = TimeSpan.FromMinutes(1);
    private const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string LogPrefix = "Capture (Windows Graphics Capture): ";

    private static readonly Lazy<bool> Supported = new(DetectSupport);
    private static readonly object AccessLock = new();
    private static string? _borderlessAccess; // result of GraphicsCaptureAccess.RequestAccessAsync(Borderless), once per process

    private readonly object _sync = new();
    private volatile bool _disposed;

    // ---- Configuration
    private string _monitorId = "";

    // ---- Device (owned by the reducer) + WinRT objects
    private readonly GpuFrameReducer _reducer = new(LogPrefix);
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private IntPtr _poolPtr; // IDirect3D11CaptureFramePool* (own reference) for the allocation-free per-frame calls
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private DirectXPixelFormat _poolFormat;
    private int _poolBuffers;
    private volatile bool _itemClosed;
    private readonly AutoResetEvent _frameEvent = new(false);
    private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> _onFrameArrived;
    private readonly TypedEventHandler<GraphicsCaptureItem, object> _onItemClosed;

    // ---- Target
    private DisplayTarget? _target;
    private bool _targetExact;
    private bool _hdr;
    private volatile MonitorInfo? _currentMonitor;

    // ---- Retry / state
    private long _nextInitTs;
    private int _backoffMs;
    private bool _immediateRetryUsed;
    private string? _lastFailure;
    private long _nextSlowCheckTs;
    private long _sessionStartTs, _lastFrameTs, _nextDesktopCheckTs;
    private bool _gotFirstFrame, _secureDesktop;
    private int _consecutiveInitFailures;
    private string? _lastSessionOptions;

    // ---- Diagnostics
    private readonly DeliveryGapTracker _gaps = new();
    private long _nextGapSummaryTs;
    private long _framesArrived, _callbackAllocated;

    public WgcScreenSampler()
    {
        _onFrameArrived = OnFrameArrived;
        _onItemClosed = OnItemClosed;
    }

    /// <summary>True when Windows.Graphics.Capture can be used (Windows 10 1903+ with a capable GPU/driver, not Server Core).</summary>
    public static bool IsSupported() => Supported.Value;

    private static bool DetectSupport()
    {
        try
        {
            return ApiInformation.IsTypePresent(SessionClass) && GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogPrefix}support check failed: {ex.Message}");
            return false;
        }
    }

    // ---- Knobs / diagnostics for tests and the benchmark (set before the first Sample)
    internal int MinUpdateIntervalMs { get; set; } = WgcCapturePolicy.DefaultMinUpdateIntervalMs;
    internal bool BorderRequired { get; set; }
    internal bool CursorCaptureEnabled { get; set; }
    internal double LastWorkMs { get; private set; }
    internal double LastMapWaitMs => _reducer.LastMapWaitMs;
    internal long FramesCaptured { get; private set; }
    internal long FramesDropped { get; private set; }
    internal long FramesArrived => Interlocked.Read(ref _framesArrived);
    /// <summary>Managed bytes allocated on the FrameArrived callback threads (event marshalling), see <see cref="OnFrameArrived"/>.</summary>
    internal long CallbackAllocatedBytes => Interlocked.Read(ref _callbackAllocated);
    internal bool IsFp16 => _reducer.IsFp16;
    internal float SdrWhiteNits => _reducer.SdrWhiteNits;
    internal (int Width, int Height) SmallImageSize => _reducer.SmallImageSize;
    internal int MipLevel => _reducer.MipLevel;
    internal SurfaceRotation Rotation => _reducer.Rotation;
    internal string? SessionOptions => _lastSessionOptions;
    internal int PoolBuffers => _poolBuffers;
    internal DirectXPixelFormat PoolFormat => _poolFormat;
    public int ConsecutiveInitFailures => Volatile.Read(ref _consecutiveInitFailures);

    // =====================================================================================================
    // Public API
    // =====================================================================================================

    public IReadOnlyList<MonitorInfo> GetMonitors() => DisplayTopology.GetMonitors();

    public MonitorInfo? CurrentMonitor => _currentMonitor;

    public (float Top, float Bottom, float Left, float Right) BlackBars => _reducer.BlackBars;

    public bool TryGetPreview(out byte[] bgra, out int width, out int height) => _reducer.TryGetPreview(out bgra, out width, out height);

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
                        TeardownSession();
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
            long callStart = Stopwatch.GetTimestamp();
            try
            {
                return SampleCore(destRgb, timeoutMs, callStart);
            }
            catch (Exception ex)
            {
                HandleError(ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult, $"Capture error ({ex.GetType().Name}: {ex.Message})");
                SleepRemaining(callStart, timeoutMs);
                return SampleResult.Unavailable;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            TeardownAll();
            _reducer.Dispose();
            _frameEvent.Dispose();
            _currentMonitor = null;
        }
    }

    // =====================================================================================================
    // Capture
    // =====================================================================================================

    private SampleResult SampleCore(Span<float> destRgb, int timeoutMs, long callStart)
    {
        if (_session is null)
        {
            if (callStart < _nextInitTs || !TryInit())
            {
                SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
        }

        if (_itemClosed)
        {
            // The monitor went away (unplugged, turned off with DP deep sleep, topology change) or DWM restarted.
            ReportFailure("capture item closed (monitor removed or display configuration changed)");
            TeardownSession();
            DisplayTopology.Invalidate();
            ScheduleRetry(immediate: true);
            SleepUnavailable(timeoutMs);
            return SampleResult.Unavailable;
        }

        if (callStart >= _nextSlowCheckTs)
        {
            _nextSlowCheckTs = callStart + (long)(SlowCheckIntervalSeconds * Stopwatch.Frequency);
            if (SlowChecks()) return SampleResult.Unavailable; // switching monitors; next call re-inits immediately
        }

        // ---- Newest queued frame; otherwise wait (on FrameArrived, never spinning) up to the timeout
        IntPtr frame = TakeNewestFrame(out int hr);
        while (frame == IntPtr.Zero && hr >= 0)
        {
            int remaining = timeoutMs - (int)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
            if (remaining <= 0 || !_frameEvent.WaitOne(remaining)) break;
            if (_itemClosed) break;
            frame = TakeNewestFrame(out hr);
        }
        if (hr < 0)
        {
            HandleError(hr, "TryGetNextFrame");
            SleepRemaining(callStart, timeoutMs);
            return SampleResult.Unavailable;
        }
        if (frame == IntPtr.Zero) return NoFrame(callStart, timeoutMs);

        long workStart = Stopwatch.GetTimestamp();
        bool ok = false;
        string? formatError = null;
        IntPtr surface = IntPtr.Zero, tex = IntPtr.Zero;
        try
        {
            hr = WgcInterop.GetContentSize(frame, out var size);
            if (hr >= 0 && (size.Width != _poolSize.Width || size.Height != _poolSize.Height))
            {
                // Resolution / orientation change: resize the pool, drop this frame (its buffer has the old size).
                OnContentSizeChanged(size);
                return SampleResult.NoChange;
            }
            if (hr >= 0) hr = WgcInterop.GetSurface(frame, out surface);
            if (hr >= 0) hr = WgcInterop.GetTexture(surface, out tex);
            if (hr >= 0)
            {
                NativeCom.GetTextureDesc(tex, out var desc);
                if (_reducer.EnsureResources(in desc, out formatError))
                {
                    _reducer.CopyIn(tex);
                    ok = true;
                }
            }
        }
        finally
        {
            // Give the buffer back to the pool right away (DWM can render the next frame into it).
            NativeCom.Release(ref tex);
            NativeCom.Release(ref surface);
            WgcInterop.Close(frame);
            NativeCom.Release(ref frame);
        }
        if (!ok)
        {
            if (formatError is not null)
            {
                ReportFailure(formatError);
                TeardownSession();
                ScheduleRetry(immediate: false);
            }
            else HandleError(hr, "frame surface");
            SleepRemaining(callStart, timeoutMs);
            return SampleResult.Unavailable;
        }

        int mapHr = _reducer.Reduce(destRgb);
        if (mapHr < 0)
        {
            HandleError(mapHr, "Map");
            SleepRemaining(callStart, timeoutMs);
            return SampleResult.Unavailable;
        }

        FramesCaptured++;
        _lastFrameTs = Stopwatch.GetTimestamp();
        _secureDesktop = false;
        if (!_gotFirstFrame)
        {
            _gotFirstFrame = true;
            Volatile.Write(ref _consecutiveInitFailures, 0);
        }
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

    /// <summary>Takes the newest queued frame and returns all older ones to the pool immediately.</summary>
    private IntPtr TakeNewestFrame(out int hr)
    {
        IntPtr newest = IntPtr.Zero;
        while (true)
        {
            hr = WgcInterop.TryGetNextFrame(_poolPtr, out var f);
            if (hr < 0)
            {
                if (newest != IntPtr.Zero) { WgcInterop.Close(newest); NativeCom.Release(ref newest); }
                return IntPtr.Zero;
            }
            if (f == IntPtr.Zero) return newest;
            if (newest != IntPtr.Zero)
            {
                WgcInterop.Close(newest);
                NativeCom.Release(ref newest);
                FramesDropped++;
            }
            newest = f;
        }
    }

    /// <summary>
    /// No frame within the timeout. Normally just "the screen didn't change" (WGC only delivers changes). Two exceptions:
    /// the session never delivered its first frame (broken → counts as an init failure), or the secure desktop is up.
    /// </summary>
    private SampleResult NoFrame(long callStart, int timeoutMs)
    {
        long now = Stopwatch.GetTimestamp();
        long sinceFrame = _gotFirstFrame ? _lastFrameTs : _sessionStartTs;
        double silentMs = Stopwatch.GetElapsedTime(sinceFrame, now).TotalMilliseconds;
        if (silentMs < SecureDesktopCheckMs) return SampleResult.NoChange;

        if (now >= _nextDesktopCheckTs)
        {
            _nextDesktopCheckTs = now + Stopwatch.Frequency * SecureDesktopCheckMs / 1000;
            _secureDesktop = !InputDesktop.IsDefaultDesktopActive();
            if (_secureDesktop) ReportFailure("secure desktop active (lock screen / UAC / Ctrl+Alt+Del) – no frames");
        }
        if (_secureDesktop)
        {
            SleepRemaining(callStart, timeoutMs);
            return SampleResult.Unavailable;
        }
        if (!_gotFirstFrame && silentMs > FirstFrameTimeoutMs)
        {
            Interlocked.Increment(ref _consecutiveInitFailures);
            ReportFailure($"no frame within {FirstFrameTimeoutMs} ms after starting the capture session");
            TeardownSession();
            ScheduleRetry(immediate: false);
            SleepRemaining(callStart, timeoutMs);
            return SampleResult.Unavailable;
        }
        return SampleResult.NoChange;
    }

    private void OnContentSizeChanged(SizeInt32 size)
    {
        Log.Info($"{LogPrefix}content size changed {_poolSize.Width}×{_poolSize.Height} → {size.Width}×{size.Height}; resizing the frame pool");
        RefreshTarget();
        if (_session is null) return; // monitor changed → full re-init
        RecreatePool(size, WgcCapturePolicy.PixelFormat(_hdr));
    }

    /// <summary>Re-reads the monitor's mode (position, size, rotation, HDR) after a display change.</summary>
    private void RefreshTarget()
    {
        DisplayTopology.Invalidate();
        var t = DisplayTopology.Resolve(DisplayTopology.GetTargets(0), _monitorId, out _);
        if (t is null || _target is null || !string.Equals(t.Info.Id, _target.Info.Id, StringComparison.OrdinalIgnoreCase))
        {
            TeardownSession();
            ScheduleRetry(immediate: true);
            return;
        }
        _target = t;
        _reducer.Target = t;
        _hdr = t.Info.IsHdr;
        _currentMonitor = t.Info;
        _reducer.Rotation = WgcCapturePolicy.Rotation(_item!.Size.Width, _item.Size.Height, t.Info.Width, t.Info.Height,
            RotationMapper.FromDxgi(t.DxgiRotation));
    }

    private void RecreatePool(SizeInt32 size, DirectXPixelFormat format)
    {
        int buffers = WgcCapturePolicy.BufferCount(size.Width, size.Height, format == DirectXPixelFormat.R16G16B16A16Float);
        _pool!.Recreate(_winrtDevice, format, buffers, size);
        _poolSize = size;
        _poolFormat = format;
        _poolBuffers = buffers;
    }

    /// <summary>Periodic work (every 2 s): HDR toggles, SDR white level, configured monitor back, delivery-gap summary.</summary>
    private bool SlowChecks()
    {
        if (_target is not null)
        {
            bool hdr = DisplayConfigNative.IsHdrActive(_target.AdapterId, _target.TargetId);
            if (hdr != _hdr)
            {
                Log.Info($"{LogPrefix}HDR turned {(hdr ? "on" : "off")} on {_target.Info.FriendlyName}; switching the frame format");
                _hdr = hdr;
                if (_currentMonitor is { } m) _currentMonitor = m with { IsHdr = hdr };
                RecreatePool(_poolSize, WgcCapturePolicy.PixelFormat(hdr));
            }
        }
        if (_reducer.IsFp16) _reducer.RefreshSdrWhite();

        long now = Stopwatch.GetTimestamp();
        if (now >= _nextGapSummaryTs)
        {
            _nextGapSummaryTs = now + (long)(GapSummaryInterval.TotalSeconds * Stopwatch.Frequency);
            var (count, max, total) = _gaps.Take();
            if (count > 0)
                Log.Warn($"{LogPrefix}{count} frame delivery stall(s) > {DeliveryGapTracker.GapMs:0} ms during steady delivery " +
                         $"in the last minute (longest {max:0} ms, total {total:0} ms)");
        }

        if (!_targetExact && _monitorId.Length > 0 && _target is not null)
        {
            var t = DisplayTopology.Resolve(DisplayTopology.GetTargets(1000), _monitorId, out bool exact);
            if (t is not null && exact && !string.Equals(t.Info.Id, _target.Info.Id, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Configured monitor {t.Info.FriendlyName} is available again; switching capture");
                TeardownSession();
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
        if (!IsSupported())
        {
            Interlocked.Increment(ref _consecutiveInitFailures);
            ReportFailure("Windows Graphics Capture is not supported on this system");
            ScheduleRetry(immediate: false);
            return false;
        }

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
        MonitorInfo info;
        IntPtr hmonitor;
        SurfaceRotation dxgiRotation;
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
            // Same device placement as Desktop Duplication: the adapter that scans out this monitor.
            if (_reducer.NeedsDevice(adapter.Description1.Luid))
            {
                TeardownAll();
                _reducer.CreateDevice(adapter);
            }
            var od = output.Description;
            hmonitor = od.Monitor;
            dxgiRotation = RotationMapper.FromDxgi((int)od.Rotation);
            info = DxgiOutputs.Describe(output, target.Info);
        }
        catch (Exception ex)
        {
            int hr = ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
            HandleError(hr, $"Capture init ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
        finally
        {
            output?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
        }

        // ---- WGC objects. Failures here are specific to this capture method (counted for the automatic fallback).
        try
        {
            EnsureBorderlessAccessRequested();
            _winrtDevice ??= WgcInterop.CreateDirect3DDevice(_reducer.Device!.NativePointer);
            _item = WgcInterop.CreateItemForMonitor(hmonitor);
            _itemClosed = false;
            _item.Closed += _onItemClosed;
            var size = _item.Size;
            _reducer.Rotation = WgcCapturePolicy.Rotation(size.Width, size.Height, info.Width, info.Height, dxgiRotation);

            _poolFormat = WgcCapturePolicy.PixelFormat(info.IsHdr);
            _poolBuffers = WgcCapturePolicy.BufferCount(size.Width, size.Height, info.IsHdr);
            _poolSize = size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, _poolFormat, _poolBuffers, size);
            _poolPtr = WgcInterop.QueryInterface(_pool, WgcInterop.IID_IDirect3D11CaptureFramePool);
            if (_poolPtr == IntPtr.Zero) throw new InvalidOperationException("IDirect3D11CaptureFramePool not available");
            _frameEvent.Reset();
            _pool.FrameArrived += _onFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            string options = ConfigureSession(_session);
            _session.StartCapture();

            bool changedTarget = _target is null || !string.Equals(_target.Info.Id, target.Info.Id, StringComparison.OrdinalIgnoreCase);
            _target = target;
            _reducer.Target = target;
            _targetExact = exact || _monitorId.Length == 0;
            _hdr = info.IsHdr;
            _currentMonitor = info;
            _sessionStartTs = Stopwatch.GetTimestamp();
            _gotFirstFrame = false;
            _secureDesktop = false;
            _nextSlowCheckTs = _sessionStartTs + (long)(SlowCheckIntervalSeconds * Stopwatch.Frequency);
            if (_nextGapSummaryTs == 0) _nextGapSummaryTs = _sessionStartTs + (long)(GapSummaryInterval.TotalSeconds * Stopwatch.Frequency);
            if (changedTarget || _lastFailure is not null || options != _lastSessionOptions)
                Log.Info($"Capturing {info.FriendlyName} [{info.Id}] on {_reducer.AdapterName} with Windows Graphics Capture, " +
                         $"{size.Width}×{size.Height} {_poolFormat}{(info.IsHdr ? " HDR" : "")}, {_poolBuffers} buffers, " +
                         $"rotation {_reducer.Rotation}, {options}{(_targetExact ? "" : " (configured monitor not found, using fallback)")}");
            _lastSessionOptions = options;
            return true;
        }
        catch (Exception ex)
        {
            int hr = ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
            Interlocked.Increment(ref _consecutiveInitFailures);
            HandleError(hr, $"Windows Graphics Capture init ({ex.GetType().Name}: {NativeCom.Describe(hr)} {ex.Message})");
            return false;
        }
    }

    /// <summary>Cursor off, border off, update rate capped. Each option only where the OS has it. Returns a summary for the log.</summary>
    private string ConfigureSession(GraphicsCaptureSession session)
    {
        var parts = new List<string>(3);
        if (ApiInformation.IsPropertyPresent(SessionClass, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
        {
            session.IsCursorCaptureEnabled = CursorCaptureEnabled;
            parts.Add(CursorCaptureEnabled ? "cursor captured" : "cursor excluded");
        }
        else parts.Add("cursor exclusion unsupported");

        if (ApiInformation.IsPropertyPresent(SessionClass, "IsBorderRequired"))
        {
            int hr = WgcInterop.SetIsBorderRequired(session, BorderRequired);
            parts.Add(hr >= 0 ? $"border {(BorderRequired ? "on" : "off")} (access {_borderlessAccess ?? "not requested"})"
                              : $"border setting failed {NativeCom.Describe(hr)}");
        }
        else parts.Add("border control unsupported (Windows 10)");

        if (MinUpdateIntervalMs > 0 && ApiInformation.IsPropertyPresent(SessionClass, "MinUpdateInterval"))
        {
            int hr = WgcInterop.SetMinUpdateInterval(session, TimeSpan.FromMilliseconds(MinUpdateIntervalMs));
            parts.Add(hr >= 0 ? $"min update interval {MinUpdateIntervalMs} ms" : $"min update interval failed {NativeCom.Describe(hr)}");
        }
        else parts.Add(MinUpdateIntervalMs > 0 ? "min update interval unsupported (before 24H2)" : "no min update interval");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// GraphicsCaptureAccess.RequestAccessAsync(Borderless) once per process (build 20348+), so IsBorderRequired = false is
    /// honored. Unpackaged desktop apps are normally allowed without a prompt.
    /// </summary>
    private static void EnsureBorderlessAccessRequested()
    {
        lock (AccessLock)
        {
            if (_borderlessAccess is not null) return;
            if (!ApiInformation.IsTypePresent(WgcInterop.GraphicsCaptureAccessClass))
            {
                _borderlessAccess = "n/a";
                return;
            }
            int? status = WgcInterop.RequestAccess(0 /* GraphicsCaptureAccessKind.Borderless */, AccessRequestTimeoutMs, out int hr);
            _borderlessAccess = status switch
            {
                4 => "Allowed",
                0 => "DeniedBySystem",
                1 => "NotDeclaredByApp",
                2 => "DeniedByUser",
                3 => "UserPromptRequired",
                null => $"failed {NativeCom.Describe(hr)}",
                _ => status.Value.ToString(),
            };
            if (status != 4) Log.Warn($"{LogPrefix}borderless capture access: {_borderlessAccess} (Windows may show a yellow border)");
        }
    }

    // ---- Event handlers (WGC worker threads)

    [ThreadStatic] private static long _callbackThreadAllocated;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Diagnostics: managed bytes allocated on this callback thread since its previous callback (= the projection's
        // event marshalling + this handler). Allocation free itself.
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (_callbackThreadAllocated != 0) Interlocked.Add(ref _callbackAllocated, allocated - _callbackThreadAllocated);
        _callbackThreadAllocated = allocated;

        if (_disposed) return;
        Interlocked.Increment(ref _framesArrived);
        _gaps.OnFrame(Stopwatch.GetTimestamp());
        try { _frameEvent.Set(); } catch (ObjectDisposedException) { }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _itemClosed = true;
        try { if (!_disposed) _frameEvent.Set(); } catch (ObjectDisposedException) { }
    }

    // =====================================================================================================
    // Failure handling
    // =====================================================================================================

    private void HandleError(int hr, string where)
    {
        if (DxgiScreenSampler.IsDeviceLost(hr))
        {
            string reason = "";
            try { if (_reducer.Device is { } d) reason = $", reason {NativeCom.Describe(d.DeviceRemovedReason.Code)}"; } catch { }
            ReportFailure($"{where}: {NativeCom.Describe(hr)} (GPU device lost{reason})");
            TeardownAll();
        }
        else
        {
            ReportFailure($"{where}: {NativeCom.Describe(hr)}");
            TeardownSession();
            if (_reducer.Device is null) TeardownAll();
        }
        DisplayTopology.Invalidate();
        ScheduleRetry(immediate: false);
    }

    private void ScheduleRetry(bool immediate)
    {
        long now = Stopwatch.GetTimestamp();
        if (immediate && !_immediateRetryUsed)
        {
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
        Log.Warn("Screen capture (Windows Graphics Capture): " + message);
    }

    /// <summary>Never spin while capture is impossible: sleep for the timeout (at least a few ms).</summary>
    private static void SleepUnavailable(int timeoutMs) => Thread.Sleep(Math.Max(timeoutMs, MinUnavailableSleepMs));

    /// <summary>Sleeps for what is left of the timeout (at least a few ms in total).</summary>
    private static void SleepRemaining(long callStart, int timeoutMs)
    {
        int left = Math.Max(timeoutMs, MinUnavailableSleepMs) - (int)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
        if (left > 0) Thread.Sleep(left);
    }

    // =====================================================================================================
    // Teardown
    // =====================================================================================================

    private void TeardownSession()
    {
        if (_pool is not null) try { _pool.FrameArrived -= _onFrameArrived; } catch { }
        if (_item is not null) try { _item.Closed -= _onItemClosed; } catch { }
        try { _session?.Dispose(); } catch { /* already gone */ }
        if (_poolPtr != IntPtr.Zero) { Marshal.Release(_poolPtr); _poolPtr = IntPtr.Zero; }
        try { _pool?.Dispose(); } catch { }
        _session = null;
        _pool = null;
        _item = null;
        _itemClosed = false;
        _poolSize = default;
        try { _frameEvent.Reset(); } catch (ObjectDisposedException) { }
    }

    private void TeardownAll()
    {
        TeardownSession();
        try { _winrtDevice?.Dispose(); } catch { }
        _winrtDevice = null;
        _reducer.ReleaseDevice();
    }
}
