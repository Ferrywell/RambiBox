using System.Net;
using System.Security.Cryptography;
using System.Text;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// E1.31 / sACN (ANSI E1.31-2018 "Streaming ACN"), UDP port 5568. One data packet per universe of up to 512 DMX
/// slots; RambiBox puts 170 RGB LEDs (510 slots) in each universe, starting at the configured universe.
/// <code>
/// Root layer      0  preamble size 0x0010        2  post-amble size 0x0000
///                 4  ACN packet identifier "ASC-E1.17\0\0\0"
///                16  flags+length 0x7000 | (len - 16)      18  vector VECTOR_ROOT_E131_DATA 0x00000004
///                22  CID (16 bytes, sender's UUID)
/// Framing layer  38  flags+length 0x7000 | (len - 38)      40  vector VECTOR_E131_DATA_PACKET 0x00000002
///                44  source name (64 bytes UTF-8, NUL-terminated)
///               108  priority (100)    109 sync address (0 = none)    111 sequence number (per universe)
///               112  options (0x40 = Stream_Terminated, 0x80 = preview, 0x20 = force sync)
///               113  universe (1..63999)
/// DMP layer     115  flags+length 0x7000 | (len - 115)     117 vector VECTOR_DMP_SET_PROPERTY 0x02
///               118  address type & data type 0xA1     119 first property address 0x0000
///               121  address increment 0x0001          123 property value count (1 + slots)
///               125  DMX start code 0x00               126 slot data (≤ 512)
/// </code>
/// Multicast destination of universe U: 239.255.{U high byte}.{U low byte}. On stop the source sends black, then three
/// packets per universe with Stream_Terminated set (E1.31 §6.2.6) so receivers release the universe immediately instead
/// of waiting for the 2.5 s network data loss timeout.
/// </summary>
internal sealed class E131Packetizer : UdpPacketizer
{
    public const int DefaultPort = 5568;
    public const int LedsPerUniverse = 170;
    public const int HeaderSize = 126;
    public const int MinUniverse = 1, MaxUniverse = 63999;
    public const byte DefaultPriority = 100;
    public const byte OptionStreamTerminated = 0x40;
    public const string DefaultSourceName = "RambiBox";

    private static readonly byte[] AcnPacketIdentifier = "ASC-E1.17\0\0\0"u8.ToArray();

    private readonly byte[] _sequence;

    public E131Packetizer(int ledCount, int firstUniverse, ColorOrder order, Guid cid, string sourceName = DefaultSourceName,
        byte priority = DefaultPriority)
        : base(Leds(ledCount, firstUniverse), order, Universes(Leds(ledCount, firstUniverse)), HeaderSize + 512)
    {
        FirstUniverse = Math.Clamp(firstUniverse, MinUniverse, MaxUniverse);
        RequestedLeds = Math.Max(1, ledCount);
        UniverseCount = Universes(LedCount);
        _sequence = new byte[UniverseCount];
        Cid = cid;
        Span<byte> cidBytes = stackalloc byte[16];
        cid.TryWriteBytes(cidBytes, bigEndian: true, out _);
        Span<byte> name = stackalloc byte[64];
        name.Clear();
        var nameBytes = Encoding.UTF8.GetBytes(sourceName ?? DefaultSourceName);
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 63)).CopyTo(name); // byte 63 stays NUL

        for (int u = 0; u < UniverseCount; u++)
        {
            int leds = Math.Min(LedsPerUniverse, LedCount - u * LedsPerUniverse);
            int slots = leds * 3;
            int len = HeaderSize + slots;
            int universe = FirstUniverse + u;
            var p = Buffer(u);
            // Root layer
            p[0] = 0x00; p[1] = 0x10;
            p[2] = 0x00; p[3] = 0x00;
            AcnPacketIdentifier.CopyTo(p, 4);
            WriteFlagsLength(p, 16, len - 16);
            WriteU32(p, 18, 0x00000004);
            cidBytes.CopyTo(p.AsSpan(22));
            // Framing layer
            WriteFlagsLength(p, 38, len - 38);
            WriteU32(p, 40, 0x00000002);
            name.CopyTo(p.AsSpan(44, 64));
            p[108] = priority;
            p[109] = 0; p[110] = 0;
            p[111] = 0;
            p[112] = 0;
            p[113] = (byte)(universe >> 8); p[114] = (byte)universe;
            // DMP layer
            WriteFlagsLength(p, 115, len - 115);
            p[117] = 0x02;
            p[118] = 0xA1;
            p[119] = 0x00; p[120] = 0x00;
            p[121] = 0x00; p[122] = 0x01;
            p[123] = (byte)((slots + 1) >> 8); p[124] = (byte)(slots + 1);
            p[125] = 0x00;
            SetPacket(u, len, u);
        }
    }

    public int FirstUniverse { get; }
    public int UniverseCount { get; }
    public int RequestedLeds { get; }
    public bool Truncated => RequestedLeds > LedCount;
    public Guid Cid { get; }

    /// <summary>LEDs that fit between the first universe and universe 63999.</summary>
    private static int Leds(int ledCount, int firstUniverse)
    {
        int first = Math.Clamp(firstUniverse, MinUniverse, MaxUniverse);
        long max = (long)(MaxUniverse - first + 1) * LedsPerUniverse;
        return (int)Math.Clamp(ledCount, 1, Math.Min(max, 1_000_000));
    }

    private static int Universes(int leds) => (leds + LedsPerUniverse - 1) / LedsPerUniverse;

    protected override int Encode(ReadOnlySpan<byte> rgb, bool final, int pass) => Fill(rgb, options: 0);

    /// <summary>Black twice, then Stream_Terminated three times (E1.31 §6.2.6).</summary>
    protected override int BlackoutPass(int pass) => pass switch
    {
        < 2 => Fill(ReadOnlySpan<byte>.Empty, options: 0),
        < 5 => Fill(ReadOnlySpan<byte>.Empty, OptionStreamTerminated),
        _ => 0,
    };

    private int Fill(ReadOnlySpan<byte> rgb, byte options)
    {
        for (int u = 0; u < UniverseCount; u++)
        {
            var p = Buffer(u);
            int first = u * LedsPerUniverse;
            int leds = Math.Min(LedsPerUniverse, LedCount - first);
            p[111] = _sequence[u]++;
            p[112] = options;
            WriteLeds(rgb, first, leds, p.AsSpan(HeaderSize, leds * 3));
        }
        return UniverseCount;
    }

    /// <summary>239.255.{hi}.{lo} for universe <paramref name="universe"/>.</summary>
    public static IPAddress MulticastAddress(int universe)
    {
        int u = Math.Clamp(universe, MinUniverse, MaxUniverse);
        return new IPAddress(new byte[] { 239, 255, (byte)(u >> 8), (byte)u });
    }

    /// <summary>
    /// Stable per-PC component identifier: a name-based (version 5 style, SHA-1) UUID of the machine name, so receivers
    /// that track sources by CID see the same source across restarts without a settings field.
    /// </summary>
    public static Guid MachineCid() => NameBasedCid("RambiBox E1.31 source/" + Environment.MachineName.ToUpperInvariant());

    internal static Guid NameBasedCid(string name)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(name), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash[..16], bigEndian: true);
    }

    private static void WriteFlagsLength(byte[] p, int offset, int length)
    {
        p[offset] = (byte)(0x70 | ((length >> 8) & 0x0F));
        p[offset + 1] = (byte)length;
    }

    private static void WriteU32(byte[] p, int offset, uint v)
    {
        p[offset] = (byte)(v >> 24);
        p[offset + 1] = (byte)(v >> 16);
        p[offset + 2] = (byte)(v >> 8);
        p[offset + 3] = (byte)v;
    }
}
