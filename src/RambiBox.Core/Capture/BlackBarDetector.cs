namespace RambiBox.Core.Capture;

/// <summary>
/// Letterbox / pillarbox detection on the small linear-light image.
/// <list type="bullet">
/// <item>A line (row/column) is "black" when almost none of its pixels have a max channel above
/// <see cref="DarkThresholdLinear"/> (sRGB ≈ 0.06).</item>
/// <item>Bars are measured from each edge, and only accepted when symmetric-ish (letterboxing is centered);
/// the smaller side is used for both so bars are never overestimated.</item>
/// <item>A new / larger bar must be stable for <see cref="AcquireSeconds"/> before it is applied; when picture
/// content shows up inside an active bar it is released after <see cref="ReleaseSeconds"/>.</item>
/// <item>Almost entirely dark frames (fade to black) never change the state.</item>
/// <item>Subtitles rendered into the bottom letterbox bar do not release the bars while the top bar is intact.</item>
/// </list>
/// Not thread-safe (engine thread only).
/// </summary>
internal sealed class BlackBarDetector
{
    /// <summary>sRGB 0.06 in linear light.</summary>
    public static readonly float DarkThresholdLinear = ColorMath.SrgbToLinear(0.06f);

    /// <summary>A line counts as black if at most this fraction of its pixels is brighter than the threshold.</summary>
    public const float MaxBrightFractionPerLine = 0.02f;
    /// <summary>A frame with less than this fraction of bright pixels is "dark" and ignored.</summary>
    public const float MinBrightFractionPerFrame = 0.03f;
    /// <summary>Bars larger than this fraction (per side) are not bars but dark content.</summary>
    public const float MaxBarFraction = 0.35f;

    public double AcquireSeconds { get; set; } = 0.5;
    public double ReleaseSeconds { get; set; } = 0.2;

    private int[] _rowBright = Array.Empty<int>();
    private int[] _colBright = Array.Empty<int>();
    private int _w, _h;
    private Axis _vertical, _horizontal;

    /// <summary>Active bar size in lines (top = bottom).</summary>
    public int VerticalBarLines => _vertical.Active;
    /// <summary>Active bar size in columns (left = right).</summary>
    public int HorizontalBarLines => _horizontal.Active;
    public int Width => _w;
    public int Height => _h;

    /// <summary>Active bars as fractions of the screen.</summary>
    public (float Top, float Bottom, float Left, float Right) Bars
    {
        get
        {
            if (_w == 0 || _h == 0) return default;
            float v = _vertical.Active / (float)_h, hz = _horizontal.Active / (float)_w;
            return (v, v, hz, hz);
        }
    }

    public void Reset()
    {
        _vertical = default;
        _horizontal = default;
    }

    /// <summary>Raw per-frame measurement (no temporal filtering).</summary>
    public readonly record struct Measurement(int Top, int Bottom, int Left, int Right, bool Dark);

    /// <summary>Analyzes one frame and updates the temporal state. Returns true if the active bars changed.</summary>
    public bool Update(ReadOnlySpan<float> rgb, int width, int height, double nowSeconds)
    {
        if (width != _w || height != _h)
        {
            _w = width; _h = height;
            _rowBright = new int[height];
            _colBright = new int[width];
            Reset();
        }
        var m = Measure(rgb, width, height, _rowBright, _colBright);
        return Apply(m, nowSeconds);
    }

    /// <summary>Applies a measurement (exposed for tests).</summary>
    public bool Apply(Measurement m, double now)
    {
        if (m.Dark)
        {
            // Fade to black / dark scene: hold the current state, but don't let it count toward any transition.
            _vertical.ClearPending();
            _horizontal.ClearPending();
            return false;
        }
        int tolV = Math.Max(1, _h / 60), tolH = Math.Max(1, _w / 100);
        bool changed = _vertical.Step(Candidate(m.Top, m.Bottom, _h, tolV, _vertical.Active, bottomMayShrink: true),
            Shrunk(m.Top, m.Bottom, _vertical.Active, tolV, bottomMayShrink: true), now, AcquireSeconds, ReleaseSeconds, tolV);
        changed |= _horizontal.Step(Candidate(m.Left, m.Right, _w, tolH, _horizontal.Active, bottomMayShrink: false),
            Shrunk(m.Left, m.Right, _horizontal.Active, tolH, bottomMayShrink: false), now, AcquireSeconds, ReleaseSeconds, tolH);
        return changed;
    }

    /// <summary>Symmetric bar candidate in lines (0 = no bars).</summary>
    internal static int Candidate(int near, int far, int size, int tol, int active, bool bottomMayShrink)
    {
        int max = (int)(size * MaxBarFraction);
        // Subtitles in the bottom bar: the top bar is still intact → keep the active value.
        if (bottomMayShrink && active > 0 && near >= active - tol && near <= max && far < active - tol)
            return active;
        if (near <= 0 || far <= 0) return 0;
        int lo = Math.Min(near, far), hi = Math.Max(near, far);
        if (hi - lo > Math.Max(tol, (int)(hi * 0.2f))) return 0; // asymmetric → not letterboxing
        return lo > max ? 0 : lo;
    }

    /// <summary>True when picture content has appeared inside the active bars.</summary>
    internal static bool Shrunk(int near, int far, int active, int tol, bool bottomMayShrink)
    {
        if (active <= 0) return false;
        if (near < active - tol) return true;
        return !bottomMayShrink && far < active - tol;
    }

    public static Measurement Measure(ReadOnlySpan<float> rgb, int width, int height, Span<int> rowBright, Span<int> colBright)
    {
        rowBright = rowBright[..height];
        colBright = colBright[..width];
        rowBright.Clear();
        colBright.Clear();
        float thr = DarkThresholdLinear;
        int total = 0;
        for (int y = 0, p = 0; y < height; y++)
        {
            int rowCount = 0;
            for (int x = 0; x < width; x++, p += 3)
            {
                float m = MathF.Max(rgb[p], MathF.Max(rgb[p + 1], rgb[p + 2]));
                if (m > thr) { rowCount++; colBright[x]++; }
            }
            rowBright[y] = rowCount;
            total += rowCount;
        }
        if (total < MinBrightFractionPerFrame * width * height)
            return new Measurement(0, 0, 0, 0, Dark: true);

        int rowLimit = (int)(MaxBrightFractionPerLine * width);
        int colLimit = (int)(MaxBrightFractionPerLine * height);
        int top = 0; while (top < height && rowBright[top] <= rowLimit) top++;
        int bottom = 0; while (bottom < height - top && rowBright[height - 1 - bottom] <= rowLimit) bottom++;
        int left = 0; while (left < width && colBright[left] <= colLimit) left++;
        int right = 0; while (right < width - left && colBright[width - 1 - right] <= colLimit) right++;
        return new Measurement(top, bottom, left, right, Dark: false);
    }

    private struct Axis
    {
        public int Active;
        private int _pending;      // candidate waiting for stability (0 = none)
        private double _pendingSince;
        private double _shrinkSince; // when content first appeared in the bars (0 = not shrinking)
        private bool _hasPending, _shrinking;

        public void ClearPending()
        {
            _hasPending = false;
            _shrinking = false;
        }

        public bool Step(int candidate, bool shrunk, double now, double acquire, double release, int tol)
        {
            if (shrunk)
            {
                _hasPending = false;
                if (!_shrinking) { _shrinking = true; _shrinkSince = now; }
                if (now - _shrinkSince >= release)
                {
                    // Release fully; a (smaller) bar must then re-qualify with the normal acquire delay.
                    Active = 0;
                    _shrinking = false;
                    return true;
                }
                return false;
            }
            _shrinking = false;

            if (Math.Abs(candidate - Active) <= tol || candidate < Active)
            {
                // Same as active (within tolerance) or smaller without content in the bars (can't happen
                // often; treat as unchanged).
                _hasPending = false;
                return false;
            }
            if (!_hasPending || Math.Abs(candidate - _pending) > tol)
            {
                _hasPending = true;
                _pending = candidate;
                _pendingSince = now;
                return false;
            }
            _pending = Math.Min(_pending, candidate); // conservative: never overestimate
            if (now - _pendingSince >= acquire)
            {
                Active = _pending;
                _hasPending = false;
                return true;
            }
            return false;
        }
    }
}
