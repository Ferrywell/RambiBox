using System.Diagnostics;
using RambiBox.Core;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;

namespace RambiBox.App.Dev;

/// <summary>
/// Synthetic "movie scene" instead of the real desktop (<c>--demo</c>): an animated sunset over the sea with
/// drifting clouds and mountain silhouettes. Used for screenshots and demos — it never captures the screen.
/// A background thread renders the picture like a 30 Hz video; <see cref="Sample"/> only averages the zones,
/// so the measured capture time stays realistic.
/// </summary>
public sealed class DemoScreenSampler : IScreenSampler
{
    private const int W = 480, H = 270;
    private const int FrameMs = 33;
    private const double EmulatedCaptureMs = 1.2;

    private static readonly MonitorInfo Display =
        new("demo:display", "Living room TV (3840×2160)", @"\\.\DISPLAY1", 0, 0, 3840, 2160, IsPrimary: true, IsHdr: true, RefreshHz: 60);

    private readonly object _frameLock = new();
    private readonly AutoResetEvent _newFrame = new(false);
    private readonly Thread _renderer;
    private float[] _front = new float[W * H * 3]; // last finished frame, sRGB 0..1 (guarded by _frameLock)
    private float[] _back = new float[W * H * 3];  // render thread only
    private readonly byte[] _bgra = new byte[W * H * 4];
    private bool _haveFrame;
    private LedZone[] _zones = [];
    private MonitorInfo? _current;
    private volatile bool _disposed;

    public DemoScreenSampler()
    {
        _renderer = new Thread(RenderLoop) { IsBackground = true, Name = "Demo scene", Priority = ThreadPriority.BelowNormal };
        _renderer.Start();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => [Display];

    public MonitorInfo? CurrentMonitor => _current;

    public (float Top, float Bottom, float Left, float Right) BlackBars => default;

    public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection)
    {
        _zones = zones.ToArray();
        _current = Display;
    }

    public SampleResult Sample(Span<float> destRgb, int timeoutMs)
    {
        if (_disposed)
        {
            Thread.Sleep(Math.Clamp(timeoutMs, 10, 100));
            return SampleResult.Unavailable;
        }
        if (!_newFrame.WaitOne(Math.Max(0, timeoutMs))) return SampleResult.NoChange;
        var sw = Stopwatch.StartNew();
        lock (_frameLock) AverageZones(_front, destRgb);
        // Emulate the cost of a real GPU capture + readback (~1.2 ms measured on an RTX 3060 Ti), so the
        // statistics in the UI look like a real run instead of "0.0 ms".
        while (sw.Elapsed.TotalMilliseconds < EmulatedCaptureMs) Thread.SpinWait(50);
        return SampleResult.NewFrame;
    }

    public bool TryGetPreview(out byte[] bgra, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (!_haveFrame)
            {
                bgra = [];
                width = height = 0;
                return false;
            }
            var src = _front;
            for (int p = 0, i = 0; p < W * H; p++, i += 3)
            {
                _bgra[4 * p] = (byte)(src[i + 2] * 255 + 0.5f);
                _bgra[4 * p + 1] = (byte)(src[i + 1] * 255 + 0.5f);
                _bgra[4 * p + 2] = (byte)(src[i] * 255 + 0.5f);
                _bgra[4 * p + 3] = 255;
            }
            bgra = (byte[])_bgra.Clone();
        }
        width = W;
        height = H;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _renderer.Join(500);
    }

    private void RenderLoop()
    {
        var clock = Stopwatch.StartNew();
        long next = 0;
        while (!_disposed)
        {
            try
            {
                Render(_back, clock.Elapsed.TotalSeconds);
                lock (_frameLock)
                {
                    (_front, _back) = (_back, _front);
                    _haveFrame = true;
                }
                _newFrame.Set();
            }
            catch (Exception ex)
            {
                Log.Error("Demo scene failed", ex);
                return;
            }
            next += FrameMs;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            else next = clock.ElapsedMilliseconds;
        }
    }

    // ------------------------------------------------------------------ scene

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private static (float R, float G, float B) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>Cheap smooth 1-D "noise" (sum of sines) for ridges, clouds and waves.</summary>
    private static float Ridge(float x, float seed) =>
        0.5f * MathF.Sin(x * 2.1f + seed) + 0.27f * MathF.Sin(x * 4.7f + seed * 1.7f) + 0.13f * MathF.Sin(x * 11.3f + seed * 2.3f);

    private static void Render(float[] rgb, double time)
    {
        float t = (float)time;
        // slow "time of day" drift: the sun sinks and rises again, the palette breathes with it
        float day = 0.5f + 0.5f * MathF.Sin(t * 0.15f + 1.2f);
        float sunX = 0.66f + 0.05f * MathF.Sin(t * 0.11f);
        const float horizon = 0.63f;
        float sunY = horizon - 0.06f - 0.12f * day;
        const float sunR = 0.06f;

        var zenith = Mix((0.13f, 0.10f, 0.48f), (0.12f, 0.24f, 0.62f), day);
        var mid = Mix((0.78f, 0.20f, 0.62f), (0.80f, 0.32f, 0.66f), day);
        var low = Mix((1.00f, 0.45f, 0.16f), (1.00f, 0.66f, 0.28f), day);
        var glow = (1.00f, 0.88f, 0.58f);
        var cool = (0.05f, 0.62f, 0.86f);

        const float aspect = (float)W / H;
        for (int y = 0; y < H; y++)
        {
            float v = (y + 0.5f) / H;
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W;
                (float R, float G, float B) c;
                float dx = (u - sunX) * aspect, dy = v - sunY;
                if (v < horizon)
                {
                    // sky: zenith → magenta → orange near the horizon; the far side (left) turns cool cyan
                    float h = v / horizon;
                    c = h < 0.5f ? Mix(zenith, mid, Smooth(0f, 0.5f, h)) : Mix(mid, low, Smooth(0.5f, 1f, h));
                    c = Mix(c, Mix(c, cool, 0.85f), MathF.Pow(1 - u, 1.6f) * (1 - 0.6f * h) * 0.9f);
                    // sun disc + halo
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float halo = MathF.Exp(-d * 4.5f) * 0.8f + MathF.Exp(-d * 16f) * 0.6f;
                    c = Mix(c, glow, Math.Clamp(halo, 0f, 1f));
                    if (d < sunR) c = Mix(c, (1f, 0.98f, 0.9f), Smooth(sunR, sunR * 0.7f, d));
                    // drifting clouds, lit from below by the sun
                    float cy = 0.2f + 0.05f * MathF.Sin(u * 3f + t * 0.05f);
                    float band = Smooth(0.08f, 0f, MathF.Abs(v - cy - 0.03f * Ridge(u * 6 + t * 0.04f, 1.3f)));
                    float cloud = band * Smooth(0.0f, 0.6f, 0.5f + 0.5f * Ridge(u * 9 - t * 0.07f, 4.1f));
                    var lit = Mix((0.55f, 0.18f, 0.55f), (1f, 0.62f, 0.50f), Math.Clamp(1 - MathF.Abs(u - sunX) * 1.5f, 0, 1));
                    c = Mix(c, lit, cloud * 0.75f);
                    // far and near mountain ridges (parallax)
                    float far = horizon - 0.07f - 0.05f * Ridge(u * 3.2f + t * 0.010f, 0.7f);
                    float near = horizon - 0.012f - 0.09f * MathF.Max(0, Ridge(u * 2.1f + t * 0.02f + 2f, 2.9f) + 0.15f)
                                 * (1.3f - 0.8f * Smooth(0.3f, 0.6f, u));
                    float aa = 1f / H;
                    c = Mix(c, Mix((0.42f, 0.18f, 0.48f), low, 0.3f), 0.8f * Smooth(far - aa, far + aa, v));
                    c = Mix(c, (0.14f, 0.07f, 0.22f), Smooth(near - aa, near + aa, v));
                }
                else
                {
                    // sea: mirrored sky, deeper towards the viewer, with a shimmering sun path
                    float depth = (v - horizon) / (1 - horizon);
                    c = Mix(Mix(low, mid, 0.45f), (0.06f, 0.22f, 0.48f), Smooth(0f, 0.85f, depth));
                    c = Mix(c, Mix(c, cool, 0.7f), MathF.Pow(1 - u, 1.5f) * 0.8f);
                    float wave = MathF.Sin(v * 260f - t * 2.2f + MathF.Sin(u * 40f + t) * 1.5f);
                    float path = MathF.Exp(-MathF.Pow((u - sunX) * aspect / (0.06f + depth * 0.24f), 2));
                    float glint = path * Smooth(0.2f, 1f, wave) * (1 - depth * 0.5f);
                    c = Mix(c, glow, Math.Clamp(glint * 0.9f + path * 0.3f, 0f, 1f));
                }
                // light vignette like a graded film frame
                float vig = 1 - 0.12f * MathF.Pow(MathF.Abs(u - 0.5f) * 2, 2.5f) - 0.12f * MathF.Pow(MathF.Abs(v - 0.5f) * 2, 3f);
                int i = (y * W + x) * 3;
                rgb[i] = Math.Clamp(c.R * vig, 0f, 1f);
                rgb[i + 1] = Math.Clamp(c.G * vig, 0f, 1f);
                rgb[i + 2] = Math.Clamp(c.B * vig, 0f, 1f);
            }
        }
    }

    private void AverageZones(float[] rgb, Span<float> dest)
    {
        var zones = _zones;
        int n = Math.Min(zones.Length, dest.Length / 3);
        for (int z = 0; z < n; z++)
        {
            var zone = zones[z];
            if (!zone.Enabled)
            {
                dest[3 * z] = dest[3 * z + 1] = dest[3 * z + 2] = 0;
                continue;
            }
            int x0 = Math.Clamp((int)MathF.Floor(MathF.Min(zone.Left, zone.Right) * W), 0, W - 1);
            int x1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(zone.Left, zone.Right) * W), x0 + 1, W);
            int y0 = Math.Clamp((int)MathF.Floor(MathF.Min(zone.Top, zone.Bottom) * H), 0, H - 1);
            int y1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(zone.Top, zone.Bottom) * H), y0 + 1, H);
            float r = 0, g = 0, b = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 3;
                    r += rgb[i];
                    g += rgb[i + 1];
                    b += rgb[i + 2];
                }
            }
            float inv = 1f / ((x1 - x0) * (y1 - y0));
            dest[3 * z] = r * inv;
            dest[3 * z + 1] = g * inv;
            dest[3 * z + 2] = b * inv;
        }
        for (int i = n * 3; i < dest.Length; i++) dest[i] = 0;
    }
}
