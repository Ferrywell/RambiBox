using System.Diagnostics;
using RambiBox.Core;

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

    public const string License = "GPL-3.0-or-later";

    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Opens a web page in the default browser. Never throws.</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {url}: {ex.Message}"); }
    }
}
