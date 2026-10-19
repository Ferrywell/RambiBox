using RambiBox.Core.Config;

namespace RambiBox.Core.Engine;

/// <summary>
/// Turns captured zone colors into LED values. Pure CPU math on a few hundred floats per frame — no allocations.
/// Pipeline (Ambilight): dark threshold → gamma → saturation/hue → smoothing → min brightness → brightness × white balance.
/// </summary>
public sealed class ColorProcessor
{
    private float[] _smoothed = Array.Empty<float>();
    private bool _hasState;

    // Cached per-settings coefficients
    private float _gamma = 1, _sat = 1, _dark, _min, _bright = 1;
    private float _wbR = 1, _wbG = 1, _wbB = 1;
    private float _tauSeconds;
    private float[]? _hueMatrix; // 3x3 row-major, null when hue shift is 0

    public void Configure(ColorSettings c)
    {
        _gamma = (float)c.Gamma;
        _sat = (float)c.Saturation;
        _dark = (float)c.DarkThreshold;
        _min = (float)c.MinBrightness;
        _bright = (float)c.Brightness;
        _wbR = (float)c.WhiteR;
        _wbG = (float)c.WhiteG;
        _wbB = (float)c.WhiteB;
        _tauSeconds = c.SmoothingMs / 1000f;
        _hueMatrix = Math.Abs(c.HueShift) < 0.01 ? null : HueRotation((float)c.HueShift);
    }

    /// <summary>Forget the smoothing state (next frame jumps straight to target).</summary>
    public void Reset() => _hasState = false;

    /// <summary>
    /// Applies the creative adjustments (dark threshold, gamma, saturation, hue) in place.
    /// Input/output: sRGB-encoded floats 0..1.
    /// </summary>
    public void Adjust(Span<float> rgb)
    {
        bool gamma = Math.Abs(_gamma - 1f) > 0.001f;
        bool sat = Math.Abs(_sat - 1f) > 0.001f;
        var hm = _hueMatrix;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            float r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
            if (MathF.Max(r, MathF.Max(g, b)) < _dark)
            {
                rgb[i] = rgb[i + 1] = rgb[i + 2] = 0;
                continue;
            }
            if (gamma)
            {
                r = MathF.Pow(r, _gamma);
                g = MathF.Pow(g, _gamma);
                b = MathF.Pow(b, _gamma);
            }
            if (sat)
            {
                float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = y + (r - y) * _sat;
                g = y + (g - y) * _sat;
                b = y + (b - y) * _sat;
            }
            if (hm is not null)
            {
                float nr = hm[0] * r + hm[1] * g + hm[2] * b;
                float ng = hm[3] * r + hm[4] * g + hm[5] * b;
                float nb = hm[6] * r + hm[7] * g + hm[8] * b;
                r = nr; g = ng; b = nb;
            }
            rgb[i] = Math.Clamp(r, 0f, 1f);
            rgb[i + 1] = Math.Clamp(g, 0f, 1f);
            rgb[i + 2] = Math.Clamp(b, 0f, 1f);
        }
    }

    /// <summary>
    /// Frame-rate independent exponential smoothing towards <paramref name="target"/>.
    /// Writes the smoothed result to <paramref name="output"/>. Returns the largest remaining
    /// difference between smoothed state and target (0..1) so callers can idle once converged.
    /// </summary>
    public float Smooth(ReadOnlySpan<float> target, Span<float> output, double dtSeconds)
    {
        if (_smoothed.Length != target.Length)
        {
            _smoothed = new float[target.Length];
            _hasState = false;
        }
        if (!_hasState || _tauSeconds <= 0)
        {
            target.CopyTo(_smoothed);
            target.CopyTo(output);
            _hasState = true;
            return 0;
        }
        // Clamp dt so a long stall (sleep, debugger) doesn't produce a weird jump, but still converges.
        float dt = (float)Math.Clamp(dtSeconds, 0, 1);
        float alpha = 1f - MathF.Exp(-dt / _tauSeconds);
        float maxDiff = 0;
        for (int i = 0; i < target.Length; i++)
        {
            float s = _smoothed[i] + (target[i] - _smoothed[i]) * alpha;
            float d = MathF.Abs(target[i] - s);
            if (d < 0.5f / 255f) s = target[i]; // snap: avoids endless tiny updates
            else if (d > maxDiff) maxDiff = d;
            _smoothed[i] = s;
            output[i] = s;
        }
        return maxDiff;
    }

    /// <summary>
    /// Final stage: min brightness, brightness and white balance, quantized to bytes.
    /// <paramref name="whiteBalanceOnly"/> skips min brightness (used by raw-ish overrides).
    /// </summary>
    public void ToDevice(ReadOnlySpan<float> rgb, Span<byte> dest, bool whiteBalanceOnly = false)
    {
        float min = whiteBalanceOnly ? 0 : _min;
        float scale = 1f - min;
        float kr = _bright * _wbR * 255f, kg = _bright * _wbG * 255f, kb = _bright * _wbB * 255f;
        for (int i = 0, n = Math.Min(rgb.Length, dest.Length); i + 2 < n; i += 3)
        {
            dest[i] = ToByte((min + rgb[i] * scale) * kr);
            dest[i + 1] = ToByte((min + rgb[i + 1] * scale) * kg);
            dest[i + 2] = ToByte((min + rgb[i + 2] * scale) * kb);
        }
    }

    public static void Raw(ReadOnlySpan<float> rgb, Span<byte> dest)
    {
        for (int i = 0, n = Math.Min(rgb.Length, dest.Length); i < n; i++)
            dest[i] = ToByte(rgb[i] * 255f);
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>Luminance-preserving hue rotation matrix (same as CSS hue-rotate()).</summary>
    internal static float[] HueRotation(float degrees)
    {
        float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return
        [
            0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f,
            0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f,
            0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f,
        ];
    }

    /// <summary>HSV (h in degrees, s/v 0..1) to RGB 0..1.</summary>
    public static (float R, float G, float B) Hsv(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360 / 60f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
