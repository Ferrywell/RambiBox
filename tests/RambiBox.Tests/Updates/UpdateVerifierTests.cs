using System.Net;
using System.Security.Cryptography;
using RambiBox.Core.Updates;
using static RambiBox.Tests.Updates.UpdateFixtures;

namespace RambiBox.Tests.Updates;

public class UpdateVerifierTests
{
    private static readonly byte[] Exe = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void Embedded_release_public_key_parses_as_P256()
    {
        using var key = UpdateVerifier.LoadKey(UpdateSigningKey.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, key.ExportParameters(false).Curve.Oid.Value);
        _ = new UpdateVerifier(); // the default constructor uses it
    }

    [Fact]
    public void Valid_signature_and_checksum_pass()
    {
        var (key, pem) = NewKey();
        using (key)
        {
            var v = new UpdateVerifier(pem);
            var r = v.Verify(new MemoryStream(Exe), Sign(key, Exe), Sha256File(Exe), Exe.Length);
            Assert.True(r.Ok, r.Detail);
        }
    }

    [Fact]
    public void A_single_changed_byte_is_rejected()
    {
        var (key, pem) = NewKey();
        using (key)
        {
            var sig = Sign(key, Exe);
            var tampered = (byte[])Exe.Clone();
            tampered[1234] ^= 1;
            var v = new UpdateVerifier(pem);
            // checksum file made for the tampered file too: the signature still catches it
            var r = v.Verify(new MemoryStream(tampered), sig, Sha256File(tampered), tampered.Length);
            Assert.Equal(VerifyFailure.SignatureInvalid, r.Failure);
            // with the original checksum the checksum check catches it first
            Assert.Equal(VerifyFailure.ChecksumMismatch, v.Verify(new MemoryStream(tampered), sig, Sha256File(Exe), tampered.Length).Failure);
        }
    }

    [Fact]
    public void Signature_from_another_key_is_rejected()
    {
        var (attacker, _) = NewKey();
        var (_, releasePem) = NewKey();
        using (attacker)
        {
            var r = new UpdateVerifier(releasePem).Verify(new MemoryStream(Exe), Sign(attacker, Exe), Sha256File(Exe), Exe.Length);
            Assert.Equal(VerifyFailure.SignatureInvalid, r.Failure);
        }
    }

    [Fact]
    public void Release_key_rejects_a_test_signature()
    {
        var (key, _) = NewKey();
        using (key)
            Assert.False(new UpdateVerifier().Verify(new MemoryStream(Exe), Sign(key, Exe), Sha256File(Exe), Exe.Length).Ok);
    }

    [Fact]
    public void Garbage_or_empty_signature_is_rejected()
    {
        var (key, pem) = NewKey();
        key.Dispose();
        var v = new UpdateVerifier(pem);
        Assert.Equal(VerifyFailure.SignatureInvalid, v.Verify(new MemoryStream(Exe), [], Sha256File(Exe), Exe.Length).Failure);
        Assert.Equal(VerifyFailure.SignatureInvalid, v.Verify(new MemoryStream(Exe), [0x30, 0x02, 0x01], Sha256File(Exe), Exe.Length).Failure);
    }

    [Fact]
    public void Size_mismatch_is_rejected()
    {
        var (key, pem) = NewKey();
        using (key)
            Assert.Equal(VerifyFailure.SizeMismatch,
                new UpdateVerifier(pem).Verify(new MemoryStream(Exe), Sign(key, Exe), Sha256File(Exe), Exe.Length + 1).Failure);
    }

    [Fact]
    public void Non_P256_keys_are_refused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<CryptographicException>(() => new UpdateVerifier(p384.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("{0}  RambiBox.exe", true)]
    [InlineData("{0} *RambiBox.exe", true)]
    [InlineData("{0}  rambibox.exe\n", true)]
    [InlineData("﻿{0}  RambiBox.exe", true)]
    [InlineData("{0}", true)]
    [InlineData("{0}\r\n", true)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  Other.exe\n{0}  RambiBox.exe", true)]
    [InlineData("{0}  Other.exe", false)]
    [InlineData("{0}00  RambiBox.exe", false)]
    [InlineData("xyz  RambiBox.exe", false)]
    [InlineData("", false)]
    public void Parses_sha256sum_files(string format, bool ok)
    {
        var hash = SHA256.HashData(Exe);
        var text = string.Format(format, Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal(ok, UpdateVerifier.TryParseChecksum(text, "RambiBox.exe", out var parsed));
        if (ok) Assert.Equal(hash, parsed);
    }
}

public class UpdateDownloaderTests
{
    private static readonly byte[] Exe = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();

    private static (ReleaseInfo Release, FakeHandler Handler, string Pem, ECDsa Key) Setup(byte[] served, Func<byte[], byte[]>? signOverride = null)
    {
        var (key, pem) = NewKey();
        var release = ReleaseParser.Parse(ReleasesJson(new FakeRelease("v1.2.0", ExeSize: Exe.Length))).Single();
        var sig = signOverride?.Invoke(Exe) ?? Sign(key, Exe);
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/RambiBox.exe") => FakeHandler.Bytes(served),
            var p when p.EndsWith(".sig") => FakeHandler.Bytes(sig),
            var p when p.EndsWith(".sha256") => FakeHandler.Ok(Sha256File(Exe)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        return (release, handler, pem, key);
    }

    [Fact]
    public async Task Downloads_verifies_and_keeps_only_the_latest()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "1.1.5"));
        File.WriteAllText(Path.Combine(dir, "1.1.5", "RambiBox.exe"), "old download");
        var (release, handler, pem, key) = Setup(Exe);
        using var keyScope = key;
        using var http = new HttpClient(handler);
        var dl = new UpdateDownloader(http, dir, new UpdateVerifier(pem));
        var progress = new List<DownloadProgress>();

        var r = await dl.DownloadAsync(release, new SyncProgress(progress.Add));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.Combine(dir, "1.2.0", "RambiBox.exe"), r.ExePath);
        Assert.Equal(Exe, File.ReadAllBytes(r.ExePath!));
        Assert.False(File.Exists(r.ExePath + ".part"));
        Assert.False(Directory.Exists(Path.Combine(dir, "1.1.5")));
        Assert.Contains(progress, p => p.Verifying);
        Assert.Contains(progress, p => !p.Verifying && p.Received == Exe.Length);
        Assert.True(dl.VerifyDownloaded(release, out _).Ok);

        // tampering with the file after the download is caught right before installing
        var bytes = File.ReadAllBytes(r.ExePath!);
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(r.ExePath!, bytes);
        Assert.False(dl.VerifyDownloaded(release, out _).Ok);
    }

    [Fact]
    public async Task Tampered_download_is_deleted_and_reported()
    {
        var dir = TempDir();
        var tampered = (byte[])Exe.Clone();
        tampered[100] ^= 0x55;
        var (release, handler, pem, key) = Setup(tampered);
        using var keyScope = key;
        using var http = new HttpClient(handler);
        var r = await new UpdateDownloader(http, dir, new UpdateVerifier(pem)).DownloadAsync(release);
        Assert.Equal(DownloadStatus.VerificationFailed, r.Status);
        Assert.Null(r.ExePath);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "1.2.0"), "RambiBox.exe*"));
    }

    [Fact]
    public async Task Wrong_signing_key_is_rejected()
    {
        var dir = TempDir();
        var (other, _) = NewKey();
        using (other)
        {
            var (release, handler, pem, key) = Setup(Exe, data => Sign(other, data));
            using var keyScope = key;
            using var http = new HttpClient(handler);
            var r = await new UpdateDownloader(http, dir, new UpdateVerifier(pem)).DownloadAsync(release);
            Assert.Equal(DownloadStatus.VerificationFailed, r.Status);
            Assert.False(File.Exists(Path.Combine(dir, "1.2.0", "RambiBox.exe")));
        }
    }

    [Fact]
    public async Task Network_failure_leaves_no_partial_file()
    {
        var dir = TempDir();
        var (release, _, pem, key) = Setup(Exe);
        using var _k = key;
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Connection reset")));
        var r = await new UpdateDownloader(http, dir, new UpdateVerifier(pem)).DownloadAsync(release);
        Assert.Equal(DownloadStatus.Offline, r.Status);
        Assert.False(File.Exists(Path.Combine(dir, "1.2.0", "RambiBox.exe.part")));
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
