using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using RambiBox.Core;
using RambiBox.Core.Updates;

namespace RambiBox.App;

/// <summary>Project facts shown in the app (About) — the one place for the project's web addresses.</summary>
public static class AppInfo
{
    /// <summary>Source code repository.</summary>
    public const string RepoUrl = "https://github.com/Ferrywell/RambiBox";

    /// <summary>Project website (GitHub Pages).</summary>
    public const string SiteUrl = "https://ferrywell.github.io/RambiBox/";

    /// <summary>New issue (bug report / feature request chooser).</summary>
    public const string IssuesUrl = RepoUrl + "/issues/new/choose";

    /// <summary>License text in the repository.</summary>
    public const string LicenseUrl = RepoUrl + "/blob/main/LICENSE";

    /// <summary>All releases (manual download).</summary>
    public const string ReleasesUrl = RepoUrl + "/releases";

    public const string License = "GPL-3.0-or-later";

    /// <summary>
    /// The running version as SemVer, from the informational version (keeps a pre-release suffix such as "-beta.1",
    /// drops the "+commit" build metadata the SDK appends).
    /// </summary>
    public static SemVersion SemVersion { get; } = ReadVersion();

    /// <summary>"1.2.0" or "1.2.0-beta.1".</summary>
    public static string Version => SemVersion.ToString();

    /// <summary>Build date (UTC, set by the build), or null.</summary>
    public static DateTime? BuildDate { get; } = ReadBuildDate();

    private static SemVersion ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVersion.TryParse(info, out var v)) return v;
        var n = asm.GetName().Version;
        return n is null ? new SemVersion(0, 0, 0) : new SemVersion(n.Major, n.Minor, Math.Max(0, n.Build));
    }

    private static DateTime? ReadBuildDate()
    {
        var raw = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value;
        return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>Opens a web page in the default browser. Never throws.</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {url}: {ex.Message}"); }
    }
}
