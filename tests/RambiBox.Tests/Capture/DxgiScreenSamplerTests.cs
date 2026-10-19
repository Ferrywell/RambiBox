using System.Diagnostics;
using RambiBox.Core.Capture;

namespace RambiBox.Tests.Capture;

/// <summary>Sampler behavior that needs no GPU.</summary>
public class DxgiScreenSamplerTests
{
    [Fact]
    public void Sample_after_dispose_is_unavailable_never_throws_and_does_not_spin()
    {
        var sampler = new DxgiScreenSampler();
        sampler.Dispose();
        sampler.Dispose(); // idempotent
        var dest = new float[6];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) Assert.Equal(SampleResult.Unavailable, sampler.Sample(dest, 0));
        Assert.True(sw.ElapsedMilliseconds >= 30, $"returned too fast ({sw.ElapsedMilliseconds} ms) → would spin the engine");
        Assert.Equal(SampleResult.Unavailable, sampler.Sample(dest, -5));
        Assert.False(sampler.TryGetPreview(out var bgra, out _, out _));
        Assert.Empty(bgra);
        Assert.Null(sampler.CurrentMonitor);
        Assert.Equal(default, sampler.BlackBars);
    }
}
