using System.Net;
using RambiBox.Core.Config;
using RambiBox.Core.Updates;
using static RambiBox.Tests.Updates.UpdateFixtures;

namespace RambiBox.Tests.Updates;

public class ReleaseSelectionTests
{
    private static readonly SemVersion Current = SemVersion.Parse("1.1.0");

    private static ReleaseInfo? Pick(UpdateChannel channel, SemVersion current, params FakeRelease[] releases) =>
        ReleaseParser.SelectUpdate(ReleaseParser.Parse(ReleasesJson(releases)), current, channel);

    [Fact]
    public void Stable_picks_the_newest_release_and_ignores_prereleases()
    {
        var r = Pick(UpdateChannel.Stable, Current,
            new("v1.3.0-beta.1", Prerelease: true), new("v1.2.1"), new("v1.2.0"), new("v1.1.0"), new("v1.0.0"));
        Assert.NotNull(r);
        Assert.Equal("1.2.1", r!.Version.ToString());
        Assert.Equal("v1.2.1", r.Tag);
        Assert.Equal(1000, r.Exe.Size);
        Assert.Equal(Base + "v1.2.1/RambiBox.exe.sig", r.Signature.Url);
        Assert.Equal("https://github.com/Ferrywell/RambiBox/releases/tag/v1.2.1", r.HtmlUrl);
        Assert.Contains("What's new", r.Notes);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), r.PublishedAt);
    }

    [Fact]
    public void Beta_includes_prereleases()
    {
        var r = Pick(UpdateChannel.Beta, Current, new("v1.3.0-beta.1", Prerelease: true), new("v1.2.1"));
        Assert.Equal("1.3.0-beta.1", r!.Version.ToString());
        Assert.True(r.IsPrerelease);
    }

    [Fact]
    public void A_tag_with_a_dash_counts_as_prerelease_even_without_the_flag()
    {
        Assert.Equal("1.2.1", Pick(UpdateChannel.Stable, Current, new("v1.3.0-rc.1"), new("v1.2.1"))!.Version.ToString());
    }

    [Fact]
    public void Beta_user_gets_the_final_release_on_stable_too()
    {
        var r = Pick(UpdateChannel.Stable, SemVersion.Parse("1.2.0-beta.2"), new("v1.2.0"), new("v1.2.0-beta.2", Prerelease: true));
        Assert.Equal("1.2.0", r!.Version.ToString());
    }

    [Fact]
    public void Nothing_newer_means_no_update()
    {
        Assert.Null(Pick(UpdateChannel.Beta, Current, new("v1.1.0"), new("v1.0.0"), new("v1.1.0-beta.3", Prerelease: true)));
        Assert.Null(Pick(UpdateChannel.Stable, Current));
    }

    [Fact]
    public void Drafts_are_ignored()
    {
        Assert.Equal("1.2.0", Pick(UpdateChannel.Stable, Current, new("v1.5.0", Draft: true), new("v1.2.0"))!.Version.ToString());
    }

    [Fact]
    public void Releases_without_all_three_update_files_are_skipped()
    {
        var r = Pick(UpdateChannel.Stable, Current,
            new("v1.4.0", Assets: ["RambiBox.exe", "RambiBox.exe.sha256"]),     // unsigned
            new("v1.3.0", Assets: ["RambiBox.exe", "RambiBox.exe.sig"]),        // no checksum
            new("v1.2.5", Assets: ["RambiBox.exe.sig", "RambiBox.exe.sha256"]), // no exe
            new("v1.2.0"));
        Assert.Equal("1.2.0", r!.Version.ToString());
    }

    [Fact]
    public void Tags_that_are_not_versions_are_skipped()
    {
        Assert.Equal("1.2.0", Pick(UpdateChannel.Stable, Current, new("nightly"), new("v1.2.0"))!.Version.ToString());
    }

    [Fact]
    public void Assets_from_other_hosts_are_not_accepted()
    {
        var json = ReleasesJson(new FakeRelease("v1.2.0")).Replace(Base, "https://evil.example/");
        Assert.Empty(ReleaseParser.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("[1, \"x\", null, {}]")]
    public void Malformed_answers_give_no_releases(string json) => Assert.Empty(ReleaseParser.Parse(json));
}

public class GitHubReleaseClientTests
{
    private static readonly SemVersion Current = SemVersion.Parse("1.1.0");

    [Fact]
    public async Task Sends_user_agent_accept_header_and_uses_the_etag_next_time()
    {
        var dir = TempDir();
        var json = ReleasesJson(new FakeRelease("v1.2.0"));
        int calls = 0;
        var handler = new FakeHandler(req =>
        {
            calls++;
            if (req.Headers.IfNoneMatch.Any(e => e.Tag == "\"abc\"")) return new HttpResponseMessage(HttpStatusCode.NotModified);
            return FakeHandler.Ok(json, "\"abc\"");
        });
        using var http = GitHubReleaseClient.CreateHttpClient("1.1.0", handler);
        using var client = new GitHubReleaseClient(dir, "1.1.0", http);

        var first = await client.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.UpdateAvailable, first.Status);
        Assert.Equal("1.2.0", first.Update!.Version.ToString());
        var req0 = handler.Requests[0];
        Assert.Equal(GitHubReleaseClient.ReleasesUrl, req0.RequestUri!.ToString());
        Assert.Equal("RambiBox/1.1.0", req0.Headers.UserAgent.ToString());
        Assert.Contains(req0.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.Empty(req0.Headers.IfNoneMatch);

        // 304: answered from the cache, and the cache survives a new client instance
        using var client2 = new GitHubReleaseClient(dir, "1.1.0", http);
        var second = await client2.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.UpdateAvailable, second.Status);
        Assert.Equal(2, calls);
        Assert.Contains(handler.Requests[1].Headers.IfNoneMatch, e => e.Tag == "\"abc\"");

        // switching channel needs no network
        Assert.Equal(CheckStatus.UpdateAvailable, client2.EvaluateCached(Current, UpdateChannel.Beta)!.Status);
        Assert.Equal(CheckStatus.UpToDate, client2.EvaluateCached(SemVersion.Parse("1.2.0"), UpdateChannel.Beta)!.Status);
    }

    [Fact]
    public async Task Rate_limit_is_remembered_and_no_request_is_made_until_it_ends()
    {
        var dir = TempDir();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddMinutes(30);
        var handler = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
            return r;
        });
        using var http = GitHubReleaseClient.CreateHttpClient("1.1.0", handler);
        using var client = new GitHubReleaseClient(dir, "1.1.0", http, () => now);

        var r1 = await client.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.RateLimited, r1.Status);
        Assert.Equal(reset, r1.RetryAfter);
        var r2 = await client.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.RateLimited, r2.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retry_after_on_429_is_honoured()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var handler = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return r;
        });
        using var http = GitHubReleaseClient.CreateHttpClient("1.1.0", handler);
        using var client = new GitHubReleaseClient(TempDir(), "1.1.0", http, () => now);
        var r = await client.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.RateLimited, r.Status);
        Assert.Equal(now.AddMinutes(5), r.RetryAfter);
    }

    [Fact]
    public async Task Network_errors_and_server_errors_never_throw()
    {
        using var offlineHttp = GitHubReleaseClient.CreateHttpClient("1.1.0", new FakeHandler(_ => throw new HttpRequestException("No such host")));
        using var offline = new GitHubReleaseClient(TempDir(), "1.1.0", offlineHttp);
        var r1 = await offline.CheckAsync(Current, UpdateChannel.Stable);
        Assert.Equal(CheckStatus.Offline, r1.Status);
        Assert.Equal("Couldn't check (offline)", UpdateText.CheckFailed(r1));

        using var errorHttp = GitHubReleaseClient.CreateHttpClient("1.1.0", new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var error = new GitHubReleaseClient(TempDir(), "1.1.0", errorHttp);
        Assert.Equal(CheckStatus.Failed, (await error.CheckAsync(Current, UpdateChannel.Stable)).Status);

        using var junkHttp = GitHubReleaseClient.CreateHttpClient("1.1.0", new FakeHandler(_ => FakeHandler.Ok("<html>")));
        using var junk = new GitHubReleaseClient(TempDir(), "1.1.0", junkHttp);
        Assert.Equal(CheckStatus.Failed, (await junk.CheckAsync(Current, UpdateChannel.Stable)).Status);
    }

    [Fact]
    public async Task Up_to_date_when_nothing_is_newer()
    {
        using var http = GitHubReleaseClient.CreateHttpClient("1.2.0",
            new FakeHandler(_ => FakeHandler.Ok(ReleasesJson(new FakeRelease("v1.2.0"), new FakeRelease("v1.1.0")))));
        using var client = new GitHubReleaseClient(TempDir(), "1.2.0", http);
        var r = await client.CheckAsync(SemVersion.Parse("1.2.0"), UpdateChannel.Beta);
        Assert.Equal(CheckStatus.UpToDate, r.Status);
        Assert.NotNull(r.CheckedAt);
    }
}
