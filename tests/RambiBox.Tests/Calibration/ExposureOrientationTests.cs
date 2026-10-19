using RambiBox.Core.Calibration;
using RambiBox.Core.Engine;
using Xunit.Abstractions;

namespace RambiBox.Tests.Calibration;

/// <summary>Any phone orientation: portrait framing (small landscape screen), 90° / −90° / 180° / odd rolls.</summary>
public class OrientationTests
{
    private const double Aspect = 16.0 / 9.0;

    public static TheoryData<int, int, double, double, double> Framings => new()
    {
        // width, height, roll, widthFraction, yaw
        { 900, 1600, 0, 0.38, 6 },    // phone held upright in portrait: monitor ~38 % of the frame width
        { 720, 1280, 3, 0.38, -8 },   // the same at the phone's default grab size
        { 1280, 720, 90, 0.33, 5 },   // portrait-held phone delivering sensor-oriented (landscape) frames
        { 1280, 720, -90, 0.33, -5 },
        { 1280, 720, 180, 0.55, 8 },  // upside down
        { 900, 1600, 180, 0.38, 0 },
        { 1280, 960, 37, 0.45, 4 },   // any roll
        { 1600, 900, 0, 0.55, 12 },   // the classic landscape case still works
    };

    [Theory]
    [MemberData(nameof(Framings))]
    public void Screen_corners_code_and_reference_are_found_in_any_orientation(int w, int h, double roll, double frac, double yaw)
    {
        var layout = new CalibrationLayout(Aspect);
        var corners = SyntheticPhoto.Pose(w, h, Aspect, yaw, -3, roll, frac);
        var img = new SyntheticPhoto
        {
            Width = w, Height = h, Corners = corners, Seed = (int)roll + w,
            Pattern = layout.BuildMeasurePattern(ScreenLevels.ForGain(0.45), 27, showQr: false),
        }.RenderThroughJpeg();

        var m = FrameAnalyzer.Measure(img, layout);

        Assert.True(m.Found, "screen not found");
        double size = Math.Max((corners[1] - corners[0]).Length, (corners[3] - corners[0]).Length);
        for (int i = 0; i < 4; i++)
        {
            double err = (m.Corners![i] - corners[i]).Length / size;
            Assert.True(err < 0.01, $"corner {i}: error {err:P2} (got {m.Corners[i]}, want {corners[i]})");
        }
        Assert.Equal(27, m.Code);
        Assert.True(m.RefOk);
        Assert.True(m.UsableSides >= 3, $"{m.UsableSides} usable sides");
        Assert.True(m.ScreenFraction > 0.3, $"screen fraction {m.ScreenFraction:0.00}");
    }

    [Fact]
    public void Ninety_degree_hypotheses_are_told_apart_by_the_marker_shapes()
    {
        // Strong yaw foreshortens the screen width (long axis ≈ short axis in the image): still the right corners.
        var layout = new CalibrationLayout(Aspect);
        var corners = SyntheticPhoto.Pose(1280, 960, Aspect, 50, 0, 90, 0.4);
        var img = new SyntheticPhoto
        {
            Width = 1280, Height = 960, Corners = corners, Seed = 9,
            Pattern = layout.BuildMeasurePattern(ScreenLevels.ForGain(0.45), 12, showQr: false),
        }.RenderThroughJpeg();
        var det = FiducialDetector.Detect(img, layout);
        Assert.NotNull(det);
        double size = (corners[1] - corners[0]).Length;
        for (int i = 0; i < 4; i++) Assert.True((det!.Corners[i] - corners[i]).Length / size < 0.015, $"corner {i}");
    }

    [Fact]
    public void Live_loop_completes_with_the_phone_in_portrait_and_upside_down()
    {
        foreach (var (w, h, roll, frac) in new[] { (720, 1280, 2.0, 0.38), (960, 540, 180.0, 0.6), (1280, 720, 90.0, 0.33) })
        {
            var rig = new VirtualRig { Width = w, Height = h, Roll = roll, WidthFraction = frac };
            var clock = new FakeClock { Now = 100 };
            var led = new CalibrationLedOverride();
            var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
            s.PhoneConnected();
            s.StartLive();
            int n = 0;
            while (s.Mode == SessionMode.Live && n < 120)
            {
                clock.Now += 0.5;
                var r = s.ProcessLiveFrame(rig.Shoot(s, led, n++));
                Assert.True(r.Measurement!.Found, $"roll {roll}: frame {n} {r.Note}");
            }
            Assert.Equal(SessionMode.Eye, s.Mode);
            Assert.Empty(s.StepNotes); // nothing timed out or was skipped
            Assert.True(rig.GrayError(1.0) < 3, $"roll {roll}: white {rig.GrayError(1.0):0.00}%");
            Assert.NotNull(s.MatrixFull);
        }
    }
}

/// <summary>Exposure control through the screen content (dark rooms, clipped references) and the LED levels.</summary>
public class ScreenGainTests(ITestOutputHelper output)
{
    private const double Aspect = 16.0 / 9.0;

    private static FrameMeasurement Shot(double gain, double exposure, int seed = 1, int code = 9)
    {
        var layout = new CalibrationLayout(Aspect);
        var img = new SyntheticPhoto
        {
            Width = 960, Height = 540, Seed = seed, Exposure = exposure,
            Corners = SyntheticPhoto.Pose(960, 540, Aspect, 5, -2, 1, 0.6),
            Pattern = layout.BuildMeasurePattern(ScreenLevels.ForGain(gain), code, showQr: false),
        }.RenderThroughJpeg();
        return FrameAnalyzer.Measure(img, layout);
    }

    [Fact]
    public void Brightest_unclipped_reference_square_is_used_and_normalizes_consistently()
    {
        const double gain = 0.45;
        var lv = ScreenLevels.ForGain(gain);
        var picked = new List<(int Index, double Scale)>();
        // Default camera WB scales blue by 1.3: the 1× square clips above exposure ≈ 1.7, the ½× above ≈ 3.4 …
        foreach (double e in new[] { 1.0, 2.6, 5.0, 12.0 })
        {
            var m = Shot(gain, e, seed: (int)(e * 10));
            Assert.True(m.Found);
            picked.Add((m.RefIndex, m.RefOk ? m.RefNet.G / lv.RefLinear(m.RefIndex) / e : double.NaN));
        }
        Assert.Equal([0, 1, 2, -1], picked.Select(p => p.Index));
        // Normalized units don't depend on which square was used (camera response per displayed linear unit).
        var scales = picked.Where(p => p.Index >= 0).Select(p => p.Scale).ToList();
        Assert.True(scales.Max() / scales.Min() < 1.06, string.Join(", ", scales.Select(x => x.ToString("0.000"))));
    }

    [Fact]
    public void Gain_control_lowers_on_clipping_raises_when_dark_and_holds_in_the_window()
    {
        var clipped = ScreenGainControl.Decide(Shot(0.45, 5.0), 0.45);
        Assert.Equal(GainAction.Lower, clipped.Action);
        Assert.NotNull(clipped.Level);                                  // estimated from the ¼× square
        Assert.InRange(clipped.Gain, 0.45 * 0.1, 0.45 * 0.4);           // jumps close to the target at once

        var blind = ScreenGainControl.Decide(Shot(0.45, 40.0), 0.45);   // everything clipped: blind step
        Assert.Equal(GainAction.Lower, blind.Action);
        Assert.Equal(0.45 * ScreenGainControl.BlindLowerFactor, blind.Gain, 6);

        var dark = ScreenGainControl.Decide(Shot(0.45, 0.12), 0.45);
        Assert.Equal(GainAction.Raise, dark.Action);
        Assert.True(dark.Gain > 0.45 * 1.25);

        var ok = ScreenGainControl.Decide(Shot(0.45, 1.0), 0.45);
        Assert.Equal(GainAction.Hold, ok.Action);
        Assert.InRange(ok.Level!.Value, ScreenGainControl.Low, ScreenGainControl.High);

        var atMin = ScreenGainControl.Decide(Shot(ScreenLevels.MinGain, 200), ScreenLevels.MinGain);
        Assert.Equal(GainAction.Hold, atMin.Action);
        Assert.True(atMin.AtLimit);
    }

    [Fact]
    public void Dark_room_with_clipped_whites_recovers_through_the_gain_loop()
    {
        // The real-device failure: dark room, LEDs off, auto exposure cranks up → markers, bands and reference
        // squares clip while the screen background turns gray; some orange/purple light on the wall.
        var rig = new VirtualRig
        {
            AutoExposureTarget = 0.18, HighlightProtect = 0.9, MaxExposure = 30, WallLight = new Rgb3(0.004, 0.0022, 0.0035),
        };
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
        s.PhoneConnected();
        s.StartLive();
        Assert.Equal("ambient", s.CurrentStep!.Id);

        // Precondition: the first frame looks like the phone's: bands clipped.
        var first = FrameAnalyzer.Measure(rig.Shoot(s, led, 0), s.Layout);
        Assert.True(first.Found);
        Assert.True(first.ClippedSides >= 2, "setup: bands should clip");
        output.WriteLine($"first frame: exposure {rig.LastExposure:0.0}, ref index {first.RefIndex}");

        int n = 0, ambientUsedAt = -1;
        var hints = new List<string>();
        while (s.Mode == SessionMode.Live && n < 150)
        {
            clock.Now += 0.5;
            var step = s.CurrentStep!.Id;
            var r = s.ProcessLiveFrame(rig.Shoot(s, led, n + 1));
            hints.AddRange(r.Hints);
            output.WriteLine($"{n,3} {step,-8} gain {s.ScreenGain:0.000} exp {rig.LastExposure,5:0.0} used {r.Used,-5} {r.Note}");
            if (step == "ambient" && r.Used && ambientUsedAt < 0) ambientUsedAt = n;
            n++;
        }
        Assert.InRange(ambientUsedAt, 0, 6);      // a few frames to settle the screen gain, then measuring
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.True(n <= 90, $"{n} frames");
        Assert.Empty(s.StepNotes);                // no step had to be skipped
        Assert.DoesNotContain(hints, h => h.Contains("tap", StringComparison.OrdinalIgnoreCase) || h.Contains("xposure"));
        Assert.True(rig.GrayError(1.0) < 3, $"white {rig.GrayError(1.0):0.00}%, WB {rig.Wb}");
        Assert.True(rig.GrayError(0.25) < 4, $"dark {rig.GrayError(0.25):0.00}%");
    }

    [Fact]
    public void Harsh_mean_metering_camera_never_hangs_and_still_calibrates_white()
    {
        // Pure mean metering, no highlight protection: saturated red/blue glows clip their own channel over most of
        // the wall. Those steps may have to be skipped, but neutral white and the gray ramp still work and the loop ends.
        var rig = new VirtualRig { AutoExposureTarget = 0.18, MaxExposure = 30, WallLight = new Rgb3(0.004, 0.0022, 0.0035) };
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
        s.PhoneConnected();
        s.StartLive();
        int n = 0;
        while (s.Mode == SessionMode.Live && n < 200)
        {
            clock.Now += 0.5;
            s.ProcessLiveFrame(rig.Shoot(s, led, n++));
        }
        output.WriteLine(string.Join(Environment.NewLine, s.StepNotes));
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.All(s.StepNotes, x => Assert.True(x.StartsWith("Red") || x.StartsWith("Green") || x.StartsWith("Blue"), x));
        Assert.Equal(3, s.GrayPoints.Count);
        Assert.True(rig.GrayError(1.0) < 3, $"white {rig.GrayError(1.0):0.00}%");
    }

    [Fact]
    public void Leds_run_at_full_level_and_the_gray_ramp_is_relative_to_it()
    {
        var rig = new VirtualRig { Brightness = 0.7 };
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
        s.PhoneConnected();
        s.StartLive();
        var seen = new Dictionary<string, (float R, float G, float B, OverrideColorPath P)>();
        while (s.Mode == SessionMode.Live)
        {
            seen[s.CurrentStep!.Id] = led.Current;
            Assert.True(s.SkipStep());
        }
        Assert.Equal((0f, 0f, 0f), (seen["ambient"].R, seen["ambient"].G, seen["ambient"].B));
        Assert.Equal((1f, 1f, 1f), (seen["white"].R, seen["white"].G, seen["white"].B));
        Assert.Equal(0.5f, seen["gray50"].G);
        Assert.Equal(0.25f, seen["gray25"].G);
        Assert.Equal(0.12f, seen["gray12"].G);
        Assert.Equal(1f, seen["white2"].B);
        Assert.Equal((1f, 0f, 0f), (seen["red"].R, seen["red"].G, seen["red"].B));
        Assert.Equal((0f, 1f, 0f), (seen["green"].R, seen["green"].G, seen["green"].B));
        Assert.Equal((0f, 0f, 1f), (seen["blue"].R, seen["blue"].G, seen["blue"].B));
        Assert.All(seen.Values, v => Assert.Equal(OverrideColorPath.WhiteBalanceNoMatrix, v.P)); // engine applies WB × brightness
        Assert.All(CalibrationSession.VideoPlan.Where(p => p.Kind is LoopStepKind.White or LoopStepKind.Primary), p => Assert.Equal(1.0, p.Level));
    }

    [Fact]
    public void Glow_that_clips_dims_the_leds_a_little()
    {
        var rig = new VirtualRig { GlowStrength = 6 };
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read);
        s.PhoneConnected();
        s.StartLive();
        s.SkipStep(); // ambient
        Assert.Equal("white", s.CurrentStep!.Id);
        for (int i = 0; i < 12 && s.CurrentStep?.Id == "white"; i++)
        {
            clock.Now += 0.5;
            s.ProcessLiveFrame(rig.Shoot(s, led, i));
        }
        Assert.True(s.LedScale < 1, $"LED scale {s.LedScale}");
        Assert.True(s.LedScale >= CalibrationSession.MinLedScale);
        Assert.Equal(s.LedScale, led.Current.G, 5);
    }
}

/// <summary>The loop never hangs: per-step timeouts and the phone's skip button.</summary>
public class StepTimeoutTests
{
    private static (VirtualRig Rig, FakeClock Clock, CalibrationLedOverride Led, CalibrationSession S) Setup(VirtualRig rig)
    {
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read)
        {
            AmbientTimeoutSeconds = 2, StepTimeoutSeconds = 3, WhiteTimeoutSeconds = 5,
        };
        return (rig, clock, led, s);
    }

    [Fact]
    public void Default_timeouts()
    {
        var s = new CalibrationSession(new VirtualRig(), new CalibrationLedOverride(), new CalibrationLayout(VirtualRig.Aspect), () => 0);
        Assert.Equal(8, s.AmbientTimeoutSeconds);
        Assert.Equal(12, s.StepTimeoutSeconds);
        Assert.Equal(25, s.WhiteTimeoutSeconds);
        s.PhoneConnected();
        s.StartLive();
        Assert.Equal(8, s.Snapshot().StepTimeoutSeconds);
        s.SkipStep();
        Assert.Equal(12, s.Snapshot().StepTimeoutSeconds); // white: 12 s until the first usable frame
    }

    [Fact]
    public void Unmeasurable_steps_time_out_one_by_one_and_the_loop_ends_in_eye_mode()
    {
        // The wall around the screen is outside the picture: nothing but the screen can be measured.
        var (rig, clock, led, s) = Setup(new VirtualRig { WidthFraction = 0.97 });
        s.PhoneConnected();
        s.StartLive();
        int n = 0;
        var notes = new List<string>();
        while (s.Mode == SessionMode.Live && n < 200)
        {
            clock.Now += 0.5;
            var r = s.ProcessLiveFrame(rig.Shoot(s, led, n++));
            notes.Add(r.Note);
        }
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.True(n < 70, $"{n} frames");
        var stepNotes = s.StepNotes;
        Assert.Equal(CalibrationSession.LivePlan.Length, stepNotes.Count);
        Assert.Contains("assuming a dark room", stepNotes[0]);
        Assert.Contains("timed out", stepNotes[0]);
        Assert.Contains("could not be measured", stepNotes[1]);
        Assert.All(stepNotes.Skip(2), x => Assert.Contains("skipped", x));
        Assert.Contains(notes, x => x.Contains("Room light (LEDs off): not measured"));
        Assert.Contains("assuming a dark room", s.Snapshot().Summary);
        Assert.Null(s.MatrixFull);
        Assert.Equal(OverrideColorPath.LedCorrection, led.ColorPath); // eye mode drives the LEDs normally again
    }

    [Fact]
    public void Timeout_counts_frame_time_only()
    {
        var (rig, clock, led, s) = Setup(new VirtualRig { WidthFraction = 0.97 });
        s.PhoneConnected();
        s.StartLive();
        clock.Now += 0.5;
        s.ProcessLiveFrame(rig.Shoot(s, led, 1));
        clock.Now += 600; // the phone was away (no frames): no timeout from that alone
        s.ProcessLiveFrame(rig.Shoot(s, led, 2));
        Assert.Equal("ambient", s.CurrentStep!.Id);
        Assert.Empty(s.StepNotes);
    }

    [Fact]
    public void Aiming_at_the_start_does_not_count_but_losing_the_screen_later_does()
    {
        var (rig, clock, led, s) = Setup(new VirtualRig { WidthFraction = 0.97 });
        s.PhoneConnected();
        s.StartLive();
        var nothing = new SyntheticPhoto { Width = 640, Height = 360, ShowScreen = false }.Render();
        for (int i = 0; i < 20; i++) { clock.Now += 0.5; s.ProcessLiveFrame(nothing); } // 10 s of aiming
        Assert.Equal("ambient", s.CurrentStep!.Id);
        clock.Now += 0.5;
        Assert.True(s.ProcessLiveFrame(rig.Shoot(s, led, 1)).Measurement!.Found);
        for (int i = 0; i < 6; i++) { clock.Now += 0.5; s.ProcessLiveFrame(nothing); } // screen lost again: counts
        Assert.Equal("white", s.CurrentStep!.Id);
        Assert.Contains("assuming a dark room", Assert.Single(s.StepNotes));
    }

    [Fact]
    public void Partial_steps_use_what_they_have_and_white_keeps_the_best_result()
    {
        var rig = new VirtualRig();
        var clock = new FakeClock { Now = 100 };
        var led = new CalibrationLedOverride();
        var s = new CalibrationSession(rig, led, new CalibrationLayout(VirtualRig.Aspect), clock.Read)
        {
            AmbientTimeoutSeconds = 2, StepTimeoutSeconds = 3, WhiteTimeoutSeconds = 5,
            FramesPerStep = 50, WhiteGoodFrames = 100, MaxWhiteFrames = 1000,
        };
        double before = rig.GrayError(1.0);
        s.PhoneConnected();
        s.StartLive();
        int n = 0;
        while (s.Mode == SessionMode.Live && n < 200)
        {
            clock.Now += 0.5;
            s.ProcessLiveFrame(rig.Shoot(s, led, n++));
        }
        Assert.Equal(SessionMode.Eye, s.Mode);
        var notes = s.StepNotes;
        Assert.Contains(notes, x => x.StartsWith("White:") && x.Contains("kept the best result"));
        Assert.Contains(notes, x => x.StartsWith("Dark tones 25 %:") && x.Contains("used"));
        Assert.Equal(3, s.GrayPoints.Count);
        Assert.NotNull(s.MatrixFull);
        Assert.True(rig.GrayError(1.0) < Math.Min(3, before / 4), $"white {rig.GrayError(1.0):0.00}% (was {before:0.0}%)");
    }

    [Fact]
    public void Skip_button_ends_each_step_and_says_so()
    {
        var (_, _, _, s) = Setup(new VirtualRig());
        Assert.False(s.SkipStep()); // not live yet
        s.PhoneConnected();
        s.StartLive();
        Assert.True(s.SkipStep());
        Assert.Equal("white", s.CurrentStep!.Id);
        Assert.Contains("Room light", s.Snapshot().Note);
        while (s.Mode == SessionMode.Live) s.SkipStep();
        Assert.Equal(SessionMode.Eye, s.Mode);
        Assert.All(s.StepNotes, x => Assert.Contains("(skipped)", x));
        Assert.Equal(CalibrationSession.LivePlan.Length, s.StepNotes.Count);
        Assert.False(s.SkipStep());
    }
}
