namespace RambiBox.Core.Calibration;

internal enum AnalysisFailure
{
    None,
    InvalidImage,
    ScreenNotFound,
    BadCorners,
    GlowOverexposed,
    ScreenOverexposed,
    GlowTooWeak,
    OutOfFrame,
    NotEnoughSides,
}

internal enum RegionStatus { Ok, OutOfFrame, Clipped, TooDark, Weak }

/// <summary>Linear-light mean color of a region (camera RGB, sRGB primaries assumed).</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public double Luma => 0.2126 * R + 0.7152 * G + 0.0722 * B;
}

internal sealed record RegionMeasurement(Side Side, bool IsGlow, PointD[] Polygon, RegionStatus Status,
    Rgb Mean, int Samples, double ClippedFraction, double InFrameFraction);

internal sealed record SideMeasurement(Side Side, RegionMeasurement Screen, RegionMeasurement Glow, bool Valid,
    double RhoR, double RhoB);

/// <summary>Outcome of analyzing one photo. <see cref="RhoR"/>/<see cref="RhoB"/>: glow chroma relative to the screen
/// reference (1 = identical, &gt;1 = glow has too much of that channel, relative to green).</summary>
internal sealed record PhotoAnalysis(
    AnalysisFailure Failure,
    string Message,
    int PhotoWidth, int PhotoHeight,
    PointD[]? Corners,
    IReadOnlyList<SideMeasurement> Sides,
    double RhoR, double RhoB,
    double BlackLevel)
{
    public bool Ok => Failure == AnalysisFailure.None;
    public double ErrorPercent => CalibrationMath.ErrorPercent(RhoR, RhoB);
}

/// <summary>
/// Measures LED glow vs. on-screen reference in a photo of the calibration pattern.
/// Camera white balance and exposure cancel out because glow and reference are in the same photo:
/// for each side ρc = (Glow_c / Glow_G) / (Screen_c / Screen_G) in linear light.
/// </summary>
internal static class CalibrationAnalyzer
{
    public const byte ClipLevel = 250;
    public const byte DarkLevel = 6;
    /// <summary>Glow must be at least this bright (linear, ≈ sRGB 13/255) to give a trustworthy color.</summary>
    public const double MinGlowLinear = 0.004;
    public const int MaxSamplesPerRegion = 120_000;

    private static readonly double[] SrgbToLinear = BuildLut();

    private static double[] BuildLut()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    public static double ToLinear(byte v) => SrgbToLinear[v];

    public static PhotoAnalysis Analyze(RgbImage photo, CalibrationLayout layout, IReadOnlyList<PointD>? manualCorners = null)
    {
        ScreenDetection? det = manualCorners is null
            ? FiducialDetector.Detect(photo, layout)
            : FiducialDetector.FromCorners(manualCorners);
        if (det is null)
        {
            return manualCorners is null
                ? Fail(photo, AnalysisFailure.ScreenNotFound,
                    "Screen not found — make sure all 4 white corner markers are visible and sharp, or drag the corner handles onto the screen corners.")
                : Fail(photo, AnalysisFailure.BadCorners, "Those corners don't form a valid screen outline — please adjust them.");
        }
        return Measure(photo, layout, det);
    }

    private static PhotoAnalysis Fail(RgbImage photo, AnalysisFailure f, string msg, PointD[]? corners = null,
        IReadOnlyList<SideMeasurement>? sides = null) =>
        new(f, msg, photo.Width, photo.Height, corners, sides ?? [], 1, 1, 0);

    internal static PhotoAnalysis Measure(RgbImage photo, CalibrationLayout layout, ScreenDetection det)
    {
        Homography inv;
        try { inv = det.ScreenToPhoto.Inverse(); }
        catch (InvalidOperationException)
        {
            return Fail(photo, AnalysisFailure.BadCorners, "Could not map the screen outline — please try again.");
        }
        var H = det.ScreenToPhoto;

        var blackRegion = SampleRegion(photo, H, inv, layout.BlackProbe(), Side.Top, false);
        double black = blackRegion.Samples > 0 ? blackRegion.Mean.Luma : 0;

        var sides = new List<SideMeasurement>(4);
        foreach (var s in Enum.GetValues<Side>())
        {
            var scr = SampleRegion(photo, H, inv, layout.BandSample(s), s, false);
            var glow = SampleRegion(photo, H, inv, layout.Glow(s), s, true);

            // Screen reference checks
            if (scr.Status == RegionStatus.Ok && scr.ClippedFraction > 0.2) scr = scr with { Status = RegionStatus.Clipped };
            // Glow checks
            if (glow.Status == RegionStatus.Ok && glow.ClippedFraction > 0.15) glow = glow with { Status = RegionStatus.Clipped };
            if (glow.Status == RegionStatus.Ok && (glow.Mean.Luma < MinGlowLinear || glow.Mean.Luma < 2.5 * black))
                glow = glow with { Status = RegionStatus.Weak };

            bool valid = scr.Status == RegionStatus.Ok && glow.Status == RegionStatus.Ok
                         && scr.Mean.G > 1e-6 && glow.Mean.G > 1e-6 && scr.Mean.R > 1e-6 && scr.Mean.B > 1e-6;
            double rr = 1, rb = 1;
            if (valid)
            {
                rr = (glow.Mean.R / glow.Mean.G) / (scr.Mean.R / scr.Mean.G);
                rb = (glow.Mean.B / glow.Mean.G) / (scr.Mean.B / scr.Mean.G);
                if (!double.IsFinite(rr) || !double.IsFinite(rb) || rr <= 0 || rb <= 0) { valid = false; rr = rb = 1; }
                else if (glow.Mean.R < 1e-5 || glow.Mean.B < 1e-5) { valid = false; rr = rb = 1; } // a channel is fully off
            }
            sides.Add(new SideMeasurement(s, scr, glow, valid, rr, rb));
        }

        var good = sides.Where(x => x.Valid).ToList();
        if (good.Count < 2)
        {
            var (f, msg) = Explain(sides);
            return Fail(photo, f, msg, det.Corners, sides) with { BlackLevel = black };
        }
        double rhoR = Median(good.Select(x => x.RhoR)), rhoB = Median(good.Select(x => x.RhoB));
        string okMsg = $"Measured {good.Count} of 4 sides.";
        return new PhotoAnalysis(AnalysisFailure.None, okMsg, photo.Width, photo.Height, det.Corners, sides, rhoR, rhoB, black);
    }

    /// <summary>Picks the most useful explanation when fewer than 2 sides could be measured.</summary>
    private static (AnalysisFailure, string) Explain(List<SideMeasurement> sides)
    {
        int glowClip = sides.Count(s => s.Glow.Status == RegionStatus.Clipped);
        int scrClip = sides.Count(s => s.Screen.Status == RegionStatus.Clipped);
        int weak = sides.Count(s => s.Glow.Status is RegionStatus.Weak or RegionStatus.TooDark);
        int oof = sides.Count(s => s.Glow.Status == RegionStatus.OutOfFrame || s.Screen.Status == RegionStatus.OutOfFrame);
        if (glowClip >= 2)
            return (AnalysisFailure.GlowOverexposed,
                "Glow too bright / overexposed — tap and hold on the screen in your camera to lock focus, then slide the exposure down a bit.");
        if (scrClip >= 2)
            return (AnalysisFailure.ScreenOverexposed,
                "Screen too bright / overexposed in the photo — tap and hold on the screen in your camera and lower the exposure.");
        if (oof >= 2)
            return (AnalysisFailure.OutOfFrame,
                "The glow around the screen is cut off — step back so the whole screen and the light around it fit in the photo.");
        if (weak >= 2)
            return (AnalysisFailure.GlowTooWeak,
                "LED glow too weak — turn off the room lights, raise the LED brightness, or make sure the LEDs are on.");
        return (AnalysisFailure.NotEnoughSides,
            "Could not measure the glow on at least 2 sides — make sure the light on the wall around the screen is visible and not blocked.");
    }

    /// <summary>
    /// Samples photo pixels whose screen-plane coordinates fall inside <paramref name="rect"/>.
    /// With <paramref name="includeDark"/> nearly black pixels count towards the mean too (and a mean is returned even
    /// when the status is TooDark): needed to measure the wall with the LEDs off.
    /// </summary>
    internal static RegionMeasurement SampleRegion(RgbImage img, Homography H, Homography inv, RectD rect, Side side, bool glow,
        bool includeDark = false)
    {
        var poly = rect.Corners();
        var ppoly = new PointD[4];
        for (int i = 0; i < 4; i++)
        {
            if (H.W(poly[i].X, poly[i].Y) <= 0)
                return new RegionMeasurement(side, glow, [], RegionStatus.OutOfFrame, default, 0, 0, 0);
            ppoly[i] = H.Map(poly[i]);
        }
        double polyArea = Math.Abs(FiducialDetector.PolygonArea(ppoly));
        if (polyArea < 4 || !double.IsFinite(polyArea))
            return new RegionMeasurement(side, glow, ppoly, RegionStatus.OutOfFrame, default, 0, 0, 0);

        double minX = ppoly.Min(p => p.X), maxX = ppoly.Max(p => p.X), minY = ppoly.Min(p => p.Y), maxY = ppoly.Max(p => p.Y);
        int x0 = Math.Max(0, (int)Math.Floor(minX)), x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(maxX));
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(maxY));
        int step = 1;
        while (polyArea / (step * step) > MaxSamplesPerRegion) step++;

        var data = img.Data;
        int inside = 0, clipped = 0, dark = 0;
        var samples = new List<(double L, double R, double G, double B)>(Math.Min(MaxSamplesPerRegion, (int)polyArea / (step * step) + 16));
        for (int y = y0; y <= y1; y += step)
        {
            for (int x = x0; x <= x1; x += step)
            {
                var uv = inv.Map(x + 0.5, y + 0.5);
                if (!rect.Contains(uv.X, uv.Y)) continue;
                inside++;
                int o = (y * img.Width + x) * 3;
                byte r = data[o], g = data[o + 1], b = data[o + 2];
                if (r >= ClipLevel || g >= ClipLevel || b >= ClipLevel) { clipped++; continue; }
                if (Math.Max(r, Math.Max(g, b)) < DarkLevel)
                {
                    dark++;
                    if (!includeDark) continue;
                }
                double lr = SrgbToLinear[r], lg = SrgbToLinear[g], lb = SrgbToLinear[b];
                samples.Add((0.2126 * lr + 0.7152 * lg + 0.0722 * lb, lr, lg, lb));
            }
        }
        double expected = polyArea / (step * step);
        double inFrame = Math.Min(1, inside / Math.Max(1, expected));
        double clipFrac = inside == 0 ? 0 : (double)clipped / inside;
        if (inFrame < 0.5 || inside < 20)
            return new RegionMeasurement(side, glow, ppoly, RegionStatus.OutOfFrame, default, inside, clipFrac, inFrame);
        int bright = includeDark ? samples.Count - dark : samples.Count;
        if (bright < Math.Max(20, inside * 0.3))
        {
            var st = clipFrac > 0.15 ? RegionStatus.Clipped : RegionStatus.TooDark;
            var darkMean = includeDark && st == RegionStatus.TooDark && samples.Count >= 20 ? TrimmedMean(samples) : default;
            return new RegionMeasurement(side, glow, ppoly, st, darkMean, samples.Count, clipFrac, inFrame);
        }
        var mean = TrimmedMean(samples);
        return new RegionMeasurement(side, glow, ppoly, RegionStatus.Ok, mean, samples.Count, clipFrac, inFrame);
    }

    /// <summary>
    /// Mean color of the samples between the 20th and 80th luminance percentile: ignores a monitor stand,
    /// cables, reflections or dark corners without biasing the color.
    /// </summary>
    internal static Rgb TrimmedMean(List<(double L, double R, double G, double B)> s)
    {
        s.Sort((a, b) => a.L.CompareTo(b.L));
        int lo = (int)(s.Count * 0.2), hi = Math.Max(lo + 1, (int)(s.Count * 0.8));
        double r = 0, g = 0, b = 0;
        for (int i = lo; i < hi; i++) { r += s[i].R; g += s[i].G; b += s[i].B; }
        int n = hi - lo;
        return new Rgb(r / n, g / n, b / n);
    }

    internal static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 1;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
