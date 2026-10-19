using System.Text;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

public class AdalightProtocolTests
{
    [Theory]
    [InlineData(112, 0x00, 0x6F, 0x00 ^ 0x6F ^ 0x55)]
    [InlineData(1, 0x00, 0x00, 0x55)]
    [InlineData(256, 0x00, 0xFF, 0xFF ^ 0x55)]
    [InlineData(300, 0x01, 0x2B, 0x01 ^ 0x2B ^ 0x55)]
    public void Header_HasMagicCountMinusOneAndChecksum(int leds, int hi, int lo, int checksum)
    {
        var h = new byte[6];
        AdalightProtocol.WriteHeader(h, leds);
        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', (byte)hi, (byte)lo, (byte)checksum }, h);
    }

    [Theory]
    [InlineData(ColorOrder.RGB, 1, 2, 3)]
    [InlineData(ColorOrder.RBG, 1, 3, 2)]
    [InlineData(ColorOrder.GRB, 2, 1, 3)]
    [InlineData(ColorOrder.GBR, 2, 3, 1)]
    [InlineData(ColorOrder.BRG, 3, 1, 2)]
    [InlineData(ColorOrder.BGR, 3, 2, 1)]
    public void Payload_AppliesColorOrder(ColorOrder order, int a, int b, int c)
    {
        byte[] rgb = [1, 2, 3, 10, 20, 30];
        var dst = new byte[6];
        AdalightProtocol.EncodePayload(rgb, dst, order);
        Assert.Equal(new byte[] { (byte)a, (byte)b, (byte)c, (byte)(a * 10), (byte)(b * 10), (byte)(c * 10) }, dst);
    }

    [Fact]
    public void Payload_ShortSourceIsPaddedBlack_LongSourceTruncated()
    {
        var dst = new byte[9];
        dst.AsSpan().Fill(0xEE);
        AdalightProtocol.EncodePayload(new byte[] { 5, 6, 7 }, dst, ColorOrder.GRB);
        Assert.Equal(new byte[] { 6, 5, 7, 0, 0, 0, 0, 0, 0 }, dst);

        var small = new byte[3];
        AdalightProtocol.EncodePayload(new byte[] { 1, 2, 3, 4, 5, 6 }, small, ColorOrder.RGB);
        Assert.Equal(new byte[] { 1, 2, 3 }, small);
    }

    [Fact]
    public void FrameFormat_BuildsCompleteFrame_AndComputesWireTime()
    {
        var f = new AdalightFrameFormat(112, ColorOrder.RGB, 115200, 1.03);
        Assert.Equal(342, f.FrameBytes);
        Assert.Equal(342 * 10.0 / 115200, f.WireSeconds, 9);
        Assert.Equal(115200 / 3420.0, f.MaxFrameRate, 6);

        var rgb = Enumerable.Range(0, 336).Select(i => (byte)i).ToArray();
        var frame = new byte[f.FrameBytes];
        f.Build(rgb, frame);
        Assert.Equal("Ada", Encoding.ASCII.GetString(frame, 0, 3));
        Assert.Equal(0x00, frame[3]);
        Assert.Equal(0x6F, frame[4]);
        Assert.Equal(0x6F ^ 0x55, frame[5]);
        Assert.Equal(rgb, frame[6..]);

        f.BuildBlack(frame);
        Assert.All(frame[6..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("Ada\n", true)]
    [InlineData("\0ÿ\u0012Ada\n", true)]
    [InlineData("AAda", true)]
    [InlineData("AdAda", true)]
    [InlineData("Adx a", false)]
    [InlineData("ada", false)]
    public void Greeting_Matcher(string input, bool expected)
    {
        int state = 0;
        Assert.Equal(expected, AdalightProtocol.FeedGreeting(Encoding.Latin1.GetBytes(input), ref state));
    }

    [Fact]
    public void Greeting_Matcher_AcrossChunks()
    {
        int state = 0;
        Assert.False(AdalightProtocol.FeedGreeting("xA"u8, ref state));
        Assert.False(AdalightProtocol.FeedGreeting("d"u8, ref state));
        Assert.True(AdalightProtocol.FeedGreeting("a\n"u8, ref state));
    }
}
