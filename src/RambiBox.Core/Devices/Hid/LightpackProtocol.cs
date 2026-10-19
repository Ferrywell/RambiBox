using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Hid;

/// <summary>
/// The Lightpack USB HID protocol as implemented by the Lightpack firmware and driven by Prismatik
/// (github.com/psieg/Lightpack: CommonHeaders/COMMANDS.h, CommonHeaders/USB_ID.h, Firmware/LightpackUSB.c,
/// Software/src/LedDeviceLightpack.cpp).
/// <para>
/// Every message is one output report of 64 bytes plus the report id byte (0): <c>[0]</c> = report id 0,
/// <c>[1]</c> = command, <c>[2..]</c> = command data. The device has no interrupt OUT endpoint, so Windows delivers
/// output reports with a SET_REPORT control transfer.
/// </para>
/// <para>
/// <see cref="CmdUpdateLeds"/> carries 10 LEDs × 6 bytes: the upper 8 bits of the 12-bit R, G, B value, then the
/// lower 4 bits of R, G, B (hardware revision ≥ 6 uses all 12 bits; older boards use the first three bytes only).
/// The LED slots are wired out of order; Prismatik maps logical LED i to slot <see cref="LedRemap"/>[i % 10].
/// </para>
/// </summary>
public static class LightpackProtocol
{
    /// <summary>Openmoko VID/PID used by current firmware.</summary>
    public const ushort VendorId = 0x1D50, ProductId = 0x6022;
    /// <summary>Atmel LUFA GenericHID VID/PID used by early Lightpacks.</summary>
    public const ushort OldVendorId = 0x03EB, OldProductId = 0x204F;

    /// <summary>Report id byte + 64 data bytes.</summary>
    public const int ReportLength = 65;
    public const int LedsPerDevice = 10;
    public const int BytesPerLed = 6;
    public const int IndexReportId = 0, IndexCommand = 1, IndexData = 2;
    /// <summary>Smallest output report that can carry a full LED update (report id + command + 60 bytes).</summary>
    public const int MinOutputReportLength = IndexData + LedsPerDevice * BytesPerLed;

    // enum COMMANDS (CommonHeaders/COMMANDS.h)
    public const byte CmdUpdateLeds = 1;
    public const byte CmdOffAll = 2;
    public const byte CmdSetTimerOptions = 3;
    /// <summary>"CMD_SET_PWM_LEVEL_MAX_VALUE" (deprecated, Prismatik's "color depth"; used by hardware revision 4/5).</summary>
    public const byte CmdSetPwmLevelMaxValue = 4;
    public const byte CmdSetSmoothSlowdown = 5;
    public const byte CmdSetBrightness = 6;
    public const byte CmdNop = 0x0F;
    /// <summary>Unofficial firmware only: switch the blue USB power LED.</summary>
    public const byte CmdUnofficialSetUsbLed = 0x81;

    /// <summary>Prismatik/firmware default timer compare value ("refresh delay", 64..1023).</summary>
    public const int DefaultRefreshDelay = 100;
    /// <summary>Prismatik/firmware default PWM levels ("color depth", 32..255).</summary>
    public const byte DefaultColorDepth = 128;

    // DATA_VERSION_INDEXES (COMMANDS.h) are payload indexes; the Windows input report has the report id in front.
    private const int InputIndexFwMajor = 1 + 1, InputIndexFwMinor = 1 + 2, InputIndexFwUnofficial = 1 + 5;

    /// <summary>Physical slot of logical LED i (Prismatik kLedRemap).</summary>
    public static ReadOnlySpan<byte> LedRemap => [4, 3, 0, 1, 2, 5, 6, 7, 8, 9];

    public static bool IsLightpackId(ushort vendorId, ushort productId)
        => (vendorId == VendorId && productId == ProductId) || (vendorId == OldVendorId && productId == OldProductId);

    /// <summary>8-bit → 12-bit exactly like Prismatik (<c>(int)(v * 4095 / 255.0)</c>, truncated).</summary>
    public static int To12Bit(byte v) => v * 4095 / 255;

    /// <summary>
    /// Builds a <see cref="CmdUpdateLeds"/> report for one device. <paramref name="rgb"/> holds up to 10 logical LEDs
    /// (R,G,B); missing LEDs are black. <paramref name="report"/> must hold at least <see cref="MinOutputReportLength"/> bytes.
    /// </summary>
    public static void BuildUpdateLeds(ReadOnlySpan<byte> rgb, ColorOrder order, Span<byte> report)
    {
        report.Clear();
        report[IndexReportId] = 0;
        report[IndexCommand] = CmdUpdateLeds;
        var (a, b, c) = Permutation(order);
        int leds = Math.Min(rgb.Length / 3, LedsPerDevice);
        var remap = LedRemap;
        for (int i = 0; i < leds; i++)
        {
            int r = To12Bit(rgb[i * 3 + a]), g = To12Bit(rgb[i * 3 + b]), bl = To12Bit(rgb[i * 3 + c]);
            int o = IndexData + remap[i] * BytesPerLed;
            report[o] = (byte)((r & 0x0FF0) >> 4);
            report[o + 1] = (byte)((g & 0x0FF0) >> 4);
            report[o + 2] = (byte)((bl & 0x0FF0) >> 4);
            report[o + 3] = (byte)(r & 0x000F);
            report[o + 4] = (byte)(g & 0x000F);
            report[o + 5] = (byte)(bl & 0x000F);
        }
    }

    /// <summary>All-black <see cref="CmdUpdateLeds"/> (what Prismatik's switchOffLeds sends).</summary>
    public static void BuildBlack(Span<byte> report) => BuildCommand(CmdUpdateLeds, report);

    /// <summary>A command without data (<see cref="CmdOffAll"/>, <see cref="CmdNop"/>).</summary>
    public static void BuildCommand(byte command, Span<byte> report)
    {
        report.Clear();
        report[IndexCommand] = command;
    }

    /// <summary>CMD_SET_TIMER_OPTIONS: 16-bit little-endian timer compare value (Prismatik "refresh delay").</summary>
    public static void BuildSetTimerOptions(int refreshDelay, Span<byte> report)
    {
        BuildCommand(CmdSetTimerOptions, report);
        report[IndexData] = (byte)(refreshDelay & 0xFF);
        report[IndexData + 1] = (byte)((refreshDelay >> 8) & 0xFF);
    }

    /// <summary>CMD_SET_SMOOTH_SLOWDOWN: number of firmware interpolation steps; 0 disables firmware smoothing.</summary>
    public static void BuildSetSmoothSlowdown(byte steps, Span<byte> report) => BuildByteCommand(CmdSetSmoothSlowdown, steps, report);

    /// <summary>CMD_SET_BRIGHTNESS (hardware revision 4/5 PWM off-time; Prismatik does brightness in software).</summary>
    public static void BuildSetBrightness(byte brightness, Span<byte> report) => BuildByteCommand(CmdSetBrightness, brightness, report);

    /// <summary>CMD_SET_PWM_LEVEL_MAX_VALUE (Prismatik "color depth").</summary>
    public static void BuildSetColorDepth(byte levels, Span<byte> report) => BuildByteCommand(CmdSetPwmLevelMaxValue, levels, report);

    private static void BuildByteCommand(byte command, byte value, Span<byte> report)
    {
        BuildCommand(command, report);
        report[IndexData] = value;
    }

    /// <summary>
    /// Parses the firmware version from an input report read with GET_REPORT (byte 0 = report id).
    /// The major number is the hardware revision the firmware was built for (4, 5, 6 or 7).
    /// </summary>
    public static bool TryParseFirmwareVersion(ReadOnlySpan<byte> inputReport, out int major, out int minor, out int unofficial)
    {
        major = minor = unofficial = 0;
        if (inputReport.Length <= InputIndexFwUnofficial) return false;
        major = inputReport[InputIndexFwMajor];
        minor = inputReport[InputIndexFwMinor];
        unofficial = inputReport[InputIndexFwUnofficial];
        return major != 0;
    }

    public static string FormatFirmware(int major, int minor, int unofficial)
        => unofficial > 0 ? $"{major}.{minor}+{unofficial}" : $"{major}.{minor}";

    /// <summary>
    /// Device order for chained Lightpacks, as Prismatik opens them: current VID/PID first, then the old one; within each,
    /// devices with a serial number sorted by serial (ordinal), then devices without one in enumeration order.
    /// (Prismatik keeps only one of two devices with the same serial; RambiBox keeps both, ordered by path.)
    /// </summary>
    internal static List<HidDeviceInfo> OrderLikePrismatik(IEnumerable<HidDeviceInfo> devices)
    {
        var list = devices.Select((d, i) => (d, i)).ToList();
        return list
            .OrderBy(x => x.d.VendorId == VendorId && x.d.ProductId == ProductId ? 0 : 1)
            .ThenBy(x => string.IsNullOrEmpty(x.d.SerialNumber) ? 1 : 0)
            .ThenBy(x => string.IsNullOrEmpty(x.d.SerialNumber) ? "" : x.d.SerialNumber, StringComparer.Ordinal)
            .ThenBy(x => string.IsNullOrEmpty(x.d.SerialNumber) ? x.i : 0)
            .ThenBy(x => x.d.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.d)
            .ToList();
    }

    /// <summary>
    /// True for a HID interface that can be driven as a Lightpack: a known VID/PID and an output report long enough for
    /// a full LED update (this also rejects other LUFA GenericHID demos that share the old Atmel VID/PID).
    /// </summary>
    internal static bool IsUsable(HidDeviceInfo d)
        => IsLightpackId(d.VendorId, d.ProductId) && d.OutputReportLength >= MinOutputReportLength;

    /// <summary>
    /// Selects the devices to drive, one slot per 10 LEDs. <paramref name="deviceId"/> null/empty/"*" = every present
    /// Lightpack in <paramref name="ordered"/> order (Prismatik behaviour). Otherwise a list of serial numbers or interface
    /// paths separated by ',' or ';': slot i is the device with the i-th id, or null while that device is absent — its
    /// LEDs are skipped, so the other devices keep their part of the strip.
    /// </summary>
    internal static List<HidDeviceInfo?> Select(IReadOnlyList<HidDeviceInfo> ordered, string? deviceId)
    {
        var result = new List<HidDeviceInfo?>();
        if (IsAll(deviceId))
        {
            result.AddRange(ordered);
            return result;
        }
        foreach (var raw in SplitIds(deviceId))
        {
            var match = ordered.FirstOrDefault(d => !result.Contains(d) &&
                (string.Equals(d.SerialNumber, raw, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(d.Path, raw, StringComparison.OrdinalIgnoreCase)));
            result.Add(match);
        }
        return result;
    }

    internal static bool IsAll(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) || deviceId.Trim() == "*";

    internal static string[] SplitIds(string? deviceId)
        => IsAll(deviceId) ? [] : deviceId!.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Source channel offsets (R,G,B positions within a logical triplet) for each wire channel.</summary>
    internal static (int, int, int) Permutation(ColorOrder order) => order switch
    {
        ColorOrder.RBG => (0, 2, 1),
        ColorOrder.GRB => (1, 0, 2),
        ColorOrder.GBR => (1, 2, 0),
        ColorOrder.BRG => (2, 0, 1),
        ColorOrder.BGR => (2, 1, 0),
        _ => (0, 1, 2),
    };
}
