using RambiBox.Core.Capture;

namespace RambiBox.Tests.Capture;

public class ColorMathTests
{
    [Fact]
    public void HalfToFloat_matches_System_Half_for_all_non_negative_finite_values()
    {
        for (int h = 0; h < 0x7C00; h++)
        {
            float expected = (float)BitConverter.UInt16BitsToHalf((ushort)h);
            float actual = ColorMath.HalfToFloatNonNegative((ushort)h);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void HalfToFloat_clamps_negative_nan_and_infinity()
    {
        Assert.Equal(0f, ColorMath.HalfToFloatNonNegative(BitConverter.HalfToUInt16Bits((Half)(-1.5f))));
        Assert.Equal(0f, ColorMath.HalfToFloatNonNegative(0x8000)); // -0
        Assert.Equal(65504f, ColorMath.HalfToFloatNonNegative(0x7C00)); // +Inf
        Assert.Equal(0f, ColorMath.HalfToFloatNonNegative(0x7E00)); // NaN
    }

    [Fact]
    public void SoftClip_is_identity_below_knee_continuous_monotonic_and_bounded()
    {
        const float knee = 0.9f;
        Assert.Equal(0.5f, ColorMath.SoftClip(0.5f, knee));
        Assert.Equal(knee, ColorMath.SoftClip(knee, knee));
        Assert.InRange(ColorMath.SoftClip(knee + 1e-4f, knee) - knee, 0.9e-4f, 1.01e-4f); // slope 1 at the knee
        float prev = 0;
        for (float x = 0; x < 50; x += 0.01f)
        {
            float y = ColorMath.SoftClip(x, knee);
            Assert.True(y >= prev - 1e-6f, $"not monotonic at {x}");
            Assert.True(y <= 1f, $"exceeds 1 at {x}");
            prev = y;
        }
        Assert.True(ColorMath.SoftClip(10f, knee) > 0.999f);
    }

    [Fact]
    public void ToneMap_preserves_hue_ratio()
    {
        float r = 4f, g = 2f, b = 1f;
        ColorMath.ToneMap(ref r, ref g, ref b, 0.9f);
        Assert.True(r <= 1f);
        Assert.Equal(0.5f, g / r, 4);
        Assert.Equal(0.25f, b / r, 4);
    }

    [Fact]
    public void Srgb_transfer_roundtrips_and_lut_matches()
    {
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            float l = ColorMath.SrgbToLinear(v);
            Assert.Equal(l, ColorMath.SrgbByteToLinear[i], 6);
            Assert.Equal(v, ColorMath.LinearToSrgb(l), 4);
            Assert.InRange(ColorMath.LinearToSrgbByte(l), Math.Max(0, i - 1), Math.Min(255, i + 1));
        }
        Assert.Equal(0.7354f, ColorMath.LinearToSrgb(0.5f), 3);
    }

    [Fact]
    public void Sdr_white_level_conversion()
    {
        Assert.Equal(80f, ColorMath.SdrWhiteLevelToNits(1000));
        Assert.Equal(200f, ColorMath.SdrWhiteLevelToNits(2500));
    }
}
