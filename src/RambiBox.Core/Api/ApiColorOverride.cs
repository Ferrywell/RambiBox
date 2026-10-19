using RambiBox.Core.Engine;

namespace RambiBox.Core.Api;

/// <summary>
/// Shows the colors an API client set. Goes through the full LED correction (color matrix, channel gamma,
/// white balance × brightness) like Prismatik's device path, so <c>setcolor:1-255,255,255</c> is the calibrated white.
/// </summary>
public sealed class ApiColorOverride : ILedOverride
{
    private byte[] _rgb;

    public ApiColorOverride(byte[] initialRgb) => _rgb = (byte[])initialRgb.Clone();

    public bool ApplyWhiteBalance => true;
    public OverrideColorPath ColorPath => OverrideColorPath.LedCorrection;

    /// <summary>Replaces the shown colors (R,G,B bytes per LED). Thread-safe.</summary>
    public void SetColors(byte[] rgb) => Volatile.Write(ref _rgb, (byte[])rgb.Clone());

    /// <summary>Copy of the shown colors.</summary>
    public byte[] GetColors() => (byte[])Volatile.Read(ref _rgb).Clone();

    public void Render(Span<float> rgb, int ledCount, double timeSeconds)
    {
        var c = Volatile.Read(ref _rgb);
        int n = Math.Min(ledCount * 3, rgb.Length);
        for (int i = 0; i < n; i++) rgb[i] = i < c.Length ? c[i] / 255f : 0f;
    }
}
