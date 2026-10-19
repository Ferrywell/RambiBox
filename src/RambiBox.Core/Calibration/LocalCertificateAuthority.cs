using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RambiBox.Core.Calibration;

/// <summary>
/// A per-PC, name-constrained root CA so the phone can open the calibration page over HTTPS (getUserMedia needs a
/// secure context; a LAN IP over plain HTTP is not one).
/// <para>
/// The CA is created once (RSA 2048 / SHA-256, ~10 years) and its private key is stored DPAPI-protected for the current
/// Windows user under %AppData%\RambiBox\phone-ca; it never leaves the PC. A critical NameConstraints extension limits
/// what it can ever vouch for to private/link-local IPv4 addresses and <c>*.local</c> names, so even a stolen key could
/// not impersonate a public web site on the phone that trusts it. A short-lived leaf certificate (≤ 397 days, Apple's
/// limit) is issued for the PC's current LAN IP + "&lt;hostname&gt;.local" whenever the calibration starts.
/// </para>
/// Thread-safe.
/// </summary>
public sealed class LocalCertificateAuthority
{
    public const string CaFileName = "ca.cer";
    public const string KeyFileName = "ca.key";
    public const string TrustFileName = "phone-trusted.txt";
    public const int LeafValidityDays = 397;
    public const int CaValidityYears = 10;

    private static readonly byte[] DpapiEntropy = "RambiBox phone CA v1"u8.ToArray();

    /// <summary>Private IPv4 ranges (+ link-local) the CA may issue for: (network, mask).</summary>
    internal static readonly (byte[] Net, byte[] Mask)[] PermittedIpRanges =
    [
        ([10, 0, 0, 0], [255, 0, 0, 0]),
        ([172, 16, 0, 0], [255, 240, 0, 0]),
        ([192, 168, 0, 0], [255, 255, 0, 0]),
        ([169, 254, 0, 0], [255, 255, 0, 0]),
    ];

    private readonly object _gate = new();
    private readonly string _dir;
    private X509Certificate2? _ca; // with private key

    /// <param name="directory">Where the CA certificate + protected key live (created on demand).</param>
    /// <param name="machineName">PC name for the CA's display name and the "&lt;host&gt;.local" name (default: this PC).</param>
    public LocalCertificateAuthority(string? directory = null, string? machineName = null)
    {
        _dir = directory ?? DefaultDirectory;
        string name = machineName ?? SafeMachineName();
        PcName = string.IsNullOrWhiteSpace(name) ? "PC" : name.Trim();
        DnsHost = SanitizeHostLabel(PcName) + ".local";
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RambiBox", "phone-ca");

    public string Directory => _dir;
    public string PcName { get; }
    /// <summary>"&lt;hostname&gt;.local" (mDNS name of this PC, sanitized to a valid DNS label).</summary>
    public string DnsHost { get; }
    public string CaDisplayName => $"RambiBox Local CA ({PcName})";

    /// <summary>True when a CA has been created (it may still need to be installed on the phone).</summary>
    public bool Exists => File.Exists(Path.Combine(_dir, CaFileName)) && File.Exists(Path.Combine(_dir, KeyFileName));

    /// <summary>The CA certificate (public part only), creating the CA if needed.</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_gate) return X509CertificateLoader.LoadCertificate(LoadOrCreate().RawData);
        }
    }

    /// <summary>When a phone last opened the HTTPS page successfully with the current CA (null = never / after a reset).</summary>
    public DateTimeOffset? PhoneTrustedAt
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    var path = Path.Combine(_dir, TrustFileName);
                    if (!File.Exists(path) || !Exists) return null;
                    var parts = File.ReadAllText(path).Trim().Split(' ');
                    if (parts.Length != 2) return null;
                    var ca = LoadOrCreate();
                    if (!string.Equals(parts[0], ca.Thumbprint, StringComparison.OrdinalIgnoreCase)) return null;
                    return DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Phone CA: reading trust marker failed: {ex.Message}");
                    return null;
                }
            }
        }
    }

    /// <summary>Records that a phone reached the HTTPS server, i.e. it trusts the current CA.</summary>
    public void MarkPhoneTrusted()
    {
        lock (_gate)
        {
            try
            {
                var ca = LoadOrCreate();
                System.IO.Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, TrustFileName),
                    ca.Thumbprint + " " + DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Log.Warn($"Phone CA: writing trust marker failed: {ex.Message}"); }
        }
    }

    /// <summary>Deletes the CA (key, certificate, trust marker). The next use creates a new one, which the phone must install again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _ca?.Dispose();
            _ca = null;
            foreach (var f in new[] { CaFileName, KeyFileName, TrustFileName })
            {
                try { File.Delete(Path.Combine(_dir, f)); }
                catch (Exception ex) { Log.Warn($"Phone CA: could not delete {f}: {ex.Message}"); }
            }
            Log.Info("Phone CA reset");
        }
    }

    /// <summary>Loads the CA (with key) from disk or creates and persists a new one.</summary>
    private X509Certificate2 LoadOrCreate()
    {
        if (_ca is not null) return _ca;
        string cerPath = Path.Combine(_dir, CaFileName), keyPath = Path.Combine(_dir, KeyFileName);
        if (File.Exists(cerPath) && File.Exists(keyPath))
        {
            try
            {
                var pub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(cerPath));
                var pkcs8 = Dpapi.Unprotect(File.ReadAllBytes(keyPath), DpapiEntropy);
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                    _ca = pub.CopyWithPrivateKey(rsa);
                    pub.Dispose();
                }
                finally { CryptographicOperations.ZeroMemory(pkcs8); }
                if (_ca.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(LeafValidityDays + 30)) return _ca;
                Log.Info("Phone CA expires soon; creating a new one");
                _ca.Dispose();
                _ca = null;
            }
            catch (Exception ex)
            {
                Log.Warn($"Phone CA could not be loaded ({ex.Message}); creating a new one");
                _ca = null;
            }
        }
        _ca = CreateCa();
        System.IO.Directory.CreateDirectory(_dir);
        using (var rsa = _ca.GetRSAPrivateKey()!)
        {
            var pkcs8 = rsa.ExportPkcs8PrivateKey();
            try { WriteAtomic(keyPath, Dpapi.Protect(pkcs8, DpapiEntropy)); }
            finally { CryptographicOperations.ZeroMemory(pkcs8); }
        }
        WriteAtomic(cerPath, _ca.RawData);
        try { File.Delete(Path.Combine(_dir, TrustFileName)); } catch { /* none */ }
        Log.Info($"Phone CA created: {CaDisplayName}, thumbprint {_ca.Thumbprint}");
        return _ca;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private X509Certificate2 CreateCa()
    {
        // The CA key is exportable in memory only long enough to be DPAPI-protected on disk.
        using var key = RSA.Create(2048);
        var dn = new X500DistinguishedName($"CN={EscapeDn(CaDisplayName)}, O=RambiBox");
        var req = new CertificateRequest(dn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(BuildNameConstraints(DnsHost));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(CaValidityYears));
        // Round-trip so the private key is a plain exportable in-memory key (needed to persist it).
        var der = cert.RawData;
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            using var pub = X509CertificateLoader.LoadCertificate(der);
            return pub.CopyWithPrivateKey(rsa);
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    /// <summary>
    /// NameConstraints (RFC 5280 4.2.1.10), critical: permitted iPAddress 10/8, 172.16/12, 192.168/16, 169.254/16 and
    /// dNSName "local" (= any name ending in .local) plus the PC's own "&lt;host&gt;.local".
    /// </summary>
    internal static X509Extension BuildNameConstraints(string dnsHost)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) // permittedSubtrees
            {
                foreach (var (net, mask) in PermittedIpRanges)
                {
                    using (w.PushSequence()) // GeneralSubtree
                        w.WriteOctetString([.. net, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7)); // iPAddress
                }
                foreach (var dns in new[] { "local", dnsHost }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    using (w.PushSequence())
                        w.WriteCharacterString(UniversalTagNumber.IA5String, dns, new Asn1Tag(TagClass.ContextSpecific, 2)); // dNSName
                }
            }
        }
        return new X509Extension("2.5.29.30", w.Encode(), critical: true);
    }

    /// <summary>
    /// Issues a TLS server certificate for <paramref name="ip"/> (+ "&lt;host&gt;.local"), valid ≤ 397 days, usable by
    /// SslStream. The caller owns (and should dispose) it.
    /// </summary>
    public X509Certificate2 IssueServerCertificate(IPAddress ip, TimeSpan? validity = null)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        X509Certificate2 ca;
        lock (_gate) ca = LoadOrCreate();
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName($"CN={DnsHost}, O=RambiBox"), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(ip);
        san.AddDnsName(DnsHost);
        req.CertificateExtensions.Add(san.Build(false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var now = DateTimeOffset.UtcNow;
        var notAfter = now.Add(validity ?? TimeSpan.FromDays(LeafValidityDays - 1)); // a day of margin for clock skew
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter;
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        using var signed = req.Create(ca, now.AddHours(-1), notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // SChannel cannot use ephemeral (in-memory) keys; a PKCS#12 round trip gives the key a (temporary) key container
        // that is deleted again when the certificate is disposed.
        var pfx = withKey.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    /// <summary>
    /// An unsigned iOS configuration profile with a com.apple.security.root payload containing the CA (public) certificate.
    /// The profile identifier is stable for this PC + user, so installing a new CA after a reset replaces the old profile
    /// on the phone; the payload UUIDs derive from the CA thumbprint.
    /// </summary>
    public byte[] BuildMobileConfig()
    {
        X509Certificate2 ca;
        lock (_gate) ca = LoadOrCreate();
        string stable = Hex(SHA256.HashData(Encoding.UTF8.GetBytes("rambibox|" + PcName.ToLowerInvariant() + "|" + Environment.UserName.ToLowerInvariant())))[..16];
        string ident = "local.rambibox.phone-ca." + stable;
        var thumb = Convert.FromHexString(ca.GetCertHashString(HashAlgorithmName.SHA256));
        string profileUuid = UuidFrom(thumb, "profile"), certUuid = UuidFrom(thumb, "cert");
        string b64 = Convert.ToBase64String(ca.RawData, Base64FormattingOptions.InsertLineBreaks);
        string name = SecurityElement.Escape(CaDisplayName)!;
        string desc = SecurityElement.Escape(
            $"Lets the RambiBox calibration page on {PcName} use the phone camera over your home network. " +
            "It can only vouch for private network addresses (192.168.x.x, 10.x.x.x, 172.16-31.x.x, 169.254.x.x) and .local names. " +
            "Remove it any time in Settings → General → VPN & Device Management.")!;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append("\t<key>PayloadContent</key>\n\t<array>\n\t\t<dict>\n");
        sb.Append("\t\t\t<key>PayloadCertificateFileName</key>\n\t\t\t<string>RambiBox-Local-CA.cer</string>\n");
        sb.Append("\t\t\t<key>PayloadContent</key>\n\t\t\t<data>\n").Append(b64.Replace("\r\n", "\n")).Append("\n\t\t\t</data>\n");
        sb.Append("\t\t\t<key>PayloadDescription</key>\n\t\t\t<string>Adds the RambiBox local root certificate.</string>\n");
        sb.Append("\t\t\t<key>PayloadDisplayName</key>\n\t\t\t<string>").Append(name).Append("</string>\n");
        sb.Append("\t\t\t<key>PayloadIdentifier</key>\n\t\t\t<string>").Append(ident).Append(".root</string>\n");
        sb.Append("\t\t\t<key>PayloadType</key>\n\t\t\t<string>com.apple.security.root</string>\n");
        sb.Append("\t\t\t<key>PayloadUUID</key>\n\t\t\t<string>").Append(certUuid).Append("</string>\n");
        sb.Append("\t\t\t<key>PayloadVersion</key>\n\t\t\t<integer>1</integer>\n");
        sb.Append("\t\t</dict>\n\t</array>\n");
        sb.Append("\t<key>PayloadDescription</key>\n\t<string>").Append(desc).Append("</string>\n");
        sb.Append("\t<key>PayloadDisplayName</key>\n\t<string>").Append(name).Append("</string>\n");
        sb.Append("\t<key>PayloadIdentifier</key>\n\t<string>").Append(ident).Append("</string>\n");
        sb.Append("\t<key>PayloadOrganization</key>\n\t<string>RambiBox</string>\n");
        sb.Append("\t<key>PayloadRemovalDisallowed</key>\n\t<false/>\n");
        sb.Append("\t<key>PayloadType</key>\n\t<string>Configuration</string>\n");
        sb.Append("\t<key>PayloadUUID</key>\n\t<string>").Append(profileUuid).Append("</string>\n");
        sb.Append("\t<key>PayloadVersion</key>\n\t<integer>1</integer>\n");
        sb.Append("</dict>\n</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string UuidFrom(byte[] seed, string purpose)
    {
        var h = SHA256.HashData([.. seed, .. Encoding.ASCII.GetBytes(purpose)]);
        h[6] = (byte)((h[6] & 0x0F) | 0x50); // version 5-style
        h[8] = (byte)((h[8] & 0x3F) | 0x80); // RFC 4122 variant
        string x = Hex(h);
        return $"{x[..8]}-{x[8..12]}-{x[12..16]}-{x[16..20]}-{x[20..32]}".ToUpperInvariant();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static string EscapeDn(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace("+", "\\+")
        .Replace("\"", "\\\"").Replace("<", "\\<").Replace(">", "\\>").Replace(";", "\\;").Replace("=", "\\=");

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return "PC"; }
    }

    /// <summary>Lower-case LDH label (a-z, 0-9, '-'), 1..63 chars, no leading/trailing '-'.</summary>
    internal static string SanitizeHostLabel(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 63) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "rambibox-pc" : s;
    }

    /// <summary>True when <paramref name="ip"/> is inside the CA's permitted IP ranges.</summary>
    public static bool IsPermittedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        foreach (var (net, mask) in PermittedIpRanges)
            if ((b[0] & mask[0]) == net[0] && (b[1] & mask[1]) == net[1] && (b[2] & mask[2]) == net[2] && (b[3] & mask[3]) == net[3])
                return true;
        return false;
    }

    /// <summary>DPAPI (CryptProtectData, current user) without the extra NuGet package.</summary>
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);
        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static unsafe byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            fixed (byte* pd = data)
            fixed (byte* pe = entropy)
            {
                var input = new Blob { Size = data.Length, Data = (IntPtr)pd };
                var ent = new Blob { Size = entropy.Length, Data = (IntPtr)pe };
                bool ok = protect
                    ? CryptProtectData(ref input, "RambiBox phone CA", ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                    {
                        new Span<byte>((void*)output.Data, output.Size).Clear();
                        LocalFree(output.Data);
                    }
                }
            }
        }
    }
}
