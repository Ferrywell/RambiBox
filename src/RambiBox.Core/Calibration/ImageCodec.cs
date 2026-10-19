using Vortice.WIC;

namespace RambiBox.Core.Calibration;

/// <summary>Simple 8-bit RGB image (row-major, 3 bytes per pixel, no padding). sRGB encoded.</summary>
internal sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? data = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length < width * height * 3) throw new ArgumentException("Pixel buffer too small", nameof(data));
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
}

/// <summary>
/// JPEG/PNG/... decoding (and JPEG encoding for tests/debug) through the Windows Imaging Component.
/// WIC ships with Windows (no admin rights, no extra installs) and its factory is free-threaded,
/// so this is safe to call from thread-pool threads.
/// </summary>
internal static class ImageCodec
{
    /// <summary>Larger images are downscaled while decoding (keeps memory bounded for hostile/huge uploads).</summary>
    public const int MaxDecodeLongSide = 2400;

    /// <summary>Decodes any WIC-supported image into 8-bit sRGB RGB. Throws on invalid data.</summary>
    public static RgbImage Decode(byte[] encoded, int maxLongSide = MaxDecodeLongSide)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length < 16) throw new InvalidDataException("Image data too short");

        using var factory = new IWICImagingFactory();
        using var stream = factory.CreateStream(encoded);
        using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        int w = size.Width, h = size.Height;
        if (w <= 0 || h <= 0 || (long)w * h > 200_000_000L) throw new InvalidDataException("Unsupported image size");

        IWICBitmapSource source = frame;
        IWICBitmapScaler? scaler = null;
        try
        {
            int longSide = Math.Max(w, h);
            if (longSide > maxLongSide)
            {
                double s = (double)maxLongSide / longSide;
                int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
                scaler = factory.CreateBitmapScaler();
                scaler.Initialize(frame, (uint)nw, (uint)nh, BitmapInterpolationMode.Fant);
                source = scaler;
                w = nw;
                h = nh;
            }

            using var converter = factory.CreateFormatConverter();
            converter.Initialize(source, PixelFormat.Format24bppRGB, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            var img = new RgbImage(w, h);
            converter.CopyPixels((uint)(w * 3), img.Data);
            return img;
        }
        finally
        {
            scaler?.Dispose();
        }
    }

    /// <summary>Encodes as baseline JPEG (used by tests and the optional debug dump).</summary>
    public static byte[] EncodeJpeg(RgbImage img, float quality = 0.92f)
    {
        using var factory = new IWICImagingFactory();
        using var ms = new MemoryStream();
        using (var wicStream = factory.CreateStream(ms))
        using (var encoder = factory.CreateEncoder(ContainerFormat.Jpeg, wicStream))
        {
            using var frame = encoder.CreateNewFrame(out var props);
            props.Set("ImageQuality", quality);
            frame.Initialize(props);
            frame.SetSize((uint)img.Width, (uint)img.Height);
            var fmt = PixelFormat.Format24bppBGR;
            frame.SetPixelFormat(ref fmt);
            // WIC's JPEG encoder wants BGR.
            var bgr = new byte[img.Data.Length];
            for (int i = 0; i + 2 < bgr.Length; i += 3)
            {
                bgr[i] = img.Data[i + 2];
                bgr[i + 1] = img.Data[i + 1];
                bgr[i + 2] = img.Data[i];
            }
            frame.WritePixels((uint)img.Height, (uint)(img.Width * 3), bgr);
            frame.Commit();
            encoder.Commit();
            props.Dispose();
        }
        return ms.ToArray();
    }
}
