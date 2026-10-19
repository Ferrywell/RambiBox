using System.Diagnostics;
using RambiBox.Core.Config;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RambiBox.Core.Capture;

/// <summary>
/// The capture pipeline shared by all GPU samplers (Desktop Duplication and Windows.Graphics.Capture), so both
/// produce bit-identical zone colors from the same desktop picture.
/// <para>Owns the D3D11 device on the adapter that drives the monitor, plus everything after "a desktop texture is
/// available": copy into an own mip-mapped texture → GenerateMips (GPU box filter in linear light via an sRGB view /
/// FP16) → copy ONE small mip level (~160×90 at 1440p) to a staging texture → GPU fence wait → Map → linear float
/// image in desktop orientation (HDR tone-mapped) → black bar detection → exact area-weighted zone averages →
/// throttled UI preview. Steady state is allocation free.</para>
/// <para>Not thread-safe except <see cref="BlackBars"/> and <see cref="TryGetPreview"/>; the owning sampler serializes
/// all other calls (engine thread). The zone configuration and the preview survive device teardown.</para>
/// </summary>
internal sealed class GpuFrameReducer : IDisposable
{
    /// <summary>Target width of the downscaled image (the chosen mip level is the smallest one at least this wide).</summary>
    internal const int MinSmallWidth = 128;
    private const double PreviewIntervalSeconds = 0.1;
    /// <summary>A single GPU step (copy, fence wait, Map) slower than this is logged (rate-limited).</summary>
    internal const double SlowGpuStepMs = 200;
    /// <summary>Upper bound for the fence wait; Map() blocks for the rest if the GPU is even slower.</summary>
    private const int FenceTimeoutMs = 250;

    internal const Format FormatBgra = Format.B8G8R8A8_UNorm;
    internal const Format FormatBgraSrgb = Format.B8G8R8A8_UNorm_SRgb;
    internal const Format FormatFp16 = Format.R16G16B16A16_Float;

    private readonly object _publicLock = new(); // preview + black bars (read from the UI thread)
    private readonly string _logPrefix;

    // ---- Configuration
    private LedZone[] _zones = [];
    private ZoneGeom[] _geoms = [];
    private bool _blackBarDetection;
    private bool _geometryDirty = true;

    // ---- Device
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IntPtr _contextPtr;
    // GPU completion fence: waiting on a kernel event instead of a blocking Map(). NVIDIA drivers busy-wait inside
    // a blocking Map, which cost ~6% of a core at 34 captures/s; the fence wait costs almost nothing.
    private ID3D11DeviceContext4? _context4;
    private ID3D11Fence? _fence;
    private ulong _fenceValue;
    private readonly AutoResetEvent _gpuDone = new(false);
    private Vortice.Luid _adapterLuid;
    private string _adapterName = "";

    // ---- Downscale resources (depend on the desktop texture size/format)
    private ID3D11Texture2D? _mipTex;
    private ID3D11ShaderResourceView? _srv;
    private ID3D11Texture2D? _staging;
    private IntPtr _mipTexPtr, _stagingPtr;
    private int _srcW, _srcH, _srcFormat = -1;
    private int _mipLevel, _smallTexW, _smallTexH;
    private bool _fp16;
    private SurfaceRotation _rotation;
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

    // ---- Diagnostics
    private readonly RateLimitedLog _slowGpuLog;

    public GpuFrameReducer(string logPrefix)
    {
        _logPrefix = logPrefix;
        _slowGpuLog = new RateLimitedLog(TimeSpan.FromMinutes(1));
    }

    // =====================================================================================================
    // Configuration / public state
    // =====================================================================================================

    public ID3D11Device? Device => _device;
    public ID3D11DeviceContext? Context => _context;
    public string AdapterName => _adapterName;

    /// <summary>The display path being captured (for the SDR white level of HDR monitors).</summary>
    public DisplayTarget? Target { get; set; }

    /// <summary>Orientation of the captured texture relative to the desktop. Changing it rebuilds the CPU image.</summary>
    public SurfaceRotation Rotation
    {
        get => _rotation;
        set
        {
            if (value == _rotation) return;
            _rotation = value;
            _srcFormat = -1; // rebuild the CPU image with the new orientation
        }
    }

    public double LastMapWaitMs { get; private set; }
    public bool IsFp16 => _fp16;
    public float SdrWhiteNits => _sdrWhiteScRgb * ColorMath.ScRgbNitsPerUnit;
    public (int Width, int Height) SmallImageSize => (_imgW, _imgH);
    public int MipLevel => _mipLevel;
    public (int Width, int Height) SourceSize => (_srcW, _srcH);

    public (float Top, float Bottom, float Left, float Right) BlackBars
    {
        get { lock (_publicLock) return _barsPublic; }
    }

    /// <summary>Zones and black bar detection. Cheap if nothing changed.</summary>
    public void Configure(IReadOnlyList<LedZone> zones, bool blackBarDetection)
    {
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

    // =====================================================================================================
    // Device
    // =====================================================================================================

    /// <summary>True when a (new) device must be created for <paramref name="adapterLuid"/>.</summary>
    public bool NeedsDevice(Vortice.Luid adapterLuid) =>
        _device is null || !_adapterLuid.Equals(adapterLuid) || _device.DeviceRemovedReason.Failure;

    /// <summary>Creates the D3D11 device (+ completion fence) on <paramref name="adapter"/>. Throws on failure.</summary>
    public void CreateDevice(IDXGIAdapter1 adapter)
    {
        ReleaseDevice();
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
        try
        {
            using var dev5 = dev.QueryInterfaceOrNull<ID3D11Device5>();
            _context4 = ctx.QueryInterfaceOrNull<ID3D11DeviceContext4>();
            if (dev5 is not null && _context4 is not null) _fence = dev5.CreateFence(0, FenceFlags.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"{_logPrefix}GPU fence unavailable, using blocking readback ({ex.Message})");
            _fence = null;
        }
        if (_fence is null) { _context4?.Dispose(); _context4 = null; }
        var desc = adapter.Description1;
        _adapterLuid = desc.Luid;
        _adapterName = desc.Description;
    }

    /// <summary>Releases the downscale resources, the fence and the device.</summary>
    public void ReleaseDevice()
    {
        ReleaseResources();
        _contextPtr = IntPtr.Zero;
        try { _fence?.Dispose(); } catch { }
        try { _context4?.Dispose(); } catch { }
        _fence = null;
        _context4 = null;
        _fenceValue = 0;
        try { _context?.ClearState(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _context = null;
        _device = null;
        _adapterLuid = default;
    }

    // =====================================================================================================
    // Per frame
    // =====================================================================================================

    /// <summary>
    /// Creates/recreates the mip texture, SRV and staging texture when the source size or format changes.
    /// Returns false (with a message) for formats the pipeline can't handle.
    /// </summary>
    public bool EnsureResources(in NativeCom.TEXTURE2D_DESC desc, out string? error)
    {
        error = null;
        if (desc.Width == _srcW && desc.Height == _srcH && desc.Format == _srcFormat && _staging is not null) return true;

        ReleaseResources();
        var device = _device!;
        var srcFormat = (Format)desc.Format;
        if (srcFormat != FormatBgra && srcFormat != FormatFp16 && srcFormat != FormatBgraSrgb)
        {
            error = $"Unsupported desktop format {srcFormat}";
            return false;
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
        Log.Info($"{_logPrefix}surface {w}×{h} {srcFormat} → mip {level} ({_smallTexW}×{_smallTexH}, work format {work}), rotation {_rotation}" +
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

    /// <summary>
    /// Copies subresource 0 of <paramref name="srcTexture"/> (an ID3D11Texture2D*, same size/format as passed to
    /// <see cref="EnsureResources"/>) into the pipeline. After this the source may be handed back to Windows.
    /// </summary>
    public void CopyIn(IntPtr srcTexture)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (_mipLevel > 0) NativeCom.CopySubresourceRegion(_contextPtr, _mipTexPtr, 0, srcTexture, 0);
        else NativeCom.CopySubresourceRegion(_contextPtr, _stagingPtr, 0, srcTexture, 0);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > SlowGpuStepMs) ReportSlow("CopySubresourceRegion (desktop copy)", ms);
    }

    /// <summary>
    /// GPU downscale + readback + CPU analysis; writes the zone colors into <paramref name="destRgb"/>.
    /// Returns the HRESULT of Map (negative = failure, nothing written; the caller runs its recovery path).
    /// </summary>
    public int Reduce(Span<float> destRgb)
    {
        var ctx = _context!;
        if (_mipLevel > 0)
        {
            ctx.GenerateMips(_srv!);
            NativeCom.CopySubresourceRegion(_contextPtr, _stagingPtr, 0, _mipTexPtr, (uint)_mipLevel);
        }
        long mapStart = Stopwatch.GetTimestamp();
        if (_fence is not null && _context4 is not null)
        {
            _fenceValue++;
            _context4.Signal(_fence, _fenceValue);
            try
            {
                _fence.SetEventOnCompletion(_fenceValue, _gpuDone.SafeWaitHandle.DangerousGetHandle());
                ctx.Flush();
                _gpuDone.WaitOne(FenceTimeoutMs); // the Map below then returns without blocking
            }
            catch (SharpGenException)
            {
                // Device lost etc.: fall through to Map, whose HRESULT drives the normal recovery path.
            }
        }
        double fenceMs = Stopwatch.GetElapsedTime(mapStart).TotalMilliseconds;
        var mr = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
        LastMapWaitMs = Stopwatch.GetElapsedTime(mapStart).TotalMilliseconds;
        if (LastMapWaitMs > SlowGpuStepMs)
            ReportSlow(fenceMs >= FenceTimeoutMs ? "GPU fence wait timed out + Map" : "GPU fence wait + Map", LastMapWaitMs);
        if (mr.Failure) return mr.Code;
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
        return 0;
    }

    /// <summary>Re-reads the SDR white level of an HDR target (cheap; DisplayConfig query, allocation free).</summary>
    public void RefreshSdrWhite()
    {
        float nits = ColorMath.DefaultSdrWhiteNits;
        if (Target is { } t && DisplayConfigNative.GetSdrWhiteNits(t.AdapterId, t.TargetId) is { } n && n >= 40f)
            nits = n;
        _sdrWhiteScRgb = nits / ColorMath.ScRgbNitsPerUnit;
    }

    private void ReportSlow(string step, double ms)
    {
        if (_slowGpuLog.ShouldLog(out int suppressed))
            Log.Warn($"{_logPrefix}{step} took {ms:0} ms (GPU busy or stalled)" +
                     (suppressed > 0 ? $"; {suppressed} more slow GPU steps since the last report" : ""));
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

    public void ReleaseResources()
    {
        _mipTexPtr = _stagingPtr = IntPtr.Zero;
        try { _srv?.Dispose(); } catch { }
        try { _mipTex?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        _srv = null; _mipTex = null; _staging = null;
        _srcW = _srcH = 0;
        _srcFormat = -1;
    }

    public void Dispose()
    {
        ReleaseDevice();
        _gpuDone.Dispose();
    }
}

/// <summary>
/// "At most one line per interval" gate for diagnostics that could otherwise flood the log. Counts what it
/// suppressed so the next line can say how often it happened. Thread-safe, allocation free.
/// </summary>
internal sealed class RateLimitedLog(TimeSpan interval)
{
    private readonly object _lock = new();
    private long _lastTs;
    private int _suppressed;

    public bool ShouldLog(out int suppressedSinceLast)
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastTs != 0 && Stopwatch.GetElapsedTime(_lastTs, now) < interval)
            {
                _suppressed++;
                suppressedSinceLast = 0;
                return false;
            }
            _lastTs = now;
            suppressedSinceLast = _suppressed;
            _suppressed = 0;
            return true;
        }
    }
}
