using System.Net;
using System.Net.Sockets;
using System.Text;
using RambiBox.Core.Config;
using RambiBox.Core.Devices.Network;

namespace RambiBox.Tests.Devices.Network;

public class WledPacketTests
{
    private static byte[] Frame(int leds, int seed = 0)
    {
        var f = new byte[leds * 3];
        for (int i = 0; i < f.Length; i++) f[i] = (byte)(i * 7 + seed);
        return f;
    }

    [Theory]
    [InlineData(1, NetworkProtocol.Warls)]
    [InlineData(255, NetworkProtocol.Warls)]
    [InlineData(256, NetworkProtocol.Drgb)]
    [InlineData(490, NetworkProtocol.Drgb)]
    [InlineData(491, NetworkProtocol.Dnrgb)]
    [InlineData(5000, NetworkProtocol.Dnrgb)]
    public void Auto_picks_protocol_by_led_count(int leds, NetworkProtocol expected)
    {
        Assert.Equal(expected, WledPacketizer.Choose(NetworkProtocol.Auto, leds));
        Assert.Equal(expected, new WledPacketizer(NetworkProtocol.Auto, leds, 2, ColorOrder.RGB).Protocol);
    }

    [Fact]
    public void Warls_packet_is_protocol_timeout_then_index_rgb()
    {
        var p = new WledPacketizer(NetworkProtocol.Warls, 3, 2, ColorOrder.RGB);
        Assert.Equal(1, p.Build([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Equal(new byte[] { 1, 2, 0, 1, 2, 3, 1, 4, 5, 6, 2, 7, 8, 9 }, p.Packet(0).ToArray());
    }

    [Fact]
    public void Color_order_is_applied_and_short_frames_are_padded_black()
    {
        var p = new WledPacketizer(NetworkProtocol.Warls, 3, 5, ColorOrder.GRB);
        p.Build([1, 2, 3, 4, 5, 6]); // only 2 of 3 LEDs
        Assert.Equal(new byte[] { 1, 5, 0, 2, 1, 3, 1, 5, 4, 6, 2, 0, 0, 0 }, p.Packet(0).ToArray());

        var d = new WledPacketizer(NetworkProtocol.Drgb, 2, 2, ColorOrder.BGR);
        d.Build([1, 2, 3, 4, 5, 6, 99, 99, 99]); // longer frame is truncated
        Assert.Equal(new byte[] { 2, 2, 3, 2, 1, 6, 5, 4 }, d.Packet(0).ToArray());
    }

    [Fact]
    public void Warls_truncates_at_255_leds()
    {
        var p = new WledPacketizer(NetworkProtocol.Warls, 300, 2, ColorOrder.RGB);
        Assert.Equal(255, p.LedCount);
        Assert.True(p.Truncated);
        p.Build(Frame(300));
        var pkt = p.Packet(0);
        Assert.Equal(2 + 4 * 255, pkt.Length);
        Assert.Equal(254, pkt[2 + 4 * 254]);
    }

    [Fact]
    public void Drgb_490_leds_fill_exactly_one_1472_byte_datagram()
    {
        var f = Frame(490);
        var p = new WledPacketizer(NetworkProtocol.Auto, 490, 2, ColorOrder.RGB);
        Assert.Equal(1, p.Build(f));
        var pkt = p.Packet(0).ToArray();
        Assert.Equal(1472, pkt.Length);
        Assert.Equal(2, pkt[0]);
        Assert.Equal(2, pkt[1]);
        Assert.Equal(f, pkt[2..]);

        var t = new WledPacketizer(NetworkProtocol.Drgb, 500, 2, ColorOrder.RGB);
        Assert.Equal(490, t.LedCount);
        Assert.True(t.Truncated);
    }

    [Theory]
    [InlineData(491, new[] { 489, 2 })]
    [InlineData(978, new[] { 489, 489 })]
    [InlineData(979, new[] { 489, 489, 1 })]
    [InlineData(10, new[] { 10 })]
    public void Dnrgb_splits_frames_in_chunks_of_489_with_16_bit_start_index(int leds, int[] chunks)
    {
        var f = Frame(leds, 3);
        var p = new WledPacketizer(NetworkProtocol.Dnrgb, leds, 7, ColorOrder.RGB);
        Assert.Equal(chunks.Length, p.Build(f));
        int start = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            var pkt = p.Packet(i).ToArray();
            Assert.Equal(4 + 3 * chunks[i], pkt.Length);
            Assert.Equal(4, pkt[0]);
            Assert.Equal(7, pkt[1]);
            Assert.Equal(start >> 8, pkt[2]);
            Assert.Equal(start & 0xFF, pkt[3]);
            Assert.Equal(f.AsSpan(start * 3, chunks[i] * 3).ToArray(), pkt[4..]);
            start += chunks[i];
        }
    }

    [Fact]
    public void Dnrgb_second_packet_header_bytes()
    {
        var p = new WledPacketizer(NetworkProtocol.Auto, 600, 2, ColorOrder.RGB);
        p.Build(Frame(600));
        Assert.Equal(new byte[] { 4, 2, 0x01, 0xE9 }, p.Packet(1)[..4].ToArray()); // 489 = 0x01E9
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(255, 255)]
    [InlineData(1000, 255)]
    public void Timeout_byte_is_clamped(int setting, byte expected)
    {
        var p = new WledPacketizer(NetworkProtocol.Drgb, 1, setting, ColorOrder.RGB);
        p.Build([9, 9, 9]);
        Assert.Equal(expected, p.Packet(0)[1]);
    }

    [Fact]
    public void Blackout_sends_black_twice_with_timeout_1_so_wled_resumes_its_own_effect()
    {
        var p = new WledPacketizer(NetworkProtocol.Dnrgb, 500, 255, ColorOrder.RGB);
        for (int pass = 0; pass < 2; pass++)
        {
            Assert.Equal(2, p.BuildBlackout(pass));
            for (int i = 0; i < 2; i++)
            {
                var pkt = p.Packet(i);
                Assert.Equal(4, pkt[0]);
                Assert.Equal(1, pkt[1]);
                Assert.All(pkt[4..].ToArray(), b => Assert.Equal(0, b));
            }
        }
        Assert.Equal(0, p.BuildBlackout(2));
    }
}

public class DdpPacketTests
{
    [Fact]
    public void Single_packet_frame_has_push_flag_and_header()
    {
        var p = new DdpPacketizer(2, ColorOrder.RGB);
        Assert.Equal(1, p.Build([1, 2, 3, 4, 5, 6]));
        Assert.Equal(new byte[] { 0x41, 1, 0x0B, 1, 0, 0, 0, 0, 0, 6, 1, 2, 3, 4, 5, 6 }, p.Packet(0).ToArray());
    }

    [Fact]
    public void Large_frames_split_at_1440_bytes_with_offsets_and_push_only_on_last()
    {
        int leds = 1000; // 3000 bytes → 1440 + 1440 + 120
        var f = new byte[leds * 3];
        for (int i = 0; i < f.Length; i++) f[i] = (byte)i;
        var p = new DdpPacketizer(leds, ColorOrder.RGB);
        Assert.Equal(3, p.Build(f));
        int[] offsets = [0, 1440, 2880];
        int[] lengths = [1440, 1440, 120];
        for (int i = 0; i < 3; i++)
        {
            var pkt = p.Packet(i).ToArray();
            Assert.Equal(10 + lengths[i], pkt.Length);
            Assert.Equal(i == 2 ? 0x41 : 0x40, pkt[0]);
            Assert.Equal(i + 1, pkt[1]);
            Assert.Equal(0x0B, pkt[2]);
            Assert.Equal(1, pkt[3]);
            Assert.Equal(offsets[i], (pkt[4] << 24) | (pkt[5] << 16) | (pkt[6] << 8) | pkt[7]);
            Assert.Equal(lengths[i], (pkt[8] << 8) | pkt[9]);
            Assert.Equal(f.AsSpan(offsets[i], lengths[i]).ToArray(), pkt[10..]);
        }
    }

    [Fact]
    public void Sequence_number_cycles_1_to_15()
    {
        var p = new DdpPacketizer(1, ColorOrder.RGB);
        var seen = new List<int>();
        for (int i = 0; i < 17; i++)
        {
            p.Build([1, 1, 1]);
            seen.Add(p.Packet(0)[1]);
        }
        Assert.Equal(Enumerable.Range(1, 15).Concat([1, 2]), seen);
    }

    [Fact]
    public void Exactly_480_leds_fit_one_packet()
    {
        var p = new DdpPacketizer(480, ColorOrder.RGB);
        Assert.Equal(1, p.Build(new byte[1440]));
        Assert.Equal(1450, p.Packet(0).Length);
        Assert.Equal(2, new DdpPacketizer(481, ColorOrder.RGB).Build(new byte[1443]));
    }
}

public class E131PacketTests
{
    private static readonly Guid TestCid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    private static int U16(byte[] p, int o) => (p[o] << 8) | p[o + 1];
    private static uint U32(byte[] p, int o) => (uint)((p[o] << 24) | (p[o + 1] << 16) | (p[o + 2] << 8) | p[o + 3]);

    [Fact]
    public void Single_universe_packet_layout_matches_E131_2018()
    {
        var p = new E131Packetizer(2, 7, ColorOrder.RGB, TestCid);
        Assert.Equal(1, p.Build([10, 20, 30, 40, 50, 60]));
        var pkt = p.Packet(0).ToArray();
        int len = 126 + 6;
        Assert.Equal(len, pkt.Length);
        // Root layer
        Assert.Equal(0x0010, U16(pkt, 0));
        Assert.Equal(0x0000, U16(pkt, 2));
        Assert.Equal("ASC-E1.17\0\0\0"u8.ToArray(), pkt[4..16]);
        Assert.Equal(0x7000 | (len - 16), U16(pkt, 16));
        Assert.Equal(0x00000004u, U32(pkt, 18));
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef }, pkt[22..38]);
        // Framing layer
        Assert.Equal(0x7000 | (len - 38), U16(pkt, 38));
        Assert.Equal(0x00000002u, U32(pkt, 40));
        Assert.Equal("RambiBox", Encoding.UTF8.GetString(pkt, 44, 8));
        Assert.All(pkt[52..108], b => Assert.Equal(0, b));
        Assert.Equal(100, pkt[108]);
        Assert.Equal(0, U16(pkt, 109)); // sync address
        Assert.Equal(0, pkt[111]); // sequence
        Assert.Equal(0, pkt[112]); // options
        Assert.Equal(7, U16(pkt, 113)); // universe
        // DMP layer
        Assert.Equal(0x7000 | (len - 115), U16(pkt, 115));
        Assert.Equal(0x02, pkt[117]);
        Assert.Equal(0xA1, pkt[118]);
        Assert.Equal(0x0000, U16(pkt, 119));
        Assert.Equal(0x0001, U16(pkt, 121));
        Assert.Equal(7, U16(pkt, 123)); // 1 start code + 6 slots
        Assert.Equal(0, pkt[125]);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, pkt[126..]);
    }

    [Fact]
    public void Leds_are_split_170_per_universe_with_per_universe_sequence_numbers()
    {
        var p = new E131Packetizer(171, 1, ColorOrder.RGB, TestCid);
        Assert.Equal(2, p.UniverseCount);
        var f = new byte[171 * 3];
        f[510] = 1; f[511] = 2; f[512] = 3; // LED 170 = first LED of universe 2
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal(2, p.Build(f));
            var a = p.Packet(0).ToArray();
            var b = p.Packet(1).ToArray();
            Assert.Equal(636, a.Length);
            Assert.Equal(129, b.Length);
            Assert.Equal(0x726C, U16(a, 16)); // 0x7000 | 620
            Assert.Equal(1, U16(a, 113));
            Assert.Equal(2, U16(b, 113));
            Assert.Equal(511, U16(a, 123));
            Assert.Equal(4, U16(b, 123));
            Assert.Equal(frame, a[111]);
            Assert.Equal(frame, b[111]);
            Assert.Equal(new byte[] { 1, 2, 3 }, b[126..]);
            Assert.Equal(0, p.Destination(0));
            Assert.Equal(1, p.Destination(1));
        }
    }

    [Fact]
    public void Sequence_wraps_after_255()
    {
        var p = new E131Packetizer(1, 1, ColorOrder.RGB, TestCid);
        for (int i = 0; i < 256; i++) p.Build([0, 0, 0]);
        p.Build([0, 0, 0]);
        Assert.Equal(0, p.Packet(0)[111]);
    }

    [Fact]
    public void Blackout_is_black_twice_then_three_stream_terminated_packets()
    {
        var p = new E131Packetizer(200, 1, ColorOrder.RGB, TestCid);
        p.Build(Enumerable.Repeat((byte)255, 600).ToArray());
        var options = new List<byte>();
        for (int pass = 0; ; pass++)
        {
            int n = p.BuildBlackout(pass);
            if (n == 0) break;
            Assert.Equal(2, n);
            for (int i = 0; i < n; i++)
            {
                var pkt = p.Packet(i).ToArray();
                Assert.All(pkt[126..], b => Assert.Equal(0, b));
            }
            options.Add(p.Packet(0)[112]);
        }
        Assert.Equal(new byte[] { 0, 0, 0x40, 0x40, 0x40 }, options);
    }

    [Theory]
    [InlineData(1, "239.255.0.1")]
    [InlineData(256, "239.255.1.0")]
    [InlineData(63999, "239.255.249.255")]
    public void Multicast_address_encodes_universe(int universe, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), E131Packetizer.MulticastAddress(universe));

    [Fact]
    public void Universes_never_exceed_63999()
    {
        var p = new E131Packetizer(1000, 63998, ColorOrder.RGB, TestCid);
        Assert.Equal(2, p.UniverseCount);
        Assert.Equal(340, p.LedCount);
        Assert.True(p.Truncated);
    }

    [Fact]
    public void Machine_cid_is_stable_rfc4122_version_5()
    {
        var a = E131Packetizer.MachineCid();
        Assert.Equal(a, E131Packetizer.MachineCid());
        Assert.NotEqual(a, E131Packetizer.NameBasedCid("another pc"));
        var bytes = a.ToByteArray(bigEndian: true);
        Assert.Equal(0x50, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}

public class NetworkAllocationTests
{
    [Fact]
    public void Encoding_and_sending_frames_allocates_nothing_in_steady_state()
    {
        var frame = new byte[600 * 3];
        var pks = new UdpPacketizer[]
        {
            new WledPacketizer(NetworkProtocol.Auto, 200, 2, ColorOrder.GRB),
            new WledPacketizer(NetworkProtocol.Auto, 600, 2, ColorOrder.RGB),
            new DdpPacketizer(600, ColorOrder.BGR),
            new E131Packetizer(600, 1, ColorOrder.RGB, Guid.NewGuid()),
        };
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var dest = ((IPEndPoint)receiver.LocalEndPoint!).Serialize();

        void Run()
        {
            foreach (var p in pks)
            {
                int n = p.Build(frame);
                for (int i = 0; i < n; i++) sender.SendTo(p.Packet(i), SocketFlags.None, dest);
            }
        }

        for (int i = 0; i < 5; i++) Run(); // warm-up (JIT, socket internals)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
