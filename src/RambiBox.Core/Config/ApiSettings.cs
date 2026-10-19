using System.Security.Cryptography;

namespace RambiBox.Core.Config;

/// <summary>
/// Prismatik/Lightpack-compatible API server (TCP text protocol) for integrations such as Home Assistant,
/// the Python "lightpack" library, scripts and Stream Deck plugins. Off by default.
/// </summary>
public sealed class ApiSettings
{
    /// <summary>Prismatik's default API port.</summary>
    public const int DefaultPort = 3636;
    public const int MaxKeyLength = 128;

    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = DefaultPort;
    /// <summary>Listen on all interfaces and accept clients from private networks (otherwise 127.0.0.1 only).</summary>
    public bool AllowLan { get; set; } = false;
    /// <summary>Optional key; when set, clients must send <c>apikey:&lt;key&gt;</c> first. Empty = no key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>A new random key (32 hex characters).</summary>
    public static string GenerateKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Repairs out-of-range values (called from <see cref="SettingsSerializer.Normalize"/>).</summary>
    public static void Normalize(ApiSettings a)
    {
        if (a.Port is < 1 or > 65535) a.Port = DefaultPort;
        var key = (a.ApiKey ?? "").Trim();
        key = new string(key.Where(ch => !char.IsControl(ch)).ToArray());
        if (key.Length > MaxKeyLength) key = key[..MaxKeyLength];
        a.ApiKey = key;
    }

    public bool SameAs(ApiSettings o) =>
        Enabled == o.Enabled && Port == o.Port && AllowLan == o.AllowLan && string.Equals(ApiKey, o.ApiKey, StringComparison.Ordinal);

    public ApiSettings Copy() => new() { Enabled = Enabled, Port = Port, AllowLan = AllowLan, ApiKey = ApiKey };
}
