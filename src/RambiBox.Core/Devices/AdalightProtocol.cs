using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>The Adalight frame for one LED count / color order / baud rate.</summary>
internal sealed class AdalightFrameFormat(int ledCount, ColorOrder order, int baudRate, double pacingFactor)
    : SerialFrameFormat(AdalightProtocol.Instance, ledCount, order, baudRate, pacingFactor)
{
    public const int HeaderSize = 6;
    public const int MaxLeds = 65536; // count-1 must fit in 16 bits
}

/// <summary>
/// Adalight: "Ada" + (count-1) big endian + checksum (hi ^ lo ^ 0x55), then R,G,B per LED in the configured order.
/// The firmware prints "Ada\n" after the reset that opening the port causes.
/// Sources: Adafruit Adalight (LEDstream.pde), Prismatik LedDeviceAdalight.cpp (github.com/psieg/Lightpack).
/// </summary>
internal sealed class AdalightProtocol : ISerialLedProtocol
{
    public static readonly AdalightProtocol Instance = new();

    public string Name => "Adalight";
    public int DefaultBaudRate => 115200;
    public int MaxLeds => AdalightFrameFormat.MaxLeds;
    public int ResolveBaudRate(int configured) => configured > 0 ? configured : DefaultBaudRate;
    public int FrameSize(int ledCount) => AdalightFrameFormat.HeaderSize + ledCount * 3;
    public bool RememberFixedPort => false;

    public void BuildFrame(ReadOnlySpan<byte> rgb, Span<byte> frame, int ledCount, ColorOrder order)
    {
        WriteHeader(frame, ledCount);
        EncodePayload(rgb, frame.Slice(AdalightFrameFormat.HeaderSize, ledCount * 3), order);
    }

    public SerialGreeting? CreateGreeting() => new Greeting();

    /// <summary>"Ada" magic + count-1 (big endian) + checksum (hi ^ lo ^ 0x55).</summary>
    public static void WriteHeader(Span<byte> dst, int ledCount)
    {
        int n = Math.Clamp(ledCount, 1, AdalightFrameFormat.MaxLeds) - 1;
        byte hi = (byte)(n >> 8), lo = (byte)n;
        dst[0] = (byte)'A';
        dst[1] = (byte)'d';
        dst[2] = (byte)'a';
        dst[3] = hi;
        dst[4] = lo;
        dst[5] = (byte)(hi ^ lo ^ 0x55);
    }

    /// <summary>
    /// Copies logical R,G,B triplets into wire order. A shorter source is zero-padded (black),
    /// a longer one truncated.
    /// </summary>
    public static void EncodePayload(ReadOnlySpan<byte> rgb, Span<byte> dst, ColorOrder order)
        => SerialFrames.EncodePayload(rgb, dst, order);

    /// <summary>Incremental matcher for the "Ada" greeting. Returns true when the greeting completed.</summary>
    public static bool FeedGreeting(ReadOnlySpan<byte> data, ref int state)
    {
        foreach (byte b in data)
        {
            byte expected = state switch { 0 => (byte)'A', 1 => (byte)'d', _ => (byte)'a' };
            if (b == expected)
            {
                if (++state == 3) return true;
            }
            else
            {
                state = b == (byte)'A' ? 1 : 0;
            }
        }
        return false;
    }

    private sealed class Greeting : SerialGreeting
    {
        private int _state;
        public override bool Feed(ReadOnlySpan<byte> data) => FeedGreeting(data, ref _state);
    }
}
