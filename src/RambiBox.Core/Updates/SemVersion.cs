using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RambiBox.Core.Updates;

/// <summary>
/// A semantic version (https://semver.org): <c>MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]</c>. Parsing accepts a leading
/// "v" (git tags such as <c>v1.2.0</c>), a missing patch ("1.2") and ignores build metadata (also for equality).
/// Ordering follows SemVer 2.0: a pre-release is lower than its release, pre-release identifiers compare numerically
/// when both are numbers, otherwise ordinally, and numbers sort before text.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Pre-release part without the dash ("beta.2"); empty for a release.</summary>
    public string Prerelease { get; }
    public bool IsPrerelease => Prerelease.Length > 0;

    public SemVersion(int major, int minor, int patch, string? prerelease = null)
    {
        if (major < 0 || minor < 0 || patch < 0) throw new ArgumentOutOfRangeException(nameof(major));
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? "";
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a version: '{text}'");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(s[(plus + 1)..], allowLeadingZeros: true)) return false;
            s = s[..plus];
        }
        string pre = "";
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (!ValidIdentifiers(pre, allowLeadingZeros: false)) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var nums = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0 || p.Length > 9 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')) return false;
            nums[i] = int.Parse(p, CultureInfo.InvariantCulture);
        }
        version = new SemVersion(nums[0], nums[1], nums[2], pre);
        return true;
    }

    private static bool ValidIdentifiers(string s, bool allowLeadingZeros)
    {
        if (s.Length == 0) return false;
        foreach (var id in s.Split('.'))
        {
            if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            if (!allowLeadingZeros && id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (!IsPrerelease || !other.IsPrerelease) return other.IsPrerelease.CompareTo(IsPrerelease); // release > pre-release
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long ai);
            bool bn = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long bi);
            c = an && bn ? ai.CompareTo(bi)
                : an ? -1 // numeric identifiers have lower precedence than alphanumeric ones
                : bn ? 1
                : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length); // more identifiers = higher ("beta.1" > "beta")
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);

    /// <summary>"1.2.0" or "1.2.0-beta.1" (no "v", no build metadata).</summary>
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(IsPrerelease ? "-" + Prerelease : "")}";
}
