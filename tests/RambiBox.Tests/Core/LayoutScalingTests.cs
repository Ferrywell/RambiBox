using RambiBox.Core.Config;

namespace RambiBox.Tests.Core;

public class LayoutScalingTests
{
    [Fact]
    public void Same_total_keeps_the_layout()
    {
        var c = new SideCounts(37, 19, 37, 19);
        Assert.Equal(c, LayoutScaling.Distribute(c, 112));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    [InlineData(113)]
    [InlineData(301)]
    [InlineData(1)]
    public void Result_adds_up_exactly_and_keeps_proportions(int total)
    {
        var c = LayoutScaling.Distribute(new SideCounts(37, 19, 37, 19), total);
        Assert.Equal(total, c.Total);
        // symmetric layout stays (nearly) symmetric
        Assert.InRange(Math.Abs(c.Bottom - c.Top), 0, 1);
        Assert.InRange(Math.Abs(c.Left - c.Right), 0, 1);
        if (total >= 20) Assert.InRange(c.Bottom / (double)c.Right, 1.6, 2.3);
    }

    [Fact]
    public void Doubling_doubles_each_side() =>
        Assert.Equal(new SideCounts(74, 38, 74, 38), LayoutScaling.Distribute(new SideCounts(37, 19, 37, 19), 224));

    [Fact]
    public void Sides_without_leds_stay_empty()
    {
        // no LEDs at the bottom (TV on a stand)
        var c = LayoutScaling.Distribute(new SideCounts(0, 20, 40, 20), 100);
        Assert.Equal(0, c.Bottom);
        Assert.Equal(100, c.Total);
        Assert.Equal(new SideCounts(0, 25, 50, 25), c);
    }

    [Fact]
    public void Empty_layout_assumes_a_16_by_9_screen()
    {
        var c = LayoutScaling.Distribute(new SideCounts(0, 0, 0, 0), 100);
        Assert.Equal(100, c.Total);
        Assert.Equal(c.Bottom, c.Top);
        Assert.Equal(c.Left, c.Right);
        Assert.True(c.Bottom > c.Left);
    }

    [Fact]
    public void A_side_never_exceeds_the_editor_limit_and_the_rest_moves_to_other_sides()
    {
        var c = LayoutScaling.Distribute(new SideCounts(100, 10, 0, 10), 500);
        Assert.Equal(LayoutScaling.MaxPerSide, c.Bottom);
        Assert.Equal(500, c.Total);
        Assert.Equal(c.Left, c.Right);

        var huge = LayoutScaling.Distribute(new SideCounts(37, 19, 37, 19), 5000);
        Assert.Equal(4 * LayoutScaling.MaxPerSide, huge.Total);
    }

    [Fact]
    public void ResizeTo_regenerates_zones_and_respects_the_offset()
    {
        var layout = new LayoutSettings { Offset = 4 };
        layout.Zones = LayoutGenerator.Generate(layout);
        LayoutScaling.ResizeTo(layout, 124); // strip reports 124 LEDs, 4 of them skipped
        Assert.Equal(120, layout.Zones.Count);
        Assert.Equal(120, layout.Bottom + layout.Right + layout.Top + layout.Left);
        Assert.Equal(LayoutGenerator.Generate(layout), layout.Zones);
    }
}
