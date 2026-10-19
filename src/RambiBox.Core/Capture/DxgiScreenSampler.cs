using System.Diagnostics;
using RambiBox.Core.Config;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RambiBox.Core.Capture;

/// <summary>
/// Screen sampler based on DXGI Desktop Duplication.
/// <para>Per frame: AcquireNextFrame → copy into an own mip-mapped texture → ReleaseFrame immediately →
/// GenerateMips (GPU box filter, done in linear light via an sRGB view / FP16) → copy ONE small mip level
/// (~160×90 at 1440p) to a staging texture → Map → convert to a linear float image (desktop orientation, HDR tone-mapped)
/// → black bar detection → exact area-weighted zone averages. Steady state is allocation free.</para>
/// <para>Failures (mode switches, secure desktop, driver resets, unplugged monitors) never throw: <see cref="Sample"/>
/// returns <see cref="SampleResult.Unavailable"/>, sleeps instead of spinning, and re-initializes with backoff.</para>
/// </summary>
public sealed class DxgiScreenSampler : IScreenSampler
{
    /// <summary>Target width of the downscaled image (the chosen mip level is the smallest one at least this wide).</summary>
    internal const int MinSmallWidth = 128;
    private const int MinUnavailableSleepMs = 16;
    private const int FirstBackoffMs = 250, MaxBackoffMs = 2000;
    private const double PreviewIntervalSeconds = 0.1;
    private const double SlowCheckIntervalSeconds = 2.0;

    private const Format FormatBgra = Format.B8G8R8A8_UNorm;
    private const Format FormatBgraSrgb = Format.B8G8R8A8_UNorm_SRgb;
    private const Format FormatFp16 = Format.R16G16B16A16_Float;
    private static readonly Format[] DuplicationFormats = [FormatBgra, FormatFp16];

    private readonly object _sync = new();
    private readonly object _publicLock = new(); // preview + black bars (read from the UI thread)
    private bool _disposed;

    // ---- Configuration
    private string _monitorId = "";
    private LedZone[] _zones = [];
    private ZoneGeom[] _geoms = [];
    private bool _blackBarDetection;
    private bool _geometryDirty = true;

    // ---- Device / duplication
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IntPtr _contextPtr;
    private Vortice.Luid _adapterLuid;
    private string _adapterName = "";
    private IDXGIOutputDuplication? _dupl;
    private IntPtr _duplPtr;
    private SurfaceRotation _rotation;
    private DisplayTarget? _target;
    private bool _targetExact;
    private volatile MonitorInfo? _currentMonitor;

    // ---- Downscale resources (depend on the desktop texture size/format)
    private ID3D11Texture2D? _mipTex;
    private ID3D11ShaderResourceView? _srv;
    private ID3D11Texture2D? _staging;
    private IntPtr _mipTexPtr, _stagingPtr;
    private int _srcW, _srcH, _srcFormat = -1;
    private int _mipLevel, _smallTexW, _smallTexH;
    private bool _fp16;
    private float _sdrWhiteScRgb = ColorMath.DefaultSdrWhiteNits / ColorMath.ScRgbNitsPerUnit;

    // ---- CPU side
    private float[] _linear = [];
    private int _imgW, _imgH; // desktop orientation
    private readonly BlackBarDetector _bars = new();
    private (float Top, float Bottom, float Left, float Right) _barsPublic;
    private byte[] _preview = [];
    private int _previewW, _previewH;
    private bool _hasPreview;
    private long _lastPreviewTs;
    private readonly long _epochTs = Stopwatch.GetTimestamp();

    // ---- Retry / logging
    private long _nextInitTs;
    private int _backoffMs;
    private bool _immediateRetryUsed;
    private string? _lastFailure;
    private bool _everCaptured;
    private long _nextSlowCheckTs;

    // ---- Diagnostics (tests / benchmark)
    internal double LastWorkMs { get; private set; }
    internal double LastMapWaitMs { get; private set; }
    internal long FramesCaptured { get; private set; }
    internal bool IsFp16 => _fp16;
    internal float SdrWhiteNits => _sdrWhiteScRgb * ColorMath.ScRgbNitsPerUnit;
    internal (int Width, int Height) SmallImageSize => (_imgW, _imgH);
    internal int MipLevel => _mipLevel;

    public DxgiScreenSampler() { }

    // =====================================================================================================
    // Public API
    // =====================================================================================================

    public IReadOnlyList<MonitorInfo> GetMonitors() => DisplayTopology.GetMonitors();

    public MonitorInfo? CurrentMonitor => _currentMonitor;

    public (float Top, float Bottom, float Left, float Right) BlackBars
    {
        get { lock (_publicLock) return _barsPublic; }
    }

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
            if (zones.Count != _zones.Length || !zones.SequenceEqual(_zones))
            {
                _zones = zones.ToArray();
                _geoms = new ZoneGeom[_zones.Length];
                _geometryDirty = true;
            }
            if (blackBarDetection != _blackBarDetection)
            {
                _blackBarDetection = blackBarDetection;
                _bars.Reset();
                PublishBars();
                _geometryDirty = true;
            }
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
                if (IsDeviceLost(hr) || _device is null) TeardownAll(); else TeardownDuplication();
                ScheduleRetry(immediate: false);
                SleepUnavailable(timeoutMs);
                return SampleResult.Unavailable;
            }
        }
    }

    public bool TryGetPreview(out byte[] bgra, out int width, out int height)
    {
        lock (_publicLock)
        {
            if (!_hasPreview)
            {
                bgra = [];
                width = height = 0;
                return false;
            }
            width = _previewW;
            height = _previewH;
            bgra = _preview.AsSpan(0, width * height * 4).ToArray();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            TeardownAll();
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
                if (EnsureResources(in desc))
                {
                    if (_mipLevel > 0) NativeCom.CopySubresourceRegion(_contextPtr, _mipTexPtr, 0, tex, 0);
                    else NativeCom.CopySubresourceRegion(_contextPtr, _stagingPtr, 0, tex, 0);
                    ok = true;
                }
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
                TeardownDuplication(); // unsupported desktop format (reported by EnsureResources)
                ScheduleRetry(immediate: false);
            }
            SleepUnavailable(timeoutMs);
            return SampleResult.Unavailable;
        }

        // ---- GPU downscale and read back the small mip level
        var ctx = _context!;
        if (_mipLevel > 0)
        {
            ctx.GenerateMips(_srv!);
            NativeCom.CopySubresourceRegion(_contextPtr, _stagingPtr, 0, _mipTexPtr, (uint)_mipLevel);
        }
        long mapStart = Stopwatch.GetTimestamp();
        var mr = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
        LastMapWaitMs = Stopwatch.GetElapsedTime(mapStart).TotalMilliseconds;
        if (mr.Failure)
        {
            HandleHresult(mr.Code, "Map");
            SleepUnavailable(timeoutMs);
            return SampleResult.Unavailable;
        }
        try
        {
            unsafe
            {
                int bpp = _fp16 ? 8 : 4;
                int len = (int)mapped.RowPitch * (_smallTexH - 1) + _smallTexW * bpp;
                var src = new ReadOnlySpan<byte>((void*)mapped.DataPointer, len);
                if (_fp16)
                    FrameConverter.ConvertFp16(src, _smallTexW, _smallTexH, (int)mapped.RowPitch, _rotation, _sdrWhiteScRgb, ColorMath.DefaultKnee, _linear);
                else
                    FrameConverter.ConvertBgra8(src, _smallTexW, _smallTexH, (int)mapped.RowPitch, _rotation, _linear);
            }
        }
        finally
        {
            ctx.Unmap(_staging!, 0);
        }

        // ---- CPU analysis on the tiny image
        double now = Stopwatch.GetElapsedTime(_epochTs).TotalSeconds;
        if (_blackBarDetection && _bars.Update(_linear, _imgW, _imgH, now))
        {
            PublishBars();
            _geometryDirty = true;
        }
        if (_geometryDirty)
        {
            var content = _blackBarDetection
                ? ZoneMath.ContentRect(_barsPublic.Top, _barsPublic.Bottom, _barsPublic.Left, _barsPublic.Right)
                : RectF.Full;
            ZoneMath.BuildGeometry(_zones, _imgW, _imgH, content, _geoms);
            _geometryDirty = false;
        }
        ZoneMath.Average(_linear, _imgW, _geoms, destRgb);
        if (destRgb.Length > _geoms.Length * 3) destRgb[(_geoms.Length * 3)..].Clear();

        if (Stopwatch.GetElapsedTime(_lastPreviewTs).TotalSeconds >= PreviewIntervalSeconds)
        {
            _lastPreviewTs = Stopwatch.GetTimestamp();
            UpdatePreview();
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

    /// <summary>Creates/recreates the mip texture, SRV and staging texture when the desktop size or format changes.</summary>
    private bool EnsureResources(in NativeCom.TEXTURE2D_DESC desc)
    {
        if (desc.Width == _srcW && desc.Height == _srcH && desc.Format == _srcFormat && _staging is not null) return true;

        ReleaseResources();
        var device = _device!;
        var srcFormat = (Format)desc.Format;
        if (srcFormat != FormatBgra && srcFormat != FormatFp16 && srcFormat != FormatBgraSrgb)
        {
            ReportFailure($"Unsupported desktop format {srcFormat}");
            return false; // caller tears the duplication down after ReleaseFrame
        }

        int w = (int)desc.Width, h = (int)desc.Height;
        int level = ChooseMipLevel(w, h);

        // BGRA: filter through an sRGB view so GenerateMips averages linear light (copying UNORM → UNORM_SRGB is
        // allowed: same typeless group). FP16 scRGB is linear already.
        var work = srcFormat == FormatFp16 ? FormatFp16 : FormatBgraSrgb;
        if (level > 0)
        {
            var need = FormatSupport.MipAutogen | FormatSupport.RenderTarget | FormatSupport.Texture2D;
            if ((device.CheckFormatSupport(work) & need) != need) work = srcFormat;

            _mipTex = device.CreateTexture2D(new Texture2DDescription(work, (uint)w, (uint)h, 1, (uint)(level + 1),
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
                ResourceOptionFlags.GenerateMips));
            _srv = device.CreateShaderResourceView(_mipTex, null);
            _mipTexPtr = _mipTex.NativePointer;
        }
        else work = srcFormat;

        _smallTexW = Math.Max(1, w >> level);
        _smallTexH = Math.Max(1, h >> level);
        _staging = device.CreateTexture2D(new Texture2DDescription(work, (uint)_smallTexW, (uint)_smallTexH, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _stagingPtr = _staging.NativePointer;

        _srcW = w; _srcH = h; _srcFormat = desc.Format;
        _mipLevel = level;
        _fp16 = srcFormat == FormatFp16;
        (_imgW, _imgH) = RotationMapper.DesktopSize(_rotation, _smallTexW, _smallTexH);
        _linear = new float[_imgW * _imgH * 3];
        _bars.Reset();
        PublishBars();
        _geometryDirty = true;
        if (_fp16) RefreshSdrWhite();
        Log.Info($"Capture surface {w}×{h} {srcFormat} → mip {level} ({_smallTexW}×{_smallTexH}, work format {work}), rotation {_rotation}" +
                 (_fp16 ? $", HDR tone mapping (SDR white {SdrWhiteNits:0} nits)" : ""));
        return true;
    }

    /// <summary>Smallest mip level whose longer side is still ≥ <see cref="MinSmallWidth"/> (2560 → 160, 1920 → 240, 3840 → 240).</summary>
    internal static int ChooseMipLevel(int width, int height)
    {
        int size = Math.Max(width, height), level = 0;
        while ((size >> (level + 1)) >= MinSmallWidth) level++;
        return level;
    }

    /// <summary>Periodic work (every 2 s): refresh SDR white level; switch to the configured monitor when it reappears.</summary>
    private bool SlowChecks()
    {
        if (_fp16) RefreshSdrWhite();
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

    private void RefreshSdrWhite()
    {
        float nits = ColorMath.DefaultSdrWhiteNits;
        if (_target is not null && DisplayConfigNative.GetSdrWhiteNits(_target.AdapterId, _target.TargetId) is { } n && n >= 40f)
            nits = n;
        _sdrWhiteScRgb = nits / ColorMath.ScRgbNitsPerUnit;
    }

    private void UpdatePreview()
    {
        int n = _imgW * _imgH;
        lock (_publicLock)
        {
            if (_preview.Length < n * 4) _preview = new byte[n * 4];
            FrameConverter.ToPreviewBgra(_linear, n, _preview);
            _previewW = _imgW;
            _previewH = _imgH;
            _hasPreview = true;
        }
    }

    private void PublishBars()
    {
        var b = _blackBarDetection ? _bars.Bars : default;
        lock (_publicLock) _barsPublic = b;
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
            for (uint a = 0; output is null && factory.EnumAdapters1(a, out var ad).Success; a++)
            {
                for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
                {
                    var d = op.Description;
                    if (d.AttachedToDesktop && string.Equals(d.DeviceName, target.Info.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        output = op;
                        break;
                    }
                    op.Dispose();
                }
                if (output is null) ad.Dispose(); else adapter = ad;
            }
            if (output is null || adapter is null)
            {
                DisplayTopology.Invalidate();
                ReportFailure($"No DXGI output for {target.Info.GdiDeviceName} ({target.Info.FriendlyName})");
                ScheduleRetry(immediate: false);
                return false;
            }

            var adesc = adapter.Description1;
            if (_device is null || !_adapterLuid.Equals(adesc.Luid) || _device.DeviceRemovedReason.Failure)
            {
                TeardownAll();
                CreateDevice(adapter);
                _adapterLuid = adesc.Luid;
                _adapterName = adesc.Description;
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
            var newRotation = RotationMapper.FromDxgi((int)dd.Rotation);
            if (newRotation != _rotation)
            {
                _rotation = newRotation;
                _srcFormat = -1; // rebuild the CPU image with the new orientation
            }

            bool hdr = target.Info.IsHdr;
            using (var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                if (o6 is not null) hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            var bounds = output.Description.DesktopCoordinates;
            var info = target.Info with
            {
                X = bounds.Left, Y = bounds.Top, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top,
                IsHdr = hdr,
            };

            bool changedTarget = _target is null || !string.Equals(_target.Info.Id, target.Info.Id, StringComparison.OrdinalIgnoreCase);
            _target = target;
            _targetExact = exact || _monitorId.Length == 0;
            _currentMonitor = info;
            _everCaptured = false;
            _nextSlowCheckTs = Stopwatch.GetTimestamp() + (long)(SlowCheckIntervalSeconds * Stopwatch.Frequency);
            if (changedTarget || _lastFailure is not null)
                Log.Info($"Capturing {info.FriendlyName} [{info.Id}] on {_adapterName}, {dd.ModeDescription.Format}" +
                         $"{(hdr ? " HDR" : "")}, rotation {_rotation}{(_targetExact ? "" : " (configured monitor not found, using fallback)")}");
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

    private void CreateDevice(IDXGIAdapter1 adapter)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var r = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
        if (r.Failure || dev is null || ctx is null)
        {
            dev?.Dispose(); ctx?.Dispose();
            throw new SharpGenException(r, "D3D11CreateDevice failed", null);
        }
        _device = dev;
        _context = ctx;
        _contextPtr = ctx.NativePointer;
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
                    dupl = o5.DuplicateOutput1(_device!, DuplicationFormats);
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
            dupl = o1.DuplicateOutput(_device!);
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

    private static bool IsDeviceLost(int hr) => hr is NativeCom.DXGI_ERROR_DEVICE_REMOVED or NativeCom.DXGI_ERROR_DEVICE_RESET
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
                if (IsDeviceLost(hr) && _device is not null)
                {
                    var reason = _device.DeviceRemovedReason;
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

    private void ReleaseResources()
    {
        _mipTexPtr = _stagingPtr = IntPtr.Zero;
        try { _srv?.Dispose(); } catch { }
        try { _mipTex?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        _srv = null; _mipTex = null; _staging = null;
        _srcW = _srcH = 0;
        _srcFormat = -1;
    }

    private void TeardownAll()
    {
        TeardownDuplication();
        ReleaseResources();
        _contextPtr = IntPtr.Zero;
        try { _context?.ClearState(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _context = null;
        _device = null;
        _adapterLuid = default;
    }
}
