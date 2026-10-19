namespace RambiBox.Core.Calibration;

internal readonly record struct Rgb3(double R, double G, double B)
{
    public override string ToString() => FormattableString.Invariant($"({R:0.000}, {G:0.000}, {B:0.000})");
}

/// <summary>
/// White balance / channel gamma update rules. All pure functions.
/// <para>
/// Model: the engine drives each LED channel with out_c = L^γc · Wc (L = rendered gray level, W = white balance,
/// γ = channel gamma; brightness is common to all channels and cancels). The camera sees glow_c ∝ out_c · t_c, where
/// t_c is the unknown LED-spectrum × wall × camera tint. The analyzer reports ρc = (glow_c/glow_G)/(screen_c/screen_G),
/// i.e. how much too strong channel c is relative to green compared with the screen's white.
/// </para>
/// </summary>
internal static class CalibrationMath
{
    /// <summary>Calibrated when the larger of the R and B chroma errors is below this (percent).</summary>
    public const double ConvergedErrorPercent = 3.0;
    public const double MinGain = 0.05, MaxGain = 1.0;
    /// <summary>Largest per-photo change of a channel gain relative to green (guards against a bad photo).</summary>
    public const double MaxStepFactor = 2.0;
    public const double FirstPhotoDamping = 1.0, LaterDamping = 0.8;

    public const double WhiteLevel = 0.6;   // LED gray level during the white stage
    public const double DarkLevel = 0.25;   // LED gray level during the dark stage
    public const double MinGamma = 0.7, MaxGamma = 1.4;
    public const double GammaDamping = 0.8;

    /// <summary>
    /// Error = 100 · max(|ρR − 1|, |ρB − 1|): the larger relative chroma deviation of the glow from screen white.
    /// Simple, monotonic and directly tied to the correction (3% ≈ just noticeable on a wall).
    /// </summary>
    public static double ErrorPercent(double rhoR, double rhoB) => 100 * Math.Max(Math.Abs(rhoR - 1), Math.Abs(rhoB - 1));

    /// <summary>
    /// White stage update: W'c = Wc / ρc^k (G untouched), per-step change clamped, then normalized so the strongest
    /// channel is 1 and clamped to [0.05, 1].
    /// Returns the new gains and the predicted residual ratio at the white level after applying them.
    /// </summary>
    public static (Rgb3 Gains, double ResidualR, double ResidualB) WhiteStep(Rgb3 w, double rhoR, double rhoB, double k)
    {
        double fr = Math.Clamp(Math.Pow(rhoR, -k), 1 / MaxStepFactor, MaxStepFactor);
        double fb = Math.Clamp(Math.Pow(rhoB, -k), 1 / MaxStepFactor, MaxStepFactor);
        var raw = new Rgb3(Math.Max(w.R, MinGain) * fr, Math.Max(w.G, MinGain), Math.Max(w.B, MinGain) * fb);
        var n = Normalize(raw);
        return (n, rhoR * Relative(n.R, n.G, w.R, w.G), rhoB * Relative(n.B, n.G, w.B, w.G));
    }

    /// <summary>
    /// Dark stage update. With ρc(L) = A_c · L^δc (δ = residual exponent relative to green) we know
    /// ρ at the white level (<paramref name="residualWhiteR"/>, predicted from the white stage) and measured ρ at the dark
    /// level, so δc = ln(ρdark / ρwhite) / ln(Ldark / Lwhite).
    /// Raising γc by Δ multiplies ρc(L) by L^Δ, so Δ = −δ (damped, γ clamped to [0.7, 1.4], G keeps its gamma).
    /// The gains are then re-solved so the white level stays neutral: ρ'(Lw) = ρw · Lw^Δ · W'c/Wc ≡ 1.
    /// </summary>
    public static (Rgb3 Gains, Rgb3 Gamma) DarkStep(Rgb3 w, Rgb3 gamma, double rhoDarkR, double rhoDarkB,
        double residualWhiteR, double residualWhiteB, double damping = GammaDamping)
    {
        double lnRatio = Math.Log(DarkLevel / WhiteLevel);
        double gR = Step(gamma.R, rhoDarkR, residualWhiteR, out double dR);
        double gB = Step(gamma.B, rhoDarkB, residualWhiteB, out double dB);
        var newGamma = new Rgb3(gR, gamma.G, gB);

        double fr = Math.Clamp(1 / (residualWhiteR * Math.Pow(WhiteLevel, dR)), 1 / MaxStepFactor, MaxStepFactor);
        double fb = Math.Clamp(1 / (residualWhiteB * Math.Pow(WhiteLevel, dB)), 1 / MaxStepFactor, MaxStepFactor);
        var gains = Normalize(new Rgb3(Math.Max(w.R, MinGain) * fr, Math.Max(w.G, MinGain), Math.Max(w.B, MinGain) * fb));
        return (gains, newGamma);

        double Step(double g, double rhoDark, double rhoWhite, out double applied)
        {
            double delta = Math.Log(Math.Max(1e-3, rhoDark) / Math.Max(1e-3, rhoWhite)) / lnRatio;
            double target = Math.Clamp(g - delta * damping, MinGamma, MaxGamma);
            applied = target - g;
            return target;
        }
    }

    public static Rgb3 Normalize(Rgb3 w)
    {
        double m = Math.Max(w.R, Math.Max(w.G, w.B));
        if (!(m > 0) || !double.IsFinite(m)) return new Rgb3(1, 1, 1);
        return new Rgb3(Math.Clamp(w.R / m, MinGain, MaxGain), Math.Clamp(w.G / m, MinGain, MaxGain), Math.Clamp(w.B / m, MinGain, MaxGain));
    }

    private static double Relative(double newC, double newG, double oldC, double oldG)
        => (newC / newG) / (Math.Max(oldC, MinGain) / Math.Max(oldG, MinGain));
}
