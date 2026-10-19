namespace RambiBox.Core.Devices.Hid;

/// <summary>A HID interface (top-level collection) present on the system.</summary>
/// <param name="Path">Device interface path (\\?\hid#vid_1d50&amp;pid_6022#…), stable while the device stays in the same USB port.</param>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
/// <param name="ReleaseNumber">bcdDevice.</param>
/// <param name="SerialNumber">iSerialNumber string, null when the device has none.</param>
/// <param name="ProductName">iProduct string, null when unavailable.</param>
/// <param name="InputReportLength">HIDP_CAPS.InputReportByteLength (report id byte included), 0 when unknown.</param>
/// <param name="OutputReportLength">HIDP_CAPS.OutputReportByteLength (report id byte included), 0 when unknown.</param>
/// <param name="UsagePage">Top-level collection usage page.</param>
/// <param name="Usage">Top-level collection usage.</param>
internal sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort ReleaseNumber = 0,
    string? SerialNumber = null,
    string? ProductName = null,
    int InputReportLength = 0,
    int OutputReportLength = 0,
    ushort UsagePage = 0,
    ushort Usage = 0)
{
    public string UsbId => $"{VendorId:X4}:{ProductId:X4}";
}

/// <summary>Enumerates and opens HID devices. Abstracted so device drivers can be tested without hardware.</summary>
internal interface IHidTransport
{
    /// <summary>
    /// Lists present HID interfaces whose VID/PID satisfy <paramref name="match"/>. Read-only: never writes to any device,
    /// and strings/caps are only queried for matching devices. May throw on a system error (callers catch).
    /// </summary>
    IReadOnlyList<HidDeviceInfo> Enumerate(Func<ushort, ushort, bool> match);

    /// <summary>Opens a device for I/O (read-only when <paramref name="readOnly"/>). Throws <see cref="HidException"/> on failure.</summary>
    IHidConnection Open(HidDeviceInfo device, bool readOnly = false);
}

/// <summary>An open HID device. Not thread-safe except <see cref="CancelIo"/>.</summary>
internal interface IHidConnection : IDisposable
{
    HidDeviceInfo Device { get; }

    /// <summary>
    /// Writes one output report (byte 0 = report id). Shorter reports are zero-padded to the device's report length.
    /// Returns false with a Win32 error code on failure or timeout (the write is cancelled). Never throws.
    /// </summary>
    bool Write(ReadOnlySpan<byte> report, int timeoutMs, out int error);

    /// <summary>Reads the current input report via GET_REPORT (byte 0 = report id). Returns false when unsupported/failed.</summary>
    bool TryGetInputReport(Span<byte> buffer);

    /// <summary>Cancels a pending write from another thread (watchdog / shutdown).</summary>
    void CancelIo();
}

/// <summary>Failure to open a HID device; <see cref="Error"/> is the Win32 error code.</summary>
internal sealed class HidException(string message, int error) : IOException(message)
{
    public int Error { get; } = error;

    /// <summary>ERROR_ACCESS_DENIED / ERROR_SHARING_VIOLATION: another program holds the device exclusively.</summary>
    public bool IsBusy => Error is 5 or 32;
}
