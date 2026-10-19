namespace RambiBox.Core.Calibration;

internal enum FrameFailure { None, InvalidImage, ScreenNotFound }

/// <summary>Screen reference band and LED glow next to it on one side.</summary>
internal sealed record SideSample(Side Side, RegionMeasurement Screen, RegionMeasurement Glow)
{
    public bool Usable => Screen.Status == RegionStatus.Ok && Glow.Status == RegionStatus.Ok;
}

/// <summary>Everything measured in one camera frame of a calibration pattern (linear camera RGB).</summary>
/// <param name="Ref">The reference square used to normalize this frame (see <see cref="RefIndex"/>), or — when none is
/// usable — the first one in the frame (for the phone's overlay).</param>
/// <param name="Black">Luma of the screen's black probe (camera linear).</param>
/// <param name="ScreenFraction">How much of the picture the screen fills along its better-filled image axis (portrait
/// framing of a landscape monitor gives ≈ the fraction of the frame width).</param>
internal sealed record FrameMeasurement(
    FrameFailure Failure,
    int Width, int Height,
    ScreenDetection? Detection,
    int? Code,
    IReadOnlyList<SideSample> Sides,
    RegionMeasurement? Ref,
    double Black,
    double ScreenFraction)
{
    /// <summary>All reference squares (index = <see cref="CalibrationLayout.RefFactors"/> index).</summary>
    public IReadOnlyList<RegionMeasurement> Refs { get; init; } = [];
    /// <summary>Index of the reference square used for normalization (brightest one neither clipped nor too dark), −1 = none.</summary>
    public int RefIndex { get; init; } = -1;
    /// <summary>Mean color of the screen's black probe (camera linear; the monitor's black level × exposure).</summary>
    public Rgb BlackRgb { get; init; }

    public bool Found => Detection is not null;
    public PointD[]? Corners => Detection?.Corners;
    public int UsableSides => Sides.Count(s => s.Usable);
    public bool RefOk => RefIndex >= 0 && Ref is not null;
    /// <summary>Chosen reference square minus the screen's black level (camera linear, per channel).</summary>
    public Rgb RefNet => Ref is null ? default : FrameAnalyzer.Net(Ref.Mean, BlackRgb);
    public int ClippedSides => Sides.Count(s => s.Glow.Status == RegionStatus.Clipped || s.Screen.Status == RegionStatus.Clipped);
    public int GlowClippedSides => Sides.Count(s => s.Glow.Status == RegionStatus.Clipped);
    public int WeakSides => Sides.Count(s => s.Glow.Status is RegionStatus.Weak or RegionStatus.TooDark);
    public int OutOfFrameSides => Sides.Count(s => s.Glow.Status == RegionStatus.OutOfFrame || s.Screen.Status == RegionStatus.OutOfFrame);

    /// <summary>
    /// Neutral steps: per side ρc = (glow_c/glow_G)/(screen_c/screen_G), median over the usable sides (camera white
    /// balance and exposure cancel because glow and reference are in the same frame).
    /// </summary>
    public (int Sides, double RhoR, double RhoB) Rho()
    {
        var rr = new List<double>(4);
        var rb = new List<double>(4);
        foreach (var s in Sides)
        {
            if (!s.Usable) continue;
            Rgb g = s.Glow.Mean, c = s.Screen.Mean;
            if (g.G < 1e-6 || c.G < 1e-6 || c.R < 1e-6 || c.B < 1e-6 || g.R < 1e-5 || g.B < 1e-5) continue;
            double r = (g.R / g.G) / (c.R / c.G), b = (g.B / g.G) / (c.B / c.G);
            if (!double.IsFinite(r) || !double.IsFinite(b) || r <= 0 || b <= 0) continue;
            rr.Add(r);
            rb.Add(b);
        }
        if (rr.Count == 0) return (0, 1, 1);
        return (rr.Count, CalibrationAnalyzer.Median(rr), CalibrationAnalyzer.Median(rb));
    }
}

/// <summary>Measures a camera frame of the v2 calibration pattern (detection, step code, bands, glow, reference squares).</summary>
internal static class FrameAnalyzer
{
    /// <summary>A reference square with more clipped pixels than this is not used (clipped pixels would bias its mean).</summary>
    public const double RefMaxClipped = 0.02;
    /// <summary>A reference square must be at least this bright above the screen black (linear, ≈ sRGB 39/255).</summary>
    public const double RefMinSignal = 0.02;
    /// <summary>Glow pixels with any channel at or above this 8-bit value are left out.</summary>
    public const int GlowClipLevel = 240;
    /// <summary>A glow region with more (nearly) clipped pixels than this is not measured.</summary>
    public const double GlowMaxClipped = 0.85;

    public static FrameMeasurement Measure(RgbImage img, CalibrationLayout layout)
    {
        ScreenDetection? det;
        try { det = FiducialDetector.Detect(img, layout); }
        catch (Exception ex)
        {
            Log.Warn($"Calibration: detection failed: {ex.Message}");
            det = null;
        }
        if (det is null) return new FrameMeasurement(FrameFailure.ScreenNotFound, img.Width, img.Height, null, null, [], null, 0, 0);
        return Measure(img, layout, det);
    }

    internal static FrameMeasurement Measure(RgbImage img, CalibrationLayout layout, ScreenDetection det)
    {
        Homography inv;
        try { inv = det.ScreenToPhoto.Inverse(); }
        catch (InvalidOperationException)
        {
            return new FrameMeasurement(FrameFailure.ScreenNotFound, img.Width, img.Height, null, null, [], null, 0, 0);
        }
        var H = det.ScreenToPhoto;
        var blackRegion = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.BlackProbe(), Side.Top, false, includeDark: true);
        var blackRgb = blackRegion.Samples > 0 ? blackRegion.Mean : default;
        double black = blackRgb.Luma;
        int? code = StepCode.Read(img, H, layout);

        var sides = new List<SideSample>(4);
        foreach (var s in Enum.GetValues<Side>())
        {
            var scr = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.BandSample(s), s, false);
            // The glow fades with the distance from the screen and keeps its chroma, so when its inner part clips
            // (saturated primaries clip their own channel), the unclipped outer pixels still give its color. Pixels
            // near clipping are skipped too: phone tone curves already compress highlights there.
            var glow = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.Glow(s), s, true, includeDark: true, clipLevel: GlowClipLevel,
                minGoodFraction: 1 - GlowMaxClipped);
            if (scr.Status == RegionStatus.Ok && scr.ClippedFraction > 0.2) scr = scr with { Status = RegionStatus.Clipped };
            if (glow.Status == RegionStatus.Ok && glow.ClippedFraction > GlowMaxClipped) glow = glow with { Status = RegionStatus.Clipped };
            // Max channel (not luma) so a pure blue glow isn't called weak.
            double gmax = MaxChannel(glow.Mean);
            if (glow.Status == RegionStatus.Ok && (gmax < CalibrationAnalyzer.MinGlowLinear || gmax < 2.5 * black))
                glow = glow with { Status = RegionStatus.Weak };
            sides.Add(new SideSample(s, scr, glow));
        }

        // Reference squares (1×, ½×, ¼× the band level): the brightest one that is neither clipped nor too dark.
        var refs = new List<RegionMeasurement>(CalibrationLayout.RefCount);
        int chosen = -1;
        for (int i = 0; i < CalibrationLayout.RefCount; i++)
        {
            var r = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.RefPatch(i).Inset(0.6), Side.Top, false);
            if (r.Status == RegionStatus.Ok && r.ClippedFraction > RefMaxClipped) r = r with { Status = RegionStatus.Clipped };
            refs.Add(r);
            if (chosen < 0 && RefUsable(r, blackRgb)) chosen = i;
        }
        var refm = chosen >= 0 ? refs[chosen] : refs.FirstOrDefault(r => r.Polygon.Length == 4) ?? refs[0];

        // Fraction of the picture the screen fills along its better-filled axis (bounding box; any orientation).
        var c = det.Corners;
        double bw = c.Max(p => p.X) - c.Min(p => p.X), bh = c.Max(p => p.Y) - c.Min(p => p.Y);
        double frac = Math.Max(bw / img.Width, bh / img.Height);
        return new FrameMeasurement(FrameFailure.None, img.Width, img.Height, det, code, sides, refm, black, frac)
        {
            Refs = refs,
            RefIndex = chosen,
            BlackRgb = blackRgb,
        };
    }

    private static bool RefUsable(RegionMeasurement r, Rgb black)
    {
        if (r.Status != RegionStatus.Ok || r.ClippedFraction > RefMaxClipped) return false;
        var n = Net(r.Mean, black);
        return MaxChannel(n) >= RefMinSignal && n.R > 1e-5 && n.G > 1e-5 && n.B > 1e-5;
    }

    public static double MaxChannel(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B));

    /// <summary>Screen element minus the screen's black level (the monitor's backlight leakage), per channel.</summary>
    public static Rgb Net(Rgb a, Rgb black) => new(Math.Max(0, a.R - black.R), Math.Max(0, a.G - black.G), Math.Max(0, a.B - black.B));

    /// <summary>Elementwise a / b (camera WB/exposure normalization by the reference patch).</summary>
    public static Rgb3 Div(Rgb a, Rgb b) => new(a.R / b.R, a.G / b.G, a.B / b.B);
}
