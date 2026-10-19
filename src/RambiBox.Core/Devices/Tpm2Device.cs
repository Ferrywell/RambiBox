using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// TPM2 LED controller on a (USB-)serial port (see <see cref="Tpm2Protocol"/>). TPM2 receivers don't identify
/// themselves: auto-detect only reconnects the remembered device (same USB id); otherwise the COM port has to be selected.
/// </summary>
[LedDevice(DeviceType.Tpm2)]
public sealed class Tpm2Device : SerialLedDevice
{
    public Tpm2Device() : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), AdalightOptions.Default) { }

    internal Tpm2Device(IPortEnumerator ports, ISerialLinkFactory links, AdalightOptions? options = null)
        : base(Tpm2Protocol.Instance, ports, links, options) { }
}

/// <summary>
/// TPM2 (serial) data frame: 0xC9 block start, 0xDA data frame, payload size in bytes (= LEDs*3) big endian,
/// the R,G,B data in the configured color order, 0x36 block end. Everything in one frame (no packet splitting).
/// <para>
/// Sources: the TPM2 protocol specification (tpm2.de, "TPM2 protocol" by the Ledstyles.de community) and its
/// implementations in Hyperion (libsrc/leddevice/dev_serial/LedDeviceTpm2.cpp, github.com/hyperion-project/hyperion.ng)
/// and HyperHDR (sources/led-drivers/serial/DriverSerialTpm2.cpp, github.com/awawa-dev/HyperHDR): both write
/// <c>0xC9, 0xDA, (ledRGBCount &gt;&gt; 8) &amp; 0xFF, ledRGBCount &amp; 0xFF</c>, the data and a trailing <c>0x36</c>.
/// The 16-bit size limits a frame to 65535 data bytes, i.e. 21845 LEDs.
/// </para>
/// </summary>
internal sealed class Tpm2Protocol : ISerialLedProtocol
{
    public const byte BlockStart = 0xC9;
    public const byte DataFrame = 0xDA;
    public const byte BlockEnd = 0x36;
    public const int HeaderSize = 4;

    public static readonly Tpm2Protocol Instance = new();

    public string Name => "TPM2";
    public int DefaultBaudRate => 115200;
    public int MaxLeds => 65535 / 3;
    public int ResolveBaudRate(int configured) => configured > 0 ? configured : DefaultBaudRate;
    public int FrameSize(int ledCount) => HeaderSize + ledCount * 3 + 1;
    public bool RememberFixedPort => true;
    public SerialGreeting? CreateGreeting() => null;

    public void BuildFrame(ReadOnlySpan<byte> rgb, Span<byte> frame, int ledCount, ColorOrder order)
    {
        int size = ledCount * 3;
        frame[0] = BlockStart;
        frame[1] = DataFrame;
        frame[2] = (byte)(size >> 8);
        frame[3] = (byte)size;
        SerialFrames.EncodePayload(rgb, frame.Slice(HeaderSize, size), order);
        frame[HeaderSize + size] = BlockEnd;
    }
}
