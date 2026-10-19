using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Ardulight LED controller on a (USB-)serial port, as supported by Prismatik (see <see cref="ArdulightProtocol"/>).
/// The firmware never identifies itself: auto-detect only reconnects the remembered device (same USB id); otherwise the
/// COM port has to be selected.
/// </summary>
[LedDevice(DeviceType.Ardulight)]
public sealed class ArdulightDevice : SerialLedDevice
{
    public ArdulightDevice() : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), AdalightOptions.Default) { }

    internal ArdulightDevice(IPortEnumerator ports, ISerialLinkFactory links, AdalightOptions? options = null)
        : base(ArdulightProtocol.Instance, ports, links, options) { }
}

/// <summary>
/// Ardulight: one sync byte 0xFF, then one triplet per LED in the configured color order. Every channel is limited to
/// 254 so that 0xFF only ever appears as the frame start. No LED count, no checksum, no greeting; the LED count is
/// compiled into the firmware.
/// <para>
/// Source: Prismatik, Software/src/LedDeviceArdulight.cpp (github.com/psieg/Lightpack): the constructor appends
/// <c>(char)255</c> to <c>m_writeBufferHeader</c>; <c>setColors</c> applies <c>PrismatikMath::maxCorrection(254, …)</c>
/// (a clamp, Software/math/PrismatikMath.cpp) to every color and appends r,g,b in the configured sequence (RGB, RBG,
/// BRG, BGR, GRB, GBR); <c>switchOffLeds</c> sends the header plus 3 zero bytes per LED. The baud rate is the user's
/// serial setting (Prismatik default 115200). Prismatik's UI limits Ardulight to 255 LEDs
/// (<c>MaximumNumberOfLeds::Ardulight</c>, Software/src/enums.hpp); the frame itself has no limit.
/// </para>
/// </summary>
internal sealed class ArdulightProtocol : ISerialLedProtocol
{
    public const byte Sync = 0xFF;
    public const byte MaxChannel = 254;

    public static readonly ArdulightProtocol Instance = new();

    public string Name => "Ardulight";
    public int DefaultBaudRate => 115200;
    public int MaxLeds => 65536;
    public int ResolveBaudRate(int configured) => configured > 0 ? configured : DefaultBaudRate;
    public int FrameSize(int ledCount) => 1 + ledCount * 3;
    public bool RememberFixedPort => true;
    public SerialGreeting? CreateGreeting() => null;

    public void BuildFrame(ReadOnlySpan<byte> rgb, Span<byte> frame, int ledCount, ColorOrder order)
    {
        frame[0] = Sync;
        var payload = frame.Slice(1, ledCount * 3);
        SerialFrames.EncodePayload(rgb, payload, order);
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] > MaxChannel) payload[i] = MaxChannel;
        }
    }
}
