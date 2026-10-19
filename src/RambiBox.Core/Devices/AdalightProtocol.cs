using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Immutable description of the Adalight frame for one LED count / color order / baud rate,
/// with the header prebuilt. Swapped atomically when the configuration changes.
/// </summary>
internal sealed class AdalightFrameFormat
{
    public const int HeaderSize = 6;
    public const int MaxLeds = 65536; // count-1 must fit in 16 bits

    public AdalightFrameFormat(int ledCount, ColorOrder order, int baudRate, double pacingFactor)
    {
        LedCount = Math.Clamp(ledCount, 1, MaxLeds);
        Order = order;
        BaudRate = baudRate > 0 ? baudRate : 115200;
        Header = new byte[HeaderSize];
        AdalightProtocol.WriteHeader(Header, LedCount);
        FrameBytes = HeaderSize + LedCount * 3;
        // 8N1: 10 bits on the wire per byte.
        WireSeconds = FrameBytes * 10.0 / BaudRate;
        IntervalTicks = (long)Math.Ceiling(WireSeconds * pacingFactor * Stopwatch.Frequency);
    }

    public int LedCount { get; }
    public ColorOrder Order { get; }
    public int BaudRate { get; }
    public byte[] Header { get; }
    public int FrameBytes { get; }
    public int PayloadBytes => LedCount * 3;
    /// <summary>Time one frame takes on the wire.</summary>
    public double WireSeconds { get; }
    /// <summary>Minimum distance between two write starts (wire time * pacing factor), in Stopwatch ticks.</summary>
    public long IntervalTicks { get; }
    public long WireTicks => (long)(WireSeconds * Stopwatch.Frequency);
    public double MaxFrameRate => 1.0 / WireSeconds;

    /// <summary>Builds a complete frame (header + payload) into <paramref name="dst"/> (length ≥ FrameBytes).</summary>
    public void Build(ReadOnlySpan<byte> rgb, Span<byte> dst)
    {
        Header.CopyTo(dst);
        AdalightProtocol.EncodePayload(rgb, dst.Slice(HeaderSize, PayloadBytes), Order);
    }

    /// <summary>Builds an all-black frame.</summary>
    public void BuildBlack(Span<byte> dst)
    {
        Header.CopyTo(dst);
        dst.Slice(HeaderSize, PayloadBytes).Clear();
    }
}

internal static class AdalightProtocol
{
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
    {
        int n = Math.Min(rgb.Length, dst.Length) / 3 * 3;
        if (order == ColorOrder.RGB)
        {
            rgb[..n].CopyTo(dst);
        }
        else
        {
            var (a, b, c) = order switch
            {
                ColorOrder.RBG => (0, 2, 1),
                ColorOrder.GRB => (1, 0, 2),
                ColorOrder.GBR => (1, 2, 0),
                ColorOrder.BRG => (2, 0, 1),
                ColorOrder.BGR => (2, 1, 0),
                _ => (0, 1, 2),
            };
            for (int i = 0; i < n; i += 3)
            {
                dst[i] = rgb[i + a];
                dst[i + 1] = rgb[i + b];
                dst[i + 2] = rgb[i + c];
            }
        }
        if (n < dst.Length) dst[n..].Clear();
    }

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
}
