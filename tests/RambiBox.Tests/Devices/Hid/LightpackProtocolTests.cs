using RambiBox.Core.Config;
using RambiBox.Core.Devices.Hid;

namespace RambiBox.Tests.Devices.Hid;

/// <summary>
/// Golden vectors derived from Prismatik's LedDeviceLightpack.cpp (setColors / setRefreshDelay / setSmoothSlowdown /
/// setColorDepth / switchOffLeds) and the firmware's LightpackUSB.c / COMMANDS.h (github.com/psieg/Lightpack).
/// </summary>
public class LightpackProtocolTests
{
    private static byte[] Report() => new byte[LightpackProtocol.ReportLength];

    [Fact]
    public void Command_ids_match_COMMANDS_h()
    {
        Assert.Equal(1, LightpackProtocol.CmdUpdateLeds);
        Assert.Equal(2, LightpackProtocol.CmdOffAll);
        Assert.Equal(3, LightpackProtocol.CmdSetTimerOptions);
        Assert.Equal(4, LightpackProtocol.CmdSetPwmLevelMaxValue);
        Assert.Equal(5, LightpackProtocol.CmdSetSmoothSlowdown);
        Assert.Equal(6, LightpackProtocol.CmdSetBrightness);
        Assert.Equal(0x0F, LightpackProtocol.CmdNop);
        Assert.Equal(0x81, LightpackProtocol.CmdUnofficialSetUsbLed);
        Assert.Equal(65, LightpackProtocol.ReportLength);
        Assert.Equal(62, LightpackProtocol.MinOutputReportLength);
    }

    [Fact]
    public void Twelve_bit_conversion_matches_prismatik_for_every_value()
    {
        for (int v = 0; v < 256; v++)
            Assert.Equal((int)(v * (4095 / 255.0)), LightpackProtocol.To12Bit((byte)v));
        Assert.Equal(4095, LightpackProtocol.To12Bit(255));
        Assert.Equal(2055, LightpackProtocol.To12Bit(128));
    }

    [Fact]
    public void Update_leds_golden_vector_single_led()
    {
        var r = Report();
        LightpackProtocol.BuildUpdateLeds([255, 128, 0], ColorOrder.RGB, r);
        var expected = Report();
        expected[1] = 0x01;
        // logical LED 0 -> slot kLedRemap[0] = 4 -> offset 2 + 4*6 = 26
        // R 4095 = 0xFFF, G 2055 = 0x807, B 0: high bytes then low nibbles
        byte[] led = [0xFF, 0x80, 0x00, 0x0F, 0x07, 0x00];
        led.CopyTo(expected, 26);
        Assert.Equal(expected, r);
    }

    [Fact]
    public void Update_leds_golden_vector_full_device_uses_prismatik_remap()
    {
        var rgb = new byte[30];
        for (int i = 0; i < 10; i++) { rgb[i * 3] = (byte)(i + 1); rgb[i * 3 + 1] = 17; rgb[i * 3 + 2] = 254; }
        var r = Report();
        LightpackProtocol.BuildUpdateLeds(rgb, ColorOrder.RGB, r);

        int[] remap = [4, 3, 0, 1, 2, 5, 6, 7, 8, 9];
        var expected = Report();
        expected[1] = 1;
        for (int i = 0; i < 10; i++)
        {
            int o = 2 + remap[i] * 6;
            int r12 = (int)((i + 1) * (4095 / 255.0)), g12 = 273 /* 17 */, b12 = 4078 /* 254 */;
            expected[o] = (byte)((r12 & 0xFF0) >> 4);
            expected[o + 1] = (byte)((g12 & 0xFF0) >> 4);
            expected[o + 2] = (byte)((b12 & 0xFF0) >> 4);
            expected[o + 3] = (byte)(r12 & 0xF);
            expected[o + 4] = (byte)(g12 & 0xF);
            expected[o + 5] = (byte)(b12 & 0xF);
        }
        Assert.Equal(expected, r);
        // Spot checks: logical LED 2 lands in slot 0 (bytes 2..7), logical LED 1 in slot 3.
        Assert.Equal(new byte[] { 0x03, 0x11, 0xFE, 0x00, 0x01, 0x0E }, r[2..8]); // 3 -> 48 = 0x030
        Assert.Equal(new byte[] { 0x02, 0x11, 0xFE, 0x00, 0x01, 0x0E }, r[20..26]); // 2 -> 32 = 0x020
        Assert.All(r[62..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Update_leds_applies_color_order_and_pads_missing_leds_black()
    {
        var r = Report();
        LightpackProtocol.BuildUpdateLeds([10, 20, 30], ColorOrder.GRB, r);
        int o = 2 + 4 * 6;
        Assert.Equal(LightpackProtocol.To12Bit(20) >> 4, r[o]);
        Assert.Equal(LightpackProtocol.To12Bit(10) >> 4, r[o + 1]);
        Assert.Equal(LightpackProtocol.To12Bit(30) >> 4, r[o + 2]);
        Assert.All(r.Where((_, i) => i != 1 && (i < o || i >= o + 6)), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Black_frame_is_an_all_zero_update_like_switchOffLeds()
    {
        var r = Enumerable.Repeat((byte)0xAA, 65).ToArray();
        LightpackProtocol.BuildBlack(r);
        var expected = Report();
        expected[1] = 0x01;
        Assert.Equal(expected, r);
    }

    [Fact]
    public void Settings_commands_golden_vectors()
    {
        var r = Report();
        LightpackProtocol.BuildSetTimerOptions(100, r);
        Assert.Equal(new byte[] { 0x00, 0x03, 0x64, 0x00, 0x00 }, r[..5]);
        LightpackProtocol.BuildSetTimerOptions(1023, r);
        Assert.Equal(new byte[] { 0x00, 0x03, 0xFF, 0x03, 0x00 }, r[..5]);

        LightpackProtocol.BuildSetSmoothSlowdown(0, r);
        Assert.Equal(new byte[] { 0x00, 0x05, 0x00, 0x00 }, r[..4]);
        LightpackProtocol.BuildSetSmoothSlowdown(100, r);
        Assert.Equal(new byte[] { 0x00, 0x05, 0x64, 0x00 }, r[..4]);

        LightpackProtocol.BuildSetBrightness(50, r);
        Assert.Equal(new byte[] { 0x00, 0x06, 0x32, 0x00 }, r[..4]);

        LightpackProtocol.BuildSetColorDepth(128, r);
        Assert.Equal(new byte[] { 0x00, 0x04, 0x80, 0x00 }, r[..4]);

        LightpackProtocol.BuildCommand(LightpackProtocol.CmdOffAll, r);
        Assert.Equal(new byte[] { 0x00, 0x02, 0x00 }, r[..3]);
        LightpackProtocol.BuildCommand(LightpackProtocol.CmdNop, r);
        Assert.Equal(new byte[] { 0x00, 0x0F, 0x00 }, r[..3]);
        Assert.All(r[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Firmware_version_is_parsed_from_the_input_report()
    {
        var input = new byte[65];
        input[2] = 6; // payload[INDEX_FW_VER_MAJOR = 1]
        input[3] = 6; // payload[INDEX_FW_VER_MINOR = 2]
        input[6] = 1; // payload[INDEX_FW_VER_UNOFFICIAL = 5]
        Assert.True(LightpackProtocol.TryParseFirmwareVersion(input, out int ma, out int mi, out int un));
        Assert.Equal("6.6+1", LightpackProtocol.FormatFirmware(ma, mi, un));
        Assert.Equal("5.4", LightpackProtocol.FormatFirmware(5, 4, 0));
        Assert.False(LightpackProtocol.TryParseFirmwareVersion(new byte[65], out _, out _, out _));
        Assert.False(LightpackProtocol.TryParseFirmwareVersion(new byte[4], out _, out _, out _));
    }

    [Theory]
    [InlineData(0x1D50, 0x6022, true)]
    [InlineData(0x03EB, 0x204F, true)]
    [InlineData(0x1D50, 0x204F, false)]
    [InlineData(0x03EB, 0x6022, false)]
    [InlineData(0x046D, 0xC52B, false)]
    [InlineData(0x1A86, 0x7523, false)]
    public void Vid_pid_matching(int vid, int pid, bool expected)
        => Assert.Equal(expected, LightpackProtocol.IsLightpackId((ushort)vid, (ushort)pid));

    [Fact]
    public void Lufa_demo_with_old_vid_pid_but_short_report_is_not_usable()
    {
        Assert.True(LightpackProtocol.IsUsable(FakeHidTransport.Lightpack("A", old: true)));
        Assert.False(LightpackProtocol.IsUsable(FakeHidTransport.Lightpack("A", old: true, outLen: 9)));
        Assert.False(LightpackProtocol.IsUsable(FakeHidTransport.Lightpack("A", outLen: 0)));
    }

    [Fact]
    public void Devices_are_ordered_like_prismatik()
    {
        var oldNoSerial = FakeHidTransport.Lightpack(null, path: "p-old-noserial", old: true);
        var oldB = FakeHidTransport.Lightpack("B", path: "p-old-b", old: true);
        var newNoSerial1 = FakeHidTransport.Lightpack(null, path: "p-z-noserial");
        var newNoSerial2 = FakeHidTransport.Lightpack(null, path: "p-a-noserial");
        var newC = FakeHidTransport.Lightpack("C", path: "p-c");
        var newA = FakeHidTransport.Lightpack("A", path: "p-a");
        var ordered = LightpackProtocol.OrderLikePrismatik([oldNoSerial, newNoSerial1, newC, oldB, newA, newNoSerial2]);
        // new VID/PID: serials sorted, then serial-less in enumeration order; then the old VID/PID the same way
        Assert.Equal(["p-a", "p-c", "p-z-noserial", "p-a-noserial", "p-old-b", "p-old-noserial"], ordered.Select(d => d.Path));
    }

    [Fact]
    public void Selection_all_or_explicit_list_with_absent_slots()
    {
        var a = FakeHidTransport.Lightpack("A", path: "pa");
        var b = FakeHidTransport.Lightpack("B", path: "pb");
        var present = new List<HidDeviceInfo> { a, b };
        Assert.Equal([a, b], LightpackProtocol.Select(present, null));
        Assert.Equal([a, b], LightpackProtocol.Select(present, " * "));
        Assert.Equal([b, a], LightpackProtocol.Select(present, "b; A"));
        Assert.Equal([b], LightpackProtocol.Select(present, "pb"));
        Assert.Equal([null, a], LightpackProtocol.Select(present, "X,A"));
    }
}
