using RambiBox.Core.Calibration;

namespace RambiBox.Tests.Calibration;

/// <summary>
/// Renders a fake phone photo of the calibration pattern: the screen (perspective-warped) on a dark wall with an LED
/// glow halo around it, then a camera model (white balance, exposure, clipping, tone curve, sRGB, noise, JPEG).
/// <para>
/// LED model: the engine drives channel c with d_c = L^γc · W_c; the strip emits e_c(d) = d^exp_c (per-channel
/// nonlinearity); the wall + LED spectrum + camera multiply that by an unknown tint t_c. The screen emits neutral
/// white, so "calibrated" means the glow is neutral too.
/// </para>
/// </summary>
internal sealed class SyntheticPhoto
{
    public const int DefaultWidth = 1280, DefaultHeight = 960;

    public int Width { get; init; } = DefaultWidth;
    public int Height { get; init; } = DefaultHeight;
    public double Aspect { get; init; } = 16.0 / 9.0;
    /// <summary>Screen corners TL, TR, BR, BL in photo pixels.</summary>
    public PointD[] Corners { get; init; } = Pose(DefaultWidth, DefaultHeight, 16.0 / 9.0);

    public Rgb3 Wb { get; init; } = new(0.64, 1.0, 0.51);
    public Rgb3 Gamma { get; init; } = new(1, 1, 1);
    public Rgb3 StripExponent { get; init; } = new(1, 1, 1);
    public Rgb3 Tint { get; init; } = new(1.05, 0.9, 1.45);
    public double LedLevel { get; init; } = CalibrationMath.WhiteLevel;
    public bool DarkStage { get; init; }

    public Rgb3 CameraWb { get; init; } = new(0.8, 1.0, 1.3);
    public double Exposure { get; init; } = 1.0;
    /// <summary>Extra camera tone curve exponent applied to linear values (1 = ideal sRGB camera).</summary>
    public double ToneExponent { get; init; } = 1.0;
    public double GlowStrength { get; init; } = 0.5;
    public double NoiseSigma { get; init; } = 1.5; // in 8-bit units
    public int Seed { get; init; } = 1;
    public bool ShowScreen { get; init; } = true;
    public int JpegQualityPercent { get; init; } = 90;
    /// <summary>Marker-like distractors (white square with black center) at these photo positions.</summary>
    public PointD[] Distractors { get; init; } = [];
    public double DistractorSize { get; init; } = 40;

    /// <summary>A plausible handheld pose: 3D rotation (yaw/pitch/roll in degrees), screen width as fraction of the photo.</summary>
    public static PointD[] Pose(int w, int h, double aspect, double yaw = 0, double pitch = 0, double roll = 0,
        double widthFraction = 0.6, double offX = 0, double offY = 0)
    {
        double f = Math.Max(w, h) * 1.1;
        double sw = 1.0, sh = 1.0 / aspect;
        double dist = f * sw / (widthFraction * w);
        double cy = Math.Cos(yaw * Math.PI / 180), sy = Math.Sin(yaw * Math.PI / 180);
        double cp = Math.Cos(pitch * Math.PI / 180), sp = Math.Sin(pitch * Math.PI / 180);
        double cr = Math.Cos(roll * Math.PI / 180), sr = Math.Sin(roll * Math.PI / 180);
        var res = new PointD[4];
        double[,] pts = { { -sw / 2, -sh / 2 }, { sw / 2, -sh / 2 }, { sw / 2, sh / 2 }, { -sw / 2, sh / 2 } };
        for (int i = 0; i < 4; i++)
        {
            double x = pts[i, 0], y = pts[i, 1], z = 0;
            // yaw (around y)
            double x1 = cy * x + sy * z, z1 = -sy * x + cy * z;
            // pitch (around x)
            double y2 = cp * y - sp * z1, z2 = sp * y + cp * z1;
            // roll (around z)
            double x3 = cr * x1 - sr * y2, y3 = sr * x1 + cr * y2;
            double Z = dist + z2;
            res[i] = new PointD(w / 2.0 + offX * w + f * x3 / Z, h / 2.0 + offY * h + f * y3 / Z);
        }
        return res;
    }

    /// <summary>Linear glow color the LEDs produce for given settings (before camera).</summary>
    public static Rgb3 GlowColor(Rgb3 wb, Rgb3 gamma, Rgb3 exp, Rgb3 tint, double level)
    {
        double C(double w, double g, double e, double t) => Math.Pow(Math.Pow(level, g) * w, e) * t;
        return new Rgb3(C(wb.R, gamma.R, exp.R, tint.R), C(wb.G, gamma.G, exp.G, tint.G), C(wb.B, gamma.B, exp.B, tint.B));
    }

    /// <summary>True chroma error (percent) of the glow vs neutral screen white at the given LED level.</summary>
    public static double TrueError(Rgb3 wb, Rgb3 gamma, Rgb3 exp, Rgb3 tint, double level)
    {
        var g = GlowColor(wb, gamma, exp, tint, level);
        return CalibrationMath.ErrorPercent(g.R / g.G, g.B / g.G);
    }

    public RgbImage Render()
    {
        var layout = new CalibrationLayout(Aspect);
        // Screen texture (linear light)
        int tw = 960, th = (int)Math.Round(tw / Aspect);
        var tex = new float[tw * th];
        Array.Fill(tex, 0.0015f); // screen black
        foreach (var r in layout.BuildPattern(false, DarkStage).Rects)
        {
            float lin = (float)CalibrationAnalyzer.ToLinear((byte)(r.Rgb & 0xFF));
            if ((r.Rgb & 0xFF) == 0) lin = 0.0015f;
            int x0 = (int)Math.Round(r.Left * tw), x1 = (int)Math.Round(r.Right * tw);
            int y0 = (int)Math.Round(r.Top * th), y1 = (int)Math.Round(r.Bottom * th);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) tex[y * tw + x] = lin;
        }

        var H = Homography.Fit([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], Corners)!;
        var inv = H.Inverse();
        var glow = GlowColor(Wb, Gamma, StripExponent, Tint, LedLevel);
        double bezel = 0.012, glowFalloff = 0.12;

        var img = new RgbImage(Width, Height);
        var rng = new Random(Seed);
        // Camera response (exposure → clip → tone curve → sRGB) as a LUT over linear 0..1, and a Gaussian noise table.
        const int lutN = 8192;
        var lut = new double[lutN + 1];
        for (int i = 0; i <= lutN; i++)
        {
            double v = Math.Pow((double)i / lutN, ToneExponent);
            lut[i] = 255 * (v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
        }
        var noise = new double[65536];
        for (int i = 0; i < noise.Length; i++) noise[i] = Gaussian(rng) * NoiseSigma;
        byte Cam(double linear)
        {
            double v = Math.Clamp(linear * Exposure, 0, 1) * lutN;
            int i0 = (int)v;
            double s = i0 >= lutN ? lut[lutN] : lut[i0] + (lut[i0 + 1] - lut[i0]) * (v - i0);
            return (byte)Math.Clamp(Math.Round(s + noise[rng.Next(65536)]), 0, 255);
        }
        var d = img.Data;
        double bx = bezel / Aspect, margin = 0.03;
        void Sample(double u, double v, ref double r, ref double g, ref double b)
        {
            if (ShowScreen && u >= 0 && u < 1 && v >= 0 && v < 1)
            {
                double s = tex[Math.Min(th - 1, (int)(v * th)) * tw + Math.Min(tw - 1, (int)(u * tw))];
                r += s; g += s; b += s;
                return;
            }
            if (ShowScreen && u >= -bx && u <= 1 + bx && v >= -bezel && v <= 1 + bezel)
            {
                r += 0.003; g += 0.003; b += 0.003;
                return;
            }
            double dx = Math.Max(0, Math.Max(-u, u - 1)) * Aspect, dy = Math.Max(0, Math.Max(-v, v - 1));
            double k = GlowStrength * Math.Exp(-Math.Sqrt(dx * dx + dy * dy) / glowFalloff);
            r += 0.002 + glow.R * k; g += 0.002 + glow.G * k; b += 0.002 + glow.B * k;
        }
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double r = 0, g = 0, b = 0;
                var c = inv.Map(x + 0.5, y + 0.5);
                if (Distractor(x + 0.5, y + 0.5) is { } dv)
                {
                    r = g = b = dv;
                }
                else if (c.X < -margin || c.X > 1 + margin || c.Y < -margin || c.Y > 1 + margin)
                {
                    Sample(c.X, c.Y, ref r, ref g, ref b); // smooth wall: one sample is enough
                }
                else
                {
                    // screen + its edges: 2x2 supersampling (anti-aliased marker edges like a real lens)
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            var uv = inv.Map(x + 0.25 + 0.5 * sx, y + 0.25 + 0.5 * sy);
                            Sample(uv.X, uv.Y, ref r, ref g, ref b);
                        }
                    r /= 4; g /= 4; b /= 4;
                }
                int o = (y * Width + x) * 3;
                d[o] = Cam(r * CameraWb.R);
                d[o + 1] = Cam(g * CameraWb.G);
                d[o + 2] = Cam(b * CameraWb.B);
            }
        }
        return img;
    }

    private double? Distractor(double x, double y)
    {
        foreach (var p in Distractors)
        {
            double dx = Math.Abs(x - p.X), dy = Math.Abs(y - p.Y), h = DistractorSize / 2;
            if (dx <= h && dy <= h) return dx <= h / 2 && dy <= h / 2 ? 0.002 : 0.8;
        }
        return null;
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public byte[] RenderJpeg() => ImageCodec.EncodeJpeg(Render(), JpegQualityPercent / 100f);

    /// <summary>Render → JPEG → decode, i.e. what the server would see.</summary>
    public RgbImage RenderThroughJpeg() => ImageCodec.Decode(RenderJpeg());
}
