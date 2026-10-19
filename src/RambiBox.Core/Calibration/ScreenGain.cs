namespace RambiBox.Core.Calibration;

internal enum GainAction { Hold, Lower, Raise }

/// <param name="Level">Estimated camera level of the band (linear max channel, 1 = clipping), null if unknown.</param>
/// <param name="AtLimit">A change was needed but the gain is already at its limit.</param>
internal readonly record struct GainDecision(GainAction Action, double Gain, double? Level, string Reason, bool AtLimit = false);

/// <summary>
/// Closed-loop exposure control through the screen content. The phone's auto exposure is set mostly by the room and
/// the wall (a dark room pushes it up until the screen's white elements clip; tap-to-expose does not exist for web
/// pages on iOS), so instead of the camera, the calibration scales the intensity of every measured screen element
/// (<see cref="ScreenLevels.Gain"/>) until the camera sees the bands inside a comfortable window.
/// <para>
/// Window (camera linear, max channel of the band): <see cref="Low"/> (sRGB ≈ 100/255, ~40 % of the range) to
/// <see cref="High"/> (≈ 235/255). Outside it the gain is multiplied towards <see cref="Target"/> (≈ 190/255) — the
/// factor is computed from the measured level, or from the dimmer reference squares when the bands are clipped (a
/// fixed ×0.4 when everything is clipped). The window spans ~6.5× in linear light, which is the hysteresis: a
/// correction always lands well inside it, so the loop does not oscillate. Gain limits: <see cref="ScreenLevels.MinGain"/>
/// … <see cref="ScreenLevels.MaxGain"/> of full white. Pure function, deterministic.
/// </para>
/// </summary>
internal static class ScreenGainControl
{
    public static readonly double Low = CalibrationAnalyzer.ToLinear(100);
    public static readonly double High = CalibrationAnalyzer.ToLinear(235);
    public static readonly double Target = CalibrationAnalyzer.ToLinear(190);
    /// <summary>A band with more clipped pixels than this counts as clipped (clipped pixels bias its color).</summary>
    public const double ClipFraction = 0.02;
    public const double BlindLowerFactor = 0.4, BlindRaiseFactor = 3.0;

    public static GainDecision Decide(FrameMeasurement m, double gain)
    {
        var inFrame = m.Sides.Select(s => s.Screen).Where(r => r.Status != RegionStatus.OutOfFrame).ToList();
        int over = inFrame.Count(r => r.Status == RegionStatus.Clipped || r.ClippedFraction > ClipFraction);
        bool clipped = inFrame.Count > 0 ? over * 2 >= inFrame.Count && over > 0
                                         : m.Refs.Count > 0 && m.Refs[0].ClippedFraction > ClipFraction;
        if (clipped)
        {
            double? est = EstimateFromRefs(m);
            double f = est is { } e ? Math.Clamp(Target / e, 0.15, 0.85) : BlindLowerFactor;
            return Make(GainAction.Lower, gain, f, est, est is null ? "screen elements clipped" : "bands clipped");
        }
        var levels = inFrame.Where(r => r.Status == RegionStatus.Ok && r.ClippedFraction <= ClipFraction)
            .Select(r => FrameAnalyzer.MaxChannel(r.Mean)).ToList();
        if (levels.Count == 0)
        {
            if (inFrame.Count > 0 && inFrame.All(r => r.Status == RegionStatus.TooDark))
                return Make(GainAction.Raise, gain, BlindRaiseFactor, null, "bands too dark");
            return new GainDecision(GainAction.Hold, gain, null, "bands not measurable");
        }
        double level = CalibrationAnalyzer.Median(levels);
        if (level > High) return Make(GainAction.Lower, gain, Math.Clamp(Target / level, 0.3, 0.9), level, "bands nearly clipped");
        if (level < Low) return Make(GainAction.Raise, gain, Math.Clamp(Target / Math.Max(level, 1e-4), 1.25, 4), level, "bands too dark");
        return new GainDecision(GainAction.Hold, gain, level, "ok");
    }

    /// <summary>Band level implied by the brightest unclipped reference square with a usable signal (raw camera linear).</summary>
    private static double? EstimateFromRefs(FrameMeasurement m)
    {
        for (int i = 0; i < m.Refs.Count && i < CalibrationLayout.RefCount; i++)
        {
            var r = m.Refs[i];
            if (r.Status != RegionStatus.Ok || r.ClippedFraction > ClipFraction) continue;
            double v = FrameAnalyzer.MaxChannel(r.Mean);
            if (v < FrameAnalyzer.RefMinSignal) continue;
            return v / CalibrationLayout.RefFactors[i];
        }
        return null;
    }

    private static GainDecision Make(GainAction a, double gain, double factor, double? level, string why)
    {
        double g = Math.Clamp(gain * factor, ScreenLevels.MinGain, ScreenLevels.MaxGain);
        if (Math.Abs(g - gain) < 1e-9) return new GainDecision(GainAction.Hold, gain, level, why + " (at the limit)", AtLimit: true);
        return new GainDecision(a, g, level, why);
    }
}
