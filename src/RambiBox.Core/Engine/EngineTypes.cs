using RambiBox.Core.Capture;
using RambiBox.Core.Devices;

namespace RambiBox.Core.Engine;

/// <summary>
/// Temporarily takes over the LED output (identify/test patterns, phone calibration).
/// <see cref="Render"/> is called on the engine thread once per output frame.
/// </summary>
public interface ILedOverride
{
    /// <summary>
    /// When true the rendered colors still go through white balance + brightness (normal output path,
    /// but WITHOUT saturation/gamma/hue/smoothing). When false they are sent raw (0..1 → 0..255).
    /// </summary>
    bool ApplyWhiteBalance { get; }

    /// <summary>Fill <paramref name="rgb"/> (ledCount*3 floats 0..1, R,G,B).</summary>
    void Render(Span<float> rgb, int ledCount, double timeSeconds);
}

[Flags]
public enum PauseReason
{
    None = 0,
    DisplayOff = 1,
    Locked = 2,
    Suspended = 4,
    /// <summary>The user disabled the lights (master switch).</summary>
    UserOff = 8,
}

public sealed record EngineStats(
    double OutputFps,
    double CaptureFps,
    double CaptureMs,
    double ProcessMs,
    DeviceStatus DeviceStatus,
    string? PortName,
    double LinkMaxFps,
    MonitorInfo? Monitor,
    PauseReason Paused,
    bool OverrideActive,
    string StatusText);
