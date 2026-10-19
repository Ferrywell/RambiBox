using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

public enum DeviceStatus
{
    /// <summary>Not started / stopped.</summary>
    Stopped,
    /// <summary>Looking for the device (auto-detect or waiting for the configured port to appear).</summary>
    Searching,
    /// <summary>Port open and handshake done; frames are being sent.</summary>
    Connected,
    /// <summary>Configured port exists but cannot be opened (e.g. in use by AmbiBox). Retrying.</summary>
    PortBusy,
}

/// <summary>A serial port as seen by the system.</summary>
/// <param name="PortName">"COM3"</param>
/// <param name="Description">"USB-SERIAL CH340"</param>
/// <param name="UsbId">"1A86:7523" or null when not USB.</param>
public sealed record SerialPortInfo(string PortName, string Description, string? UsbId);

/// <summary>
/// An Adalight-compatible LED controller. Owns its own background thread that (re)connects automatically,
/// including when the USB device is re-plugged into another port (new COM number).
/// </summary>
public interface ILedDevice : IDisposable
{
    DeviceStatus Status { get; }
    /// <summary>Port currently used or being tried, e.g. "COM3".</summary>
    string? PortName { get; }
    /// <summary>Raised on the device thread when <see cref="Status"/> or <see cref="PortName"/> changes.</summary>
    event Action? StatusChanged;

    /// <summary>
    /// Raised when auto-detect found a working port, so the app can remember it (DeviceSettings.LastPort / LastUsbId).
    /// </summary>
    event Action<SerialPortInfo>? DeviceFound;

    /// <summary>Apply settings and LED count. Reconnects only if port/baud changed. Starts the device thread on first call.</summary>
    void Configure(DeviceSettings settings, int ledCount);

    /// <summary>
    /// Hands over the latest frame (ledCount*3 bytes, R,G,B per LED in logical order; the device applies
    /// <see cref="DeviceSettings.ColorOrder"/>). Non-blocking, latest-wins: an unsent older frame is replaced.
    /// </summary>
    void Submit(ReadOnlySpan<byte> rgb);

    /// <summary>
    /// Blocks until the link can accept a new frame without queueing (i.e. the previous frame is on the wire),
    /// so the caller can capture "just in time" for minimal latency. Returns false on timeout or when not connected.
    /// </summary>
    bool WaitForSlot(int timeoutMs);

    /// <summary>Theoretical max frame rate of the link for the current LED count and baud rate.</summary>
    double MaxFrameRate { get; }

    /// <summary>Frames actually written per second (measured).</summary>
    double FramesPerSecond { get; }

    /// <summary>Asks the device thread to rescan ports immediately (e.g. on WM_DEVICECHANGE).</summary>
    void Rescan();

    /// <summary>Sends black and closes the port (blocking, max ~200 ms). Used on exit/sleep.</summary>
    void Blackout();

    /// <summary>Lists serial ports present on the system. Thread-safe, fast (registry based).</summary>
    IReadOnlyList<SerialPortInfo> ListPorts();
}
