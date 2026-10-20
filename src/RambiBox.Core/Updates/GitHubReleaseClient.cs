using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RambiBox.Core.Config;

namespace RambiBox.Core.Updates;

public enum CheckStatus
{
    UpToDate,
    UpdateAvailable,
    /// <summary>No connection, DNS failure or timeout.</summary>
    Offline,
    /// <summary>GitHub's limit for anonymous requests (60/hour per IP address) is used up; see RetryAfter.</summary>
    RateLimited,
    /// <summary>GitHub answered with an error or something unreadable.</summary>
    Failed,
}

/// <summary>Outcome of an update check. Never an exception.</summary>
public sealed record UpdateCheckResult(CheckStatus Status, ReleaseInfo? Update = null, string? Error = null,
    DateTimeOffset? RetryAfter = null, DateTimeOffset? CheckedAt = null)
{
    public bool Succeeded => Status is CheckStatus.UpToDate or CheckStatus.UpdateAvailable;
}

/// <summary>What is kept between checks: the ETag and body of the last answer (conditional requests) and a rate limit.</summary>
internal sealed class ReleaseCache
{
    public string? ETag { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset? RateLimitedUntil { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseCache))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

/// <summary>
/// Asks GitHub for the latest releases of RambiBox. The only data sent is a normal HTTPS request to api.github.com
/// (User-Agent "RambiBox/&lt;version&gt;", and the ETag of the previous answer so an unchanged list costs nothing).
/// Honours GitHub's rate limits and never throws.
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    public const string ReleasesUrl = "https://api.github.com/repos/Ferrywell/RambiBox/releases?per_page=10";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();

    /// <param name="cacheDirectory">Where the ETag cache lives (%LocalAppData%\RambiBox\updates).</param>
    /// <param name="appVersion">Sent in the User-Agent (GitHub requires one).</param>
    /// <param name="http">Shared client (tests pass one with a fake handler); null = a new one.</param>
    public GitHubReleaseClient(string cacheDirectory, string appVersion, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _cacheFile = Path.Combine(cacheDirectory, "releases-cache.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ownsHttp = http is null;
        _http = http ?? CreateHttpClient(appVersion);
    }

    /// <summary>An HttpClient for api.github.com and the release downloads (timeouts are applied per request).</summary>
    public static HttpClient CreateHttpClient(string appVersion, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        ApplyUserAgent(http, appVersion);
        return http;
    }

    private static void ApplyUserAgent(HttpClient http, string appVersion)
    {
        http.DefaultRequestHeaders.UserAgent.Clear();
        var v = SemVersion.TryParse(appVersion, out var sv) ? sv.ToString() : "0.0.0";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RambiBox", v));
    }

    /// <summary>Checks for an update newer than <paramref name="current"/> on <paramref name="channel"/>.</summary>
    public async Task<UpdateCheckResult> CheckAsync(SemVersion current, UpdateChannel channel, CancellationToken ct = default)
    {
        var now = _clock();
        var cache = ReadCache();
        if (cache.RateLimitedUntil is { } until && until > now)
            return new UpdateCheckResult(CheckStatus.RateLimited, Error: "GitHub's request limit is used up for now.", RetryAfter: until);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(cache.ETag) && !string.IsNullOrEmpty(cache.Body)
                && EntityTagHeaderValue.TryParse(cache.ETag, out var etag))
                req.Headers.IfNoneMatch.Add(etag);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            string body;
            if (resp.StatusCode == HttpStatusCode.NotModified && cache.Body is not null)
            {
                body = cache.Body;
            }
            else if (resp.IsSuccessStatusCode)
            {
                body = await ReadLimitedAsync(resp.Content, timeout.Token).ConfigureAwait(false);
                cache.ETag = resp.Headers.ETag?.ToString();
                cache.Body = body;
            }
            else if (RateLimit(resp, now) is { } limitedUntil)
            {
                cache.RateLimitedUntil = limitedUntil;
                WriteCache(cache);
                Log.Info($"Update check: GitHub rate limit reached; next try after {limitedUntil.ToLocalTime():HH:mm}");
                return new UpdateCheckResult(CheckStatus.RateLimited, Error: "GitHub's request limit is used up for now.", RetryAfter: limitedUntil);
            }
            else
            {
                return new UpdateCheckResult(CheckStatus.Failed, Error: $"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd());
            }

            if (!LooksLikeArray(body))
                return new UpdateCheckResult(CheckStatus.Failed, Error: "GitHub's answer could not be read.");
            cache.RateLimitedUntil = null;
            WriteCache(cache);
            return Evaluate(body, current, channel, now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(CheckStatus.Offline, Error: "GitHub didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(CheckStatus.Offline, Error: ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(CheckStatus.Failed, Error: "Cancelled.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(CheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Re-evaluates the last answer without network access (e.g. after switching the channel); null without one.</summary>
    public UpdateCheckResult? EvaluateCached(SemVersion current, UpdateChannel channel)
    {
        var cache = ReadCache();
        return cache.Body is { } body && LooksLikeArray(body) ? Evaluate(body, current, channel, null) : null;
    }

    private static UpdateCheckResult Evaluate(string body, SemVersion current, UpdateChannel channel, DateTimeOffset? checkedAt)
    {
        var update = ReleaseParser.SelectUpdate(ReleaseParser.Parse(body), current, channel);
        return update is null
            ? new UpdateCheckResult(CheckStatus.UpToDate, CheckedAt: checkedAt)
            : new UpdateCheckResult(CheckStatus.UpdateAvailable, update, CheckedAt: checkedAt);
    }

    private static bool LooksLikeArray(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>When a 403/429 answer is GitHub's rate limit: the time it ends (at least one minute, at most a day).</summary>
    internal static DateTimeOffset? RateLimit(HttpResponseMessage resp, DateTimeOffset now)
    {
        if (resp.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;
        DateTimeOffset? until = null;
        if (resp.Headers.RetryAfter is { } ra)
            until = ra.Delta is { } delta ? now + delta : ra.Date;
        if (until is null && Header(resp, "x-ratelimit-remaining") == "0"
            && long.TryParse(Header(resp, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out long epoch))
            until = DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (until is null)
        {
            if (resp.StatusCode != HttpStatusCode.TooManyRequests && Header(resp, "x-ratelimit-remaining") is null) return null; // a plain 403
            until = now + TimeSpan.FromHours(1);
        }
        var min = now + TimeSpan.FromMinutes(1);
        var max = now + TimeSpan.FromDays(1);
        return until < min ? min : until > max ? max : until;
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault()?.Trim() : null;

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxBodyBytes) throw new InvalidDataException("The answer is too large.");
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBodyBytes) throw new InvalidDataException("The answer is too large.");
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private ReleaseCache ReadCache()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_cacheFile))
                    return JsonSerializer.Deserialize(File.ReadAllText(_cacheFile), UpdateJsonContext.Default.ReleaseCache) ?? new();
            }
            catch (Exception ex) { Log.Warn($"Update cache unreadable ({ex.Message}); starting fresh"); }
            return new();
        }
    }

    private void WriteCache(ReleaseCache cache)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                var tmp = _cacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, UpdateJsonContext.Default.ReleaseCache), new UTF8Encoding(false));
                File.Move(tmp, _cacheFile, overwrite: true);
            }
            catch (Exception ex) { Log.Warn($"Could not write the update cache: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
