using RambiBox.Core.Calibration;
using RambiBox.Core.Engine;

namespace RambiBox.Tests.Calibration;

/// <summary>
/// A simulated PC + LED strip + wall + phone camera for the calibration session: implements the app's settings target
/// and "films" the current pattern + LED drive exactly as the engine would output them.
/// <para>
/// Physics: the engine sends bytes round(255·bright·WB_c·((M·x)_c)^γc); the strip emits e_c = (byte/255)^exp_c · tint_c
/// (tint = wall color × LED spectrum); the camera sees the glow as <see cref="LedMix"/>·e and the screen as
/// <see cref="ScreenMix"/>·linear(rgb) — different spectral mixing for narrow-band LEDs and the monitor —, then applies a
/// per-frame auto white balance (random drift) and exposure.
/// </para>
/// </summary>
internal sealed class VirtualRig : ICalibrationTarget
{
    public const double Aspect = 16.0 / 9.0;

    public Rgb3 Wb = new(0.64, 1.0, 0.51);
    public Rgb3 Gamma = new(1, 1, 1);
    public double[]? Matrix;
    public double Brightness = 1;

    public Rgb3 Tint = new(1.05, 0.9, 1.45);
    public Rgb3 StripExponent = new(1.12, 1.0, 0.92);
    /// <summary>Camera response to the LED channels (narrow band: nearly pure).</summary>
    public double[] LedMix = [1.0, 0.04, 0.0, 0.03, 1.0, 0.03, 0.0, 0.05, 1.0];
    /// <summary>Camera response to the monitor's linear RGB (broader primaries: more crosstalk).</summary>
    public double[] ScreenMix = [0.86, 0.12, 0.02, 0.07, 0.88, 0.05, 0.02, 0.09, 0.89];
    public double CameraWbJitter = 0.08;
    public int Width = 960, Height = 540;
    /// <summary>Phone pose: roll (degrees, any value — portrait/upside down), screen width as a fraction of the frame width.</summary>
    public double Roll = 1, WidthFraction = 0.6;
    /// <summary>Phone-like auto exposure (mean frame luminance → target, capped at <see cref="MaxExposure"/>); null = fixed exposure.</summary>
    public double? AutoExposureTarget;
    public double? HighlightProtect;
    public double MaxExposure = 30;
    /// <summary>Room light on the wall (LEDs off), camera space.</summary>
    public Rgb3 WallLight = new(0.002, 0.002, 0.002);
    public double GlowStrength = 0.5;
    /// <summary>Exposure of the last shot.</summary>
    public double LastExposure;

    public int Applies;

    public CalibrationValues Read() => new((Wb.R, Wb.G, Wb.B), (Gamma.R, Gamma.G, Gamma.B), Matrix, Brightness);
    public void ApplyWhiteBalance(double r, double g, double b) { Wb = new Rgb3(r, g, b); Applies++; }
    public void ApplyChannelGamma(double r, double g, double b) { Gamma = new Rgb3(r, g, b); Applies++; }
    public void ApplyColorMatrix(double[]? matrix) { Matrix = matrix; Applies++; }

    /// <summary>Byte-exact LED drive (0..1 per channel) for an override color through the given path.</summary>
    public Rgb3 Drive(double r, double g, double b, OverrideColorPath path)
    {
        if (path == OverrideColorPath.Raw) return new Rgb3(Q(r), Q(g), Q(b));
        if (path == OverrideColorPath.LedCorrection && Matrix is { } m)
        {
            double nr = m[0] * r + m[1] * g + m[2] * b, ng = m[3] * r + m[4] * g + m[5] * b, nb = m[6] * r + m[7] * g + m[8] * b;
            (r, g, b) = (Math.Clamp(nr, 0, 1), Math.Clamp(ng, 0, 1), Math.Clamp(nb, 0, 1));
        }
        return new Rgb3(
            ColorFit.LedByte(r, Gamma.R, Wb.R, Brightness) / 255.0,
            ColorFit.LedByte(g, Gamma.G, Wb.G, Brightness) / 255.0,
            ColorFit.LedByte(b, Gamma.B, Wb.B, Brightness) / 255.0);
        static double Q(double v) => Math.Round(Math.Clamp(v, 0, 1) * 255) / 255;
    }

    /// <summary>Glow in camera space (before camera WB) for an LED drive.</summary>
    public Rgb3 GlowCamera(Rgb3 drive)
    {
        double er = Math.Pow(drive.R, StripExponent.R) * Tint.R, eg = Math.Pow(drive.G, StripExponent.G) * Tint.G,
               eb = Math.Pow(drive.B, StripExponent.B) * Tint.B;
        var m = LedMix;
        return new Rgb3(m[0] * er + m[1] * eg + m[2] * eb, m[3] * er + m[4] * eg + m[5] * eb, m[6] * er + m[7] * eg + m[8] * eb);
    }

    /// <summary>Screen color in camera space for an sRGB color.</summary>
    public Rgb3 ScreenCamera(int rgb)
    {
        double r = CalibrationAnalyzer.ToLinear((byte)(rgb >> 16)), g = CalibrationAnalyzer.ToLinear((byte)(rgb >> 8)), b = CalibrationAnalyzer.ToLinear((byte)rgb);
        var m = ScreenMix;
        return new Rgb3(m[0] * r + m[1] * g + m[2] * b, m[3] * r + m[4] * g + m[5] * b, m[6] * r + m[7] * g + m[8] * b);
    }

    /// <summary>A camera frame of what the session currently shows.</summary>
    public RgbImage Shoot(CalibrationSession s, CalibrationLedOverride led, int seed, double yaw = 4, double pitch = -2)
    {
        var (r, g, b, path) = led.Current;
        var drive = Drive(r, g, b, path);
        var rng = new Random(seed);
        double J() => 1 + (rng.NextDouble() * 2 - 1) * CameraWbJitter;
        var photo = new SyntheticPhoto
        {
            Width = Width, Height = Height, Aspect = Aspect,
            Corners = SyntheticPhoto.Pose(Width, Height, Aspect, yaw, pitch, Roll, WidthFraction),
            Pattern = s.Pattern,
            GlowCamera = GlowCamera(drive),
            GlowStrength = GlowStrength,
            WallLight = WallLight,
            ScreenMix = ScreenMix,
            CameraWb = new Rgb3(0.85 * J(), 1.0, 1.2 * J()),
            Exposure = 0.9 * J(),
            AutoExposureTarget = AutoExposureTarget,
            HighlightProtect = HighlightProtect,
            MaxExposure = MaxExposure,
            Seed = seed,
        };
        var img = photo.Render();
        LastExposure = photo.UsedExposure;
        return img;
    }

    /// <summary>True chroma error (percent) of the glow vs a neutral screen gray at an LED level (LedCorrection path).</summary>
    public double GrayError(double level)
    {
        var glow = GlowCamera(Drive(level, level, level, OverrideColorPath.LedCorrection));
        var scr = ScreenCamera(0xB3B3B3);
        double rr = (glow.R / glow.G) / (scr.R / scr.G), rb = (glow.B / glow.G) / (scr.B / scr.G);
        return CalibrationMath.ErrorPercent(rr, rb);
    }

    /// <summary>Chromaticity distance (×100) between the glow for screen color <paramref name="rgb"/> and the screen itself.</summary>
    public double ColorError(int rgb, double[]? matrix)
    {
        var saved = Matrix;
        Matrix = matrix;
        try
        {
            double r = ((rgb >> 16) & 0xFF) / 255.0 * 0.6, g = ((rgb >> 8) & 0xFF) / 255.0 * 0.6, b = (rgb & 0xFF) / 255.0 * 0.6;
            var glow = GlowCamera(Drive(r, g, b, OverrideColorPath.LedCorrection));
            var scr = ScreenCamera(rgb);
            static (double, double) Chroma(Rgb3 c) { double s = c.R + c.G + c.B; return (c.R / s, c.G / s); }
            var (gx, gy) = Chroma(glow);
            var (sx, sy) = Chroma(scr);
            return 100 * Math.Sqrt((gx - sx) * (gx - sx) + (gy - sy) * (gy - sy));
        }
        finally { Matrix = saved; }
    }
}

/// <summary>Manual clock for the session (seconds).</summary>
internal sealed class FakeClock
{
    public double Now;
    public double Read() => Now;
}
