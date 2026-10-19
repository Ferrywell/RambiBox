using RambiBox.Core.Engine;

namespace RambiBox.App.Services;

/// <summary>Test patterns the UI can show on the LEDs (see <see cref="AppHost.SetOverride"/>).</summary>
public enum UiOverride
{
    None,
    /// <summary>All LEDs white through the white balance (Colors page).</summary>
    White,
    /// <summary>LED 1 red + a green dot walking in strip order (LED layout page).</summary>
    Identify,
    /// <summary>Red → green → blue → white, raw (Device page).</summary>
    TestColors,
}

/// <summary>All LEDs white, rendered through white balance + brightness so the user can judge the white balance.</summary>
internal sealed class WhiteOverride : ILedOverride
{
    public bool ApplyWhiteBalance => true;

    public void Render(Span<float> rgb, int ledCount, double timeSeconds) => rgb[..(ledCount * 3)].Fill(1f);
}

/// <summary>
/// Strip-order chase: LED 0 is red, a green dot (with a short fading tail) walks from LED 1 to the end.
/// Raw colors, so it is independent of all color settings.
/// </summary>
internal sealed class IdentifyOverride : ILedOverride
{
    private const double LedsPerSecond = 14;
    public bool ApplyWhiteBalance => false;

    public void Render(Span<float> rgb, int ledCount, double timeSeconds)
    {
        rgb[..(ledCount * 3)].Clear();
        if (ledCount <= 0) return;
        rgb[0] = 1f; // LED 1: red
        if (ledCount < 2) return;
        int span = ledCount - 1;
        double pos = timeSeconds * LedsPerSecond % (span + 6); // short pause at the end of each run
        int head = (int)pos;
        for (int k = 0; k < 4; k++)
        {
            int i = head - k;
            if (i < 0 || i >= span) continue;
            float g = k == 0 ? 1f : 0.35f / k;
            rgb[3 * (i + 1) + 1] = g;
        }
    }
}

/// <summary>Cycles red → green → blue → white (one second each), raw.</summary>
internal sealed class TestColorsOverride : ILedOverride
{
    public bool ApplyWhiteBalance => false;

    public void Render(Span<float> rgb, int ledCount, double timeSeconds)
    {
        int phase = (int)(timeSeconds % 4);
        float r = phase is 0 or 3 ? 1 : 0, g = phase is 1 or 3 ? 1 : 0, b = phase is 2 or 3 ? 1 : 0;
        for (int i = 0; i < ledCount; i++)
        {
            rgb[3 * i] = r;
            rgb[3 * i + 1] = g;
            rgb[3 * i + 2] = b;
        }
    }
}
