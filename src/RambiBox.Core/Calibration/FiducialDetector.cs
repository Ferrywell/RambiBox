namespace RambiBox.Core.Calibration;

/// <summary>A marker candidate found in the (downscaled) detection image.</summary>
internal sealed record MarkerCandidate(PointD Center, PointD[] Quad, double Size, double Area);

/// <summary>Result of screen detection. All coordinates are full-resolution photo pixels (pixel centers at +0.5).</summary>
/// <param name="ScreenToPhoto">Maps normalized screen coordinates (0..1, extending beyond for the wall) to photo pixels.</param>
/// <param name="Corners">Screen corners TL, TR, BR, BL in photo pixels.</param>
internal sealed record ScreenDetection(Homography ScreenToPhoto, PointD[] Corners, PointD[] MarkerCenters);

/// <summary>
/// Finds the four corner markers of the calibration pattern in a photo:
/// downscale → adaptive threshold → connected components → (square outline + one centered hole) test →
/// choose the 4 markers forming the largest plausible convex quad → decide which marker is which screen corner
/// (orientation-agnostic: the phone may be held in portrait or rolled by any angle) → homography (refined with all
/// marker corners). Pure managed code, deterministic.
/// </summary>
internal static class FiducialDetector
{
    /// <summary>
    /// Pixel budget of the detection image. A pixel budget (instead of a long-side limit) keeps portrait frames, where
    /// the landscape monitor covers only ~40 % of the frame width, at full resolution (e.g. 900×1600).
    /// </summary>
    public const int DetectMaxPixels = 1_500_000;

    public static ScreenDetection? Detect(RgbImage photo, CalibrationLayout layout)
    {
        var (lum, w, h, sx, sy) = DownscaleLuma(photo, DetectMaxPixels);
        int longSide = Math.Max(w, h);
        foreach (int win in new[] { longSide / 8, longSide / 4, longSide / 16 })
        {
            var mask = AdaptiveThreshold(lum, w, h, Math.Max(15, win | 1));
            var cands = FindMarkers(mask, w, h);
            // Largest plausible quads first; the first one whose reference bands are really where the pattern
            // says they should be wins (rejects e.g. reflections of the markers on a glossy desk).
            foreach (var quad in ChooseFour(cands, layout))
            {
                var chosen = Orient(quad, lum, w, h, layout);
                if (chosen is null) continue;
                var H = FitHomography(chosen, layout);
                if (H is null || !Verify(H, lum, w, h, layout)) continue;
                var full = H.ScaledOutput(sx, sy);
                var corners = new[] { full.Map(0, 0), full.Map(1, 0), full.Map(1, 1), full.Map(0, 1) };
                var centers = chosen.Select(c => new PointD(c.Center.X * sx, c.Center.Y * sy)).ToArray();
                return new ScreenDetection(full, corners, centers);
            }
        }
        return null;
    }

    /// <summary>
    /// Checks a candidate screen mapping against the pattern: the gray bands must be clearly brighter than the black
    /// screen area and the marker holes on at least 3 of 4 sides.
    /// </summary>
    internal static bool Verify(Homography H, float[] lum, int w, int h, CalibrationLayout layout)
    {
        float At(PointD screen)
        {
            var p = H.Map(screen);
            int x = (int)p.X, y = (int)p.Y;
            if (x < 0 || y < 0 || x >= w || y >= h || !double.IsFinite(p.X) || !double.IsFinite(p.Y)) return float.NaN;
            return lum[y * w + x];
        }
        var blackSamples = new List<float>();
        var probe = layout.BlackProbe();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float v = At(new PointD(probe.L + (probe.R - probe.L) * (i + 0.5) / 3, probe.T + (probe.B - probe.T) * (j + 0.5) / 3));
                if (!float.IsNaN(v)) blackSamples.Add(v);
            }
        foreach (var m in layout.Markers())
        {
            float v = At(m.Center);
            if (!float.IsNaN(v)) blackSamples.Add(v);
        }
        if (blackSamples.Count < 5) return false;
        blackSamples.Sort();
        float black = blackSamples[blackSamples.Count / 2];
        int goodSides = 0;
        // Line through the middle of each band, and a parallel line further inside the screen (must be black).
        double bandDepth = CalibrationLayout.BandInset + CalibrationLayout.BandThickness / 2;
        double innerDepth = CalibrationLayout.BandInset + CalibrationLayout.BandThickness + 0.05;
        foreach (var side in Enum.GetValues<Side>())
        {
            float band = LineMedian(side, bandDepth), inner = LineMedian(side, innerDepth);
            if (float.IsNaN(band) || float.IsNaN(inner)) continue;
            if (band > black + 20 && inner < black + 0.4f * (band - black)) goodSides++;
        }
        return goodSides >= 3;

        float LineMedian(Side side, double depth)
        {
            var vals = new List<float>(7);
            for (int i = 0; i < 7; i++)
            {
                double t = CalibrationLayout.SampleStart + (CalibrationLayout.SampleEnd - CalibrationLayout.SampleStart) * (i + 0.5) / 7;
                var pt = side switch
                {
                    Side.Top => new PointD(t, depth),
                    Side.Bottom => new PointD(t, 1 - depth),
                    Side.Left => new PointD(layout.Hx(depth), t),
                    _ => new PointD(1 - layout.Hx(depth), t),
                };
                float v = At(pt);
                if (!float.IsNaN(v)) vals.Add(v);
            }
            if (vals.Count < 4) return float.NaN;
            vals.Sort();
            return vals[vals.Count / 2];
        }
    }

    /// <summary>Homography from user-supplied screen corners (TL, TR, BR, BL in photo px). Null if not a convex quad.</summary>
    public static ScreenDetection? FromCorners(IReadOnlyList<PointD> corners)
    {
        if (corners.Count != 4 || !IsConvex(corners)) return null;
        PointD[] src = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var H = Homography.Fit(src, corners);
        if (H is null) return null;
        return new ScreenDetection(H, corners.ToArray(), []);
    }

    // ------------------------------------------------------------------ image prep

    /// <summary>
    /// Box-filter downscale to a luminance plane (sRGB-encoded 0..255) of at most <paramref name="maxPixels"/> pixels.
    /// Returns the scale factors back to full res.
    /// </summary>
    internal static (float[] Lum, int W, int H, double Sx, double Sy) DownscaleLuma(RgbImage img, int maxPixels)
    {
        int W = img.Width, Ht = img.Height;
        double s = Math.Max(1.0, Math.Sqrt((double)W * Ht / maxPixels));
        int w = Math.Max(1, (int)Math.Round(W / s)), h = Math.Max(1, (int)Math.Round(Ht / s));
        double sx = (double)W / w, sy = (double)Ht / h;
        var lum = new float[w * h];
        var d = img.Data;
        var x0s = new int[w + 1];
        for (int x = 0; x <= w; x++) x0s[x] = Math.Min(W, (int)Math.Round(x * sx));
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(Ht - 1, (int)Math.Round(y * sy)), y1 = Math.Max(y0 + 1, Math.Min(Ht, (int)Math.Round((y + 1) * sy)));
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Min(W - 1, x0s[x]), xb = Math.Max(xa + 1, x0s[x + 1]);
                double sum = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int o = (yy * W + xa) * 3;
                    for (int xx = xa; xx < xb; xx++, o += 3)
                        sum += 0.2126 * d[o] + 0.7152 * d[o + 1] + 0.0722 * d[o + 2];
                }
                lum[y * w + x] = (float)(sum / ((y1 - y0) * (xb - xa)));
            }
        }
        return (lum, w, h, sx, sy);
    }

    /// <summary>White where the pixel is clearly brighter than its neighbourhood (box window of size <paramref name="win"/>).</summary>
    internal static bool[] AdaptiveThreshold(float[] lum, int w, int h, int win)
    {
        var integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++)
            {
                row += lum[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        // Global floor: markers are the brightest things on the (black) screen; ignore dim stuff entirely.
        float max = 0;
        foreach (var v in lum) if (v > max) max = v;
        float floor = Math.Max(40f, max * 0.25f);
        int r = win / 2;
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int ya = Math.Max(0, y - r), yb = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                float v = lum[y * w + x];
                if (v < floor) continue;
                int xa = Math.Max(0, x - r), xb = Math.Min(w, x + r + 1);
                double sum = integral[yb * (w + 1) + xb] - integral[ya * (w + 1) + xb] - integral[yb * (w + 1) + xa] + integral[ya * (w + 1) + xa];
                double mean = sum / ((yb - ya) * (xb - xa));
                mask[y * w + x] = v > mean * 1.1 + 18;
            }
        }
        return mask;
    }

    // ------------------------------------------------------------------ components

    internal static List<MarkerCandidate> FindMarkers(bool[] mask, int w, int h)
    {
        var labels = new int[w * h];
        var stack = new Stack<int>();
        var result = new List<MarkerCandidate>();
        int longSide = Math.Max(w, h);
        int minArea = 24;
        long maxArea = (long)(longSide * 0.25) * (long)(longSide * 0.25);
        int next = 0;
        var pixels = new List<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            int label = ++next;
            pixels.Clear();
            labels[start] = label;
            stack.Push(start);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            bool tooBig = false;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                if (!tooBig)
                {
                    pixels.Add(p);
                    if (pixels.Count > maxArea) tooBig = true;
                }
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if (nx < 0 || nx >= w || (dx == 0 && dy == 0)) continue;
                        int q = ny * w + nx;
                        if (mask[q] && labels[q] == 0)
                        {
                            labels[q] = label;
                            stack.Push(q);
                        }
                    }
                }
            }
            if (tooBig || pixels.Count < minArea) continue;
            // Touching the image border: marker may be cut off.
            if (minX == 0 || minY == 0 || maxX == w - 1 || maxY == h - 1) continue;
            var cand = Analyze(pixels, labels, label, w, minX, minY, maxX, maxY);
            if (cand is not null) result.Add(cand);
        }
        return result;
    }

    private static MarkerCandidate? Analyze(List<int> pixels, int[] labels, int label, int w,
        int minX, int minY, int maxX, int maxY)
    {
        int bw = maxX - minX + 3, bh = maxY - minY + 3; // 1 px margin
        int ox = minX - 1, oy = minY - 1;
        // 0 = unknown, 1 = component, 2 = outside
        var local = new byte[bw * bh];
        foreach (int p in pixels) local[(p / w - oy) * bw + (p % w - ox)] = 1;
        var st = new Stack<int>();
        st.Push(0);
        local[0] = 2;
        while (st.Count > 0)
        {
            int p = st.Pop();
            int x = p % bw, y = p / bw;
            if (x > 0 && local[p - 1] == 0) { local[p - 1] = 2; st.Push(p - 1); }
            if (x < bw - 1 && local[p + 1] == 0) { local[p + 1] = 2; st.Push(p + 1); }
            if (y > 0 && local[p - bw] == 0) { local[p - bw] = 2; st.Push(p - bw); }
            if (y < bh - 1 && local[p + bw] == 0) { local[p + bw] = 2; st.Push(p + bw); }
        }
        double holes = 0, hx = 0, hy = 0, fx = 0, fy = 0;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                byte v = local[y * bw + x];
                if (v == 2) continue;
                fx += x; fy += y;
                if (v == 0) { holes++; hx += x; hy += y; }
            }
        double area = pixels.Count, filled = area + holes;
        double holeFrac = holes / filled;
        if (holeFrac < 0.07 || holeFrac > 0.5) return null;
        fx /= filled; fy /= filled; hx /= holes; hy /= holes;
        double size = Math.Sqrt(filled);
        if (Math.Sqrt((fx - hx) * (fx - hx) + (fy - hy) * (fy - hy)) > 0.2 * size) return null;

        // Outer outline: per row the left edge of the leftmost and the right edge of the rightmost filled pixel.
        var pts = new List<PointD>(bh * 4);
        for (int y = 1; y < bh - 1; y++)
        {
            int l = -1, r = -1;
            for (int x = 0; x < bw; x++) if (local[y * bw + x] != 2) { l = x; break; }
            if (l < 0) continue;
            for (int x = bw - 1; x >= 0; x--) if (local[y * bw + x] != 2) { r = x; break; }
            double gy = y + oy;
            pts.Add(new(l + ox, gy)); pts.Add(new(l + ox, gy + 1));
            pts.Add(new(r + ox + 1, gy)); pts.Add(new(r + ox + 1, gy + 1));
        }
        var hull = ConvexHull(pts);
        if (hull.Count < 4) return null;
        var quad = FitQuad(hull);
        if (quad is null) return null;
        double qa = Math.Abs(PolygonArea(quad));
        double ratio = qa / filled;
        if (ratio < 0.75 || ratio > 1.3) return null;
        double minSide = double.MaxValue, maxSide = 0;
        for (int i = 0; i < 4; i++)
        {
            double len = (quad[(i + 1) % 4] - quad[i]).Length;
            minSide = Math.Min(minSide, len);
            maxSide = Math.Max(maxSide, len);
        }
        if (minSide < 3 || minSide / maxSide < 0.35) return null;
        var center = Intersect(quad[0], quad[2], quad[1], quad[3]);
        if (center is null) return null;
        return new MarkerCandidate(center.Value, quad, size, filled);
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Plausible marker quadruples (clockwise, see <see cref="Order"/>), largest quad area first (at most 64).</summary>
    internal static List<MarkerCandidate[]> ChooseFour(List<MarkerCandidate> cands, CalibrationLayout layout)
    {
        var found = new List<(double Area, MarkerCandidate[] Quad)>();
        if (cands.Count < 4) return [];
        var top = cands.OrderByDescending(c => c.Area).Take(20).ToArray();
        int n = top.Length;
        // Expected (marker area / area of the quad through the marker centers) on the monitor itself.
        var ms = layout.Markers();
        var mc = ms.Select(m => m.Center).ToArray();
        double expectedQuad = Math.Abs(PolygonArea(mc)) * layout.Aspect; // in height² units
        double expectedMarker = CalibrationLayout.MarkerSize * CalibrationLayout.MarkerSize;
        double expectedRatio = expectedMarker / expectedQuad;

        var tmp = new MarkerCandidate[4];
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                for (int c = b + 1; c < n; c++)
                    for (int d = c + 1; d < n; d++)
                    {
                        tmp[0] = top[a]; tmp[1] = top[b]; tmp[2] = top[c]; tmp[3] = top[d];
                        double minS = tmp.Min(t => t.Size), maxS = tmp.Max(t => t.Size);
                        if (maxS / minS > 3) continue;
                        var ordered = Order(tmp);
                        var pts = ordered.Select(o => o.Center).ToArray();
                        if (!IsConvex(pts)) continue;
                        double area = Math.Abs(PolygonArea(pts));
                        double meanMarker = ordered.Average(o => o.Area);
                        double ratio = meanMarker / area;
                        if (ratio < expectedRatio / 6 || ratio > expectedRatio * 6) continue;
                        // Opposite sides must be comparable (perspective, not a sliver).
                        double s0 = (pts[1] - pts[0]).Length, s1 = (pts[2] - pts[1]).Length,
                               s2 = (pts[3] - pts[2]).Length, s3 = (pts[0] - pts[3]).Length;
                        if (Math.Min(s0, s2) / Math.Max(s0, s2) < 0.4 || Math.Min(s1, s3) / Math.Max(s1, s3) < 0.4) continue;
                        found.Add((area, ordered));
                    }
        return found.OrderByDescending(f => f.Area).Take(64).Select(f => f.Quad).ToList();
    }

    /// <summary>
    /// Orders 4 candidates clockwise (image y pointing down), starting with the one nearest the image's top-left.
    /// Which of them is the screen's top-left is decided later by <see cref="Orient"/>.
    /// </summary>
    internal static MarkerCandidate[] Order(IReadOnlyList<MarkerCandidate> c)
    {
        double cx = c.Average(p => p.Center.X), cy = c.Average(p => p.Center.Y);
        var byAngle = c.OrderBy(p => Math.Atan2(p.Center.Y - cy, p.Center.X - cx)).ToArray();
        int start = 0;
        for (int i = 1; i < 4; i++)
            if (byAngle[i].Center.X + byAngle[i].Center.Y < byAngle[start].Center.X + byAngle[start].Center.Y) start = i;
        return [byAngle[start], byAngle[(start + 1) % 4], byAngle[(start + 2) % 4], byAngle[(start + 3) % 4]];
    }

    /// <summary>
    /// Decides which marker of a clockwise quad is the screen's top-left (then TR, BR, BL) from the screen's own
    /// geometry and content, not from the image axes, so any phone orientation works:
    /// <list type="number">
    /// <item>90° ambiguity: the markers are squares on the monitor. A mapping with the wrong 90° assignment swaps the
    /// screen's width and height and squeezes every marker by ≈ aspect², so the hypothesis whose predicted marker
    /// outlines match the detected outlines best wins (robust even with strong perspective).</item>
    /// <item>180° ambiguity: markers and bands are point-symmetric, but the step-code row and the reference squares sit
    /// below the centre and their mirror images fall into the black probe / empty area above it
    /// (<see cref="CalibrationLayout.OrientationCuePoints"/>).</item>
    /// <item>Without that cue (QR / v1 patterns): the rotation whose top edge points most to the image's right.</item>
    /// </list>
    /// </summary>
    internal static MarkerCandidate[]? Orient(MarkerCandidate[] clockwise, float[] lum, int w, int h, CalibrationLayout layout)
    {
        var rects = layout.Markers();
        var src = rects.Select(r => r.Center).ToArray();
        var err = new double[2];
        var hs = new Homography?[2];
        for (int k = 0; k < 2; k++)
        {
            var rot = Rotate(clockwise, k);
            hs[k] = Homography.Fit(src, rot.Select(c => c.Center).ToArray());
            err[k] = hs[k] is { } hk ? ShapeError(hk, rot, rects) : double.PositiveInfinity;
        }
        if (double.IsPositiveInfinity(err[0]) && double.IsPositiveInfinity(err[1])) return null;
        int k0 = err[0] <= err[1] ? 0 : 1;
        var a = Rotate(clockwise, k0);
        var b = Rotate(clockwise, k0 + 2);
        double cue = OrientationCue(hs[k0]!, lum, w, h, layout);
        if (cue >= 8) return a;
        if (cue <= -8) return b;
        double ax = a[1].Center.X - a[0].Center.X + a[2].Center.X - a[3].Center.X;
        return ax >= 0 ? a : b;
    }

    private static MarkerCandidate[] Rotate(MarkerCandidate[] c, int k) => [c[k % 4], c[(k + 1) % 4], c[(k + 2) % 4], c[(k + 3) % 4]];

    /// <summary>Mean distance (in marker sizes) between the predicted and the detected outer corners of the markers.</summary>
    internal static double ShapeError(Homography H, MarkerCandidate[] chosen, RectD[] rects)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            double size = Math.Max(1, chosen[i].Size);
            foreach (var corner in rects[i].Corners())
            {
                var p = H.Map(corner);
                double best = double.PositiveInfinity;
                foreach (var q in chosen[i].Quad) best = Math.Min(best, (q - p).Length);
                if (!double.IsFinite(best)) return double.PositiveInfinity;
                sum += best / size;
                n++;
            }
        }
        return sum / n;
    }

    /// <summary>
    /// Luma of the always-bright cue points minus luma of their 180°-rotated positions (8-bit units; positive = the
    /// mapping is the right way up, negative = upside down, ≈ 0 = no cue in this pattern).
    /// </summary>
    internal static double OrientationCue(Homography H, float[] lum, int w, int h, CalibrationLayout layout)
    {
        var bright = new List<float>(20);
        var mirror = new List<float>(20);
        double du = layout.Hx(CalibrationLayout.CodeCellSize * 0.25), dv = CalibrationLayout.CodeCellSize * 0.25;
        foreach (var c in layout.OrientationCuePoints())
        {
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    double u = c.X + i * du, v = c.Y + j * dv;
                    float a = At(u, v), m = At(1 - u, 1 - v);
                    if (float.IsNaN(a) || float.IsNaN(m)) continue;
                    bright.Add(a);
                    mirror.Add(m);
                }
        }
        if (bright.Count < 9) return 0;
        bright.Sort();
        mirror.Sort();
        return bright[bright.Count / 2] - mirror[mirror.Count / 2];

        float At(double u, double v)
        {
            var p = H.Map(u, v);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return float.NaN;
            int x = (int)p.X, y = (int)p.Y;
            return x < 0 || y < 0 || x >= w || y >= h ? float.NaN : lum[y * w + x];
        }
    }

    private static Homography? FitHomography(MarkerCandidate[] chosen, CalibrationLayout layout)
    {
        var rects = layout.Markers();
        var src = rects.Select(r => r.Center).ToList();
        var dst = chosen.Select(c => c.Center).ToList();
        var h0 = Homography.Fit(src, dst);
        if (h0 is null) return null;

        // Refine with the outer corners of all markers (16 extra correspondences).
        var s2 = new List<PointD>(src);
        var d2 = new List<PointD>(dst);
        for (int i = 0; i < 4; i++)
        {
            double tol = chosen[i].Size * 0.35;
            foreach (var corner in rects[i].Corners())
            {
                var pred = h0.Map(corner);
                PointD? bestP = null;
                double bestD = tol;
                foreach (var q in chosen[i].Quad)
                {
                    double dd = (q - pred).Length;
                    if (dd < bestD) { bestD = dd; bestP = q; }
                }
                if (bestP is { } bp) { s2.Add(corner); d2.Add(bp); }
            }
        }
        if (s2.Count < 12) return h0;
        var h1 = Homography.Fit(s2, d2);
        if (h1 is null) return h0;
        // Sanity: the refined fit must stay close to the center-based one.
        double maxDev = 0;
        foreach (var p in new PointD[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) })
            maxDev = Math.Max(maxDev, (h1.Map(p) - h0.Map(p)).Length);
        double scale = (h0.Map(1, 0) - h0.Map(0, 0)).Length;
        return maxDev < scale * 0.03 ? h1 : h0;
    }

    // ------------------------------------------------------------------ geometry helpers

    internal static List<PointD> ConvexHull(List<PointD> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        var hull = new List<PointD>(p.Count * 2);
        foreach (var q in p)
        {
            while (hull.Count >= 2 && PointD.Cross(hull[^1] - hull[^2], q - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }
        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            var q = p[i];
            while (hull.Count >= lower && PointD.Cross(hull[^1] - hull[^2], q - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>Approximates a convex hull by a quadrilateral (two diagonal corners + farthest point on each side).</summary>
    internal static PointD[]? FitQuad(List<PointD> hull)
    {
        double cx = hull.Average(p => p.X), cy = hull.Average(p => p.Y);
        var c = new PointD(cx, cy);
        var p1 = hull.MaxBy(p => (p - c).Length);
        var p3 = hull.MaxBy(p => (p - p1).Length);
        var dir = p3 - p1;
        if (dir.Length < 1e-9) return null;
        PointD p2 = p1, p4 = p1;
        double maxPos = 0, maxNeg = 0;
        foreach (var p in hull)
        {
            double s = PointD.Cross(dir, p - p1);
            if (s > maxPos) { maxPos = s; p2 = p; }
            if (s < maxNeg) { maxNeg = s; p4 = p; }
        }
        if (maxPos <= 0 || maxNeg >= 0) return null;
        PointD[] q = [p1, p2, p3, p4];
        return q;
    }

    internal static double PolygonArea(IReadOnlyList<PointD> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    internal static bool IsConvex(IReadOnlyList<PointD> p)
    {
        int sign = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            var c = p[(i + 2) % p.Count];
            double cr = PointD.Cross(b - a, c - b);
            if (!double.IsFinite(cr) || Math.Abs(cr) < 1e-9) return false;
            int s = Math.Sign(cr);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    internal static PointD? Intersect(PointD a1, PointD a2, PointD b1, PointD b2)
    {
        var r = a2 - a1;
        var s = b2 - b1;
        double den = PointD.Cross(r, s);
        if (Math.Abs(den) < 1e-12) return null;
        double t = PointD.Cross(b1 - a1, s) / den;
        return a1 + r * t;
    }
}
