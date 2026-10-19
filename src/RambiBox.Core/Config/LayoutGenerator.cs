namespace RambiBox.Core.Config;

/// <summary>
/// Generates LED capture zones from simple per-side counts (the way most people describe their strip).
/// Each side's zones span the full edge (corners overlap), matching AmbiBox' own generator.
/// </summary>
public static class LayoutGenerator
{
    private enum Side { Bottom, Right, Top, Left }

    public static List<LedZone> Generate(LayoutSettings p)
    {
        // Walk order around the screen as seen from the front.
        // Counter-clockwise from bottom-left: Bottom (L→R), Right (B→T), Top (R→L), Left (T→B).
        var ccw = new[] { Side.Bottom, Side.Right, Side.Top, Side.Left };
        var cw = new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }; // from bottom-left going up first

        // Starting side for each corner/direction
        Side first = (p.StartCorner, p.Clockwise) switch
        {
            (StartCorner.BottomLeft, false) => Side.Bottom,
            (StartCorner.BottomRight, false) => Side.Right,
            (StartCorner.TopRight, false) => Side.Top,
            (StartCorner.TopLeft, false) => Side.Left,
            (StartCorner.BottomLeft, true) => Side.Left,
            (StartCorner.TopLeft, true) => Side.Top,
            (StartCorner.TopRight, true) => Side.Right,
            (StartCorner.BottomRight, true) => Side.Bottom,
            _ => Side.Bottom,
        };
        var order = p.Clockwise ? cw : ccw;
        int start = Array.IndexOf(order, first);

        var zones = new List<LedZone>();
        for (int k = 0; k < 4; k++)
        {
            var side = order[(start + k) % 4];
            int n = side switch { Side.Bottom => p.Bottom, Side.Right => p.Right, Side.Top => p.Top, _ => p.Left };
            if (n <= 0) continue;
            for (int i = 0; i < n; i++)
                zones.Add(Zone(side, i, n, p.Clockwise, p.DepthHorizontal, p.DepthVertical));
        }
        return zones;
    }

    private static LedZone Zone(Side side, int i, int n, bool clockwise, float depthH, float depthV)
    {
        // Position along the side in the walking direction, 0..n-1
        float a = (float)i / n, b = (float)(i + 1) / n;
        switch (side)
        {
            case Side.Bottom:
                // CCW walks left→right along the bottom; CW walks right→left
                return clockwise
                    ? new LedZone(1 - b, 1 - depthH, 1 - a, 1)
                    : new LedZone(a, 1 - depthH, b, 1);
            case Side.Right:
                // CCW walks bottom→top; CW walks top→bottom
                return clockwise
                    ? new LedZone(1 - depthV, a, 1, b)
                    : new LedZone(1 - depthV, 1 - b, 1, 1 - a);
            case Side.Top:
                // CCW walks right→left; CW walks left→right
                return clockwise
                    ? new LedZone(a, 0, b, depthH)
                    : new LedZone(1 - b, 0, 1 - a, depthH);
            default: // Left
                // CCW walks top→bottom; CW walks bottom→top
                return clockwise
                    ? new LedZone(0, 1 - b, depthV, 1 - a)
                    : new LedZone(0, a, depthV, b);
        }
    }
}
