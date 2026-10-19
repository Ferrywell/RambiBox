namespace RambiBox.Core.Calibration;

internal enum FrameFailure { None, InvalidImage, ScreenNotFound }

/// <summary>Screen reference band and LED glow next to it on one side.</summary>
internal sealed record SideSample(Side Side, RegionMeasurement Screen, RegionMeasurement Glow)
{
    public bool Usable => Screen.Status == RegionStatus.Ok && Glow.Status == RegionStatus.Ok;
}

/// <summary>Everything measured in one camera frame of a calibration pattern (linear camera RGB).</summary>
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
    public bool Found => Detection is not null;
    public PointD[]? Corners => Detection?.Corners;
    public int UsableSides => Sides.Count(s => s.Usable);
    public bool RefOk => Ref is { Status: RegionStatus.Ok } r && r.Mean.R > 1e-5 && r.Mean.G > 1e-5 && r.Mean.B > 1e-5;
    public int ClippedSides => Sides.Count(s => s.Glow.Status == RegionStatus.Clipped || s.Screen.Status == RegionStatus.Clipped);
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

/// <summary>Measures a camera frame of the v2 calibration pattern (detection, step code, bands, glow, reference patch).</summary>
internal static class FrameAnalyzer
{
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
        var blackRegion = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.BlackProbe(), Side.Top, false);
        double black = blackRegion.Samples > 0 ? blackRegion.Mean.Luma : 0;
        int? code = StepCode.Read(img, H, layout);

        var sides = new List<SideSample>(4);
        foreach (var s in Enum.GetValues<Side>())
        {
            var scr = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.BandSample(s), s, false);
            var glow = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.Glow(s), s, true, includeDark: true);
            if (scr.Status == RegionStatus.Ok && scr.ClippedFraction > 0.2) scr = scr with { Status = RegionStatus.Clipped };
            if (glow.Status == RegionStatus.Ok && glow.ClippedFraction > 0.15) glow = glow with { Status = RegionStatus.Clipped };
            // Max channel (not luma) so a pure blue glow isn't called weak.
            double gmax = Math.Max(glow.Mean.R, Math.Max(glow.Mean.G, glow.Mean.B));
            if (glow.Status == RegionStatus.Ok && (gmax < CalibrationAnalyzer.MinGlowLinear || gmax < 2.5 * black))
                glow = glow with { Status = RegionStatus.Weak };
            sides.Add(new SideSample(s, scr, glow));
        }

        // Reference patches: mean of the usable ones.
        RegionMeasurement? refm = null;
        var refs = new List<RegionMeasurement>(2);
        for (int i = 0; i < 2; i++)
        {
            var r = CalibrationAnalyzer.SampleRegion(img, H, inv, layout.RefPatch(i).Inset(0.6), Side.Top, false);
            if (r.Status == RegionStatus.Ok && r.ClippedFraction > 0.1) r = r with { Status = RegionStatus.Clipped };
            refs.Add(r);
        }
        var okRefs = refs.Where(r => r.Status == RegionStatus.Ok).ToList();
        if (okRefs.Count > 0)
        {
            var m = new Rgb(okRefs.Average(r => r.Mean.R), okRefs.Average(r => r.Mean.G), okRefs.Average(r => r.Mean.B));
            refm = okRefs[0] with { Mean = m, Polygon = refs[0].Polygon };
        }
        else refm = refs.FirstOrDefault(r => r.Polygon.Length == 4) ?? refs[0];

        var c = det.Corners;
        double wTop = (c[1] - c[0]).Length, wBot = (c[2] - c[3]).Length, hL = (c[3] - c[0]).Length, hR = (c[2] - c[1]).Length;
        double frac = Math.Max((wTop + wBot) / 2 / img.Width, (hL + hR) / 2 / img.Height);
        return new FrameMeasurement(FrameFailure.None, img.Width, img.Height, det, code, sides, refm, black, frac);
    }

    /// <summary>Elementwise a / b (camera WB/exposure normalization by the reference patch).</summary>
    public static Rgb3 Div(Rgb a, Rgb b) => new(a.R / b.R, a.G / b.G, a.B / b.B);
}
