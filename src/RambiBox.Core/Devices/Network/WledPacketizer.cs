using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// WLED UDP realtime protocols, byte-compatible with Prismatik's "WARLS", "DRGB" and "DNRGB" devices
/// (psieg/Lightpack Software/src/LedDeviceWarls.cpp, LedDeviceDrgb.cpp, LedDeviceDnrgb.cpp; protocol ids and limits in
/// enums.hpp) and the WLED documentation (https://kno.wled.ge/interfaces/udp-realtime/):
/// <code>
/// byte 0      protocol: 1 = WARLS, 2 = DRGB, 4 = DNRGB
/// byte 1      timeout: seconds WLED keeps showing realtime data after the last packet before it returns to its own
///             state/effect (255 = stay in realtime mode until something else ends it)
/// WARLS       [index, R, G, B] × n          — max 255 LEDs (1-byte index)
/// DRGB        [R, G, B] × n                 — max 490 LEDs (2 + 1470 = 1472 bytes = one unfragmented datagram)
/// DNRGB       start index hi, lo, [R, G, B] × n — max 489 LEDs per datagram, several datagrams per frame
/// </code>
/// Unlike Prismatik (which sends only the LEDs that changed), every datagram carries a complete range: UDP may drop
/// packets, and a full frame (the engine's 1 s keep-alive) then repairs every LED.
/// </summary>
internal sealed class WledPacketizer : UdpPacketizer
{
    public const int DefaultPort = 21324;
    public const byte ProtocolWarls = 1, ProtocolDrgb = 2, ProtocolDnrgb = 4;
    public const int MaxWarlsLeds = 255, MaxDrgbLeds = 490, DnrgbLedsPerPacket = 489;
    /// <summary>DNRGB start index is 16-bit.</summary>
    public const int MaxLeds = ushort.MaxValue + DnrgbLedsPerPacket;

    private readonly byte _timeout;

    public WledPacketizer(NetworkProtocol requested, int ledCount, int timeoutSeconds, ColorOrder order)
        : base(Limit(Choose(requested, ledCount), ledCount), order,
            PacketsFor(Choose(requested, ledCount), Limit(Choose(requested, ledCount), ledCount)),
            MaxPacketSize(Choose(requested, ledCount)))
    {
        Protocol = Choose(requested, ledCount);
        RequestedLeds = Math.Max(1, ledCount);
        _timeout = (byte)Math.Clamp(timeoutSeconds, 1, 255);
    }

    /// <summary>The protocol actually used (never Auto).</summary>
    public NetworkProtocol Protocol { get; }

    /// <summary>LED count asked for; more than <see cref="UdpPacketizer.LedCount"/> when the protocol had to truncate.</summary>
    public int RequestedLeds { get; }

    public bool Truncated => RequestedLeds > LedCount;

    /// <summary>Auto: WARLS up to 255 LEDs, DRGB up to 490, DNRGB above (like picking the matching Prismatik device).</summary>
    public static NetworkProtocol Choose(NetworkProtocol requested, int ledCount) => requested switch
    {
        NetworkProtocol.Warls or NetworkProtocol.Drgb or NetworkProtocol.Dnrgb => requested,
        _ => ledCount <= MaxWarlsLeds ? NetworkProtocol.Warls : ledCount <= MaxDrgbLeds ? NetworkProtocol.Drgb : NetworkProtocol.Dnrgb,
    };

    private static int Limit(NetworkProtocol p, int leds) => Math.Clamp(leds, 1, p switch
    {
        NetworkProtocol.Warls => MaxWarlsLeds,
        NetworkProtocol.Drgb => MaxDrgbLeds,
        _ => MaxLeds,
    });

    private static int PacketsFor(NetworkProtocol p, int leds) =>
        p == NetworkProtocol.Dnrgb ? (leds + DnrgbLedsPerPacket - 1) / DnrgbLedsPerPacket : 1;

    private static int MaxPacketSize(NetworkProtocol p) => p switch
    {
        NetworkProtocol.Warls => 2 + 4 * MaxWarlsLeds,
        NetworkProtocol.Drgb => 2 + 3 * MaxDrgbLeds,
        _ => 4 + 3 * DnrgbLedsPerPacket,
    };

    /// <param name="final">Stop sequence: timeout 1 s, so WLED leaves realtime mode right after the black frame and
    /// restores its own state (on/off, effect, brightness) instead of keeping the strip black for the configured timeout.</param>
    protected override int Encode(ReadOnlySpan<byte> rgb, bool final, int pass)
    {
        byte timeout = final ? (byte)1 : _timeout;
        int n = LedCount;
        switch (Protocol)
        {
            case NetworkProtocol.Warls:
            {
                var p = Buffer(0);
                p[0] = ProtocolWarls;
                p[1] = timeout;
                for (int i = 0, o = 2; i < n; i++, o += 4)
                {
                    p[o] = (byte)i;
                    WriteLed(rgb, i, p.AsSpan(o + 1, 3));
                }
                SetPacket(0, 2 + 4 * n);
                return 1;
            }
            case NetworkProtocol.Drgb:
            {
                var p = Buffer(0);
                p[0] = ProtocolDrgb;
                p[1] = timeout;
                WriteLeds(rgb, 0, n, p.AsSpan(2, 3 * n));
                SetPacket(0, 2 + 3 * n);
                return 1;
            }
            default:
            {
                int count = 0;
                for (int start = 0; start < n; start += DnrgbLedsPerPacket, count++)
                {
                    int k = Math.Min(DnrgbLedsPerPacket, n - start);
                    var p = Buffer(count);
                    p[0] = ProtocolDnrgb;
                    p[1] = timeout;
                    p[2] = (byte)(start >> 8);
                    p[3] = (byte)start;
                    WriteLeds(rgb, start, k, p.AsSpan(4, 3 * k));
                    SetPacket(count, 4 + 3 * k);
                }
                return count;
            }
        }
    }
}
