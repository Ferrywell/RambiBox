using System.Runtime.CompilerServices;

namespace RambiBox.Core.Capture;

/// <summary>
/// Pure color math used by the capture pipeline: sRGB transfer functions, half-float decoding and
/// HDR (scRGB) → SDR tone mapping. Everything here is allocation free and GPU independent (unit-testable).
/// </summary>
internal static class ColorMath
{
    /// <summary>scRGB: 1.0 = 80 nits.</summary>
    public const float ScRgbNitsPerUnit = 80f;

    /// <summary>SDR white level assumed when Windows does not report one.</summary>
    public const float DefaultSdrWhiteNits = 200f;

    /// <summary>
    /// Start of the highlight roll-off (linear, relative to SDR white). Everything below is passed through unchanged,
    /// so SDR content shown on an HDR desktop maps back to (almost exactly) its original values.
    /// </summary>
    public const float DefaultKnee = 0.9f;

    /// <summary>sRGB 8-bit code value → linear light 0..1.</summary>
    public static readonly float[] SrgbByteToLinear = BuildDecodeLut();

    private const int EncodeLutBits = 12;
    private const int EncodeLutMax = (1 << EncodeLutBits) - 1;
    private static readonly byte[] LinearToSrgbByteLut = BuildEncodeLut();

    public static float SrgbToLinear(float v)
    {
        if (v <= 0f) return 0f;
        if (v >= 1f) return 1f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSrgb(float l)
    {
        if (!(l > 0f)) return 0f; // also catches NaN
        if (l >= 1f) return 1f;
        return l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Fast approximate linear → sRGB byte (12-bit LUT; used for the preview image only).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToSrgbByte(float l)
    {
        if (!(l > 0f)) return 0;
        if (l >= 1f) return 255;
        return LinearToSrgbByteLut[(int)(l * EncodeLutMax + 0.5f)];
    }

    /// <summary>
    /// IEEE 754 binary16 → float for the values the desktop can contain. Negative values (out-of-gamut scRGB)
    /// return 0, NaN returns 0 and +Inf returns 65504 (max half), since everything is clamped later anyway.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HalfToFloatNonNegative(ushort h)
    {
        if ((h & 0x8000) != 0) return 0f;            // negative (or -0)
        if (h >= 0x7C00) return h == 0x7C00 ? 65504f : 0f; // +Inf / NaN
        // Shift exponent+mantissa into float position and rebias by multiplying with 2^112.
        // Works for normals and subnormals alike (a subnormal half becomes a subnormal float, which the multiply normalizes).
        return BitConverter.UInt32BitsToSingle((uint)h << 13) * 5.192296858534828e33f; // 2^112
    }

    /// <summary>
    /// Smooth highlight compression: identity below <paramref name="knee"/>, then an exponential shoulder with slope 1
    /// at the knee that approaches 1.0 asymptotically.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SoftClip(float x, float knee)
    {
        if (x <= knee) return x;
        float range = 1f - knee;
        return knee + range * (1f - MathF.Exp(-(x - knee) / range));
    }

    /// <summary>
    /// Tone-maps one linear scRGB pixel (already divided by the SDR white level) into 0..1 linear SDR.
    /// Compression is driven by the max channel and applied as a common scale, so hue is preserved.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToneMap(ref float r, ref float g, ref float b, float knee)
    {
        float m = MathF.Max(r, MathF.Max(g, b));
        if (m > knee)
        {
            float s = SoftClip(m, knee) / m;
            r *= s; g *= s; b *= s;
        }
    }

    /// <summary>SDR white level from DISPLAYCONFIG_SDR_WHITE_LEVEL (1000 = 80 nits) in nits.</summary>
    public static float SdrWhiteLevelToNits(uint sdrWhiteLevel) => sdrWhiteLevel / 1000f * ScRgbNitsPerUnit;

    private static float[] BuildDecodeLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++) lut[i] = SrgbToLinear(i / 255f);
        return lut;
    }

    private static byte[] BuildEncodeLut()
    {
        var lut = new byte[EncodeLutMax + 1];
        for (int i = 0; i <= EncodeLutMax; i++)
            lut[i] = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(i / (float)EncodeLutMax) * 255f), 0, 255);
        return lut;
    }
}
