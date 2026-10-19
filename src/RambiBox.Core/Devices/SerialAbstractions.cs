namespace RambiBox.Core.Devices;

/// <summary>An open serial connection. Used from one thread at a time (the device thread),
/// except <see cref="CancelPendingIo"/> which may be called from any thread.</summary>
internal interface ISerialLink : IDisposable
{
    string PortName { get; }

    /// <summary>Writes all bytes (blocking). Throws IOException / TimeoutException / ObjectDisposedException on failure.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads what is available, waiting up to <paramref name="timeoutMs"/> for the first byte. Returns 0 on timeout.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Bytes still queued in the driver for transmission, or -1 when unknown.</summary>
    int BytesToWrite { get; }

    /// <summary>Drops anything received so far.</summary>
    void DiscardInput();

    /// <summary>Aborts a blocked Write/Read (watchdog for drivers that hang after surprise removal).</summary>
    void CancelPendingIo();
}

internal interface ISerialLinkFactory
{
    /// <summary>
    /// Opens the port. Throws <see cref="UnauthorizedAccessException"/> when another program has it open,
    /// other exceptions (IOException, ...) when it does not exist / cannot be configured.
    /// </summary>
    ISerialLink Open(string portName, int baudRate);
}

internal interface IPortEnumerator
{
    /// <summary>Cheap: names of the serial ports currently present (HKLM\HARDWARE\DEVICEMAP\SERIALCOMM).</summary>
    IReadOnlyCollection<string> GetPresentPortNames();

    /// <summary>Present ports with description, USB id and Bluetooth flag. Cached while the port set is unchanged.</summary>
    IReadOnlyList<SerialPortInfo> ListPorts();

    /// <summary>
    /// Present ports that belong to the Modem device class (GUID_DEVCLASS_MODEM: built-in/USB modems, phone and
    /// LTE-module AT ports). Auto-detect never opens these. Cached while the port set is unchanged.
    /// </summary>
    IReadOnlyCollection<string> GetModemPortNames();
}

/// <summary>Timing knobs; defaults are for real hardware, tests shrink them.</summary>
internal sealed class AdalightOptions
{
    /// <summary>Auto-detect: how long to wait for "Ada" after opening a candidate port.</summary>
    public int ProbeTimeoutMs { get; init; } = 2500;
    /// <summary>Fixed port: how long to wait for the greeting before sending anyway.</summary>
    public int FixedGreetingTimeoutMs { get; init; } = 1500;
    /// <summary>How often the present-port set is re-read while searching / checked while connected.</summary>
    public int RescanIntervalMs { get; init; } = 1000;
    /// <summary>First retry delay for a port that did not answer; doubles per failure up to <see cref="MaxRetryMs"/>.</summary>
    public int FailedRetryMs { get; init; } = 5000;
    /// <summary>Retry cap for ports that keep failing (avoid resetting someone else's Arduino every 5 s forever).</summary>
    public int MaxRetryMs { get; init; } = 30000;
    /// <summary>Retry delay for a port that is in use by another program.</summary>
    public int BusyRetryMs { get; init; } = 2000;
    /// <summary>Next write allowed at lastWriteStart + wireTime * PacingFactor.</summary>
    public double PacingFactor { get; init; } = 1.03;
    /// <summary>Extra time after the black frame's wire time before closing the port.</summary>
    public int BlackoutDrainMarginMs { get; init; } = 6;
    /// <summary>A single write blocked longer than this is cancelled (driver hang after unplug).</summary>
    public int WriteWatchdogMs { get; init; } = 1500;

    /// <summary>
    /// Minimum time between two opens of the same port. Opening resets the board via DTR, but a re-open within
    /// ~0.8 s of the previous one does not (measured), so the greeting would never come.
    /// </summary>
    public int MinReopenIntervalMs { get; init; } = 1000;

    public static AdalightOptions Default { get; } = new();
}
