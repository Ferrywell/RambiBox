using RambiBox.Core.Engine;

namespace RambiBox.Core.Calibration;

/// <summary>
/// Phone-assisted LED color calibration.
/// PUBLIC API IS A CONTRACT used by the UI — implementation to be completed (see CALIBRATION brief).
/// </summary>
public sealed class PhoneCalibrationService : IDisposable
{
    /// <param name="engine">Used to show the LED calibration pattern via <see cref="LightEngine.SetOverride"/>.</param>
    /// <param name="currentWhiteBalance">Returns the white balance (R,G,B 0..1) in effect when calibration starts.</param>
    /// <param name="applyWhiteBalance">Called (any thread) when a new white balance should be applied and persisted.</param>
    public PhoneCalibrationService(LightEngine engine,
        Func<(double R, double G, double B)> currentWhiteBalance,
        Action<double, double, double> applyWhiteBalance)
    {
        throw new NotImplementedException();
    }

    /// <summary>URL to encode in the QR code (http://&lt;lan-ip&gt;:&lt;port&gt;/c/&lt;token&gt;). Null until started.</summary>
    public Uri? Url => throw new NotImplementedException();

    public CalibrationState State => throw new NotImplementedException();

    /// <summary>What the calibration window should draw right now.</summary>
    public CalibrationPattern Pattern => throw new NotImplementedException();

    /// <summary>Raised on any thread when <see cref="State"/> or <see cref="Pattern"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the local web server and the LED pattern. Returns false (State = Failed) if impossible.</summary>
    public bool Start() => throw new NotImplementedException();

    /// <summary>
    /// Stops server and LED override. <paramref name="keepResult"/> false restores the white balance from before Start.
    /// </summary>
    public void Stop(bool keepResult) => throw new NotImplementedException();

    public void Dispose() => Stop(keepResult: true);
}
