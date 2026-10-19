using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using RambiBox.Core.Calibration;

namespace RambiBox.Tests.Calibration;

/// <summary>Temp directory that is deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => System.IO.Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rambibox-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        try { System.IO.Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

public class LocalCaTests
{
    // One CA for the whole class (RSA key generation is the slow part).
    private static readonly Lazy<(TempDir Dir, LocalCertificateAuthority Ca)> Shared = new(() =>
    {
        var d = new TempDir();
        return (d, new LocalCertificateAuthority(d.Path, "Test-PC_1"));
    });

    private static LocalCertificateAuthority Ca => Shared.Value.Ca;

    [Fact]
    public void Ca_has_critical_name_constraints_for_private_ranges_and_local()
    {
        using var ca = Ca.Certificate;
        Assert.Equal("CN=RambiBox Local CA (Test-PC_1), O=RambiBox", ca.Subject);
        Assert.Equal(ca.Subject, ca.Issuer);
        Assert.True(ca.NotAfter > DateTime.Now.AddYears(9));
        var bc = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(bc.CertificateAuthority);
        Assert.True(bc.Critical);
        var ku = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, ku.KeyUsages);
        Assert.False(ca.HasPrivateKey); // the public copy handed out never carries the key

        var nc = ca.Extensions.Single(e => e.Oid?.Value == "2.5.29.30");
        Assert.True(nc.Critical);
        var (ips, dns) = ParseNameConstraints(nc.RawData);
        Assert.Equal(["10.0.0.0/255.0.0.0", "172.16.0.0/255.240.0.0", "192.168.0.0/255.255.0.0", "169.254.0.0/255.255.0.0"], ips);
        Assert.Equal(["local", "test-pc-1.local"], dns);
    }

    private static (List<string> Ips, List<string> Dns) ParseNameConstraints(byte[] der)
    {
        var ips = new List<string>();
        var dns = new List<string>();
        var r = new AsnReader(der, AsnEncodingRules.DER);
        var seq = r.ReadSequence();
        var permitted = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        Assert.False(seq.HasData); // no excluded subtrees
        while (permitted.HasData)
        {
            var sub = permitted.ReadSequence();
            var tag = sub.PeekTag();
            Assert.Equal(TagClass.ContextSpecific, tag.TagClass);
            if (tag.TagValue == 7)
            {
                var b = sub.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                Assert.Equal(8, b.Length);
                ips.Add($"{new IPAddress(b[..4])}/{new IPAddress(b[4..])}");
            }
            else
            {
                Assert.Equal(2, tag.TagValue);
                dns.Add(sub.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
            }
            Assert.False(sub.HasData); // no minimum/maximum
        }
        return (ips, dns);
    }

    [Fact]
    public void Leaf_has_san_eku_short_validity_and_chains_to_the_ca()
    {
        var ip = IPAddress.Parse("192.168.1.23");
        using var leaf = Ca.IssueServerCertificate(ip);
        using var ca = Ca.Certificate;
        Assert.True(leaf.HasPrivateKey);
        Assert.Equal(ca.Subject, leaf.Issuer);
        Assert.Equal("CN=test-pc-1.local, O=RambiBox", leaf.Subject);
        Assert.True((leaf.NotAfter - leaf.NotBefore).TotalDays <= 397, $"{(leaf.NotAfter - leaf.NotBefore).TotalDays} days");
        Assert.True(leaf.NotAfter > DateTime.Now.AddDays(390));
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()).Value);
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal([ip], san.EnumerateIPAddresses().ToArray());
        Assert.Equal(["test-pc-1.local"], san.EnumerateDnsNames().ToArray());
        Assert.True(leaf.GetRSAPublicKey()!.KeySize >= 2048);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        bool ok = chain.Build(leaf);
        Assert.True(ok, string.Join("; ", chain.ChainStatus.Select(s => s.Status + " " + s.StatusInformation)));
        Assert.Equal(2, chain.ChainElements.Count);
    }

    [Fact]
    public void Name_constraints_reject_a_leaf_for_a_public_address()
    {
        // The CA itself refuses nothing (it's a library call), but any verifier must reject such a certificate.
        using var leaf = Ca.IssueServerCertificate(IPAddress.Parse("8.8.8.8"));
        using var ca = Ca.Certificate;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.False(chain.Build(leaf));
        Assert.Contains(chain.ChainStatus, s => s.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
        Assert.False(LocalCertificateAuthority.IsPermittedAddress(IPAddress.Parse("8.8.8.8")));
        Assert.True(LocalCertificateAuthority.IsPermittedAddress(IPAddress.Parse("172.31.4.5")));
        Assert.False(LocalCertificateAuthority.IsPermittedAddress(IPAddress.Parse("172.32.4.5")));
    }

    [Fact]
    public void Key_is_persisted_protected_and_reloaded_reset_creates_a_new_ca()
    {
        using var dir = new TempDir();
        var a = new LocalCertificateAuthority(dir.Path, "Box");
        Assert.False(a.Exists);
        string thumb;
        using (var c = a.Certificate) thumb = c.Thumbprint;
        Assert.True(a.Exists);
        var keyFile = File.ReadAllBytes(Path.Combine(dir.Path, LocalCertificateAuthority.KeyFileName));
        Assert.DoesNotContain("PRIVATE", System.Text.Encoding.ASCII.GetString(keyFile));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            System.Security.Cryptography.RSA.Create().ImportPkcs8PrivateKey(keyFile, out _)); // not plain PKCS#8

        // A new instance (= app restart) loads the same CA and can still sign with it.
        var b = new LocalCertificateAuthority(dir.Path, "Box");
        using (var c = b.Certificate) Assert.Equal(thumb, c.Thumbprint);
        using (var leaf = b.IssueServerCertificate(IPAddress.Parse("10.0.0.7")))
        using (var c = b.Certificate)
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(c);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(leaf));
        }

        Assert.Null(b.PhoneTrustedAt);
        b.MarkPhoneTrusted();
        Assert.NotNull(b.PhoneTrustedAt);
        b.Reset();
        Assert.False(b.Exists);
        Assert.Null(b.PhoneTrustedAt);
        using (var c = b.Certificate) Assert.NotEqual(thumb, c.Thumbprint);
    }

    [Fact]
    public void Mobileconfig_is_a_plist_with_the_ca_der()
    {
        var bytes = Ca.BuildMobileConfig();
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("plist", doc.Root!.Name.LocalName);
        var top = PlistDict(doc.Root.Element("dict")!);
        Assert.Equal("Configuration", top["PayloadType"].Value);
        Assert.StartsWith("local.rambibox.phone-ca.", top["PayloadIdentifier"].Value);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-5[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", top["PayloadUUID"].Value);
        var payload = PlistDict(top["PayloadContent"].Element("dict")!);
        Assert.Equal("com.apple.security.root", payload["PayloadType"].Value);
        var der = Convert.FromBase64String(payload["PayloadContent"].Value.Trim());
        using var ca = Ca.Certificate;
        Assert.Equal(ca.RawData, der);
        // Stable: building again gives the same identifiers.
        var again = PlistDict(XDocument.Parse(System.Text.Encoding.UTF8.GetString(Ca.BuildMobileConfig())).Root!.Element("dict")!);
        Assert.Equal(top["PayloadUUID"].Value, again["PayloadUUID"].Value);
        Assert.Equal(top["PayloadIdentifier"].Value, again["PayloadIdentifier"].Value);
    }

    private static Dictionary<string, XElement> PlistDict(XElement dict)
    {
        var d = new Dictionary<string, XElement>();
        var items = dict.Elements().ToList();
        for (int i = 0; i + 1 < items.Count; i += 2)
        {
            Assert.Equal("key", items[i].Name.LocalName);
            d[items[i].Value] = items[i + 1];
        }
        return d;
    }

    [Fact]
    public void Host_label_is_sanitized()
    {
        Assert.Equal("desktop-ab12", LocalCertificateAuthority.SanitizeHostLabel("DESKTOP-AB12"));
        Assert.Equal("my-pc", LocalCertificateAuthority.SanitizeHostLabel("My_PC!"));
        Assert.Equal("rambibox-pc", LocalCertificateAuthority.SanitizeHostLabel("___"));
    }

    [Fact]
    public async Task Tls_round_trip_with_a_client_trusting_only_the_ca()
    {
        // Loopback is not in the permitted ranges, so the server certificate is issued for a LAN IP and the client
        // checks the chain itself (custom root) and the name against that IP.
        using var leaf = Ca.IssueServerCertificate(IPAddress.Parse("192.168.1.2"));
        using var server = new MiniHttpServer((req, _) => Task.FromResult(MiniHttpResponse.Text(200, "hello " + req.Path)),
            (_, _, _) => null, 1024) { ServerCertificate = leaf };
        Assert.True(server.Start(47830, 47870));
        using var ca = Ca.Certificate;
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var c2 = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(c2) && c2.MatchesHostname("192.168.1.2");
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync($"https://127.0.0.1:{server.Port}/x/y");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("hello /x/y", await resp.Content.ReadAsStringAsync());

        // A client that doesn't trust the CA fails the handshake (this is how the phone page detects "setup needed").
        using var strict = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync($"https://127.0.0.1:{server.Port}/x"));
        // ...and the server keeps working afterwards.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"https://127.0.0.1:{server.Port}/again")).StatusCode);
    }
}
