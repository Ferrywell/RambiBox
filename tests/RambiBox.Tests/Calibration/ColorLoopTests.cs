using System.Diagnostics;
using RambiBox.Core.Calibration;
using RambiBox.Core.Engine;
using Xunit.Abstractions;

namespace RambiBox.Tests.Calibration;

public class StepCodeTests
{
    [Fact]
    public void Encode_decode_round_trip_for_all_values()
    {
        for (int v = 0; v <= StepCode.MaxValue; v++)
        {
            var cells = StepCode.Encode(v);
            Assert.Equal(StepCode.Cells, cells.Length);
            Assert.True(cells[0]);
            Assert.Contains(true, cells[1..]); // never an all-black data row
            var lum = cells.Select(c => c ? 230.0 : 25.0).ToArray();
            Assert.Equal(v, StepCode.Decode(lum, 10));
        }
    }

    [Fact]
    public void Decode_rejects_bit_errors_low_contrast_and_ambiguous_cells()
    {
        var lum = StepCode.Encode(37).Select(c => c ? 200.0 : 20.0).ToArray();
        Assert.Equal(37, StepCode.Decode(lum, 15));
        var flipped = (double[])lum.Clone();
        flipped[3] = flipped[3] > 100 ? 20 : 200;
        Assert.Null(StepCode.Decode(flipped, 15)); // parity
        var ambiguous = (double[])lum.Clone();
        ambiguous[4] = 108;
        Assert.Null(StepCode.Decode(ambiguous, 15));
        Assert.Null(StepCode.Decode(lum.Select(x => x * 0.1 + 15).ToArray(), 15)); // no contrast
        Assert.Throws<ArgumentOutOfRangeException>(() => StepCode.Encode(64));
    }

    [Fact]
    public void Code_is_read_from_a_synthetic_frame()
    {
        var layout = new CalibrationLayout(16.0 / 9.0);
        foreach (int code in new[] { 1, 22, 39, 50 })
        {
            var img = new SyntheticPhoto
            {
                Width = 960, Height = 540, Pattern = layout.BuildMeasurePattern(CalibrationLayout.WhiteBandRgb, code, false),
                Corners = SyntheticPhoto.Pose(960, 540, 16.0 / 9.0, 10, -4, 2, 0.55), Seed = code,
            }.RenderThroughJpeg();
            var m = FrameAnalyzer.Measure(img, layout);
            Assert.True(m.Found);
            Assert.Equal(code, m.Code);
            Assert.True(m.RefOk);
            Assert.Equal(4, m.UsableSides);
        }
    }
}

public class ColorFitTests
{
    [Fact]
    public void Gamma_fit_recovers_a_known_per_channel_response()
    {
        // ρc(L) = A·L^δ exactly (G is the reference): δR = +0.12, δB = −0.09, A = 1.03 / 0.96.
        double aR = 1.03, dR = 0.12, aB = 0.96, dB = -0.09;
        var pts = new[] { 0.6, 0.35, 0.2, 0.12 }.Select(L => new GrayPoint(L, aR * Math.Pow(L, dR), aB * Math.Pow(L, dB))).ToList();
        var fit = ColorFit.FitGamma(pts)!.Value;
        Assert.Equal(dR, fit.DeltaR, 9);
        Assert.Equal(dB, fit.DeltaB, 9);
        Assert.Equal(Math.Log(aR), fit.LnAR, 9);
        Assert.Equal(Math.Log(aB), fit.LnAB, 9);

        var (wb, gamma) = ColorFit.ApplyGammaFit(new Rgb3(0.7, 1, 0.5), new Rgb3(1, 1, 1), fit);
        Assert.Equal(1 / (1 + dR), gamma.R, 9);
        Assert.Equal(1 / (1 + dB), gamma.B, 9);
        Assert.Equal(1.0, gamma.G);
        // After the correction ρ is constant (= A·(W'/W)^e) at every level and the WB makes it 1, e = (1 + δ)/γ.
        Assert.Equal(0.7 * Math.Pow(1 / aR, 1 / (1 + dR)), wb.R / wb.G, 9);
        Assert.Equal(0.5 * Math.Pow(1 / aB, 1 / (1 + dB)), wb.B / wb.G, 9);

        // Through a simulated strip with a real exponent the fitted correction makes all levels neutral.
        var rig = new VirtualRig { Wb = new Rgb3(1 / 1.05, 1 / 0.9 * 0.9, 1 / 1.45), StripExponent = new Rgb3(1.15, 1, 0.88), LedMix = Identity, ScreenMix = Identity };
        rig.Wb = CalibrationMath.Normalize(rig.Wb);
        var measured = new[] { 0.6, 0.35, 0.2, 0.12 }.Select(L =>
        {
            var g = rig.GlowCamera(rig.Drive(L, L, L, OverrideColorPath.WhiteBalanceNoMatrix));
            var (r, b) = ColorFit.CorrectQuantization(g.R / g.G, g.B / g.G, L, rig.Wb, rig.Gamma, 1);
            return new GrayPoint(L, r, b);
        }).ToList();
        var f2 = ColorFit.FitGamma(measured)!.Value;
        var (wb2, g2) = ColorFit.ApplyGammaFit(rig.Wb, rig.Gamma, f2);
        rig.Wb = wb2; rig.Gamma = g2;
        foreach (var L in new[] { 0.6, 0.35, 0.2 }) Assert.True(rig.GrayError(L) < 1.5, $"L={L}: {rig.GrayError(L):0.00}% gamma {g2}");
        // At 12 % the blue channel is ~14 LED steps: ±1 step rounding alone is ±3.5 %.
        Assert.True(rig.GrayError(0.12) < 4, $"L=0.12: {rig.GrayError(0.12):0.00}%");
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    [Fact]
    public void Matrix_fit_recovers_a_known_mixing_and_is_white_preserving()
    {
        // Screen primaries in camera space = LED primaries · Mtrue (LEDs more saturated, so Mtrue has positive mixing).
        double[] mTrue = [0.8, 0.15, 0.05, 0.1, 0.82, 0.08, 0.03, 0.12, 0.85]; // rows sum to 1
        var ledCols = new[] { new Rgb3(0.9, 0.05, 0.01), new Rgb3(0.04, 0.8, 0.06), new Rgb3(0.01, 0.07, 1.1) };
        var P = ColorFit.Columns(ledCols);
        var S = ColorFit.Mul(P, mTrue);
        var scrCols = Enumerable.Range(0, 3).Select(k => new Rgb3(S[k], S[3 + k], S[6 + k])).ToArray();
        var white = new Rgb3(scrCols.Sum(c => c.R), scrCols.Sum(c => c.G), scrCols.Sum(c => c.B));
        // Arbitrary per-column exposure of the measurements must not matter.
        var ledMeasured = ledCols.Select((c, k) => new Rgb3(c.R * (1 + k * 0.3), c.G * (1 + k * 0.3), c.B * (1 + k * 0.3))).ToArray();
        var scrMeasured = scrCols.Select((c, k) => new Rgb3(c.R * (2 - k * 0.4), c.G * (2 - k * 0.4), c.B * (2 - k * 0.4))).ToArray();

        var m = ColorFit.FitMatrix(ledMeasured, scrMeasured, white)!;
        for (int i = 0; i < 9; i++) Assert.Equal(mTrue[i], m[i], 6);
        for (int r = 0; r < 3; r++) Assert.Equal(1.0, m[r * 3] + m[r * 3 + 1] + m[r * 3 + 2], 9);

        // Regularized: rows still sum to 1, entries between identity and the fit.
        var b = ColorFit.Blend(m, 0.6);
        for (int r = 0; r < 3; r++) Assert.Equal(1.0, b[r * 3] + b[r * 3 + 1] + b[r * 3 + 2], 9);
        Assert.Equal(0.4 + 0.6 * 0.8, b[0], 9);
        Assert.Equal(0.6 * 0.15, b[1], 9);
        Assert.True(ColorFit.Deviation(b) < ColorFit.Deviation(m));
        Assert.NotNull(RambiBox.Core.Config.SettingsSerializer.NormalizeMatrix(b)); // valid engine matrix
    }

    [Fact]
    public void Matrix_fit_clamps_out_of_gamut_and_rejects_nonsense()
    {
        // Screen red more saturated than the LED red → negative entries → clamped to 0, rows renormalized.
        var led = new[] { new Rgb3(0.8, 0.15, 0.05), new Rgb3(0.05, 0.9, 0.05), new Rgb3(0.02, 0.05, 0.95) };
        var scr = new[] { new Rgb3(0.95, 0.03, 0.02), new Rgb3(0.05, 0.9, 0.05), new Rgb3(0.02, 0.05, 0.95) };
        var w = new Rgb3(1.02, 0.98, 1.02);
        var m = ColorFit.FitMatrix(led, scr, w)!;
        Assert.All(m, v => Assert.True(v >= 0));
        for (int r = 0; r < 3; r++) Assert.Equal(1.0, m[r * 3] + m[r * 3 + 1] + m[r * 3 + 2], 9);
        // Degenerate (two identical columns) → null
        Assert.Null(ColorFit.FitMatrix([led[0], led[0], led[2]], scr, w));
        // White not reachable with positive amounts → null
        Assert.Null(ColorFit.FitMatrix(led, scr, new Rgb3(-1, 1, 1)));
    }

    [Fact]
    public void Eye_pad_maps_to_white_balance_and_gamma()
    {
        var baseWb = new Rgb3(0.8, 1, 0.4);
        Assert.Equal(baseWb, ColorFit.WhiteFromPad(baseWb, 0, 0));
        var warm = ColorFit.WhiteFromPad(baseWb, 0.5, 0);
        Assert.True(warm.R / warm.G > baseWb.R / baseWb.G && warm.B / warm.G < baseWb.B / baseWb.G);
        Assert.Equal(Math.Exp(2 * ColorFit.PadWhiteK * 0.5), (warm.R / warm.B) / (baseWb.R / baseWb.B), 9);
        var magenta = ColorFit.WhiteFromPad(baseWb, 0, 1);
        Assert.Equal(1.0, Math.Max(magenta.R, Math.Max(magenta.G, magenta.B)), 9);
        Assert.True(magenta.G / magenta.R < baseWb.G / baseWb.R); // less green
        Assert.Equal(ColorFit.WhiteFromPad(baseWb, 1, 1), ColorFit.WhiteFromPad(baseWb, 5, 9)); // clamped

        var g0 = new Rgb3(1.1, 1, 0.95);
        var g = ColorFit.GammaFromPad(g0, 0.5, 0.5);
        Assert.Equal(1.1 - 0.5 * ColorFit.PadDarkK, g.R, 9);
        Assert.Equal(1 + 0.5 * ColorFit.PadDarkK, g.G, 9);
        Assert.Equal(0.95 + 0.5 * ColorFit.PadDarkK, g.B, 9);
        // white (L = 1) is unchanged by any gamma; a dark level gets warmer (more red relative to blue)
        Assert.Equal(1.0, Math.Pow(1.0, g.R));
        Assert.True(Math.Pow(0.2, g.R) / Math.Pow(0.2, g.B) > Math.Pow(0.2, g0.R) / Math.Pow(0.2, g0.B));
        var clamped = ColorFit.GammaFromPad(new Rgb3(0.72, 1, 1.38), 1, -1);
        Assert.Equal(0.7, clamped.R);
        Assert.Equal(1.4, clamped.B);
    }

    [Fact]
    public void Quantization_factor_reflects_rounding()
    {
        Assert.Equal(1.0, ColorFit.QuantizationFactor(1, 1, 1, 1), 9);
        int b = ColorFit.LedByte(0.12, 1, 0.39, 1); // 11.93 → 12
        Assert.Equal(12, b);
        Assert.Equal(0.12 * 0.39 * 255 / 12, ColorFit.QuantizationFactor(0.12, 1, 0.39, 1), 9);
    }
}

public class FrameSpeedTests(ITestOutputHelper output)
{
    [Fact]
    public void A_1280px_frame_is_decoded_and_analyzed_quickly()
    {
        var layout = new CalibrationLayout(16.0 / 9.0);
        var jpeg = new SyntheticPhoto
        {
            Width = 1280, Height = 720, Pattern = layout.BuildMeasurePattern(CalibrationLayout.WhiteBandRgb, 5, false),
            Corners = SyntheticPhoto.Pose(1280, 720, 16.0 / 9.0, 6, -3, 1, 0.6),
        }.RenderJpeg();
        FrameAnalyzer.Measure(ImageCodec.Decode(jpeg), layout); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        const int n = 5;
        for (int i = 0; i < n; i++) Assert.Equal(5, FrameAnalyzer.Measure(ImageCodec.Decode(jpeg), layout).Code);
        double ms = sw.Elapsed.TotalMilliseconds / n;
        output.WriteLine($"decode + analyze: {ms:0} ms per frame");
        Assert.True(ms < 600, $"{ms:0} ms"); // target < 300 ms on a desktop PC; generous for CI/debug builds
    }
}

public class CalibrationSessionTests
{
    private static (VirtualRig Rig, FakeClock Clock, CalibrationLedOverride Led, CalibrationSession S) Setup(VirtualRig? rig = null)
    {
        rig ??= new VirtualRig();
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
        return (rig, clock, led, s);
    }

    [Fact]
    public void Live_loop_converges_fits_gamma_and_matrix_then_enters_eye_mode()
    {
        var (rig, clock, led, s) = Setup();
        double whiteBefore = rig.GrayError(0.6), darkBefore = rig.GrayError(0.12);
        Assert.True(whiteBefore > 10 && darkBefore > 10, $"setup should start off: {whiteBefore:0.0}% / {darkBefore:0.0}%");
        Assert.True(s.PhoneConnected());
        Assert.False(s.Pattern.ShowQr);
        s.StartLive();
        Assert.Equal(SessionMode.Live, s.Mode);
        Assert.Equal(OverrideColorPath.WhiteBalanceNoMatrix, led.ColorPath);

        int frames = 0, used = 0;
        var seen = new List<string>();
        var sw = Stopwatch.StartNew();
        while (s.Mode == SessionMode.Live && frames < 120)
        {
            clock.Now += 0.5;
            var step = s.CurrentStep!;
            if (seen.Count == 0 || seen[^1] != step.Id) seen.Add(step.Id);
            var r = s.ProcessLiveFrame(rig.Shoot(s, led, frames, yaw: 4 + frames % 3, pitch: -2));
            Assert.True(r.Measurement!.Found, r.Note);
            if (r.Used) used++;
            frames++;
        }
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.Equal(CalibrationSession.LivePlan.Select(p => p.Id), seen);
        Assert.True(frames <= 60, $"{frames} frames");

        double white = rig.GrayError(0.6), white1 = rig.GrayError(1.0), dark = rig.GrayError(0.12), mid = rig.GrayError(0.3);
        Assert.True(white < 2.5, $"white {white:0.00}% (was {whiteBefore:0.0}%), WB {rig.Wb}");
        Assert.True(white1 < 3.5, $"full white {white1:0.00}%");
        Assert.True(dark < 4 && dark < darkBefore / 3, $"dark {dark:0.00}% (was {darkBefore:0.0}%), gamma {rig.Gamma}");
        Assert.True(mid < 3, $"mid {mid:0.00}%");
        Assert.True(rig.Gamma.R < 1 && rig.Gamma.B > 1, $"gamma direction {rig.Gamma}");
        Assert.Equal(1.0, rig.Gamma.G);

        // Matrix candidate: not applied yet, but brings the LED primaries closer to the screen's.
        Assert.Null(rig.Matrix);
        var full = s.MatrixFull;
        Assert.NotNull(full);
        foreach (int c in new[] { 0xB30000, 0x00B300, 0x0000B3 })
        {
            double before = rig.ColorError(c, null), after = rig.ColorError(c, full);
            Assert.True(after < before * 0.5, $"color {c:X6}: {before:0.00} → {after:0.00}, M {CalibrationSession.FormatMatrix(full)}");
        }
        var cand = s.MatrixCandidate!;
        for (int r = 0; r < 3; r++) Assert.Equal(1.0, cand[r * 3] + cand[r * 3 + 1] + cand[r * 3 + 2], 6);
        Assert.True(ColorFit.Deviation(cand) < ColorFit.Deviation(full));

        // Eye mode shows white on the LEDs through the full correction path.
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.Equal(OverrideColorPath.LedCorrection, led.ColorPath);
        Assert.True(s.Pattern.EyeMode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Live_frames_with_a_stale_code_or_too_early_are_ignored()
    {
        var (rig, clock, led, s) = Setup();
        s.PhoneConnected();
        s.StartLive();
        Assert.Equal("ambient", s.CurrentStep!.Id);
        int seed = 10;
        while (s.CurrentStep!.Id == "ambient" && seed < 30) { clock.Now += 1; s.ProcessLiveFrame(rig.Shoot(s, led, seed++)); }
        Assert.Equal("white", s.CurrentStep!.Id);
        clock.Now += 1;
        var oldFrame = rig.Shoot(s, led, 1);
        var r = s.ProcessLiveFrame(oldFrame);
        Assert.True(r.Used);                     // first white frame → correction applied, new code
        var wb = rig.Wb;
        clock.Now += 1;
        r = s.ProcessLiveFrame(oldFrame);        // frame still showing the previous code
        Assert.False(r.Used);
        Assert.Contains("new pattern", r.Note);
        Assert.Equal(wb, rig.Wb);

        var fresh = rig.Shoot(s, led, 2);
        clock.Now += 0.1;                        // pattern changed < settle time ago
        s.StartLive();                           // restart → new code + switch time
        fresh = rig.Shoot(s, led, 3);
        r = s.ProcessLiveFrame(fresh);
        Assert.False(r.Used);
        Assert.Contains("settle", r.Note);
        Assert.True(r.DelayMs > 0);
    }

    [Fact]
    public void No_screen_gives_a_hint()
    {
        var (_, clock, _, s) = Setup();
        s.PhoneConnected();
        s.StartLive();
        clock.Now += 1;
        var r = s.ProcessLiveFrame(new SyntheticPhoto { Width = 640, Height = 360, ShowScreen = false }.Render());
        Assert.False(r.Used);
        Assert.Contains(r.Hints, h => h.Contains("corner squares"));
    }

    [Fact]
    public void Video_frames_are_processed_by_their_step_code()
    {
        var rig = new VirtualRig { Width = 800, Height = 450 };
        var (_, clock, led, s) = Setup(rig);
        s.PhoneConnected();
        double whiteBefore = rig.GrayError(0.6), darkBefore = rig.GrayError(0.12);
        s.StartVideoLoop();
        Assert.Equal(SessionMode.Video, s.Mode);
        double t0 = clock.Now;
        // "Record": a frame every 0.4 s over a bit more than one loop, starting in the middle of a step.
        var frames = new List<(double T, RgbImage Img)>();
        for (double t = 1.1; t < CalibrationSession.VideoLoopSeconds + 2.6; t += 0.4)
        {
            clock.Now = t0 + t;
            s.Tick();
            frames.Add((t, rig.Shoot(s, led, (int)(t * 10))));
        }
        // Upload in batches (any order).
        var codes = new HashSet<int>();
        foreach (var (t, img) in frames.OrderBy(f => f.T % 3))
        {
            var (code, _) = s.ProcessVideoFrame(img, t);
            if (code is { } c) codes.Add(c);
        }
        Assert.Equal(CalibrationSession.VideoPlan.Length, codes.Count);
        Assert.Equal(frames.Count, s.VideoCounts.Received);
        Assert.Null(s.FinishVideo());
        Assert.Equal(SessionMode.Eye, s.Mode);

        double white = rig.GrayError(0.6), dark = rig.GrayError(0.12);
        Assert.True(white < 3, $"white {white:0.00}% (was {whiteBefore:0.0}%), WB {rig.Wb}");
        Assert.True(dark < darkBefore / 2, $"dark {dark:0.00}% (was {darkBefore:0.0}%), gamma {rig.Gamma}");
        var full = s.MatrixFull;
        Assert.NotNull(full);
        foreach (int c in new[] { 0xB30000, 0x00B300, 0x0000B3 })
            Assert.True(rig.ColorError(c, full) < rig.ColorError(c, null) * 0.6, $"color {c:X6}");
    }

    [Fact]
    public void Video_without_the_pattern_reports_a_problem_and_keeps_looping()
    {
        var (_, clock, _, s) = Setup();
        s.PhoneConnected();
        s.StartVideoLoop();
        s.ProcessVideoFrame(new SyntheticPhoto { Width = 640, Height = 360, ShowScreen = false }.Render(), 1);
        Assert.NotNull(s.FinishVideo());
        Assert.Equal(SessionMode.Video, s.Mode);
        clock.Now += CalibrationSession.VideoStepSeconds;
        Assert.True(s.Tick());
    }

    [Fact]
    public void Eye_tune_pad_ab_votes_finish_and_cancel()
    {
        var rig = new VirtualRig { Matrix = null };
        var (_, _, led, s) = Setup(rig);
        s.PhoneConnected();
        s.SkipCamera();
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.Equal(EyeStage.White, s.EyeStage);
        var baseWb = rig.Wb;

        Assert.Null(s.Eye(new EyeCommand("pad", T: 0.4, M: -0.2)));
        Assert.Equal(ColorFit.WhiteFromPad(baseWb, 0.4, -0.2), rig.Wb);
        Assert.Null(s.Eye(new EyeCommand("reset")));
        Assert.Equal(baseWb, rig.Wb);
        Assert.Null(s.Eye(new EyeCommand("pad", T: 0.2, M: 0.1)));
        var tunedWb = rig.Wb;

        Assert.Null(s.Eye(new EyeCommand("stage", Stage: EyeStage.Dark)));
        Assert.Equal(CalibrationSession.EyeDarkLevel, led.Current.R, 5);
        Assert.Null(s.Eye(new EyeCommand("pad", T: -0.5, M: 0)));
        Assert.Equal(ColorFit.GammaFromPad(new Rgb3(1, 1, 1), -0.5, 0), rig.Gamma);
        Assert.Equal(tunedWb, rig.Wb); // dark tuning leaves white alone
        Assert.NotNull(s.Eye(new EyeCommand("stage", Stage: EyeStage.Colors))); // no matrix measured when skipped

        s.Finish();
        Assert.Equal(SessionMode.Finished, s.Mode);
        Assert.Null(rig.Matrix);
        s.RestoreOriginal();
        Assert.Equal(new Rgb3(0.64, 1.0, 0.51), rig.Wb);
        Assert.Equal(new Rgb3(1, 1, 1), rig.Gamma);
    }

    [Fact]
    public void Ab_check_keeps_the_matrix_only_if_a_wins()
    {
        foreach (var (votes, expectMatrix) in new[] { (new[] { "A", "A", "B" }, true), (new[] { "B", "same", "A", "B" }, false) })
        {
            var rig = new VirtualRig { Width = 640, Height = 360 };
            var (_, clock, led, s) = Setup(rig);
            s.PhoneConnected();
            s.StartLive();
            int n = 0;
            while (s.Mode == SessionMode.Live && n < 120) { clock.Now += 0.5; s.ProcessLiveFrame(rig.Shoot(s, led, n++)); }
            Assert.Equal(SessionMode.Eye, s.Mode);
            Assert.NotNull(s.MatrixCandidate);
            Assert.Null(s.Eye(new EyeCommand("stage", Stage: EyeStage.Colors)));
            Assert.Equal(1.0, led.Current.R, 3); // red first, shown with the candidate (A)
            Assert.True(CalibrationSession.SameMatrix(rig.Matrix, s.MatrixCandidate));
            Assert.Null(s.Eye(new EyeCommand("show", Variant: "B")));
            Assert.Null(rig.Matrix);
            Assert.Null(s.Eye(new EyeCommand("strength", Delta: +1)));
            Assert.Equal(0.75, s.Snapshot().MatrixStrength);
            foreach (var v in votes) Assert.Null(s.Eye(new EyeCommand("vote", Variant: v)));
            s.Finish();
            if (expectMatrix)
            {
                Assert.NotNull(rig.Matrix);
                Assert.True(CalibrationSession.SameMatrix(rig.Matrix, ColorFit.Blend(s.MatrixFull!, 0.75)));
            }
            else Assert.Null(rig.Matrix);
            s.RestoreOriginal();
            Assert.Null(rig.Matrix);
        }
    }
}
