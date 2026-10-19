namespace RambiBox.Core.Calibration;

/// <summary>One gray-ramp measurement: LED level and the glow chroma relative to the (neutral) screen reference.</summary>
internal readonly record struct GrayPoint(double Level, double RhoR, double RhoB, double Weight = 1);

/// <summary>Result of the multi-level fit: ρc(L) = Ac · L^δc (δ = residual exponent relative to green).</summary>
internal readonly record struct GammaFit(double LnAR, double DeltaR, double LnAB, double DeltaB, double RmsR, double RmsB);

/// <summary>
/// Pure color math of the v2 calibration: multi-level gamma fit, white-preserving LED matrix fit, eye-pad mapping and
/// LED quantization. Engine model: LED drive d = WB_c · (M·x)_c^γc (x = rendered color 0..1), light ∝ d.
/// </summary>
internal static class ColorFit
{
    public const double MinGamma = CalibrationMath.MinGamma, MaxGamma = CalibrationMath.MaxGamma;
    /// <summary>Default share of the fitted matrix blended with identity (conservative; the eye A/B check decides).</summary>
    public const double DefaultMatrixStrength = 0.6;
    public static readonly double[] StrengthSteps = [0.3, 0.45, 0.6, 0.75, 0.9];

    // ------------------------------------------------------------------ gray ramp → channel gamma

    /// <summary>Weighted least squares of ln ρ = ln A + δ·ln L per channel. Needs ≥ 2 distinct levels.</summary>
    public static GammaFit? FitGamma(IReadOnlyList<GrayPoint> pts)
    {
        var p = pts.Where(q => q.Level > 0 && q.RhoR > 0 && q.RhoB > 0 && q.Weight > 0
                               && double.IsFinite(q.RhoR) && double.IsFinite(q.RhoB)).ToList();
        if (p.Select(q => Math.Round(q.Level, 4)).Distinct().Count() < 2) return null;
        var (aR, dR, rmsR) = Line(p, q => Math.Log(q.RhoR));
        var (aB, dB, rmsB) = Line(p, q => Math.Log(q.RhoB));
        if (!double.IsFinite(aR + dR + aB + dB)) return null;
        return new GammaFit(aR, dR, aB, dB, rmsR, rmsB);

        static (double A, double D, double Rms) Line(List<GrayPoint> p, Func<GrayPoint, double> y)
        {
            double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var q in p)
            {
                double x = Math.Log(q.Level), w = q.Weight, v = y(q);
                sw += w; sx += w * x; sy += w * v; sxx += w * x * x; sxy += w * x * v;
            }
            double den = sw * sxx - sx * sx;
            double d = Math.Abs(den) < 1e-12 ? 0 : (sw * sxy - sx * sy) / den;
            double a = (sy - d * sx) / sw;
            double ss = 0;
            foreach (var q in p) { double r = y(q) - (a + d * Math.Log(q.Level)); ss += q.Weight * r * r; }
            return (a, d, Math.Sqrt(ss / sw));
        }
    }

    /// <summary>
    /// Applies a gamma fit measured with (<paramref name="wb"/>, <paramref name="gamma"/>). Model: the strip's light is a
    /// power of its drive, light_c ∝ (L^γc·Wc)^ec, with green taken as the linear reference (γG·eG = 1), so the measured
    /// residual exponent is δc = γc·ec − 1 and the strip exponent ec = (1 + δc)/γc. The new gamma γc' = γc/(1 + δc) makes
    /// γc'·ec = 1 (exact for power-law channels; G keeps its gamma; clamped to [0.7, 1.4]). The white balance is then
    /// re-solved so the white level stays neutral: ρ'(Lw) = A · Lw^(δ + (γ'−γ)·e) · W'/W ≡ 1.
    /// </summary>
    public static (Rgb3 Wb, Rgb3 Gamma) ApplyGammaFit(Rgb3 wb, Rgb3 gamma, GammaFit fit, double whiteLevel = CalibrationMath.WhiteLevel)
    {
        var (gR, xR) = NewGamma(gamma.R, fit.DeltaR);
        var (gB, xB) = NewGamma(gamma.B, fit.DeltaB);
        // Wanted light change at the white level; the WB gain acts on the light with the channel's exponent e.
        double fr = Math.Pow(1 / (Math.Exp(fit.LnAR) * Math.Pow(whiteLevel, fit.DeltaR + xR)), 1 / Exponent(gamma.R, fit.DeltaR));
        double fb = Math.Pow(1 / (Math.Exp(fit.LnAB) * Math.Pow(whiteLevel, fit.DeltaB + xB)), 1 / Exponent(gamma.B, fit.DeltaB));
        fr = Math.Clamp(fr, 1 / CalibrationMath.MaxStepFactor, CalibrationMath.MaxStepFactor);
        fb = Math.Clamp(fb, 1 / CalibrationMath.MaxStepFactor, CalibrationMath.MaxStepFactor);
        var w = CalibrationMath.Normalize(new Rgb3(Math.Max(wb.R, CalibrationMath.MinGain) * fr, Math.Max(wb.G, CalibrationMath.MinGain),
            Math.Max(wb.B, CalibrationMath.MinGain) * fb));
        return (w, new Rgb3(gR, gamma.G, gB));

        static double Exponent(double g, double delta) => 1 + delta > 0.2 && g > 0 ? Math.Clamp((1 + delta) / g, 0.5, 2) : 1;

        // New gamma and the resulting change of the channel's light exponent.
        static (double Gamma, double ExpChange) NewGamma(double g, double delta)
        {
            if (!(1 + delta > 0.2) || !(g > 0)) return (g, 0);
            double e = (1 + delta) / g;
            double ng = Math.Clamp(g / (1 + delta), MinGamma, MaxGamma);
            return (ng, (ng - g) * e);
        }
    }

    // ------------------------------------------------------------------ LED quantization

    /// <summary>The byte the engine sends for one channel (see ColorProcessor.ToDevice).</summary>
    public static int LedByte(double level, double gamma, double wb, double brightness)
    {
        double v = Math.Pow(Math.Clamp(level, 0, 1), gamma) * brightness * wb * 255;
        return v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5);
    }

    /// <summary>ideal / actually-sent drive of one channel (≈ 1; large at low levels and small gains).</summary>
    public static double QuantizationFactor(double level, double gamma, double wb, double brightness)
    {
        double ideal = Math.Pow(Math.Clamp(level, 0, 1), gamma) * brightness * wb * 255;
        int b = LedByte(level, gamma, wb, brightness);
        return b <= 0 || ideal <= 0 ? 1 : ideal / b;
    }

    /// <summary>Corrects a measured ρ pair for the rounding of the LED bytes (what the strip would show with exact values).</summary>
    public static (double RhoR, double RhoB) CorrectQuantization(double rhoR, double rhoB, double level, Rgb3 wb, Rgb3 gamma, double brightness)
    {
        double qg = QuantizationFactor(level, gamma.G, wb.G, brightness);
        return (rhoR * QuantizationFactor(level, gamma.R, wb.R, brightness) / qg,
                rhoB * QuantizationFactor(level, gamma.B, wb.B, brightness) / qg);
    }

    // ------------------------------------------------------------------ primaries → LED matrix

    /// <summary>
    /// Fits the white-preserving LED matrix M (row-major) from camera-space measurements in one consistent space
    /// (normalized by the reference patch): <paramref name="led"/>[k] = glow of LED primary k, <paramref name="screen"/>[k]
    /// = screen primary k, <paramref name="white"/> = screen white. LED and screen columns are scaled so each set sums to
    /// the screen white (P·1 = S·1 = white), then M = P⁻¹·S, so M·1 = 1 (rows sum to 1). Returns the full (un-blended)
    /// matrix with negative (out-of-gamut) entries clamped to 0 and rows renormalized, or null if the data is unusable.
    /// </summary>
    public static double[]? FitMatrix(IReadOnlyList<Rgb3> led, IReadOnlyList<Rgb3> screen, Rgb3 white)
    {
        if (led.Count != 3 || screen.Count != 3) return null;
        var P = Columns(led);
        var S = Columns(screen);
        var w = new[] { white.R, white.G, white.B };
        var Pi = Invert(P);
        var Si = Invert(S);
        if (Pi is null || Si is null) return null;
        var a = Mul(Pi, w);
        var b = Mul(Si, w);
        if (a.Any(v => !(v > 0)) || b.Any(v => !(v > 0))) return null; // white is not a positive mix: measurement nonsense
        var Ps = ScaleColumns(P, a);
        var Ss = ScaleColumns(S, b);
        var Psi = Invert(Ps);
        if (Psi is null) return null;
        var M = Mul(Psi, Ss);
        // Clamp out-of-gamut (negative) contributions, renormalize rows so white stays white.
        for (int r = 0; r < 3; r++)
        {
            double sum = 0;
            for (int c = 0; c < 3; c++) sum += M[r * 3 + c] = Math.Max(0, M[r * 3 + c]);
            if (!(sum > 1e-6) || !double.IsFinite(sum)) return null;
            for (int c = 0; c < 3; c++) M[r * 3 + c] /= sum;
        }
        return M;
    }

    /// <summary>(1 − s)·I + s·M: rows still sum to 1.</summary>
    public static double[] Blend(double[] m, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        var r = new double[9];
        for (int i = 0; i < 9; i++) r[i] = (1 - strength) * (i % 4 == 0 ? 1 : 0) + strength * m[i];
        return r;
    }

    /// <summary>Largest |M − I| entry (0 = identity).</summary>
    public static double Deviation(double[]? m)
    {
        if (m is null) return 0;
        double d = 0;
        for (int i = 0; i < 9; i++) d = Math.Max(d, Math.Abs(m[i] - (i % 4 == 0 ? 1 : 0)));
        return d;
    }

    internal static double[] Columns(IReadOnlyList<Rgb3> cols) =>
    [
        cols[0].R, cols[1].R, cols[2].R,
        cols[0].G, cols[1].G, cols[2].G,
        cols[0].B, cols[1].B, cols[2].B,
    ];

    private static double[] ScaleColumns(double[] m, double[] s)
    {
        var r = (double[])m.Clone();
        for (int row = 0; row < 3; row++)
            for (int c = 0; c < 3; c++) r[row * 3 + c] *= s[c];
        return r;
    }

    internal static double[] Mul(double[] a, double[] b)
    {
        if (b.Length == 3)
            return [a[0] * b[0] + a[1] * b[1] + a[2] * b[2], a[3] * b[0] + a[4] * b[1] + a[5] * b[2], a[6] * b[0] + a[7] * b[1] + a[8] * b[2]];
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    internal static double[]? Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        double scale = m.Max(Math.Abs);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-9 * scale * scale * scale) return null;
        double[] r =
        [
            A, -(b * i - c * h), b * f - c * e,
            B, a * i - c * g, -(a * f - c * d),
            C, -(a * h - b * g), a * e - b * d,
        ];
        for (int k = 0; k < 9; k++) r[k] /= det;
        return r;
    }

    // ------------------------------------------------------------------ eye pad

    /// <summary>Pad range is [-1, 1] on both axes.</summary>
    public const double PadWhiteK = 0.25, PadDarkK = 0.2;

    /// <summary>
    /// White by eye: <paramref name="t"/> warm(+)↔cool(−), <paramref name="m"/> magenta(+)↔green(−), relative to the
    /// camera result: R·e^{+kt}, B·e^{−kt}, G·e^{−km}, then normalized so the strongest channel is 1.
    /// </summary>
    public static Rgb3 WhiteFromPad(Rgb3 baseWb, double t, double m)
    {
        t = Math.Clamp(t, -1, 1);
        m = Math.Clamp(m, -1, 1);
        return CalibrationMath.Normalize(new Rgb3(
            Math.Max(baseWb.R, CalibrationMath.MinGain) * Math.Exp(PadWhiteK * t),
            Math.Max(baseWb.G, CalibrationMath.MinGain) * Math.Exp(-PadWhiteK * m),
            Math.Max(baseWb.B, CalibrationMath.MinGain) * Math.Exp(-PadWhiteK * t)));
    }

    /// <summary>
    /// Dark tones by eye: warmer (t &gt; 0) lowers γR and raises γB (at L &lt; 1 a lower γ is brighter); more magenta
    /// (m &gt; 0) raises γG. White (L = 1) is unaffected since 1^γ = 1.
    /// </summary>
    public static Rgb3 GammaFromPad(Rgb3 baseGamma, double t, double m)
    {
        t = Math.Clamp(t, -1, 1);
        m = Math.Clamp(m, -1, 1);
        return new Rgb3(
            Math.Clamp(baseGamma.R - PadDarkK * t, MinGamma, MaxGamma),
            Math.Clamp(baseGamma.G + PadDarkK * m, MinGamma, MaxGamma),
            Math.Clamp(baseGamma.B + PadDarkK * t, MinGamma, MaxGamma));
    }
}
