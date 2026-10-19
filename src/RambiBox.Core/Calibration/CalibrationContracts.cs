namespace RambiBox.Core.Calibration;

/// <summary>A filled rectangle of the on-screen calibration pattern, in normalized monitor coordinates (0..1).</summary>
/// <param name="Rgb">0xRRGGBB</param>
public sealed record PatternRect(float Left, float Top, float Right, float Bottom, int Rgb);

/// <summary>
/// What the full-screen calibration window must draw: black background + <see cref="Rects"/> in order.
/// While <see cref="ShowQr"/> is true, the window also draws the QR code (for <see cref="PhoneCalibrationService.Url"/>)
/// centered inside <see cref="QrArea"/> together with short instructions.
/// </summary>
public sealed record CalibrationPattern(
    IReadOnlyList<PatternRect> Rects,
    float QrLeft, float QrTop, float QrRight, float QrBottom,
    bool ShowQr);

public enum CalibrationPhase
{
    /// <summary>Server running, QR visible, no phone has opened the page yet.</summary>
    WaitingForPhone,
    /// <summary>Phone opened the page; pattern shown, waiting for a photo.</summary>
    WaitingForPhoto,
    /// <summary>Photo received and being analyzed.</summary>
    Analyzing,
    /// <summary>A correction was computed and applied live; user can take another photo to verify or finish.</summary>
    Adjusted,
    /// <summary>Error below the target: calibrated.</summary>
    Converged,
    /// <summary>Last photo could not be used (see message); waiting for a new photo.</summary>
    PhotoRejected,
    /// <summary>Server could not start (no network etc.).</summary>
    Failed,
}

/// <param name="Message">Short human readable status (English), shown on PC and phone.</param>
/// <param name="ErrorPercent">Color mismatch between LED glow and screen white of the last photo (lower is better).</param>
/// <param name="WhiteR">White balance currently applied by the calibration (0..1).</param>
public sealed record CalibrationState(
    CalibrationPhase Phase,
    string Message,
    int PhotosAnalyzed,
    double? ErrorPercent,
    double WhiteR, double WhiteG, double WhiteB);
