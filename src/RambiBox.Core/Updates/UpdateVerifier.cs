using System.Security.Cryptography;

namespace RambiBox.Core.Updates;

public enum VerifyFailure { None, SizeMismatch, ChecksumUnreadable, ChecksumMismatch, SignatureInvalid, Unreadable }

public sealed record VerifyResult(VerifyFailure Failure, string Detail)
{
    public bool Ok => Failure == VerifyFailure.None;
    public static VerifyResult Success { get; } = new(VerifyFailure.None, "Signature and checksum verified");
}

/// <summary>
/// Checks a downloaded RambiBox.exe before it may be installed: the size from the release, the SHA-256 from the
/// RambiBox.exe.sha256 file, and — the part that matters — the ECDSA P-256 signature (DER, over the exe bytes, as made
/// by <c>openssl dgst -sha256 -sign</c>) against the public key built into the app. All three must pass.
/// </summary>
public sealed class UpdateVerifier
{
    private readonly string _publicKeyPem;

    public UpdateVerifier(string? publicKeyPem = null)
    {
        _publicKeyPem = publicKeyPem ?? UpdateSigningKey.PublicKeyPem;
        using var key = LoadKey(_publicKeyPem); // fail fast on a broken key
    }

    /// <summary>Loads a P-256 public key from PEM; throws when it isn't one.</summary>
    public static ECDsa LoadKey(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != 256) throw new CryptographicException($"Expected a P-256 key, got {key.KeySize} bits");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public VerifyResult VerifyFile(string path, byte[] signature, string checksumText, long expectedSize)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Verify(fs, signature, checksumText, expectedSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VerifyResult(VerifyFailure.Unreadable, ex.Message);
        }
    }

    /// <param name="expectedSize">Size from the release (-1 = don't check).</param>
    public VerifyResult Verify(Stream data, byte[] signature, string checksumText, long expectedSize, string fileName = ReleaseParser.ExeName)
    {
        if (!TryParseChecksum(checksumText, fileName, out var expectedHash))
            return new VerifyResult(VerifyFailure.ChecksumUnreadable, "The checksum file could not be read");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = data.Read(buf, 0, buf.Length)) > 0)
        {
            sha.AppendData(buf, 0, n);
            total += n;
        }
        var hash = sha.GetHashAndReset();

        if (expectedSize >= 0 && total != expectedSize)
            return new VerifyResult(VerifyFailure.SizeMismatch, $"Size is {total} bytes, the release says {expectedSize}");
        if (!CryptographicOperations.FixedTimeEquals(hash, expectedHash))
            return new VerifyResult(VerifyFailure.ChecksumMismatch, "SHA-256 doesn't match the checksum file");

        using var key = LoadKey(_publicKeyPem);
        bool valid;
        try { valid = signature.Length is > 0 and < 512 && key.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { valid = false; }
        return valid ? VerifyResult.Success : new VerifyResult(VerifyFailure.SignatureInvalid, "The digital signature is not valid");
    }

    /// <summary>
    /// Reads a <c>sha256sum</c> style file ("&lt;64 hex&gt;  RambiBox.exe", "&lt;hex&gt; *RambiBox.exe" or just the hash).
    /// A line naming another file is ignored.
    /// </summary>
    public static bool TryParseChecksum(string? text, string fileName, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Replace("﻿", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 64) continue;
            var hex = line[..64];
            if (!hex.All(char.IsAsciiHexDigit)) continue;
            var rest = line[64..];
            if (rest.Length > 0)
            {
                if (!char.IsWhiteSpace(rest[0])) continue; // longer hex string: not SHA-256
                var name = rest.Trim().TrimStart('*').Trim();
                if (name.Length > 0 && !name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            }
            hash = Convert.FromHexString(hex);
            return true;
        }
        return false;
    }
}
