using RambiBox.Core.Config;
using RambiBox.Core.Engine;

namespace RambiBox.Core.Api;

/// <summary>Answer of <c>getstatus</c>.</summary>
public enum PrismatikStatus { On, Off, DeviceError, Unknown }

/// <summary>A rectangle in desktop pixel coordinates (x, y = top-left).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{X},{Y},{Width},{Height}";
}

/// <summary>Thread-safe snapshot of what the API can read. Values are in RambiBox units; the server converts.</summary>
/// <param name="Status">Master switch / device state.</param>
/// <param name="Mode">Current light mode (Static color and Mood are reported as Prismatik's "moodlamp").</param>
/// <param name="Brightness">0..1.</param>
/// <param name="SmoothingMs">Smoothing time constant.</param>
/// <param name="Gamma">Color gamma (1 = unchanged).</param>
/// <param name="Fps">LED updates per second.</param>
/// <param name="Screen">The captured monitor in desktop pixels.</param>
/// <param name="Zones">The LED layout (one zone per LED, normalized to the captured monitor).</param>
/// <param name="DeviceName">Name for <c>getdevice</c>.</param>
/// <param name="MaxLeds">Answer of <c>getmaxleds</c>.</param>
public sealed record PrismatikHostState(
    PrismatikStatus Status,
    LightMode Mode,
    double Brightness,
    int SmoothingMs,
    double Gamma,
    double Fps,
    PixelRect Screen,
    IReadOnlyList<LedZone> Zones,
    string DeviceName = "Adalight",
    int MaxLeds = 1000)
{
    public int LedCount => Zones.Count;
}

/// <summary>A new capture rectangle for one LED (0-based index), normalized to the captured monitor.</summary>
public readonly record struct ZoneUpdate(int Index, float Left, float Top, float Right, float Bottom);

/// <summary>
/// A settings change requested by an API client. Null fields stay unchanged. The host applies it through the
/// same commit/save path as the UI (persisted like a slider change).
/// </summary>
public sealed record PrismatikChange
{
    public bool? Enabled { get; init; }
    public LightMode? Mode { get; init; }
    /// <summary>0..1.</summary>
    public double? Brightness { get; init; }
    public int? SmoothingMs { get; init; }
    public double? Gamma { get; init; }
    public IReadOnlyList<ZoneUpdate>? Zones { get; init; }
}

/// <summary>Who drives the LEDs through the API right now (see <see cref="IPrismatikHost.OnApiControlChanged"/>).</summary>
/// <param name="Locked">A client holds the lock (its colors are shown).</param>
/// <param name="Persisting">Unlocked with "persist on unlock": the last API colors stay until the lights are switched
/// or the mode changes (Prismatik 2.2 semantics).</param>
/// <param name="Owner">Remote address of the client that holds / held the lock.</param>
/// <param name="Override">The colors to show while <see cref="Active"/> (LED-correction color path: white balance applies).</param>
public sealed record PrismatikControl(bool Locked, bool Persisting, string? Owner, ILedOverride? Override)
{
    public static PrismatikControl None { get; } = new(false, false, null, null);
    public bool Active => Locked || Persisting;
}

/// <summary>What the Prismatik API server needs from the application. Implementations must be thread-safe.</summary>
public interface IPrismatikHost
{
    /// <summary>Current state (called from socket threads).</summary>
    PrismatikHostState GetState();

    /// <summary>The colors the LEDs currently show (before white balance), R,G,B bytes per LED.</summary>
    byte[] GetColors();

    /// <summary>All monitors in desktop pixels (for <c>getcountmonitors</c> / <c>getsizemonitor</c>).</summary>
    IReadOnlyList<PixelRect> GetMonitors();

    /// <summary>
    /// Applies a change; the task completes once it is applied (later reads see it). Returns false when rejected.
    /// </summary>
    Task<bool> ApplyAsync(PrismatikChange change);

    /// <summary>The API took or released control of the LEDs. Must not block (called while the server holds its lock).</summary>
    void OnApiControlChanged(PrismatikControl control);
}
