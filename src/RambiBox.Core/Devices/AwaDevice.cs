using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// AWA protocol LED controller: HyperSerialESP32 / HyperSerialEsp8266 / HyperSerialPico firmware (awawa-dev, used with
/// HyperHDR) on a high-speed USB serial link (see <see cref="AwaProtocol"/>). The firmware answers a hello request with
/// "Awa driver …", so auto-detect identifies it reliably.
/// </summary>
[LedDevice(DeviceType.Awa)]
public sealed class AwaDevice : SerialLedDevice
{
    public AwaDevice() : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), AdalightOptions.Default) { }

    internal AwaDevice(IPortEnumerator ports, ISerialLinkFactory links, AdalightOptions? options = null)
        : base(AwaProtocol.Instance, ports, links, options) { }
}

/// <summary>
/// AWA ("Adalight With Awa") frame, RGB variant:
/// <code>
/// 'A' 'w' 'a'  hi lo  hi^lo^0x55   R G B … (count = LEDs-1, big endian)   fletcher1 fletcher2 fletcherExt
/// </code>
/// Over the color data (not the header): <c>f1 = (f1 + b) % 255; f2 = (f2 + f1) % 255;
/// fExt = (fExt + (b ^ position++)) % 255</c> with all sums starting at 0 and <c>position</c> a byte that wraps at 256;
/// a final fExt of 0x41 ('A') is sent as 0xAA so it can never look like the start of the next header.
/// <para>
/// Sources (MIT, awawa-dev): HyperHDR sources/led-drivers/serial/DriverSerialAdalight.cpp (<c>CreateHeader</c>,
/// <c>writeFiniteColors</c>: <c>uint16_t fletcher1 = 0, fletcher2 = 0, fletcherExt = 0; uint8_t position = 0;</c>),
/// HyperSerialESP32 include/framestate.h + main.h (receiver: count = (hi&lt;&lt;8 | lo) + 1, CRC = hi ^ lo ^ 0x55,
/// all three trailer bytes verified). HyperSerialEsp8266 checks only fletcher1/fletcher2 and skips the extra byte
/// while looking for the next 'A', so the 3-byte trailer works with both.
/// </para>
/// <para>
/// Variants not used here: "AwA" adds 4 white-channel calibration bytes before the trailer (RGBW SK6812 strips) and
/// "AWa" carries 4 bytes per LED (RGBW) — RambiBox drives RGB strips.
/// </para>
/// <para>
/// Hello: HyperHDR's EspTools.h <c>initializeEsp</c> sends <c>41 77 41 2A A2 15</c> + "hyperhdr"; the firmware sees
/// count 0x2AA2 with a mismatching CRC byte 0x15 and replies "\r\nWelcome!\r\nAwa driver N." (HELLO_MESSAGE); HyperHDR
/// detects the device by the text "Awa driver".
/// </para>
/// <para>
/// Baud rate: the firmwares run at 2,000,000 baud by default (HyperSerialESP32 / Esp8266 README: "set Baudrate to
/// 2000000"). 115200 is RambiBox's generic serial default, not a value anyone picks for AWA, so it maps to 2,000,000;
/// any other configured rate is used as is.
/// </para>
/// </summary>
internal sealed class AwaProtocol : ISerialLedProtocol
{
    public const int HeaderSize = 6;
    public const int TrailerSize = 3;
    public const int FirmwareDefaultBaud = 2_000_000;
    /// <summary>RambiBox's generic DeviceSettings.BaudRate default; never an AWA firmware rate.</summary>
    public const int GenericDefaultBaud = 115200;

    /// <summary>Hello request (HyperHDR EspTools::initializeEsp).</summary>
    public static readonly byte[] HelloRequest = [0x41, 0x77, 0x41, 0x2A, 0xA2, 0x15, .. "hyperhdr"u8];
    public const string HelloMarker = "Awa driver";

    public static readonly AwaProtocol Instance = new();

    public string Name => "AWA";
    public int DefaultBaudRate => FirmwareDefaultBaud;
    public int MaxLeds => 65536; // count-1 must fit in 16 bits
    public bool RememberFixedPort => true;

    public int ResolveBaudRate(int configured)
        => configured <= 0 || configured == GenericDefaultBaud ? FirmwareDefaultBaud : configured;

    public int FrameSize(int ledCount) => HeaderSize + ledCount * 3 + TrailerSize;

    public SerialGreeting? CreateGreeting() => new TextGreeting(HelloMarker, HelloRequest);

    public void BuildFrame(ReadOnlySpan<byte> rgb, Span<byte> frame, int ledCount, ColorOrder order)
    {
        int n = Math.Clamp(ledCount, 1, MaxLeds) - 1;
        byte hi = (byte)(n >> 8), lo = (byte)n;
        frame[0] = (byte)'A';
        frame[1] = (byte)'w';
        frame[2] = (byte)'a';
        frame[3] = hi;
        frame[4] = lo;
        frame[5] = (byte)(hi ^ lo ^ 0x55);
        var data = frame.Slice(HeaderSize, ledCount * 3);
        SerialFrames.EncodePayload(rgb, data, order);
        var (f1, f2, fExt) = Fletcher(data);
        var trailer = frame.Slice(HeaderSize + data.Length, TrailerSize);
        trailer[0] = f1;
        trailer[1] = f2;
        trailer[2] = fExt;
    }

    /// <summary>The three AWA checksum bytes over the color data (fExt already mapped 0x41 → 0xAA).</summary>
    public static (byte F1, byte F2, byte FExt) Fletcher(ReadOnlySpan<byte> data)
    {
        int f1 = 0, f2 = 0, fExt = 0;
        byte position = 0;
        foreach (byte b in data)
        {
            fExt = (fExt + (b ^ position++)) % 255;
            f1 = (f1 + b) % 255;
            f2 = (f2 + f1) % 255;
        }
        return ((byte)f1, (byte)f2, (byte)(fExt != 0x41 ? fExt : 0xAA));
    }
}
