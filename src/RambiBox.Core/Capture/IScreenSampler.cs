using RambiBox.Core.Config;

namespace RambiBox.Core.Capture;

/// <summary>A monitor that can be captured.</summary>
/// <param name="Id">Stable identifier that survives reboots / display re-enumeration
/// (e.g. derived from the monitor device path / EDID); used in <see cref="CaptureSettings.MonitorId"/>.</param>
/// <param name="FriendlyName">Human readable, e.g. "ROG XG27AQ (2560×1440)".</param>
/// <param name="GdiDeviceName">Current GDI name like \\.\DISPLAY1 (NOT stable, informational / for window placement).</param>
/// <param name="Bounds">Desktop coordinates in physical pixels.</param>
public sealed record MonitorInfo(
    string Id,
    string FriendlyName,
    string GdiDeviceName,
    int X, int Y, int Width, int Height,
    bool IsPrimary,
    bool IsHdr,
    double RefreshHz);

public enum SampleResult
{
    /// <summary>A new desktop frame was captured and the zone colors were written.</summary>
    NewFrame,
    /// <summary>The screen did not change within the timeout; destination untouched (previous colors still valid).</summary>
    NoChange,
    /// <summary>Capture is temporarily impossible (secure desktop/UAC, mode change, monitor missing). Sampler retries internally.</summary>
    Unavailable,
}

/// <summary>
/// Captures the screen and reduces it to one average color per LED zone.
/// All members except <see cref="GetMonitors"/> and <see cref="TryGetPreview"/> are called from a single engine thread.
/// </summary>
public interface IScreenSampler : IDisposable
{
    /// <summary>Enumerate monitors attached to the desktop. Thread-safe.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>The monitor currently being captured (null if none).</summary>
    MonitorInfo? CurrentMonitor { get; }

    /// <summary>
    /// (Re)configure. Cheap if nothing changed. <paramref name="monitorId"/> empty or unknown = primary monitor.
    /// </summary>
    void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection);

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new desktop frame and writes the average color of each zone
    /// into <paramref name="destRgb"/> as sRGB-encoded floats 0..1 (length = zones * 3, order R,G,B).
    /// HDR desktops must be tone-mapped to SDR sRGB so values look like the SDR picture.
    /// Disabled zones produce black.
    /// </summary>
    SampleResult Sample(Span<float> destRgb, int timeoutMs);

    /// <summary>
    /// Copies the most recent downscaled frame as BGRA8 sRGB (for UI preview, ~160 px wide). Thread-safe, may return false.
    /// </summary>
    bool TryGetPreview(out byte[] bgra, out int width, out int height);

    /// <summary>Currently detected black bars as fractions of the screen (0 when none / detection disabled).</summary>
    (float Top, float Bottom, float Left, float Right) BlackBars { get; }
}
