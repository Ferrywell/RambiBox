using System.Text.Json;
using RambiBox.Core.Config;

namespace RambiBox.Core.Updates;

/// <summary>A downloadable file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A GitHub release that carries a signed RambiBox.exe.</summary>
public sealed record ReleaseInfo(
    SemVersion Version,
    string Tag,
    string Title,
    string Notes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    ReleaseAsset Exe,
    ReleaseAsset Signature,
    ReleaseAsset Checksum);

/// <summary>Reads the GitHub "list releases" JSON and picks the update to offer.</summary>
public static class ReleaseParser
{
    public const string ExeName = "RambiBox.exe";
    public const string SignatureName = "RambiBox.exe.sig";
    public const string ChecksumName = "RambiBox.exe.sha256";

    /// <summary>Only files of this repository's releases are downloaded (the signature is the real guard).</summary>
    public const string DownloadPrefix = "https://github.com/Ferrywell/RambiBox/releases/download/";

    /// <summary>
    /// Parses the releases array. Drafts, tags that aren't versions and releases without the three update files
    /// (exe, signature, checksum) are left out. Never throws (malformed JSON → empty list).
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        var list = new List<ReleaseInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (Bool(r, "draft")) continue;
                var tag = Str(r, "tag_name");
                if (!SemVersion.TryParse(tag, out var version)) continue;
                ReleaseAsset? exe = null, sig = null, sum = null;
                if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object) continue;
                        var name = Str(a, "name");
                        var url = Str(a, "browser_download_url");
                        long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) ? n : -1;
                        if (name is null || url is null || size <= 0 || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                        var asset = new ReleaseAsset(name, url, size);
                        if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) exe = asset;
                        else if (name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase)) sig = asset;
                        else if (name.Equals(ChecksumName, StringComparison.OrdinalIgnoreCase)) sum = asset;
                    }
                }
                if (exe is null || sig is null || sum is null) continue;
                var prerelease = Bool(r, "prerelease") || version.IsPrerelease;
                DateTimeOffset? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                    && p.TryGetDateTimeOffset(out var d) ? d : null;
                var html = Str(r, "html_url");
                if (html is null || !html.StartsWith("https://github.com/", StringComparison.Ordinal))
                    html = $"https://github.com/Ferrywell/RambiBox/releases/tag/{Uri.EscapeDataString(tag!)}";
                var title = Str(r, "name");
                list.Add(new ReleaseInfo(version, tag!, string.IsNullOrWhiteSpace(title) ? $"RambiBox {version}" : title.Trim(),
                    Str(r, "body") ?? "", html, published, prerelease, exe, sig, sum));
            }
        }
        catch (JsonException)
        {
            list.Clear();
        }
        return list;
    }

    /// <summary>
    /// The newest release newer than <paramref name="current"/> for the channel (Stable: no pre-releases), or null.
    /// </summary>
    public static ReleaseInfo? SelectUpdate(IEnumerable<ReleaseInfo> releases, SemVersion current, UpdateChannel channel) =>
        releases
            .Where(r => channel == UpdateChannel.Beta || !r.IsPrerelease)
            .Where(r => r.Version > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
