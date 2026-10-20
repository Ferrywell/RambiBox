using System.Globalization;

namespace RambiBox.Core.Updates;

/// <summary>Wording shared by the Settings page, the InfoBars and the tray.</summary>
public static class UpdateText
{
    /// <summary>"checked today 10:32" / "checked yesterday 18:02" / "checked 12 Oct" (local times).</summary>
    public static string Checked(DateTimeOffset when, DateTimeOffset now)
    {
        var w = when.ToLocalTime();
        var n = now.ToLocalTime();
        var inv = CultureInfo.InvariantCulture;
        if (w.Date == n.Date) return "checked today " + w.ToString("HH:mm", inv);
        if (w.Date == n.Date.AddDays(-1)) return "checked yesterday " + w.ToString("HH:mm", inv);
        return "checked " + w.ToString(w.Year == n.Year ? "d MMM" : "d MMM yyyy", inv);
    }

    /// <summary>The page with the release notes of a version (also for the version that is running now).</summary>
    public static string ReleasePage(SemVersion v) => $"https://github.com/Ferrywell/RambiBox/releases/tag/v{v}";

    /// <summary>Why a check didn't work, short enough for a status line.</summary>
    public static string CheckFailed(UpdateCheckResult r) => r.Status switch
    {
        CheckStatus.Offline => "Couldn't check (offline)",
        CheckStatus.RateLimited when r.RetryAfter is { } t =>
            $"Couldn't check (GitHub limit reached, try again after {t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)})",
        CheckStatus.RateLimited => "Couldn't check (GitHub limit reached, try again later)",
        _ => "Couldn't check (GitHub didn't answer as expected)",
    };
}
