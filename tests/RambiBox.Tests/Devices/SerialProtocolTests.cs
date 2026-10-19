using System.Text;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>Golden frame bytes for the serial protocols (derived from the sources cited in the protocol classes).</summary>
public class SerialProtocolTests
{
    private static byte[] Frame(ISerialLedProtocol p, byte[] rgb, int leds, ColorOrder order = ColorOrder.RGB)
    {
        var f = new SerialFrameFormat(p, leds, order, 0, 1.03);
        var dst = new byte[f.FrameBytes];
        f.Build(rgb, dst);
        return dst;
    }

    private static byte[] Black(ISerialLedProtocol p, int leds)
    {
        var f = new SerialFrameFormat(p, leds, ColorOrder.RGB, 0, 1.03);
        var dst = new byte[f.FrameBytes];
        dst.AsSpan().Fill(0xEE);
        f.BuildBlack(dst);
        return dst;
    }

    // ---------------- Adalight (unchanged format through the generic path)

    [Fact]
    public void Adalight_GenericPath_MatchesHeaderAndPayload()
    {
        var frame = Frame(AdalightProtocol.Instance, [1, 2, 3, 4, 5, 6], 2, ColorOrder.GRB);
        Assert.Equal(new byte[] { 0x41, 0x64, 0x61, 0x00, 0x01, 0x54, 2, 1, 3, 5, 4, 6 }, frame);
    }

    // ---------------- Ardulight

    [Fact]
    public void Ardulight_SyncByte_ColorOrder_ChannelsLimitedTo254()
    {
        var frame = Frame(ArdulightProtocol.Instance, [255, 0, 128, 1, 254, 255], 2, ColorOrder.GRB);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xFE, 0x80, 0xFE, 0x01, 0xFE }, frame);
    }

    [Theory]
    [InlineData(ColorOrder.RGB, 10, 20, 30)]
    [InlineData(ColorOrder.RBG, 10, 30, 20)]
    [InlineData(ColorOrder.BRG, 30, 10, 20)]
    [InlineData(ColorOrder.BGR, 30, 20, 10)]
    [InlineData(ColorOrder.GRB, 20, 10, 30)]
    [InlineData(ColorOrder.GBR, 20, 30, 10)]
    public void Ardulight_ColorSequences(ColorOrder order, int a, int b, int c)
        => Assert.Equal(new byte[] { 0xFF, (byte)a, (byte)b, (byte)c }, Frame(ArdulightProtocol.Instance, [10, 20, 30], 1, order));

    [Fact]
    public void Ardulight_BlackFrame_And_ShortSourcePadded()
    {
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0, 0, 0, 0 }, Black(ArdulightProtocol.Instance, 2));
        Assert.Equal(new byte[] { 0xFF, 7, 8, 9, 0, 0, 0 }, Frame(ArdulightProtocol.Instance, [7, 8, 9], 2));
        Assert.Equal(1 + 3 * 100, ArdulightProtocol.Instance.FrameSize(100));
        Assert.Null(ArdulightProtocol.Instance.CreateGreeting());
    }

    // ---------------- AWA

    [Fact]
    public void Awa_GoldenFrame_TwoLeds()
    {
        // count-1 = 1 → 00 01, CRC 00^01^55 = 54. Fletcher over 10 20 30 40 50 60:
        // f1 = 81 (0x51), f2 = 131 (0x83), fExt = 96 (0x60).
        var frame = Frame(AwaProtocol.Instance, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60], 2);
        Assert.Equal(new byte[] { 0x41, 0x77, 0x61, 0x00, 0x01, 0x54, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x51, 0x83, 0x60 }, frame);
    }

    [Fact]
    public void Awa_FletcherExtOf0x41_IsSentAs0xAA()
    {
        // fExt: 41^0 = 0x41, +(01^1 = 0), +(02^2 = 0) → 0x41 → replaced by 0xAA. f1 = 68, f2 = 199.
        var frame = Frame(AwaProtocol.Instance, [0x41, 0x01, 0x02], 1);
        Assert.Equal(new byte[] { 0x41, 0x77, 0x61, 0x00, 0x00, 0x55, 0x41, 0x01, 0x02, 0x44, 0xC7, 0xAA }, frame);
    }

    [Fact]
    public void Awa_PositionWrapsAtByte_LikeTheFirmware()
    {
        // 300 data bytes: the position counter is a uint8_t in HyperHDR and HyperSerialESP32, so it wraps after 256.
        // (A non-wrapping counter would give fExt 0x82 instead of 0x56.)
        var rgb = Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 3)).ToArray();
        var frame = Frame(AwaProtocol.Instance, rgb, 100);
        Assert.Equal(6 + 300 + 3, frame.Length);
        Assert.Equal(new byte[] { 0x41, 0x77, 0x61, 0x00, 0x63, 0x00 ^ 0x63 ^ 0x55 }, frame[..6]);
        Assert.Equal(rgb, frame[6..306]);
        Assert.Equal(new byte[] { 0x75, 0x8B, 0x56 }, frame[306..]);
    }

    [Fact]
    public void Awa_ColorOrderIsAppliedBeforeTheChecksum()
    {
        var bgr = Frame(AwaProtocol.Instance, [0x30, 0x20, 0x10, 0x60, 0x50, 0x40], 2, ColorOrder.BGR);
        var rgb = Frame(AwaProtocol.Instance, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60], 2);
        Assert.Equal(rgb, bgr);
    }

    [Fact]
    public void Awa_BlackFrame_HasValidChecksum()
        => Assert.Equal(new byte[] { 0x41, 0x77, 0x61, 0x00, 0x01, 0x54, 0, 0, 0, 0, 0, 0, 0, 0, 0x0F }, Black(AwaProtocol.Instance, 2));

    [Fact]
    public void Awa_HelloRequest_AndGreeting()
    {
        Assert.Equal(new byte[] { 0x41, 0x77, 0x41, 0x2A, 0xA2, 0x15, 0x68, 0x79, 0x70, 0x65, 0x72, 0x68, 0x64, 0x72 }, AwaProtocol.HelloRequest);
        var g = AwaProtocol.Instance.CreateGreeting()!;
        Assert.Equal(AwaProtocol.HelloRequest, g.Request);
        Assert.False(g.Feed("\r\nWelcome!\r\nAwa dr"u8));
        Assert.True(g.Feed("iver 11.\r\n"u8));
        Assert.False(AwaProtocol.Instance.CreateGreeting()!.Feed(Encoding.ASCII.GetBytes("Ada\nAwa")));
    }

    [Theory]
    [InlineData(0, 2_000_000)]
    [InlineData(115200, 2_000_000)] // RambiBox's generic default → the HyperSerial firmware rate
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(921600, 921600)]
    [InlineData(4_000_000, 4_000_000)]
    public void Awa_BaudRateMapping(int configured, int expected)
        => Assert.Equal(expected, AwaProtocol.Instance.ResolveBaudRate(configured));

    // ---------------- TPM2

    [Fact]
    public void Tpm2_GoldenFrame()
    {
        var frame = Frame(Tpm2Protocol.Instance, [1, 2, 3, 4, 5, 6], 2, ColorOrder.BGR);
        Assert.Equal(new byte[] { 0xC9, 0xDA, 0x00, 0x06, 3, 2, 1, 6, 5, 4, 0x36 }, frame);
    }

    [Fact]
    public void Tpm2_SizeIsDataBytesBigEndian()
    {
        var frame = Frame(Tpm2Protocol.Instance, [], 300);
        Assert.Equal(4 + 900 + 1, frame.Length);
        Assert.Equal(new byte[] { 0xC9, 0xDA, 0x03, 0x84 }, frame[..4]);
        Assert.Equal(0x36, frame[^1]);
        Assert.All(frame[4..^1], b => Assert.Equal(0, b));
        Assert.Equal(21845, new SerialFrameFormat(Tpm2Protocol.Instance, 100000, ColorOrder.RGB, 0, 1).LedCount);
    }

    // ---------------- wire time / max frame rate

    [Theory]
    [InlineData("Adalight", 100, 115200, 306)]
    [InlineData("Ardulight", 100, 115200, 301)]
    [InlineData("AWA", 100, 2_000_000, 309)]
    [InlineData("TPM2", 100, 500_000, 305)]
    [InlineData("AWA", 300, 2_000_000, 909)]
    public void WireTimeAndMaxFrameRate(string protocol, int leds, int baud, int frameBytes)
    {
        ISerialLedProtocol p = protocol switch
        {
            "Adalight" => AdalightProtocol.Instance,
            "Ardulight" => ArdulightProtocol.Instance,
            "AWA" => AwaProtocol.Instance,
            _ => Tpm2Protocol.Instance,
        };
        var f = new SerialFrameFormat(p, leds, ColorOrder.RGB, baud, 1.03);
        Assert.Equal(frameBytes, f.FrameBytes);
        Assert.Equal(frameBytes * 10.0 / baud, f.WireSeconds, 12);
        Assert.Equal(baud / (frameBytes * 10.0), f.MaxFrameRate, 6);
    }

    [Fact]
    public void DefaultBaudRates()
    {
        Assert.Equal(115200, AdalightProtocol.Instance.ResolveBaudRate(0));
        Assert.Equal(115200, ArdulightProtocol.Instance.ResolveBaudRate(0));
        Assert.Equal(115200, Tpm2Protocol.Instance.ResolveBaudRate(0));
        Assert.Equal(2_000_000, AwaProtocol.Instance.ResolveBaudRate(0));
        Assert.Equal(57600, ArdulightProtocol.Instance.ResolveBaudRate(57600));
    }
}
