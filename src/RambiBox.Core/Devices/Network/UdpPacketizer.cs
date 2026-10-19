using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// Turns one LED frame into the UDP datagrams of a network protocol. All packet buffers are allocated up front (in the
/// constructor), so <see cref="Build"/> never allocates: the device thread encodes and sends frames garbage-free.
/// Not thread-safe; owned by one device thread.
/// </summary>
internal abstract class UdpPacketizer
{
    private readonly byte[][] _packets;
    private readonly int[] _lengths;
    private readonly int[] _destinations;
    private readonly int _a, _b, _c; // source channel for wire channel 0/1/2 (ColorOrder)

    protected UdpPacketizer(int ledCount, ColorOrder order, int packetCount, int maxPacketSize)
    {
        LedCount = Math.Max(1, ledCount);
        Order = order;
        (_a, _b, _c) = ChannelMap(order);
        _packets = new byte[packetCount][];
        for (int i = 0; i < packetCount; i++) _packets[i] = new byte[maxPacketSize];
        _lengths = new int[packetCount];
        _destinations = new int[packetCount];
    }

    /// <summary>LEDs carried per frame (after protocol limits).</summary>
    public int LedCount { get; }

    public ColorOrder Order { get; }

    /// <summary>Datagrams per frame (pre-allocated packet buffers).</summary>
    public int Capacity => _packets.Length;

    /// <summary>Datagrams produced by the last <see cref="Build"/> / <see cref="BuildBlackout"/>.</summary>
    public int PacketCount { get; private set; }

    /// <summary>
    /// Encodes <paramref name="rgb"/> (R,G,B per LED, logical order). A shorter frame is padded with black, a longer
    /// one truncated. Returns the number of datagrams (see <see cref="Packet"/>).
    /// </summary>
    public int Build(ReadOnlySpan<byte> rgb) => PacketCount = Encode(rgb, final: false, pass: 0);

    /// <summary>
    /// Datagrams to send when the device stops (black, then protocol-specific "end of stream"), one pass at a time;
    /// returns 0 when there are no more passes. Passes are sent a few milliseconds apart.
    /// </summary>
    public int BuildBlackout(int pass) => PacketCount = BlackoutPass(pass);

    /// <summary>Default stop sequence: a black frame, twice (UDP may drop one).</summary>
    protected virtual int BlackoutPass(int pass) => pass < 2 ? Encode(ReadOnlySpan<byte>.Empty, final: true, pass) : 0;

    /// <summary>Encodes a frame into the packet buffers; returns the datagram count.</summary>
    protected abstract int Encode(ReadOnlySpan<byte> rgb, bool final, int pass);

    public ReadOnlySpan<byte> Packet(int i) => _packets[i].AsSpan(0, _lengths[i]);

    /// <summary>Index into the device's destination list for datagram <paramref name="i"/> (E1.31 multicast: per universe).</summary>
    public int Destination(int i) => _destinations[i];

    protected byte[] Buffer(int i) => _packets[i];

    protected void SetPacket(int i, int length, int destination = 0)
    {
        _lengths[i] = length;
        _destinations[i] = destination;
    }

    /// <summary>
    /// Copies LEDs [<paramref name="firstLed"/>, +<paramref name="count"/>) of <paramref name="rgb"/> to
    /// <paramref name="dst"/> as 3 bytes each in wire color order; LEDs missing from the frame are black.
    /// </summary>
    protected void WriteLeds(ReadOnlySpan<byte> rgb, int firstLed, int count, Span<byte> dst)
    {
        int src = firstLed * 3;
        int available = src >= rgb.Length ? 0 : Math.Clamp((rgb.Length - src) / 3, 0, count);
        if (available == 0) { }
        else if (_a == 0 && _b == 1 && _c == 2)
        {
            rgb.Slice(src, available * 3).CopyTo(dst);
        }
        else
        {
            for (int i = 0, s = src, d = 0; i < available; i++, s += 3, d += 3)
            {
                dst[d] = rgb[s + _a];
                dst[d + 1] = rgb[s + _b];
                dst[d + 2] = rgb[s + _c];
            }
        }
        dst.Slice(available * 3, (count - available) * 3).Clear();
    }

    /// <summary>Writes one LED (3 bytes, wire order) or black when the frame is too short.</summary>
    protected void WriteLed(ReadOnlySpan<byte> rgb, int led, Span<byte> dst)
    {
        int s = led * 3;
        if (s + 2 < rgb.Length)
        {
            dst[0] = rgb[s + _a];
            dst[1] = rgb[s + _b];
            dst[2] = rgb[s + _c];
        }
        else dst[..3].Clear();
    }

    internal static (int A, int B, int C) ChannelMap(ColorOrder order) => order switch
    {
        ColorOrder.RBG => (0, 2, 1),
        ColorOrder.GRB => (1, 0, 2),
        ColorOrder.GBR => (1, 2, 0),
        ColorOrder.BRG => (2, 0, 1),
        ColorOrder.BGR => (2, 1, 0),
        _ => (0, 1, 2),
    };
}
