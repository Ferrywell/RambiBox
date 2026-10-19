namespace RambiBox.Core.Calibration;

internal enum Side { Top, Right, Bottom, Left }

/// <summary>Axis-aligned rectangle in normalized screen coordinates (u = x / width, v = y / height; may extend beyond 0..1).</summary>
internal readonly record struct RectD(double L, double T, double R, double B)
{
    public bool Contains(double u, double v) => u >= L && u <= R && v >= T && v <= B;
    public PointD[] Corners() => [new(L, T), new(R, T), new(R, B), new(L, B)];
    public PointD Center => new((L + R) / 2, (T + B) / 2);

    /// <summary>The central part of the rectangle (<paramref name="f"/> = kept fraction of width and height).</summary>
    public RectD Inset(double f)
    {
        double dx = (R - L) * (1 - f) / 2, dy = (B - T) * (1 - f) / 2;
        return new(L + dx, T + dy, R - dx, B - dy);
    }
}

/// <summary>
/// Geometry of the on-screen calibration patterns (shared by the pattern renderer, the analyzer and the tests).
/// Sizes that must look square on the monitor are specified in units of the screen HEIGHT and converted
/// to normalized x with the monitor aspect ratio.
/// <para>
/// Camera pattern (top to bottom, v in screen heights): fiducial markers in the corners (white square with a black inner
/// square, 8% of the height, inset 2.5%); reference bands along the middle 70% of each edge (v 0.025–0.075 from the
/// edge) that the LED glow next to them is compared with; the PC's status text (v ≈ 0.09–0.25); a black probe
/// (v 0.28–0.56, camera black level); a row with the visual step code and two neutral reference patches (v ≈ 0.64–0.73,
/// used to normalize camera white balance/exposure between frames); the PC's buttons (v ≈ 0.80–0.87).
/// </para>
/// Eye pattern: wide bands along the full length of every edge so the glow can be compared with the screen edge by eye.
/// </summary>
internal sealed class CalibrationLayout
{
    public const double MarkerSize = 0.08;      // × screen height
    public const double MarkerInset = 0.025;    // × screen height
    public const double MarkerInnerFraction = 0.5;
    public const double BandInset = 0.025;      // × screen height
    public const double BandThickness = 0.05;   // × screen height
    public const double BandStart = 0.15, BandEnd = 0.85;   // fraction of the edge length
    public const double SampleStart = 0.2, SampleEnd = 0.8; // middle 60% of each edge is sampled
    public const double GlowNear = 0.04, GlowFar = 0.14;    // × screen height beyond the edge
    public const double QrSize = 0.40;          // × screen height

    // Step-code row and reference patches
    public const double CodeCellSize = 0.05;    // × screen height
    public const double CodeCellGap = 0.02;     // × screen height
    public const double CodeRowCenter = 0.685;  // v
    public const double RefPatchSize = 0.09;    // × screen height
    public const double RefPatchOffset = 0.42;  // × screen height, horizontal distance of each patch centre from the middle
    /// <summary>Neutral reference patch (≈ 0.22 linear): same in every camera frame.</summary>
    public const int RefPatchRgb = 0x808080;

    /// <summary>Eye pattern: band thickness along the edges.</summary>
    public const double EyeBandThickness = 0.13; // × screen height

    /// <summary>White step: 70% gray (0xB3 → 0.45 linear) leaves head-room so the camera doesn't clip.</summary>
    public const int WhiteBandRgb = 0xB3B3B3;
    public const double WhiteBandLinear = 0.4508; // linear value of 0xB3

    /// <summary>v1 dark stage band (0.25 LED level): kept for the v1 analyzer tests.</summary>
    public const int DarkBandRgb = 0x787878;

    public CalibrationLayout(double aspect)
    {
        Aspect = double.IsFinite(aspect) && aspect > 0.3 && aspect < 6 ? aspect : 16.0 / 9.0;
    }

    /// <summary>Monitor width / height.</summary>
    public double Aspect { get; }

    /// <summary>Converts a length in screen heights to normalized x.</summary>
    public double Hx(double h) => h / Aspect;

    /// <summary>Outer squares of the markers, order TL, TR, BR, BL.</summary>
    public RectD[] Markers()
    {
        double mx = Hx(MarkerSize), my = MarkerSize, ix = Hx(MarkerInset), iy = MarkerInset;
        return
        [
            new(ix, iy, ix + mx, iy + my),
            new(1 - ix - mx, iy, 1 - ix, iy + my),
            new(1 - ix - mx, 1 - iy - my, 1 - ix, 1 - iy),
            new(ix, 1 - iy - my, ix + mx, 1 - iy),
        ];
    }

    public static RectD Inner(RectD outer)
    {
        double f = (1 - MarkerInnerFraction) / 2;
        double w = outer.R - outer.L, h = outer.B - outer.T;
        return new(outer.L + w * f, outer.T + h * f, outer.R - w * f, outer.B - h * f);
    }

    /// <summary>Full reference band drawn on screen.</summary>
    public RectD Band(Side s) => BandRect(s, BandStart, BandEnd, 0, 1);

    /// <summary>Part of the band that is sampled (middle of the length, inner half of the thickness).</summary>
    public RectD BandSample(Side s) => BandRect(s, SampleStart, SampleEnd, 0.25, 0.75);

    private RectD BandRect(Side s, double a, double b, double t0, double t1)
    {
        double n0 = BandInset + BandThickness * t0, n1 = BandInset + BandThickness * t1; // distance from edge (heights)
        return s switch
        {
            Side.Top => new(a, n0, b, n1),
            Side.Bottom => new(a, 1 - n1, b, 1 - n0),
            Side.Left => new(Hx(n0), a, Hx(n1), b),
            _ => new(1 - Hx(n1), a, 1 - Hx(n0), b),
        };
    }

    /// <summary>Wall region outside the given edge where the LED glow is measured (screen plane coordinates).</summary>
    public RectD Glow(Side s) => s switch
    {
        Side.Top => new(SampleStart, -GlowFar, SampleEnd, -GlowNear),
        Side.Bottom => new(SampleStart, 1 + GlowNear, SampleEnd, 1 + GlowFar),
        Side.Left => new(-Hx(GlowFar), SampleStart, -Hx(GlowNear), SampleEnd),
        _ => new(1 + Hx(GlowNear), SampleStart, 1 + Hx(GlowFar), SampleEnd),
    };

    /// <summary>Area in the middle of the screen that stays black (used to estimate the photo's black level).</summary>
    public RectD BlackProbe() => new(0.5 - Hx(0.3), 0.28, 0.5 + Hx(0.3), 0.56);

    public RectD QrArea()
    {
        double hx = Hx(QrSize) / 2;
        return new(0.5 - hx, 0.5 - QrSize / 2, 0.5 + hx, 0.5 + QrSize / 2);
    }

    /// <summary>Cell <paramref name="i"/> (0..<see cref="StepCode.Cells"/>-1, left to right) of the step-code row.</summary>
    public RectD CodeCell(int i)
    {
        double total = StepCode.Cells * CodeCellSize + (StepCode.Cells - 1) * CodeCellGap;
        double x0 = 0.5 - Hx(total) / 2 + Hx(i * (CodeCellSize + CodeCellGap));
        return new(x0, CodeRowCenter - CodeCellSize / 2, x0 + Hx(CodeCellSize), CodeRowCenter + CodeCellSize / 2);
    }

    /// <summary>Neutral reference patch: 0 = left of the code row, 1 = right.</summary>
    public RectD RefPatch(int i)
    {
        double cx = 0.5 + (i == 0 ? -1 : 1) * Hx(RefPatchOffset), hw = Hx(RefPatchSize) / 2, hh = RefPatchSize / 2;
        return new(cx - hw, CodeRowCenter - hh, cx + hw, CodeRowCenter + hh);
    }

    /// <summary>
    /// Camera measurement pattern: markers, reference bands in <paramref name="bandRgb"/>, and (unless the QR code is
    /// shown) the step code + neutral reference patches.
    /// </summary>
    public CalibrationPattern BuildMeasurePattern(int bandRgb, int code, bool showQr)
    {
        var rects = new List<PatternRect>(24);
        foreach (var m in Markers())
        {
            rects.Add(Rect(m, 0xFFFFFF));
            rects.Add(Rect(Inner(m), 0x000000));
        }
        foreach (var s in Enum.GetValues<Side>()) rects.Add(Rect(Band(s), bandRgb));
        if (!showQr)
        {
            for (int i = 0; i < 2; i++) rects.Add(Rect(RefPatch(i), RefPatchRgb));
            var cells = StepCode.Encode(code);
            for (int i = 0; i < cells.Length; i++)
                if (cells[i]) rects.Add(Rect(CodeCell(i), 0xFFFFFF));
        }
        var q = QrArea();
        return new CalibrationPattern(rects, (float)q.L, (float)q.T, (float)q.R, (float)q.B, showQr) { Code = showQr ? null : code };
    }

    /// <summary>Eye pattern: wide bands of <paramref name="rgb"/> along the full length of every edge, black centre.</summary>
    public CalibrationPattern BuildEyePattern(int rgb)
    {
        double t = EyeBandThickness, tx = Hx(EyeBandThickness);
        var rects = new List<PatternRect>
        {
            Rect(new RectD(0, 0, 1, t), rgb),
            Rect(new RectD(0, 1 - t, 1, 1), rgb),
            Rect(new RectD(0, t, tx, 1 - t), rgb),
            Rect(new RectD(1 - tx, t, 1, 1 - t), rgb),
        };
        var q = QrArea();
        return new CalibrationPattern(rects, (float)q.L, (float)q.T, (float)q.R, (float)q.B, false) { EyeMode = true };
    }

    /// <summary>v1 pattern (white or dark band, no code). Kept for the analyzer tests.</summary>
    public CalibrationPattern BuildPattern(bool showQr, bool darkStage)
    {
        var p = BuildMeasurePattern(darkStage ? DarkBandRgb : WhiteBandRgb, 0, showQr: true);
        return p with { ShowQr = showQr };
    }

    private static PatternRect Rect(RectD r, int rgb) => new((float)r.L, (float)r.T, (float)r.R, (float)r.B, rgb);

    // ------------------------------------------------------------------ levels

    /// <summary>sRGB code (0..255) for a linear value.</summary>
    public static int LinearToSrgb8(double lin)
    {
        lin = Math.Clamp(lin, 0, 1);
        double v = lin <= 0.0031308 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1 / 2.4) - 0.055;
        return (int)Math.Round(v * 255);
    }

    /// <summary>
    /// Neutral band for an LED gray level: the screen reference is scaled with the LED level (0.6 ↔ 0xB3) so screen and
    /// glow keep the same relative exposure in the photo.
    /// </summary>
    public static int GrayBandRgb(double ledLevel)
    {
        int c = LinearToSrgb8(WhiteBandLinear * ledLevel / CalibrationMath.WhiteLevel);
        return (c << 16) | (c << 8) | c;
    }

    /// <summary>Band showing one monitor primary (0 = R, 1 = G, 2 = B) at the white band level.</summary>
    public static int PrimaryBandRgb(int channel) => 0xB3 << (8 * (2 - channel));
}
