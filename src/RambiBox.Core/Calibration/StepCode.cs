namespace RambiBox.Core.Calibration;

/// <summary>
/// Visual step code: a row of <see cref="Cells"/> squares on the screen tells which pattern a camera frame shows, so
/// every frame is matched to the pattern actually displayed (no clock synchronisation between phone and PC).
/// Cells: [always white] [6 data bits, MSB first] [odd parity]. Odd parity guarantees at least one white data/parity
/// cell, so an all-black row (no code) never decodes.
/// </summary>
internal static class StepCode
{
    public const int Bits = 6;
    public const int Cells = Bits + 2;
    public const int MaxValue = (1 << Bits) - 1;

    public static bool[] Encode(int value)
    {
        if (value < 0 || value > MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        var cells = new bool[Cells];
        cells[0] = true;
        int ones = 0;
        for (int i = 0; i < Bits; i++)
        {
            bool bit = ((value >> (Bits - 1 - i)) & 1) != 0;
            cells[1 + i] = bit;
            if (bit) ones++;
        }
        cells[Cells - 1] = ones % 2 == 0; // odd parity over data + parity
        return cells;
    }

    /// <summary>
    /// Decodes cell brightnesses (any monotonic unit, e.g. 8-bit luma) given the screen's black level.
    /// Returns null when the row is unreadable (too little contrast, ambiguous cells, parity error).
    /// </summary>
    public static int? Decode(ReadOnlySpan<double> cells, double black)
    {
        if (cells.Length != Cells) return null;
        double white = cells[0];
        double range = white - black;
        if (!(range >= 30)) return null;
        double thr = black + range / 2, margin = range * 0.12;
        int value = 0, ones = 0;
        for (int i = 1; i < Cells; i++)
        {
            double v = cells[i];
            if (!double.IsFinite(v) || Math.Abs(v - thr) < margin) return null;
            bool bit = v > thr;
            if (bit) ones++;
            if (i <= Bits) value = (value << 1) | (bit ? 1 : 0);
        }
        return ones % 2 == 1 ? value : null;
    }

    /// <summary>Reads the code from a photo given the screen → photo homography.</summary>
    public static int? Read(RgbImage img, Homography screenToPhoto, CalibrationLayout layout)
    {
        Span<double> cells = stackalloc double[Cells];
        for (int i = 0; i < Cells; i++) cells[i] = MedianLuma(img, screenToPhoto, layout.CodeCell(i).Inset(0.5), 5);
        double black = MedianLuma(img, screenToPhoto, layout.BlackProbe(), 5);
        return Decode(cells, black);
    }

    /// <summary>Median 8-bit luma over an n×n grid of points inside <paramref name="r"/> (screen coordinates).</summary>
    internal static double MedianLuma(RgbImage img, Homography H, RectD r, int n)
    {
        var vals = new List<double>(n * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var p = H.Map(r.L + (r.R - r.L) * (i + 0.5) / n, r.T + (r.B - r.T) * (j + 0.5) / n);
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                int x = (int)p.X, y = (int)p.Y;
                if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                int o = (y * img.Width + x) * 3;
                vals.Add(0.2126 * img.Data[o] + 0.7152 * img.Data[o + 1] + 0.0722 * img.Data[o + 2]);
            }
        if (vals.Count < (n * n + 1) / 2) return double.NaN;
        vals.Sort();
        return vals[vals.Count / 2];
    }
}
