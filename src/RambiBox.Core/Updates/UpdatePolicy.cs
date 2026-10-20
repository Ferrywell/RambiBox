using RambiBox.Core.Config;

namespace RambiBox.Core.Updates;

/// <summary>What the PC is doing right now (for "install when you're not using the PC").</summary>
/// <param name="IdleFor">Time since the last keyboard/mouse input.</param>
/// <param name="DisplayOff">Windows switched the display off.</param>
/// <param name="LightsOff">The LEDs are switched off in RambiBox.</param>
/// <param name="WindowOpen">The RambiBox window is open.</param>
/// <param name="CalibrationActive">A phone calibration is running.</param>
/// <param name="ApiLocked">An integration (Prismatik API) controls the LEDs.</param>
public readonly record struct ActivityState(TimeSpan IdleFor, bool DisplayOff, bool LightsOff, bool WindowOpen,
    bool CalibrationActive, bool ApiLocked);

/// <summary>
/// The update rules in one place (pure functions, unit-tested). The user always decides: no automatic contact with
/// GitHub before the user answered the one-time question, nothing installed automatically unless that's turned on,
/// and a skipped version is never offered again.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>First automatic check after the app started (not during the busy start).</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    /// <summary>No keyboard/mouse input for this long counts as "not using the PC".</summary>
    public static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When the next automatic check is due, or null when there are no automatic checks (not answered yet, or off).
    /// </summary>
    /// <param name="startedAt">When the app (or the automatic checking) started.</param>
    /// <param name="lastAutomaticCheck">The previous automatic check in this session, if any.</param>
    public static DateTimeOffset? NextAutomaticCheck(UpdateSettings s, DateTimeOffset startedAt, DateTimeOffset? lastAutomaticCheck)
    {
        if (!s.AutoCheckEnabled) return null;
        return lastAutomaticCheck is { } last ? last + CheckInterval : startedAt + FirstCheckDelay;
    }

    /// <summary>An automatic check may run now (never on a metered connection).</summary>
    public static bool MayCheckAutomatically(UpdateSettings s, bool metered) => s.AutoCheckEnabled && !metered;

    /// <summary>The release is worth showing: newer and not skipped by the user.</summary>
    public static bool ShouldOffer(ReleaseInfo? release, UpdateSettings s) =>
        release is not null && !(SemVersion.TryParse(s.SkippedVersion, out var skipped) && skipped == release.Version);

    /// <summary>One tray notification per version.</summary>
    public static bool ShouldNotify(ReleaseInfo? release, UpdateSettings s) =>
        ShouldOffer(release, s) && !(SemVersion.TryParse(s.NotifiedVersion, out var n) && n == release!.Version);

    /// <summary>Download in the background for an automatic install. A version that failed to start once is left to the user.</summary>
    public static bool ShouldDownloadAutomatically(ReleaseInfo? release, UpdateSettings s, bool installSupported, bool metered) =>
        installSupported && s.AutoCheckEnabled && s.InstallAutomatically && !metered && ShouldOffer(release, s)
        && !(SemVersion.TryParse(s.FailedVersion, out var failed) && failed == release!.Version);

    /// <summary>
    /// An automatic install may happen now: never during a calibration or while an integration holds the LEDs; otherwise
    /// when the PC has been idle for 10 minutes, the display is off, or the lights are off and the window is closed.
    /// </summary>
    public static bool MayInstallNow(ActivityState a)
    {
        if (a.CalibrationActive || a.ApiLocked) return false;
        if (a.IdleFor >= IdleBeforeInstall || a.DisplayOff) return true;
        return a.LightsOff && !a.WindowOpen;
    }
}
