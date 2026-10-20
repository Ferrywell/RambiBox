using RambiBox.Core.Config;
using RambiBox.Core.Updates;
using static RambiBox.Tests.Updates.UpdateFixtures;

namespace RambiBox.Tests.Updates;

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static ReleaseInfo Release(string tag) => ReleaseParser.Parse(ReleasesJson(new FakeRelease(tag))).Single();

    [Fact]
    public void Defaults_never_contact_github_before_the_user_answered()
    {
        var s = new UpdateSettings();
        Assert.True(s.CheckAutomatically);   // the suggested answer…
        Assert.False(s.ConsentAsked);        // …but not asked yet
        Assert.False(s.InstallAutomatically);
        Assert.Equal(UpdateChannel.Stable, s.Channel);
        Assert.Null(UpdatePolicy.NextAutomaticCheck(s, Start, null));
        Assert.False(UpdatePolicy.MayCheckAutomatically(s, metered: false));
    }

    [Fact]
    public void After_consent_first_check_is_two_minutes_after_start_then_daily()
    {
        var s = new UpdateSettings { ConsentAsked = true };
        Assert.Equal(Start.AddMinutes(2), UpdatePolicy.NextAutomaticCheck(s, Start, null));
        Assert.Equal(Start.AddMinutes(2).AddHours(24), UpdatePolicy.NextAutomaticCheck(s, Start, Start.AddMinutes(2)));
        Assert.True(UpdatePolicy.MayCheckAutomatically(s, metered: false));
        Assert.False(UpdatePolicy.MayCheckAutomatically(s, metered: true));
    }

    [Fact]
    public void Declined_means_no_automatic_checks()
    {
        var s = new UpdateSettings { ConsentAsked = true, CheckAutomatically = false };
        Assert.Null(UpdatePolicy.NextAutomaticCheck(s, Start, null));
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(Release("v1.2.0"), s, installSupported: true, metered: false));
    }

    [Fact]
    public void Skipped_version_is_not_offered_but_a_newer_one_is()
    {
        var s = new UpdateSettings { ConsentAsked = true, SkippedVersion = "1.2.0" };
        Assert.False(UpdatePolicy.ShouldOffer(Release("v1.2.0"), s));
        Assert.False(UpdatePolicy.ShouldNotify(Release("v1.2.0"), s));
        Assert.True(UpdatePolicy.ShouldOffer(Release("v1.2.1"), s));
        Assert.False(UpdatePolicy.ShouldOffer(null, s));
    }

    [Fact]
    public void One_notification_per_version()
    {
        var s = new UpdateSettings { ConsentAsked = true, NotifiedVersion = "1.2.0" };
        Assert.False(UpdatePolicy.ShouldNotify(Release("v1.2.0"), s));
        Assert.True(UpdatePolicy.ShouldOffer(Release("v1.2.0"), s)); // the InfoBar still shows it
        Assert.True(UpdatePolicy.ShouldNotify(Release("v1.3.0"), s));
    }

    [Fact]
    public void Automatic_download_needs_opt_in_support_and_an_unmetered_connection()
    {
        var r = Release("v1.2.0");
        var s = new UpdateSettings { ConsentAsked = true };
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(r, s, true, false)); // off by default
        s.InstallAutomatically = true;
        Assert.True(UpdatePolicy.ShouldDownloadAutomatically(r, s, true, false));
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(r, s, installSupported: false, metered: false));
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(r, s, installSupported: true, metered: true));
        s.FailedVersion = "1.2.0"; // it failed to start once: only a manual install
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(r, s, true, false));
        Assert.True(UpdatePolicy.ShouldDownloadAutomatically(Release("v1.2.1"), s, true, false));
        s.FailedVersion = null;
        s.SkippedVersion = "1.2.0";
        Assert.False(UpdatePolicy.ShouldDownloadAutomatically(r, s, true, false));
    }

    [Theory]
    [InlineData(11, false, false, false, false, false, true)]  // idle for 11 minutes
    [InlineData(2, true, false, true, false, false, true)]     // display off
    [InlineData(2, false, true, false, false, false, true)]    // lights off, window closed
    [InlineData(2, false, true, true, false, false, false)]    // lights off but the user has the window open
    [InlineData(2, false, false, false, false, false, false)]  // in use
    [InlineData(30, true, true, false, true, false, false)]    // calibrating
    [InlineData(30, true, true, false, false, true, false)]    // an integration holds the LEDs
    public void Installs_only_when_the_pc_is_not_in_use(int idleMinutes, bool displayOff, bool lightsOff, bool windowOpen,
        bool calibrating, bool apiLocked, bool expected)
    {
        var a = new ActivityState(TimeSpan.FromMinutes(idleMinutes), displayOff, lightsOff, windowOpen, calibrating, apiLocked);
        Assert.Equal(expected, UpdatePolicy.MayInstallNow(a));
    }
}

public class UpdateSettingsTests
{
    [Fact]
    public void Old_settings_files_get_update_defaults_and_unanswered_consent()
    {
        var s = SettingsSerializer.Deserialize("""{ "schemaVersion": 1, "enabled": true }""");
        Assert.NotNull(s.Updates);
        Assert.False(s.Updates.ConsentAsked);
        Assert.False(s.Updates.InstallAutomatically);
    }

    [Fact]
    public void Round_trips_and_tolerates_unknown_channels()
    {
        var s = new AppSettings();
        s.Updates.ConsentAsked = true;
        s.Updates.Channel = UpdateChannel.Beta;
        s.Updates.SkippedVersion = "1.3.0-beta.1";
        s.Updates.LastCheck = new DateTimeOffset(2026, 10, 20, 10, 32, 0, TimeSpan.FromHours(2));
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(UpdateChannel.Beta, back.Updates.Channel);
        Assert.Equal("1.3.0-beta.1", back.Updates.SkippedVersion);
        Assert.Equal(s.Updates.LastCheck, back.Updates.LastCheck);
        Assert.True(back.Updates.ConsentAsked);

        var odd = SettingsSerializer.Deserialize("""{ "updates": { "channel": "Nightly", "skippedVersion": "banana", "consentAsked": true } }""");
        Assert.Equal(UpdateChannel.Stable, odd.Updates.Channel);
        Assert.Null(odd.Updates.SkippedVersion);
    }

    [Fact]
    public void Normalize_turns_off_auto_install_without_auto_check()
    {
        var u = new UpdateSettings { CheckAutomatically = false, InstallAutomatically = true };
        UpdateSettings.Normalize(u, DateTimeOffset.UtcNow);
        Assert.False(u.InstallAutomatically);
    }
}

public class UpdateTextTests
{
    [Fact]
    public void Checked_text_uses_today_yesterday_or_the_date()
    {
        static DateTimeOffset Local(int month, int day, int hour, int minute) =>
            new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Local));
        var now = Local(10, 20, 15, 0);
        Assert.Equal("checked today 10:32", UpdateText.Checked(Local(10, 20, 10, 32), now));
        Assert.Equal("checked yesterday 18:02", UpdateText.Checked(Local(10, 19, 18, 2), now));
        Assert.Equal("checked 12 Oct", UpdateText.Checked(Local(10, 12, 9, 0), now));
    }

    [Fact]
    public void Release_notes_markdown_is_parsed_simply()
    {
        var blocks = SimpleMarkdown.Parse("""
            ## What's new
            - **Updates** from GitHub, see [the docs](https://ferrywell.github.io/RambiBox/)
              - nested `code`
            Plain text with a link https://github.com/Ferrywell/RambiBox/pull/1.

            **Full Changelog**: https://github.com/Ferrywell/RambiBox/compare/v1.1.0...v1.2.0
            [bad](javascript:alert(1))
            """);
        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal(MdBlockKind.Bullet, blocks[1].Kind);
        Assert.Equal(MdInlineKind.Bold, blocks[1].Inlines[0].Kind);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Link && i.Url == "https://ferrywell.github.io/RambiBox/");
        Assert.Equal(1, blocks[2].Level);
        Assert.Contains(blocks[3].Inlines, i => i.Kind == MdInlineKind.Link && i.Url == "https://github.com/Ferrywell/RambiBox/pull/1");
        var last = blocks[^1];
        Assert.DoesNotContain(last.Inlines, i => i.Kind == MdInlineKind.Link && i.Url!.StartsWith("javascript"));
    }
}
