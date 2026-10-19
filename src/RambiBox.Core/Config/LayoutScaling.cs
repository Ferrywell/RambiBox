namespace RambiBox.Core.Config;

/// <summary>LED counts per side of the screen (as used by <see cref="LayoutGenerator"/>).</summary>
public readonly record struct SideCounts(int Bottom, int Right, int Top, int Left)
{
    public int Total => Bottom + Right + Top + Left;
}

/// <summary>Resizes a generated layout to a new LED count while keeping its shape.</summary>
public static class LayoutScaling
{
    /// <summary>Most LEDs the layout editor allows on one side.</summary>
    public const int MaxPerSide = 300;

    /// <summary>
    /// Distributes <paramref name="total"/> LEDs over the four sides in proportion to <paramref name="current"/>
    /// (largest remainder, so the result adds up exactly). Sides without LEDs stay empty; when every side is empty a
    /// 16:9 screen is assumed. A side never exceeds <see cref="MaxPerSide"/> (the rest goes to the other sides), so
    /// the result can be smaller than <paramref name="total"/> only above 4 × <see cref="MaxPerSide"/>.
    /// </summary>
    public static SideCounts Distribute(SideCounts current, int total)
    {
        total = Math.Max(0, total);
        int[] w = [current.Bottom, current.Right, current.Top, current.Left];
        for (int i = 0; i < 4; i++) w[i] = Math.Max(0, w[i]);
        if (w.Sum() == 0) w = [16, 9, 16, 9];

        var result = new int[4];
        var open = Enumerable.Range(0, 4).Where(i => w[i] > 0).ToList();
        int remaining = total;
        while (remaining > 0 && open.Count > 0)
        {
            double weight = open.Sum(i => (double)w[i]);
            var exact = open.ToDictionary(i => i, i => remaining * w[i] / weight);
            var share = open.ToDictionary(i => i, i => (int)Math.Floor(exact[i]));
            int left = remaining - share.Values.Sum();
            // leftovers to the largest fractions; ties go bottom, right, top, left (stable order)
            foreach (var i in open.OrderByDescending(i => exact[i] - share[i]).ThenBy(i => i).Take(left)) share[i]++;

            bool capped = false;
            foreach (var i in open.ToList())
            {
                int room = MaxPerSide - result[i];
                if (share[i] >= room)
                {
                    result[i] += room;
                    remaining -= room;
                    open.Remove(i);
                    capped = true;
                }
            }
            if (capped) continue; // redistribute what the capped sides couldn't take
            foreach (var i in open) result[i] += share[i];
            remaining = 0;
        }
        return new SideCounts(result[0], result[1], result[2], result[3]);
    }

    /// <summary>
    /// Sets the per-side counts of <paramref name="layout"/> so the strip has <paramref name="stripLeds"/> LEDs in total
    /// (including the skipped <see cref="LayoutSettings.Offset"/> LEDs) and regenerates its zones.
    /// </summary>
    public static void ResizeTo(LayoutSettings layout, int stripLeds)
    {
        int zones = Math.Max(1, stripLeds - Math.Max(0, layout.Offset));
        var c = Distribute(new SideCounts(layout.Bottom, layout.Right, layout.Top, layout.Left), zones);
        layout.Bottom = c.Bottom;
        layout.Right = c.Right;
        layout.Top = c.Top;
        layout.Left = c.Left;
        layout.Zones = LayoutGenerator.Generate(layout);
    }
}
