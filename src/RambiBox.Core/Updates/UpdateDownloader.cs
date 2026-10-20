using System.Text;

namespace RambiBox.Core.Updates;

public enum DownloadStatus { Ok, Offline, VerificationFailed, Failed, Cancelled }

/// <summary>Outcome of downloading + verifying an update. <see cref="ExePath"/> is set only when it verified.</summary>
public sealed record DownloadResult(DownloadStatus Status, string? ExePath = null, string? Error = null)
{
    public bool Ok => Status == DownloadStatus.Ok;
}

/// <param name="Received">Bytes of the exe downloaded so far.</param>
/// <param name="Total">Size of the exe.</param>
/// <param name="Verifying">Download complete, checking the signature.</param>
public readonly record struct DownloadProgress(long Received, long Total, bool Verifying)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>
/// Downloads a release's RambiBox.exe (+ signature and checksum) to
/// <c>%LocalAppData%\RambiBox\updates\&lt;version&gt;\</c>: written to a .part file, verified, then renamed, so a
/// half-downloaded or unverified file never has the final name. Only the latest download is kept.
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>Largest exe accepted (the real one is ~70 MB).</summary>
    public const long MaxExeBytes = 400L * 1024 * 1024;
    private const int MaxSmallFileBytes = 16 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly UpdateVerifier _verifier;

    public string UpdatesDirectory { get; }

    public UpdateDownloader(HttpClient http, string updatesDirectory, UpdateVerifier verifier)
    {
        _http = http;
        UpdatesDirectory = updatesDirectory;
        _verifier = verifier;
    }

    public string DirectoryFor(SemVersion v) => Path.Combine(UpdatesDirectory, v.ToString());

    public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (release.Exe.Size is <= 0 or > MaxExeBytes)
            return new DownloadResult(DownloadStatus.Failed, Error: $"Unexpected download size ({release.Exe.Size} bytes)");
        var dir = DirectoryFor(release.Version);
        var exe = Path.Combine(dir, ReleaseParser.ExeName);
        var part = exe + ".part";
        try
        {
            Directory.CreateDirectory(dir);
            var sig = await GetSmallAsync(release.Signature.Url, ct).ConfigureAwait(false);
            var sumText = Encoding.UTF8.GetString(await GetSmallAsync(release.Checksum.Url, ct).ConfigureAwait(false));

            if (File.Exists(exe))
            {
                // downloaded earlier (e.g. by the automatic installer): reuse when it still verifies
                progress?.Report(new DownloadProgress(release.Exe.Size, release.Exe.Size, true));
                var again = await Task.Run(() => _verifier.VerifyFile(exe, sig, sumText, release.Exe.Size), ct).ConfigureAwait(false);
                if (again.Ok)
                {
                    await SaveProofAsync(dir, sig, sumText).ConfigureAwait(false);
                    Cleanup(UpdatesDirectory, release.Version);
                    return new DownloadResult(DownloadStatus.Ok, exe);
                }
                File.Delete(exe);
            }

            await DownloadToAsync(release.Exe, part, progress, ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(release.Exe.Size, release.Exe.Size, true));
            var result = await Task.Run(() => _verifier.VerifyFile(part, sig, sumText, release.Exe.Size), ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                TryDelete(part);
                Log.Warn($"Update {release.Version} rejected: {result.Detail}");
                return new DownloadResult(DownloadStatus.VerificationFailed, Error: result.Detail);
            }
            File.Move(part, exe, overwrite: true);
            await SaveProofAsync(dir, sig, sumText).ConfigureAwait(false);
            Cleanup(UpdatesDirectory, release.Version);
            Log.Info($"Update {release.Version} downloaded and verified ({release.Exe.Size / 1048576.0:0.0} MB)");
            return new DownloadResult(DownloadStatus.Ok, exe);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(part);
            return new DownloadResult(DownloadStatus.Cancelled, Error: "Cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            TryDelete(part);
            return new DownloadResult(DownloadStatus.Offline, Error: ex is OperationCanceledException ? "The download stalled" : ex.Message);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return new DownloadResult(DownloadStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Verifies a finished download again (right before installing it). Uses the saved signature and checksum.</summary>
    public VerifyResult VerifyDownloaded(ReleaseInfo release, out string exePath)
    {
        var dir = DirectoryFor(release.Version);
        exePath = Path.Combine(dir, ReleaseParser.ExeName);
        try
        {
            var sig = File.ReadAllBytes(Path.Combine(dir, ReleaseParser.SignatureName));
            var sum = File.ReadAllText(Path.Combine(dir, ReleaseParser.ChecksumName));
            return _verifier.VerifyFile(exePath, sig, sum, release.Exe.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VerifyResult(VerifyFailure.Unreadable, ex.Message);
        }
    }

    private static async Task SaveProofAsync(string dir, byte[] sig, string sumText)
    {
        await File.WriteAllBytesAsync(Path.Combine(dir, ReleaseParser.SignatureName), sig).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, ReleaseParser.ChecksumName), sumText).ConfigureAwait(false);
    }

    private async Task<byte[]> GetSmallAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(GitHubReleaseClient.RequestTimeout);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxSmallFileBytes) throw new InvalidDataException("Unexpectedly large signature/checksum file");
        }
        return ms.ToArray();
    }

    private async Task DownloadToAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len && len != asset.Size)
            throw new InvalidDataException($"The server sends {len} bytes, the release says {asset.Size}");
        await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            long received = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout); // a timeout for a stalled connection, not for a slow one
                received += n;
                if (received > asset.Size) throw new InvalidDataException("The download is larger than the release says");
                await dst.WriteAsync(buf.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                if (received - lastReport >= 256 * 1024 || received == asset.Size)
                {
                    lastReport = received;
                    progress?.Report(new DownloadProgress(received, asset.Size, false));
                }
            }
            await dst.FlushAsync(stall.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes downloads of other versions than <paramref name="keep"/> (null = all), and stray .part files. Never throws.
    /// </summary>
    public static void Cleanup(string updatesDirectory, SemVersion? keep) =>
        CleanupWhere(updatesDirectory, v => keep is not null && v == keep);

    /// <summary>Deletes the downloads whose version doesn't satisfy <paramref name="keep"/>. Never throws.</summary>
    public static void CleanupWhere(string updatesDirectory, Func<SemVersion, bool> keep)
    {
        try
        {
            if (!Directory.Exists(updatesDirectory)) return;
            foreach (var d in Directory.GetDirectories(updatesDirectory))
            {
                if (!SemVersion.TryParse(Path.GetFileName(d), out var v)) continue;
                if (keep(v)) continue;
                try { Directory.Delete(d, recursive: true); }
                catch (Exception ex) { Log.Warn($"Could not delete the old update download {d}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Update download cleanup failed: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
