namespace RambiBox.Core.Calibration;

/// <summary>A filled rectangle of the on-screen calibration pattern, in normalized monitor coordinates (0..1).</summary>
/// <param name="Rgb">0xRRGGBB</param>
public sealed record PatternRect(float Left, float Top, float Right, float Bottom, int Rgb);

/// <summary>
/// What the full-screen calibration window must draw: black background + <see cref="Rects"/> in order.
/// While <see cref="ShowQr"/> is true, the window also draws the QR code (for <see cref="PhoneCalibrationService.Url"/>)
/// centered inside the QR area together with short instructions.
/// </summary>
public sealed record CalibrationPattern(
    IReadOnlyList<PatternRect> Rects,
    float QrLeft, float QrTop, float QrRight, float QrBottom,
    bool ShowQr)
{
    /// <summary>Step code shown in the pattern (null when none is drawn).</summary>
    public int? Code { get; init; }

    /// <summary>Eye fine-tune pattern (wide edge bands, no markers): the status text goes in the middle.</summary>
    public bool EyeMode { get; init; }
}

public enum CalibrationPhase
{
    /// <summary>Server running, QR visible, no phone has opened the page yet.</summary>
    WaitingForPhone,
    /// <summary>Phone opened the page; it is choosing live camera / video / setup.</summary>
    PhoneConnected,
    /// <summary>Live camera color loop running.</summary>
    LiveCamera,
    /// <summary>Video fallback: the fixed color loop is cycling on the screen while the phone records.</summary>
    VideoLoop,
    /// <summary>Video frames are being uploaded/analyzed.</summary>
    VideoAnalyzing,
    /// <summary>Fine-tuning by eye with the phone as a remote control.</summary>
    EyeTune,
    /// <summary>The user tapped Done: everything is saved.</summary>
    Finished,
    /// <summary>Server could not start (no network etc.) or the session timed out.</summary>
    Failed,
}

/// <summary>Snapshot for the PC window.</summary>
/// <param name="Message">Main status line (English).</param>
/// <param name="Detail">Secondary line (step progress, numbers).</param>
/// <param name="Progress">Overall progress of the camera loop 0..1 (null when not applicable).</param>
/// <param name="ErrorPercent">Color difference between LED glow and screen white of the last frame (lower is better).</param>
public sealed record CalibrationState(
    CalibrationPhase Phase,
    string Message,
    string Detail,
    double? Progress,
    double? ErrorPercent,
    double WhiteR, double WhiteG, double WhiteB);

/// <summary>Color settings the calibration reads and changes.</summary>
/// <param name="ColorMatrix">Row-major 3×3 LED color matrix, null = identity.</param>
/// <param name="Brightness">Global LED brightness (0..1), used to predict the exact bytes sent to the strip.</param>
public sealed record CalibrationValues(
    (double R, double G, double B) WhiteBalance,
    (double R, double G, double B) ChannelGamma,
    double[]? ColorMatrix,
    double Brightness = 1.0);

/// <summary>
/// Where the calibration applies its results (the app's settings). All members may be called from any thread;
/// Apply* must take effect on the LEDs promptly and persist the value.
/// </summary>
public interface ICalibrationTarget
{
    CalibrationValues Read();
    void ApplyWhiteBalance(double r, double g, double b);
    void ApplyChannelGamma(double r, double g, double b);
    void ApplyColorMatrix(double[]? matrix);
}
