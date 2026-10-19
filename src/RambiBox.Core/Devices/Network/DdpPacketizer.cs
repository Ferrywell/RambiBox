using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// DDP — Distributed Display Protocol (http://www.3waylabs.com/ddp/), UDP port 4048. 10-byte header, big-endian:
/// <code>
/// byte 0      flags: VV x T S R Q P — version 1 (0x40), T timecode, S storage, R reply, Q query, P push (0x01)
/// byte 1      x x x x n n n n — sequence number 1..15 (0 = unused), ignores duplicates / detects reordering
/// byte 2      data type: C R TTT SSS — TTT 1 = RGB, SSS 3 = 8 bits per element → 0x0B ("RGB24", same value WLED sends)
/// byte 3      destination id: 1 = default output device
/// bytes 4-7   data offset in bytes (32-bit)
/// bytes 8-9   data length in bytes (16-bit)
/// bytes 10..  pixel data, at most 1440 bytes (480 RGB pixels) per datagram
/// </code>
/// A frame larger than one datagram is split; only the last datagram carries the push flag, so the receiver
/// (WLED, ESPixelStick, xLights controllers…) latches the whole frame at once.
/// </summary>
internal sealed class DdpPacketizer : UdpPacketizer
{
    public const int DefaultPort = 4048;
    public const int HeaderSize = 10;
    public const int MaxDataBytes = 1440;
    public const int LedsPerPacket = MaxDataBytes / 3;
    public const byte FlagVersion1 = 0x40, FlagPush = 0x01;
    public const byte DataTypeRgb24 = 0x0B;
    public const byte DestinationDefault = 1;
    /// <summary>Offset is 32-bit but the frame buffer is ours: cap at something sane.</summary>
    public const int MaxLeds = 100_000;

    private byte _sequence; // last used, 1..15

    public DdpPacketizer(int ledCount, ColorOrder order)
        : base(Math.Clamp(ledCount, 1, MaxLeds), order, (Math.Clamp(ledCount, 1, MaxLeds) + LedsPerPacket - 1) / LedsPerPacket,
            HeaderSize + MaxDataBytes)
    { }

    protected override int Encode(ReadOnlySpan<byte> rgb, bool final, int pass)
    {
        int n = LedCount, count = 0;
        for (int start = 0; start < n; start += LedsPerPacket, count++)
        {
            int k = Math.Min(LedsPerPacket, n - start);
            bool last = start + k >= n;
            var p = Buffer(count);
            _sequence = (byte)(_sequence % 15 + 1);
            int offset = start * 3, length = k * 3;
            p[0] = (byte)(FlagVersion1 | (last ? FlagPush : 0));
            p[1] = _sequence;
            p[2] = DataTypeRgb24;
            p[3] = DestinationDefault;
            p[4] = (byte)(offset >> 24);
            p[5] = (byte)(offset >> 16);
            p[6] = (byte)(offset >> 8);
            p[7] = (byte)offset;
            p[8] = (byte)(length >> 8);
            p[9] = (byte)length;
            WriteLeds(rgb, start, k, p.AsSpan(HeaderSize, length));
            SetPacket(count, HeaderSize + length);
        }
        return count;
    }
}
