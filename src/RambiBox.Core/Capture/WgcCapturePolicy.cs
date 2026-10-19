using System.Diagnostics;
using Windows.Graphics.DirectX;

namespace RambiBox.Core.Capture;

/// <summary>Pure decisions of the Windows.Graphics.Capture sampler (unit-tested without a GPU).</summary>
internal static class WgcCapturePolicy
{
    /// <summary>
    /// GraphicsCaptureSession.MinUpdateInterval (Windows 11 24H2+): DWM delivers at most one frame per interval
    /// instead of one per monitor refresh (170 Hz on the XG27AQ). 20 ms (≤ 50 fps) is above every LED link rate
    /// RambiBox offers in the UI (default link ≈ 34 fps) but cuts DWM's copy work by ~3× on a high refresh monitor.
    /// </summary>
    public const int DefaultMinUpdateIntervalMs = 20;

    /// <summary>One frame buffer above this size is enough (DWM then waits for us; we return frames immediately).</summary>
    internal const long LargeBufferBytes = 64L * 1024 * 1024;

    /// <summary>FP16 scRGB for HDR monitors (tone-mapped by the shared FP16 path), 8-bit BGRA otherwise.</summary>
    public static DirectXPixelFormat PixelFormat(bool hdr) =>
        hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>
    /// Frame pool buffers: two let DWM fill the next frame while we copy the current one (we hold a frame only for
    /// the duration of one GPU copy). Huge surfaces (8K FP16 = 265 MB per buffer) use one to save video memory.
    /// </summary>
    public static int BufferCount(int width, int height, bool hdr)
    {
        long bytes = (long)Math.Max(1, width) * Math.Max(1, height) * (hdr ? 8 : 4);
        return bytes > LargeBufferBytes ? 1 : 2;
    }

    /// <summary>
    /// Orientation of WGC frames relative to the desktop. WGC hands out monitor content upright (desktop orientation,
    /// i.e. already rotated like the user sees it), so this is normally Identity. Defensive check: if a frame arrives in
    /// the panel's native orientation instead (width/height swapped against the desktop rectangle of a 90°/270° rotated
    /// monitor), the DXGI rotation is applied like for Desktop Duplication.
    /// </summary>
    public static SurfaceRotation Rotation(int frameW, int frameH, int desktopW, int desktopH, SurfaceRotation dxgiRotation)
    {
        if (frameW == desktopW && frameH == desktopH) return SurfaceRotation.Identity;
        bool swapped = frameW == desktopH && frameH == desktopW;
        return swapped && dxgiRotation is SurfaceRotation.Rotate90 or SurfaceRotation.Rotate270 ? dxgiRotation : SurfaceRotation.Identity;
    }
}

/// <summary>
/// Detects hiccups in WGC frame delivery: a gap longer than <see cref="GapMs"/> between FrameArrived events that sits
/// inside otherwise steady delivery (≥ <see cref="SteadyBefore"/> short intervals before it and ≥
/// <see cref="SteadyAfter"/> after it). A static screen simply produces no frames, so plain silence is not counted;
/// a stall in the middle of video playback is. Thread-safe, allocation free.
/// </summary>
internal sealed class DeliveryGapTracker
{
    public const double GapMs = 500, ShortMs = 100;
    public const int SteadyBefore = 10, SteadyAfter = 3;

    private readonly object _lock = new();
    private long _lastTs;
    private int _steady;          // consecutive short intervals before the current position
    private double _pendingGapMs; // a long gap waiting for steady frames after it
    private int _after;
    private int _gaps;
    private double _maxGapMs, _totalGapMs;

    /// <summary>Records one delivered frame at <paramref name="timestamp"/> (Stopwatch ticks).</summary>
    public void OnFrame(long timestamp)
    {
        lock (_lock)
        {
            if (_lastTs == 0) { _lastTs = timestamp; return; }
            double ms = (timestamp - _lastTs) * 1000.0 / Stopwatch.Frequency;
            _lastTs = timestamp;
            if (ms > GapMs)
            {
                _pendingGapMs = _steady >= SteadyBefore ? ms : 0;
                _after = 0;
                _steady = 0;
                return;
            }
            if (ms <= ShortMs)
            {
                _steady++;
                if (_pendingGapMs > 0 && ++_after >= SteadyAfter)
                {
                    _gaps++;
                    _totalGapMs += _pendingGapMs;
                    _maxGapMs = Math.Max(_maxGapMs, _pendingGapMs);
                    _pendingGapMs = 0;
                }
            }
            else
            {
                _steady = 0;
                _pendingGapMs = 0; // not steady after the gap → a normal pause, not a stall
            }
        }
    }

    /// <summary>Takes (and resets) the stalls counted so far.</summary>
    public (int Count, double MaxMs, double TotalMs) Take()
    {
        lock (_lock)
        {
            var r = (_gaps, _maxGapMs, _totalGapMs);
            _gaps = 0;
            _maxGapMs = _totalGapMs = 0;
            return r;
        }
    }
}
