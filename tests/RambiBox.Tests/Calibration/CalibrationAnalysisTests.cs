using RambiBox.Core.Calibration;

namespace RambiBox.Tests.Calibration;

public class CalibrationAnalysisTests
{
    private const double Aspect = 16.0 / 9.0;
    private const int W = SyntheticPhoto.DefaultWidth, H = SyntheticPhoto.DefaultHeight;

    public static TheoryData<int, int, double, double, double, double, double, double> Poses => new()
    {
        // width, height, yaw, pitch, roll, widthFraction, offX, offY
        { 1600, 1200, 0, 0, 0, 0.6, 0, 0 },
        { 1600, 1200, 18, -6, 3, 0.55, 0.03, -0.02 },
        { 2000, 1500, -25, 8, -5, 0.5, -0.04, 0.03 },  // full upload size
        { 1200, 1600, 10, 12, 2, 0.75, 0, 0 },      // portrait photo
        { 1600, 900, 0, -15, -8, 0.45, 0.05, 0.05 }, // small and rolled
    };

    [Theory]
    [MemberData(nameof(Poses))]
    public void Detects_screen_corners_within_1_percent(int w, int h, double yaw, double pitch, double roll, double frac, double ox, double oy)
    {
        var corners = SyntheticPhoto.Pose(w, h, Aspect, yaw, pitch, roll, frac, ox, oy);
        var scene = new SyntheticPhoto { Width = w, Height = h, Corners = corners, Seed = w + h };
        var img = scene.RenderThroughJpeg();

        var det = FiducialDetector.Detect(img, new CalibrationLayout(Aspect));

        Assert.NotNull(det);
        double size = Math.Max((corners[1] - corners[0]).Length, (corners[3] - corners[0]).Length);
        for (int i = 0; i < 4; i++)
        {
            double err = (det!.Corners[i] - corners[i]).Length / size;
            Assert.True(err < 0.01, $"corner {i}: error {err:P2} (got {det.Corners[i]}, want {corners[i]})");
        }
    }


    [Fact]
    public void Ignores_marker_like_distractors_outside_the_screen()
    {
        var corners = SyntheticPhoto.Pose(W, H, Aspect, 6, 3, 1, 0.5);
        var scene = new SyntheticPhoto
        {
            Corners = corners, Seed = 77,
            // Four marker look-alikes near the photo corners form a much larger quad than the real markers.
            Distractors = [new(70, 70), new(W - 70, 75), new(W - 75, H - 70), new(72, H - 72)],
        };
        var det = FiducialDetector.Detect(scene.RenderThroughJpeg(), new CalibrationLayout(Aspect));
        Assert.NotNull(det);
        double size = (corners[1] - corners[0]).Length;
        for (int i = 0; i < 4; i++) Assert.True((det!.Corners[i] - corners[i]).Length / size < 0.01);
    }
}

public class CalibrationConvergenceTests
{
    private const double Aspect = 16.0 / 9.0;
    private const int W = SyntheticPhoto.DefaultWidth, H = SyntheticPhoto.DefaultHeight;

    [Fact]
    public void White_balance_converges_within_three_photos()
    {
        var wb = new Rgb3(0.64, 1.0, 0.51); // AmbiBox import
        var tint = new Rgb3(1.05, 0.9, 1.45);
        var gamma = new Rgb3(1, 1, 1);
        var exp = new Rgb3(1, 1, 1);
        var layout = new CalibrationLayout(Aspect);
        double initial = SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.WhiteLevel);
        Assert.True(initial > 10, $"test setup should start clearly off, was {initial:0.0}%");

        double measured = double.NaN, trueErr = initial;
        for (int i = 0; i < 3; i++)
        {
            var scene = new SyntheticPhoto
            {
                Wb = wb, Tint = tint, Seed = 10 + i, ToneExponent = 1.08, // slightly non-ideal camera
                Corners = SyntheticPhoto.Pose(W, H, Aspect, 8 * i - 6, 4, 1.5 * i, 0.6),
            };
            var a = CalibrationAnalyzer.Analyze(scene.RenderThroughJpeg(), layout);
            Assert.True(a.Ok, a.Message);
            measured = a.ErrorPercent;
            double k = i == 0 ? CalibrationMath.FirstPhotoDamping : CalibrationMath.LaterDamping;
            (wb, _, _) = CalibrationMath.WhiteStep(wb, a.RhoR, a.RhoB, k);
            trueErr = SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.WhiteLevel);
            if (i == 0) Assert.InRange(Math.Abs(measured - initial), 0, initial * 0.25); // measurement is sane
        }
        Assert.True(trueErr < CalibrationMath.ConvergedErrorPercent, $"true error after 3 photos {trueErr:0.00}% (wb {wb})");
        Assert.Equal(1.0, Math.Max(wb.R, Math.Max(wb.G, wb.B)), 6);
    }

    [Fact]
    public void Measured_error_of_a_calibrated_setup_is_small()
    {
        var tint = new Rgb3(1.05, 0.9, 1.45);
        var wb = CalibrationMath.Normalize(new Rgb3(1 / tint.R, 1 / tint.G, 1 / tint.B));
        var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto { Wb = wb, Tint = tint, Seed = 3 }.RenderThroughJpeg(),
            new CalibrationLayout(Aspect));
        Assert.True(a.Ok, a.Message);
        Assert.True(a.ErrorPercent < 1.5, $"measured {a.ErrorPercent:0.00}%");
        Assert.Equal(4, a.Sides.Count(s => s.Valid));
    }

}

public class CalibrationGammaTests
{
    private const double Aspect = 16.0 / 9.0;
    private const int W = SyntheticPhoto.DefaultWidth, H = SyntheticPhoto.DefaultHeight;

    [Fact]
    public void Dark_stage_fits_channel_gamma()
    {
        // LED strip with channel-dependent response: red too strong in the darks? exp > 1 means darker darks.
        var exp = new Rgb3(1.15, 1.0, 0.9);
        var tint = new Rgb3(1.05, 0.9, 1.45);
        var wb = new Rgb3(0.64, 1.0, 0.51);
        var gamma = new Rgb3(1, 1, 1);
        var layout = new CalibrationLayout(Aspect);
        double rr = 1, rb = 1;
        for (int i = 0; i < 3; i++)
        {
            var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto
            {
                Wb = wb, Gamma = gamma, StripExponent = exp, Tint = tint, Seed = 20 + i,
                Corners = SyntheticPhoto.Pose(W, H, Aspect, 5, -3, 1, 0.6),
            }.RenderThroughJpeg(), layout);
            Assert.True(a.Ok, a.Message);
            (wb, rr, rb) = CalibrationMath.WhiteStep(wb, a.RhoR, a.RhoB, i == 0 ? 1.0 : 0.8);
        }
        Assert.True(SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.WhiteLevel) < 3);
        double darkBefore = SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.DarkLevel);
        Assert.True(darkBefore > 6, $"setup: dark error before {darkBefore:0.0}%");

        for (int i = 0; i < 2; i++)
        {
            var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto
            {
                Wb = wb, Gamma = gamma, StripExponent = exp, Tint = tint, Seed = 40 + i,
                LedLevel = CalibrationMath.DarkLevel, DarkStage = true,
                Corners = SyntheticPhoto.Pose(W, H, Aspect, -4, 2, -1, 0.6),
            }.RenderThroughJpeg(), layout);
            Assert.True(a.Ok, a.Message);
            (wb, gamma) = CalibrationMath.DarkStep(wb, gamma, a.RhoR, a.RhoB, rr, rb);
            rr = rb = 1;
        }
        double dark = SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.DarkLevel);
        double white = SyntheticPhoto.TrueError(wb, gamma, exp, tint, CalibrationMath.WhiteLevel);
        Assert.True(dark < 3, $"dark error after fit {dark:0.00}% (was {darkBefore:0.0}%), gamma {gamma}");
        Assert.True(white < 3, $"white error after fit {white:0.00}%");
        Assert.True(gamma.R < 1 && gamma.B > 1, $"gamma direction {gamma}");
        Assert.Equal(1.0, gamma.G);
    }

    [Fact]
    public void Dark_step_math_is_exact_for_ideal_strip()
    {
        // Ideal model, residual exponent δR = +0.1, δB = −0.08, white currently neutral.
        double rhoR = Math.Pow(CalibrationMath.DarkLevel / CalibrationMath.WhiteLevel, 0.1);
        double rhoB = Math.Pow(CalibrationMath.DarkLevel / CalibrationMath.WhiteLevel, -0.08);
        var (wb, g) = CalibrationMath.DarkStep(new Rgb3(0.7, 1, 0.6), new Rgb3(1, 1, 1), rhoR, rhoB, 1, 1, damping: 1);
        Assert.Equal(0.9, g.R, 6);
        Assert.Equal(1.08, g.B, 6);
        // White level must stay neutral: L^Δ · W'/W = 1  →  W'R/W'G = 0.7 · 0.6^0.1
        Assert.Equal(0.7 * Math.Pow(0.6, 0.1), wb.R / wb.G, 6);
        Assert.Equal(0.6 * Math.Pow(0.6, -0.08), wb.B / wb.G, 6);
    }

}

public class CalibrationRejectionTests
{
    private const double Aspect = 16.0 / 9.0;
    private const int W = SyntheticPhoto.DefaultWidth, H = SyntheticPhoto.DefaultHeight;

    [Fact]
    public void Rejects_overexposed_photo()
    {
        var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto { Exposure = 8, Seed = 5 }.RenderThroughJpeg(),
            new CalibrationLayout(Aspect));
        Assert.False(a.Ok);
        Assert.Contains(a.Failure, new[] { AnalysisFailure.GlowOverexposed, AnalysisFailure.ScreenOverexposed });
        Assert.Contains("overexposed", a.Message);
    }

    [Fact]
    public void Rejects_photo_without_screen()
    {
        var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto { ShowScreen = false, Seed = 6 }.RenderThroughJpeg(),
            new CalibrationLayout(Aspect));
        Assert.False(a.Ok);
        Assert.Equal(AnalysisFailure.ScreenNotFound, a.Failure);
        Assert.StartsWith("Screen not found", a.Message);
    }

    [Fact]
    public void Rejects_weak_glow()
    {
        var a = CalibrationAnalyzer.Analyze(new SyntheticPhoto { GlowStrength = 0.004, Seed = 7 }.RenderThroughJpeg(),
            new CalibrationLayout(Aspect));
        Assert.False(a.Ok);
        Assert.Equal(AnalysisFailure.GlowTooWeak, a.Failure);
    }

    [Fact]
    public void Manual_corners_give_same_result_as_detection()
    {
        var corners = SyntheticPhoto.Pose(W, H, Aspect, 12, 5, -2, 0.55);
        var img = new SyntheticPhoto { Corners = corners, Seed = 8 }.RenderThroughJpeg();
        var layout = new CalibrationLayout(Aspect);
        var auto = CalibrationAnalyzer.Analyze(img, layout);
        var manual = CalibrationAnalyzer.Analyze(img, layout, corners);
        Assert.True(auto.Ok && manual.Ok);
        Assert.Equal(auto.RhoR, manual.RhoR, 2);
        Assert.Equal(auto.RhoB, manual.RhoB, 2);
    }

    [Fact]
    public void White_step_clamps_and_normalizes()
    {
        var (w, _, _) = CalibrationMath.WhiteStep(new Rgb3(0.5, 1, 0.5), 0.1, 10, 1.0);
        Assert.Equal(1.0, Math.Max(w.R, Math.Max(w.G, w.B)), 9);
        Assert.Equal(1.0, w.R, 6);           // 0.5 × 2 (clamped step) = 1.0
        Assert.Equal(0.25, w.B, 6);          // 0.5 / 2 (clamped step)
        Assert.InRange(w.G, 0.99, 1.0);
    }
}
