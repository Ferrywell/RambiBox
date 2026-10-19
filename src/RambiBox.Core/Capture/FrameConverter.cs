using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RambiBox.Core.Capture;

/// <summary>Orientation of the desktop relative to the duplicated (native, unrotated) surface.</summary>
internal enum SurfaceRotation
{
    Identity = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
}

/// <summary>
/// Maps desktop pixels to texels of the (unrotated) duplicated surface.
/// DXGI hands out the desktop image in the monitor's native orientation; for rotated monitors
/// (DXGI_MODE_ROTATION_ROTATE90/180/270) the image must be rotated back so zones (defined as the user sees the
/// desktop) line up. Continuous mapping, desktop (u,v) → texture (u,v), all normalized 0..1:
/// <list type="bullet">
/// <item>Identity: (u, v)</item>
/// <item>Rotate90: (v, 1 − u)</item>
/// <item>Rotate180: (1 − u, 1 − v)</item>
/// <item>Rotate270: (1 − v, u)</item>
/// </list>
/// (Same convention as the Microsoft desktop duplication sample.)
/// </summary>
internal static class RotationMapper
{
    public static SurfaceRotation FromDxgi(int dxgiModeRotation) => dxgiModeRotation switch
    {
        2 => SurfaceRotation.Rotate90,
        3 => SurfaceRotation.Rotate180,
        4 => SurfaceRotation.Rotate270,
        _ => SurfaceRotation.Identity, // 0 = unspecified, 1 = identity
    };

    /// <summary>Desktop-oriented size of a texture of <paramref name="texW"/> × <paramref name="texH"/>.</summary>
    public static (int Width, int Height) DesktopSize(SurfaceRotation rot, int texW, int texH)
        => rot is SurfaceRotation.Rotate90 or SurfaceRotation.Rotate270 ? (texH, texW) : (texW, texH);

    /// <summary>Continuous mapping of a normalized desktop point to a normalized texture point.</summary>
    public static (float U, float V) DesktopToTexture(SurfaceRotation rot, float u, float v) => rot switch
    {
        SurfaceRotation.Rotate90 => (v, 1f - u),
        SurfaceRotation.Rotate180 => (1f - u, 1f - v),
        SurfaceRotation.Rotate270 => (1f - v, u),
        _ => (u, v),
    };

    /// <summary>
    /// Byte offsets to walk the texture in desktop order: texel of desktop pixel (dx,dy) is at
    /// <c>Base + dx*StepX + dy*StepY</c>.
    /// </summary>
    public static (long Base, long StepX, long StepY) Walk(SurfaceRotation rot, int texW, int texH, int rowPitch, int bytesPerPixel)
    {
        long pitch = rowPitch, bpp = bytesPerPixel;
        return rot switch
        {
            // texel (tx,ty) = (dy, texH-1-dx)
            SurfaceRotation.Rotate90 => ((texH - 1) * pitch, -pitch, bpp),
            // texel = (texW-1-dx, texH-1-dy)
            SurfaceRotation.Rotate180 => ((texH - 1) * pitch + (texW - 1) * bpp, -bpp, -pitch),
            // texel = (texW-1-dy, dx)
            SurfaceRotation.Rotate270 => ((texW - 1) * bpp, pitch, -bpp),
            _ => (0, bpp, pitch),
        };
    }
}

/// <summary>
/// Converts the small mapped staging image into a desktop-oriented, linear-light float RGB buffer
/// (interleaved R,G,B; 0..1 relative to SDR white). All later math (zones, black bars, preview) works on that buffer.
/// </summary>
internal static class FrameConverter
{
    /// <summary>BGRA8 (sRGB-encoded bytes, as produced by B8G8R8A8_UNORM[_SRGB]) → linear RGB.</summary>
    public static void ConvertBgra8(ReadOnlySpan<byte> src, int texW, int texH, int rowPitch, SurfaceRotation rot, Span<float> dstRgb)
    {
        var (dw, dh) = RotationMapper.DesktopSize(rot, texW, texH);
        if (dstRgb.Length < dw * dh * 3) throw new ArgumentException("Destination too small", nameof(dstRgb));
        if (src.Length < (texH - 1) * rowPitch + texW * 4) throw new ArgumentException("Source too small", nameof(src));
        var (b0, sx, sy) = RotationMapper.Walk(rot, texW, texH, rowPitch, 4);
        ref byte s0 = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dstRgb);
        float[] lut = ColorMath.SrgbByteToLinear;
        int o = 0;
        for (int y = 0; y < dh; y++)
        {
            long p = b0 + y * sy;
            for (int x = 0; x < dw; x++, p += sx, o += 3)
            {
                ref byte px = ref Unsafe.Add(ref s0, (nint)p);
                Unsafe.Add(ref d, o) = lut[Unsafe.Add(ref px, 2)];
                Unsafe.Add(ref d, o + 1) = lut[Unsafe.Add(ref px, 1)];
                Unsafe.Add(ref d, o + 2) = lut[px];
            }
        }
    }

    /// <summary>
    /// R16G16B16A16_FLOAT scRGB (linear, 1.0 = 80 nits) → linear SDR RGB: divide by the SDR white level,
    /// soft-clip highlights (hue preserving), clamp negatives (out-of-gamut) to 0.
    /// </summary>
    public static void ConvertFp16(ReadOnlySpan<byte> src, int texW, int texH, int rowPitch, SurfaceRotation rot,
        float sdrWhiteScRgb, float knee, Span<float> dstRgb)
    {
        var (dw, dh) = RotationMapper.DesktopSize(rot, texW, texH);
        if (dstRgb.Length < dw * dh * 3) throw new ArgumentException("Destination too small", nameof(dstRgb));
        if (src.Length < (texH - 1) * rowPitch + texW * 8) throw new ArgumentException("Source too small", nameof(src));
        var (b0, sx, sy) = RotationMapper.Walk(rot, texW, texH, rowPitch, 8);
        float inv = 1f / MathF.Max(sdrWhiteScRgb, 1e-3f);
        ref byte s0 = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dstRgb);
        int o = 0;
        for (int y = 0; y < dh; y++)
        {
            long p = b0 + y * sy;
            for (int x = 0; x < dw; x++, p += sx, o += 3)
            {
                ref ushort px = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref s0, (nint)p));
                float r = ColorMath.HalfToFloatNonNegative(px) * inv;
                float g = ColorMath.HalfToFloatNonNegative(Unsafe.Add(ref px, 1)) * inv;
                float b = ColorMath.HalfToFloatNonNegative(Unsafe.Add(ref px, 2)) * inv;
                ColorMath.ToneMap(ref r, ref g, ref b, knee);
                Unsafe.Add(ref d, o) = r;
                Unsafe.Add(ref d, o + 1) = g;
                Unsafe.Add(ref d, o + 2) = b;
            }
        }
    }

    /// <summary>Linear RGB → BGRA8 sRGB (alpha 255), top-down, for the UI preview.</summary>
    public static void ToPreviewBgra(ReadOnlySpan<float> rgb, int pixelCount, Span<byte> bgra)
    {
        for (int i = 0, o = 0, s = 0; i < pixelCount; i++, o += 4, s += 3)
        {
            bgra[o] = ColorMath.LinearToSrgbByte(rgb[s + 2]);
            bgra[o + 1] = ColorMath.LinearToSrgbByte(rgb[s + 1]);
            bgra[o + 2] = ColorMath.LinearToSrgbByte(rgb[s]);
            bgra[o + 3] = 255;
        }
    }
}
