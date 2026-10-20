using RambiBox.Core.Updates;

namespace RambiBox.Tests.Updates;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.2.0", 1, 2, 0, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V10.0.1", 10, 0, 1, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("1.3.0-beta.1", 1, 3, 0, "beta.1")]
    [InlineData("v2.0.0-rc.1+build.5", 2, 0, 0, "rc.1")]
    [InlineData("1.1.0+abc123def", 1, 1, 0, "")]
    [InlineData(" 1.0.0 ", 1, 0, 0, "")]
    public void Parses_tags_and_informational_versions(string text, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVersion.TryParse(text, out var v));
        Assert.Equal((major, minor, patch, pre), (v.Major, v.Minor, v.Patch, v.Prerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3+")]
    [InlineData("v")]
    public void Rejects_invalid_versions(string? text) => Assert.False(SemVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.1.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.2.0", "1.10.0")]
    [InlineData("1.2.0-beta", "1.2.0")]
    [InlineData("1.2.0-alpha", "1.2.0-beta")]
    [InlineData("1.2.0-beta", "1.2.0-beta.1")]
    [InlineData("1.2.0-beta.1", "1.2.0-beta.2")]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10")]
    [InlineData("1.2.0-1", "1.2.0-alpha")]
    [InlineData("1.2.0-rc.1", "1.2.1-alpha")]
    [InlineData("1.1.0", "1.2.0-beta.1")]
    public void Orders_like_semver(string lower, string higher)
    {
        var a = SemVersion.Parse(lower);
        var b = SemVersion.Parse(higher);
        Assert.True(a < b, $"{lower} < {higher}");
        Assert.True(b > a);
        Assert.True(a.CompareTo(b) < 0 && b.CompareTo(a) > 0);
    }

    [Fact]
    public void Build_metadata_and_v_prefix_are_ignored_for_equality()
    {
        Assert.Equal(SemVersion.Parse("v1.2.0+abc"), SemVersion.Parse("1.2.0"));
        Assert.True(SemVersion.Parse("1.2.0+a") == SemVersion.Parse("1.2.0+b"));
        Assert.Equal("1.2.0-beta.1", SemVersion.Parse("v1.2.0-beta.1+sha").ToString());
    }
}
