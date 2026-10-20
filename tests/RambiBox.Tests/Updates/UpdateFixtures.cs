using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RambiBox.Core.Updates;

namespace RambiBox.Tests.Updates;

/// <summary>GitHub API answers, test signing keys and a fake HTTP handler for the updater tests.</summary>
internal static class UpdateFixtures
{
    public const string Base = "https://github.com/Ferrywell/RambiBox/releases/download/";

    public sealed record FakeRelease(string Tag, bool Prerelease = false, bool Draft = false, string[]? Assets = null,
        string Body = "## What's new\n- Something", long ExeSize = 1000);

    /// <summary>A GitHub "list releases" JSON array.</summary>
    public static string ReleasesJson(params FakeRelease[] releases)
    {
        var arr = releases.Select(r => new Dictionary<string, object?>
        {
            ["tag_name"] = r.Tag,
            ["name"] = $"RambiBox {r.Tag.TrimStart('v')}",
            ["draft"] = r.Draft,
            ["prerelease"] = r.Prerelease,
            ["html_url"] = $"https://github.com/Ferrywell/RambiBox/releases/tag/{r.Tag}",
            ["published_at"] = "2026-10-20T10:00:00Z",
            ["body"] = r.Body,
            ["assets"] = (r.Assets ?? ["RambiBox.exe", "RambiBox.exe.sig", "RambiBox.exe.sha256"]).Select(a => new Dictionary<string, object?>
            {
                ["name"] = a,
                ["size"] = a == "RambiBox.exe" ? r.ExeSize : 72,
                ["browser_download_url"] = $"{Base}{r.Tag}/{a}",
            }).ToArray(),
        }).ToArray();
        return JsonSerializer.Serialize(arr);
    }

    public static (ECDsa Key, string PublicPem) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Same as <c>openssl dgst -sha256 -sign</c>: DER-encoded ECDSA over SHA-256 of the bytes.</summary>
    public static byte[] Sign(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static string Sha256File(byte[] data, string name = "RambiBox.exe") =>
        $"{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}  {name}";

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "RambiBoxTests", "updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Answers requests from a routing function and records them.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(route(request));
    }

    public static HttpResponseMessage Ok(string body, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }

    public static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
}
