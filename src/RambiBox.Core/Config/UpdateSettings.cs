using System.Text.Json.Serialization;
using RambiBox.Core.Updates;

namespace RambiBox.Core.Config;

/// <summary>Which GitHub releases are offered as updates.</summary>
[JsonConverter(typeof(TolerantEnumConverter<UpdateChannel>))]
public enum UpdateChannel
{
    /// <summary>Releases only.</summary>
    Stable,
    /// <summary>Releases and pre-releases (tags with a "-", e.g. v1.3.0-beta.1).</summary>
    Beta,
}

/// <summary>
/// In-app updates. Nothing is downloaded or installed without the user's choice: automatic checking only starts
/// after the user answered the one-time question (<see cref="ConsentAsked"/>), and automatic installing is off
/// unless the user turns it on.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>Check GitHub once a day (only after <see cref="ConsentAsked"/>).</summary>
    public bool CheckAutomatically { get; set; } = true;
    /// <summary>Download, verify and install new versions while the PC is idle. Off by default.</summary>
    public bool InstallAutomatically { get; set; } = false;
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    /// <summary>"Skip this version": not offered again (a newer one is).</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Last successful check (also when nothing new was found).</summary>
    public DateTimeOffset? LastCheck { get; set; }
    /// <summary>True once the user answered "Stay up to date?" (or changed the automatic check setting).</summary>
    public bool ConsentAsked { get; set; } = false;
    /// <summary>The version the tray notification was shown for (one notification per version).</summary>
    public string? NotifiedVersion { get; set; }
    /// <summary>A version that failed to start after installing: never installed automatically again (manually it can be).</summary>
    public string? FailedVersion { get; set; }

    /// <summary>Automatic checks are allowed: the user said yes and didn't turn them off.</summary>
    [JsonIgnore]
    public bool AutoCheckEnabled => ConsentAsked && CheckAutomatically;

    /// <summary>Repairs hand-edited values (called from <see cref="SettingsSerializer.Normalize"/>).</summary>
    public static void Normalize(UpdateSettings u, DateTimeOffset now)
    {
        if (!Enum.IsDefined(u.Channel)) u.Channel = UpdateChannel.Stable;
        u.SkippedVersion = SemVersion.TryParse(u.SkippedVersion, out var skipped) ? skipped.ToString() : null;
        u.NotifiedVersion = SemVersion.TryParse(u.NotifiedVersion, out var notified) ? notified.ToString() : null;
        u.FailedVersion = SemVersion.TryParse(u.FailedVersion, out var failed) ? failed.ToString() : null;
        if (u.LastCheck is { } last && last > now + TimeSpan.FromDays(1)) u.LastCheck = null; // the clock was wrong
        if (!u.CheckAutomatically) u.InstallAutomatically = false;
    }
}
