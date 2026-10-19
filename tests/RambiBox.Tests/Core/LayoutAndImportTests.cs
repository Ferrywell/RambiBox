using RambiBox.Core.Config;

namespace RambiBox.Tests.Core;

public class LayoutAndImportTests
{
    // Zones exactly as stored by the user's AmbiBox (0..10000), captured from a real AmbiBox profile
    private static readonly (int led, int l, int t, int r, int b)[] AmbiBoxSamples =
    [
        (0, 0, 9473, 270, 10000), (18, 4864, 9473, 5135, 10000), (36, 9729, 9473, 10000, 10000),
        (37, 9729, 9473, 10000, 10000), (38, 9729, 8947, 10000, 9473), (55, 9729, 0, 10000, 526),
        (56, 9729, 0, 10000, 526), (57, 9459, 0, 9729, 526), (92, 0, 0, 270, 526),
        (93, 0, 0, 270, 526), (94, 0, 526, 270, 1052), (111, 0, 9473, 270, 10000),
    ];

    [Fact]
    public void Generator_with_default_params_reproduces_the_AmbiBox_layout()
    {
        var zones = LayoutGenerator.Generate(new LayoutSettings());
        Assert.Equal(112, zones.Count);
        foreach (var (led, l, t, r, b) in AmbiBoxSamples)
        {
            var z = zones[led];
            Assert.InRange(Math.Abs(z.Left - l / 10000f), 0, 0.0006f);
            Assert.InRange(Math.Abs(z.Top - t / 10000f), 0, 0.0006f);
            Assert.InRange(Math.Abs(z.Right - r / 10000f), 0, 0.0006f);
            Assert.InRange(Math.Abs(z.Bottom - b / 10000f), 0, 0.0006f);
        }
    }

    [Theory]
    [InlineData(StartCorner.BottomLeft, false)]
    [InlineData(StartCorner.BottomLeft, true)]
    [InlineData(StartCorner.TopRight, false)]
    [InlineData(StartCorner.TopLeft, true)]
    [InlineData(StartCorner.BottomRight, true)]
    [InlineData(StartCorner.BottomRight, false)]
    public void Inferring_params_from_generated_zones_round_trips(StartCorner corner, bool clockwise)
    {
        var p = new LayoutSettings { Bottom = 30, Right = 17, Top = 31, Left = 16, StartCorner = corner, Clockwise = clockwise };
        var layout = new LayoutSettings { Zones = LayoutGenerator.Generate(p) };
        AmbiBoxImporter.InferGeneratorParams(layout);
        Assert.Equal((30, 17, 31, 16), (layout.Bottom, layout.Right, layout.Top, layout.Left));
        Assert.Equal(corner, layout.StartCorner);
        Assert.Equal(clockwise, layout.Clockwise);
    }

    [Fact]
    public void All_generated_zones_are_inside_the_screen_and_non_empty()
    {
        foreach (var corner in Enum.GetValues<StartCorner>())
        foreach (var cw in new[] { false, true })
        {
            var zones = LayoutGenerator.Generate(new LayoutSettings { StartCorner = corner, Clockwise = cw });
            Assert.All(zones, z =>
            {
                Assert.InRange(z.Left, 0, 1); Assert.InRange(z.Right, 0, 1);
                Assert.InRange(z.Top, 0, 1); Assert.InRange(z.Bottom, 0, 1);
                Assert.True(z.Width > 0 && z.Height > 0);
            });
            // consecutive LEDs are neighbours (the strip never jumps across the screen)
            for (int i = 1; i < zones.Count; i++)
            {
                float d = MathF.Abs((zones[i].Left + zones[i].Right) / 2 - (zones[i - 1].Left + zones[i - 1].Right) / 2)
                        + MathF.Abs((zones[i].Top + zones[i].Bottom) / 2 - (zones[i - 1].Top + zones[i - 1].Bottom) / 2);
                Assert.True(d < 0.08f, $"jump between LED {i - 1} and {i} ({corner}, cw={cw})");
            }
        }
    }

    [Fact]
    [Trait("Category", "Local")]
    public void Imports_this_machines_AmbiBox_profile()
    {
        if (!AmbiBoxImporter.IsInstalled()) return;
        var s = AmbiBoxImporter.TryImport(out var summary);
        Assert.NotNull(s);
        Assert.Equal(112, s!.Layout.Zones.Count);
        Assert.Equal((37, 19, 37, 19), (s.Layout.Bottom, s.Layout.Right, s.Layout.Top, s.Layout.Left));
        Assert.Equal(StartCorner.BottomLeft, s.Layout.StartCorner);
        Assert.False(s.Layout.Clockwise);
        Assert.Equal(0.64, s.Color.WhiteR, 3);
        Assert.Equal(0.51, s.Color.WhiteB, 3);
        Assert.Equal(1.3, s.Color.Saturation, 3);
        Assert.Equal("COM3", s.Device.LastPort);
        Assert.Equal(ColorOrder.RGB, s.Device.ColorOrder);
        Assert.Contains("112 LEDs", summary);
    }
}
