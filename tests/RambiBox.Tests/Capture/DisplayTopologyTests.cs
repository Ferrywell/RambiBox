using System.Runtime.CompilerServices;
using RambiBox.Core.Capture;

namespace RambiBox.Tests.Capture;

public class DisplayTopologyTests
{
    private static DisplayTarget T(string id, string edid, bool primary, int x = 0) =>
        new(new MonitorInfo(id, id, @"\\.\DISPLAY1", x, 0, 1920, 1080, primary, false, 60), default, 0, 1, edid);

    [Fact]
    public void Interop_struct_sizes_match_the_windows_sdk()
    {
        Assert.Equal(72, Unsafe.SizeOf<DisplayConfigNative.PATH_INFO>());
        Assert.Equal(64, Unsafe.SizeOf<DisplayConfigNative.MODE_INFO>());
        Assert.Equal(20, Unsafe.SizeOf<DisplayConfigNative.DEVICE_INFO_HEADER>());
        Assert.Equal(84, Unsafe.SizeOf<DisplayConfigNative.SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Unsafe.SizeOf<DisplayConfigNative.TARGET_DEVICE_NAME>());
        Assert.Equal(32, Unsafe.SizeOf<DisplayConfigNative.ADVANCED_COLOR_INFO>());
        Assert.Equal(24, Unsafe.SizeOf<DisplayConfigNative.SDR_WHITE_LEVEL>());
        Assert.Equal(48, Unsafe.SizeOf<NativeCom.OUTDUPL_FRAME_INFO>());
        Assert.Equal(44, Unsafe.SizeOf<NativeCom.TEXTURE2D_DESC>());
    }

    [Fact]
    public void Edid_manufacturer_is_decoded_from_big_endian_bytes()
    {
        Assert.Equal("AUS", DisplayTopology.DecodeEdidManufacturer(0xB306));
    }

    [Fact]
    public void Edid_key_is_extracted_from_device_path_and_fallback_ids()
    {
        Assert.Equal("AUS27AF", DisplayTopology.EdidKeyFromId(@"\\?\DISPLAY#AUS27AF#5&2a7c2b4f&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Equal("AUS27AF", DisplayTopology.EdidKeyFromId("edid:AUS27AF:3:10"));
        Assert.Null(DisplayTopology.EdidKeyFromId(@"gdi:\\.\DISPLAY1"));
    }

    [Fact]
    public void Resolve_prefers_exact_then_unique_model_then_primary()
    {
        var a = T(@"\\?\DISPLAY#AUS27AF#5&1&0&UID1#{x}", "AUS27AF", primary: false, x: 2560);
        var b = T(@"\\?\DISPLAY#DEL4321#5&2&0&UID2#{x}", "DEL4321", primary: true);
        DisplayTarget[] all = [b, a];

        Assert.Same(a, DisplayTopology.Resolve(all, a.Info.Id.ToUpperInvariant(), out bool exact));
        Assert.True(exact);

        // Same monitor model on another port (instance part of the path changed).
        Assert.Same(a, DisplayTopology.Resolve(all, @"\\?\DISPLAY#AUS27AF#5&9&0&UID9#{x}", out exact));
        Assert.False(exact);

        Assert.Same(b, DisplayTopology.Resolve(all, "", out exact));
        Assert.Same(b, DisplayTopology.Resolve(all, "unknown", out _));
        Assert.Null(DisplayTopology.Resolve([], "", out _));

        // Ambiguous model → primary.
        var a2 = T(@"\\?\DISPLAY#AUS27AF#5&3&0&UID3#{x}", "AUS27AF", primary: false, x: -2560);
        Assert.Same(b, DisplayTopology.Resolve([b, a, a2], @"\\?\DISPLAY#AUS27AF#5&9&0&UID9#{x}", out _));
    }
}
