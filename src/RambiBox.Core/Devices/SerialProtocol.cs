using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// The wire format of one serial LED protocol (Adalight, Ardulight, AWA, TPM2). <see cref="SerialLedDevice"/> owns
/// everything else (port search, probing, reconnect, pacing, blackout); a protocol only says how a frame looks, how
/// fast the link runs by default and how the firmware identifies itself.
/// </summary>
internal interface ISerialLedProtocol
{
    /// <summary>Display name, also the log prefix ("Adalight").</summary>
    string Name { get; }

    /// <summary>Baud rate used when the settings don't specify one.</summary>
    int DefaultBaudRate { get; }

    /// <summary>Largest LED count the frame format can carry.</summary>
    int MaxLeds { get; }

    /// <summary>Maps the configured baud rate (DeviceSettings.BaudRate, ≤ 0 = unset) to the one actually used.</summary>
    int ResolveBaudRate(int configured);

    /// <summary>Bytes on the wire for one frame of <paramref name="ledCount"/> LEDs.</summary>
    int FrameSize(int ledCount);

    /// <summary>
    /// Builds a complete frame into <paramref name="frame"/> (exactly <see cref="FrameSize"/> bytes).
    /// <paramref name="rgb"/> holds logical R,G,B triplets; a shorter source is padded black, a longer one truncated.
    /// </summary>
    void BuildFrame(ReadOnlySpan<byte> rgb, Span<byte> frame, int ledCount, ColorOrder order);

    /// <summary>
    /// A fresh matcher for the firmware's self-identification, or null when the firmware never identifies itself.
    /// Auto-detect accepts a port of a protocol without greeting only when it is unambiguously the remembered device.
    /// </summary>
    SerialGreeting? CreateGreeting();

    /// <summary>
    /// When true, a working fixed (user-selected) port is reported through <see cref="ILedDevice.DeviceFound"/> so that
    /// auto-detect can later reconnect it by USB id. False for Adalight (unchanged behaviour: auto-detect finds it by
    /// its greeting anyway).
    /// </summary>
    bool RememberFixedPort { get; }
}

/// <summary>Incremental detector for a firmware greeting. One instance per opened port.</summary>
internal abstract class SerialGreeting
{
    /// <summary>
    /// Bytes to send to ask the firmware to identify itself, or null when it greets on its own (Adalight greets after
    /// the reset caused by opening the port).
    /// </summary>
    public virtual byte[]? Request => null;

    /// <summary>The request is repeated at this interval while waiting (the board may still be booting).</summary>
    public virtual int RequestIntervalMs => 300;

    /// <summary>Feeds received bytes; true once the greeting is complete.</summary>
    public abstract bool Feed(ReadOnlySpan<byte> data);
}

/// <summary>Matches an ASCII marker anywhere in the received text (case-insensitive), across chunk boundaries.</summary>
internal sealed class TextGreeting(string marker, byte[]? request = null) : SerialGreeting
{
    private readonly System.Text.StringBuilder _tail = new();

    public override byte[]? Request => request;

    public override bool Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _tail.Append((char)b);
        if (_tail.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        int keep = marker.Length - 1;
        if (_tail.Length > keep) _tail.Remove(0, _tail.Length - keep);
        return false;
    }
}

/// <summary>
/// Immutable description of one protocol's frame for one LED count / color order / baud rate. Swapped atomically when
/// the configuration changes.
/// </summary>
internal class SerialFrameFormat
{
    public SerialFrameFormat(ISerialLedProtocol protocol, int ledCount, ColorOrder order, int baudRate, double pacingFactor)
    {
        Protocol = protocol;
        LedCount = Math.Clamp(ledCount, 1, protocol.MaxLeds);
        Order = order;
        BaudRate = baudRate > 0 ? baudRate : protocol.DefaultBaudRate;
        FrameBytes = protocol.FrameSize(LedCount);
        // 8N1: 10 bits on the wire per byte.
        WireSeconds = FrameBytes * 10.0 / BaudRate;
        IntervalTicks = (long)Math.Ceiling(WireSeconds * pacingFactor * Stopwatch.Frequency);
    }

    public ISerialLedProtocol Protocol { get; }
    public int LedCount { get; }
    public ColorOrder Order { get; }
    public int BaudRate { get; }
    public int FrameBytes { get; }
    public int PayloadBytes => LedCount * 3;
    /// <summary>Time one frame takes on the wire.</summary>
    public double WireSeconds { get; }
    /// <summary>Minimum distance between two write starts (wire time * pacing factor), in Stopwatch ticks.</summary>
    public long IntervalTicks { get; }
    public long WireTicks => (long)(WireSeconds * Stopwatch.Frequency);
    public double MaxFrameRate => 1.0 / WireSeconds;

    /// <summary>Builds a complete frame into <paramref name="dst"/> (length ≥ FrameBytes).</summary>
    public void Build(ReadOnlySpan<byte> rgb, Span<byte> dst)
        => Protocol.BuildFrame(rgb, dst[..FrameBytes], LedCount, Order);

    /// <summary>Builds an all-black frame.</summary>
    public void BuildBlack(Span<byte> dst)
        => Protocol.BuildFrame(ReadOnlySpan<byte>.Empty, dst[..FrameBytes], LedCount, Order);
}

/// <summary>Helpers shared by the serial protocols.</summary>
internal static class SerialFrames
{
    /// <summary>
    /// Copies logical R,G,B triplets into wire order. A shorter source is zero-padded (black), a longer one truncated.
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
}
