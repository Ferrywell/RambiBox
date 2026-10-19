namespace RambiBox.Core.Calibration;

internal readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
}

/// <summary>3x3 projective transform (row-major, m[8] normally 1).</summary>
internal sealed class Homography
{
    private readonly double[] _m;

    public Homography(double[] m)
    {
        if (m.Length != 9) throw new ArgumentException("need 9 elements");
        _m = (double[])m.Clone();
    }

    public double this[int i] => _m[i];

    public PointD Map(PointD p) => Map(p.X, p.Y);

    public PointD Map(double x, double y)
    {
        var m = _m;
        double w = m[6] * x + m[7] * y + m[8];
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return new PointD((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }

    /// <summary>Denominator sign of the projective map (points with w ≤ 0 are "behind the camera").</summary>
    public double W(double x, double y) => _m[6] * x + _m[7] * y + _m[8];

    public Homography Inverse()
    {
        var m = _m;
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-18) throw new InvalidOperationException("Singular homography");
        var r = new[]
        {
            A, -(b * i - c * h), b * f - c * e,
            B, a * i - c * g, -(a * f - c * d),
            C, -(a * h - b * g), a * e - b * d,
        };
        for (int k = 0; k < 9; k++) r[k] /= det;
        return new Homography(Normalize(r));
    }

    /// <summary>Returns a homography that additionally scales the output coordinates by (<paramref name="sx"/>, <paramref name="sy"/>).</summary>
    public Homography ScaledOutput(double sx, double sy)
    {
        var r = (double[])_m.Clone();
        for (int k = 0; k < 3; k++) r[k] *= sx;
        for (int k = 3; k < 6; k++) r[k] *= sy;
        return new Homography(r);
    }

    private static double[] Normalize(double[] r)
    {
        if (Math.Abs(r[8]) > 1e-12)
        {
            double s = 1 / r[8];
            for (int k = 0; k < 9; k++) r[k] *= s;
        }
        return r;
    }

    /// <summary>
    /// Least-squares (DLT with Hartley normalization, h33 = 1) homography mapping src[i] → dst[i]. Needs ≥ 4 points.
    /// Returns null when degenerate.
    /// </summary>
    public static Homography? Fit(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        int n = src.Count;
        if (n < 4 || dst.Count != n) return null;
        var (ts, ns) = NormalizationOf(src);
        var (td, nd) = NormalizationOf(dst);

        // Normal equations (8x8) for: [x y 1 0 0 0 -x*X -y*X] h = X ; [0 0 0 x y 1 -x*Y -y*Y] h = Y
        var ata = new double[8, 8];
        var atb = new double[8];
        Span<double> row = stackalloc double[8];
        for (int k = 0; k < n; k++)
        {
            var s = ns[k];
            var d = nd[k];
            for (int pass = 0; pass < 2; pass++)
            {
                row.Clear();
                double rhs;
                if (pass == 0)
                {
                    row[0] = s.X; row[1] = s.Y; row[2] = 1;
                    row[6] = -s.X * d.X; row[7] = -s.Y * d.X;
                    rhs = d.X;
                }
                else
                {
                    row[3] = s.X; row[4] = s.Y; row[5] = 1;
                    row[6] = -s.X * d.Y; row[7] = -s.Y * d.Y;
                    rhs = d.Y;
                }
                for (int i = 0; i < 8; i++)
                {
                    if (row[i] == 0) continue;
                    atb[i] += row[i] * rhs;
                    for (int j = 0; j < 8; j++) ata[i, j] += row[i] * row[j];
                }
            }
        }
        var h = Solve(ata, atb);
        if (h is null) return null;
        var hn = new Homography([h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1]);
        // Denormalize: H = Td^-1 * Hn * Ts
        var r = Mul(Mul(Invert3(td), hn._m), ts);
        foreach (var v in r) if (!double.IsFinite(v)) return null;
        try { return new Homography(Normalize(r)); }
        catch { return null; }
    }

    private static (double[] T, PointD[] Pts) NormalizationOf(IReadOnlyList<PointD> p)
    {
        double cx = 0, cy = 0;
        foreach (var q in p) { cx += q.X; cy += q.Y; }
        cx /= p.Count; cy /= p.Count;
        double md = 0;
        foreach (var q in p) md += Math.Sqrt((q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy));
        md /= p.Count;
        double s = md < 1e-12 ? 1 : Math.Sqrt(2) / md;
        var t = new[] { s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1 };
        var pts = new PointD[p.Count];
        for (int i = 0; i < p.Count; i++) pts[i] = new PointD((p[i].X - cx) * s, (p[i].Y - cy) * s);
        return (t, pts);
    }

    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    private static double[] Invert3(double[] t) => new Homography(t).Inverse()._m;

    /// <summary>Gaussian elimination with partial pivoting. Returns null when singular.</summary>
    internal static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-14) return null;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }
}
