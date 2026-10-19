using RambiBox.Core.Capture;

namespace RambiBox.Tests.Capture;

public class FrameConverterTests
{
    /// <summary>Builds a BGRA texture where each texel encodes its index (B = index, G = 0, R = 255).</summary>
    private static byte[] IndexTexture(int w, int h, int pitch)
    {
        var data = new byte[pitch * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = y * pitch + x * 4;
            data[o] = (byte)(y * w + x);
            data[o + 2] = 255;
            data[o + 3] = 255;
        }
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotation_walk_matches_continuous_mapping(int rotation)
    {
        var rot = (SurfaceRotation)rotation;
        const int tw = 5, th = 3, pitch = 32; // padded rows
        var tex = IndexTexture(tw, th, pitch);
        var (dw, dh) = RotationMapper.DesktopSize(rot, tw, th);
        var dst = new float[dw * dh * 3];
        FrameConverter.ConvertBgra8(tex, tw, th, pitch, rot, dst);

        for (int dy = 0; dy < dh; dy++)
        for (int dx = 0; dx < dw; dx++)
        {
            var (u, v) = RotationMapper.DesktopToTexture(rot, (dx + 0.5f) / dw, (dy + 0.5f) / dh);
            int tx = (int)(u * tw), ty = (int)(v * th);
            float expected = ColorMath.SrgbByteToLinear[ty * tw + tx];
            int o = (dy * dw + dx) * 3;
            Assert.Equal(expected, dst[o + 2]); // B channel carries the index
            Assert.Equal(1f, dst[o]);           // R = 255
            Assert.Equal(0f, dst[o + 1]);
        }
    }

    [Fact]
    public void Rotate90_swaps_dimensions_and_moves_top_left_correctly()
    {
        // Texture top-left texel must end up at the desktop's top-right for Rotate90: (u_t, v_t) = (v_d, 1 - u_d).
        Assert.Equal((3, 5), RotationMapper.DesktopSize(SurfaceRotation.Rotate90, 5, 3));
        var (u, v) = RotationMapper.DesktopToTexture(SurfaceRotation.Rotate90, 0.99f, 0.01f);
        Assert.True(u < 0.05f && v < 0.05f);
        Assert.Equal(SurfaceRotation.Rotate90, RotationMapper.FromDxgi(2));
        Assert.Equal(SurfaceRotation.Identity, RotationMapper.FromDxgi(0));
        Assert.Equal(SurfaceRotation.Rotate270, RotationMapper.FromDxgi(4));
    }

    private static byte[] Fp16Texture(int w, int h, Func<int, int, (float r, float g, float b)> f)
    {
        var data = new byte[w * h * 8];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b) = f(x, y);
            int o = (y * w + x) * 8;
            BitConverter.TryWriteBytes(data.AsSpan(o), (Half)r);
            BitConverter.TryWriteBytes(data.AsSpan(o + 2), (Half)g);
            BitConverter.TryWriteBytes(data.AsSpan(o + 4), (Half)b);
            BitConverter.TryWriteBytes(data.AsSpan(o + 6), (Half)1f);
        }
        return data;
    }

    [Fact]
    public void Fp16_is_divided_by_sdr_white_soft_clipped_and_negatives_clamped()
    {
        // SDR white 200 nits = 2.5 scRGB.
        var tex = Fp16Texture(4, 1, (x, _) => x switch
        {
            0 => (1.25f, 0.5f, 0f),   // half of SDR white, 20% → passes through
            1 => (2.5f, 2.5f, 2.5f),  // SDR white → just below 1 (inside the soft shoulder)
            2 => (12.5f, 0f, 0f),     // 1000 nits red highlight → clipped to ~1
            _ => (-0.1f, 0.25f, 0.5f) // out of gamut negative
        });
        var dst = new float[4 * 3];
        FrameConverter.ConvertFp16(tex, 4, 1, 32, SurfaceRotation.Identity, 2.5f, ColorMath.DefaultKnee, dst);

        Assert.Equal(0.5f, dst[0], 3);
        Assert.Equal(0.2f, dst[1], 3);
        Assert.Equal(0f, dst[2]);
        Assert.InRange(dst[3], 0.95f, 1f);
        Assert.Equal(dst[3], dst[4]);
        Assert.InRange(dst[6], 0.99f, 1f);
        Assert.Equal(0f, dst[7]);
        Assert.Equal(0f, dst[9]);
        Assert.Equal(0.1f, dst[10], 3);
        Assert.Equal(0.2f, dst[11], 3);
    }

    [Fact]
    public void Preview_is_bgra_srgb_with_opaque_alpha()
    {
        float[] rgb = [1f, 0.5f, 0f];
        var bgra = new byte[4];
        FrameConverter.ToPreviewBgra(rgb, 1, bgra);
        Assert.Equal(0, bgra[0]);
        Assert.InRange(bgra[1], 187, 188); // linear 0.5 → sRGB 0.735
        Assert.Equal(255, bgra[2]);
        Assert.Equal(255, bgra[3]);
    }

    [Theory]
    [InlineData(2560, 1440, 4, 160)]
    [InlineData(1920, 1080, 3, 240)]
    [InlineData(3840, 2160, 4, 240)]
    [InlineData(1440, 2560, 4, 160)]
    [InlineData(1280, 720, 3, 160)]
    [InlineData(100, 80, 0, 100)]
    public void Mip_level_targets_128_to_256_pixels(int w, int h, int level, int longSide)
    {
        int l = GpuFrameReducer.ChooseMipLevel(w, h);
        Assert.Equal(level, l);
        Assert.Equal(longSide, Math.Max(w, h) >> l);
    }
}
