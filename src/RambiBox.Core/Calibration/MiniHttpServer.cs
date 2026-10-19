using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RambiBox.Core.Calibration;

internal sealed record MiniHttpRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers,
    byte[] Body, IPAddress Remote, bool IsTls = false);

internal sealed record MiniHttpResponse(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static MiniHttpResponse Text(int status, string text) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// Minimal HTTP/1.1 server on a raw <see cref="TcpListener"/> (HttpListener would need an admin URL ACL to listen on
/// the LAN). One request per connection (Connection: close), bounded header/body sizes, read timeouts and a cap on
/// concurrent connections, both in total and per client IP (so one LAN host holding connections open with slow
/// headers cannot lock the phone out). Never throws out of its background tasks.
/// With <see cref="ServerCertificate"/> set it speaks HTTPS instead (TLS 1.2/1.3 via SslStream; the handshake must
/// finish within <see cref="HeaderTimeout"/>, like the headers).
/// </summary>
internal sealed class MiniHttpServer : IDisposable
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxConcurrent = 16;
    /// <summary>Concurrent connections allowed per client IP; more are answered 503 at once.</summary>
    public const int MaxPerClient = 4;

    private readonly Func<MiniHttpRequest, CancellationToken, Task<MiniHttpResponse>> _handler;
    private readonly Func<string, string, IPAddress, MiniHttpResponse?> _precheck;
    private readonly int _maxBodyBytes;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly Dictionary<IPAddress, int> _perClient = new();
    private int _activeTasks;

    /// <param name="precheck">(method, path, client IP) → null to accept the request, or the response to reject it
    /// with (unknown route, forbidden client, ...). Runs before the request body is read, so rejected requests never
    /// get their upload accepted.</param>
    public MiniHttpServer(Func<MiniHttpRequest, CancellationToken, Task<MiniHttpResponse>> handler,
        Func<string, string, IPAddress, MiniHttpResponse?> precheck, int maxBodyBytes)
    {
        _handler = handler;
        _precheck = precheck;
        _maxBodyBytes = maxBodyBytes;
    }

    public int Port { get; private set; }
    /// <summary>Time allowed for the request line + headers; slow-header clients are dropped after this.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>When set, connections are TLS (HTTPS) with this certificate (must have a private key).</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>
    /// Called (thread pool) when a TLS handshake fails, with the client address and the reason — typically the phone
    /// does not trust the certificate yet. Used for diagnostics shown on the phone and in the log.
    /// </summary>
    public Action<IPAddress, string>? TlsHandshakeFailed { get; init; }

    public bool IsTls => ServerCertificate is not null;

    /// <summary>Client IP of a connection. Tests substitute other addresses to simulate several LAN hosts.</summary>
    internal Func<IPEndPoint, IPAddress> ClientAddress { get; init; } = ep => ep.Address;

    /// <summary>Connections currently being served (tests).</summary>
    internal int ActiveConnections => Volatile.Read(ref _activeTasks);

    /// <summary>Binds to 0.0.0.0 on the first free port in [first, last]. Returns false if none is free.</summary>
    public bool Start(int firstPort, int lastPort)
    {
        for (int port = firstPort; port <= lastPort; port++)
        {
            var l = new TcpListener(IPAddress.Any, port);
            try
            {
                l.Server.ExclusiveAddressUse = true;
                l.Start(32);
            }
            catch (SocketException)
            {
                try { l.Stop(); } catch { }
                continue;
            }
            _listener = l;
            Port = port;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(l, _cts.Token));
            return true;
        }
        return false;
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn($"Calibration server accept failed: {ex.Message}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }
            catch (Exception ex)
            {
                Log.Warn($"Calibration server accept error: {ex.Message}");
                break;
            }

            IPAddress? ip = null;
            try
            {
                if (client.Client.RemoteEndPoint is IPEndPoint ep)
                {
                    ip = ClientAddress(ep);
                    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            if (ip is null || !LanAddress.IsAllowedClient(ip))
            {
                try { client.Dispose(); } catch { } // not from the local network: close without a word
                continue;
            }
            if (!TryEnterClient(ip))
            {
                Reject(client, "Too many connections from this device.", IsTls); // per-IP cap
                continue;
            }
            if (!_slots.Wait(0))
            {
                LeaveClient(ip);
                Reject(client, "Server busy.", IsTls); // too many concurrent connections in total
                continue;
            }
            Interlocked.Increment(ref _activeTasks);
            var clientIp = ip;
            _ = Task.Run(async () =>
            {
                try { await Serve(client, clientIp, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
                catch (Exception ex) { Log.Warn($"Calibration server: request failed: {ex.Message}"); }
                finally
                {
                    try { client.Dispose(); } catch { }
                    Interlocked.Decrement(ref _activeTasks);
                    LeaveClient(clientIp);
                    _slots.Release();
                }
            });
        }
    }

    private bool TryEnterClient(IPAddress ip)
    {
        lock (_perClient)
        {
            _perClient.TryGetValue(ip, out int n);
            if (n >= MaxPerClient) return false;
            _perClient[ip] = n + 1;
            return true;
        }
    }

    private void LeaveClient(IPAddress ip)
    {
        lock (_perClient)
        {
            if (!_perClient.TryGetValue(ip, out int n)) return;
            if (n <= 1) _perClient.Remove(ip);
            else _perClient[ip] = n - 1;
        }
    }

    /// <summary>
    /// Answers 503 right away and closes the connection. Runs on the accept loop: a ~200-byte write into a fresh
    /// socket's empty send buffer does not block in practice, and a send timeout bounds it anyway.
    /// </summary>
    private static void Reject(TcpClient client, string message, bool tls)
    {
        try
        {
            if (tls) return; // no plain-text answer possible on a TLS port: just close (finally)
            client.Client.SendTimeout = 500;
            client.Client.Send(Format(MiniHttpResponse.Text(503, message), headOnly: false));
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private async Task Serve(TcpClient client, IPAddress remote, CancellationToken serverCt)
    {
        client.NoDelay = true;
        Stream stream = client.GetStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(HeaderTimeout);
        SslStream? ssl = null;
        if (ServerCertificate is { } cert)
        {
            ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or Win32Exception)
            {
                // Usually the phone does not trust the certificate yet (that is how the page detects it): not an error,
                // but report it so the phone page can tell the user exactly what is missing.
                string reason = ex is OperationCanceledException ? "handshake timed out"
                    : ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
                try { TlsHandshakeFailed?.Invoke(remote, reason); } catch { /* diagnostics only */ }
                await ssl.DisposeAsync().ConfigureAwait(false);
                return;
            }
            stream = ssl;
        }
        await using var sslScope = ssl;

        // ---- headers
        var buf = new byte[MaxHeaderBytes];
        int len = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (len == buf.Length)
            {
                await Write(stream, MiniHttpResponse.Text(431, "Request header too large"), cts.Token).ConfigureAwait(false);
                return;
            }
            int n = await stream.ReadAsync(buf.AsMemory(len), cts.Token).ConfigureAwait(false);
            if (n <= 0) return;
            int searchFrom = Math.Max(0, len - 3);
            len += n;
            headerEnd = IndexOfCrlfCrlf(buf, searchFrom, len);
        }
        string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            await Write(stream, MiniHttpResponse.Text(400, "Bad request"), cts.Token).ConfigureAwait(false);
            return;
        }
        string method = parts[0].ToUpperInvariant();
        string target = parts[1];
        int q = target.IndexOf('?');
        string path = q >= 0 ? target[..q] : target, query = q >= 0 ? target[(q + 1)..] : "";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }

        if (_precheck(method, path, remote) is { } rejection)
        {
            await Write(stream, rejection, cts.Token, method == "HEAD").ConfigureAwait(false);
            return;
        }

        // ---- body
        byte[] body = [];
        if (headers.TryGetValue("Transfer-Encoding", out var te) && !te.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            await Write(stream, MiniHttpResponse.Text(411, "Content-Length required"), cts.Token).ConfigureAwait(false);
            return;
        }
        if (headers.TryGetValue("Content-Length", out var clText))
        {
            if (!long.TryParse(clText, out long cl) || cl < 0)
            {
                await Write(stream, MiniHttpResponse.Text(400, "Bad Content-Length"), cts.Token).ConfigureAwait(false);
                return;
            }
            if (cl > _maxBodyBytes)
            {
                await Write(stream, MiniHttpResponse.Text(413, "Upload too large"), cts.Token).ConfigureAwait(false);
                return;
            }
            body = new byte[cl];
            int have = Math.Min((int)cl, len - (headerEnd + 4));
            Buffer.BlockCopy(buf, headerEnd + 4, body, 0, have);
            cts.CancelAfter(BodyTimeout);
            while (have < cl)
            {
                int n = await stream.ReadAsync(body.AsMemory(have), cts.Token).ConfigureAwait(false);
                if (n <= 0) return;
                have += n;
            }
        }

        var req = new MiniHttpRequest(method, path, query, headers, body, remote, IsTls);
        MiniHttpResponse resp;
        try { resp = await _handler(req, serverCt).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Error("Calibration server handler failed", ex);
            resp = MiniHttpResponse.Text(500, "Internal error");
        }
        using var wcts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        wcts.CancelAfter(TimeSpan.FromSeconds(30));
        await Write(stream, resp, wcts.Token, method == "HEAD").ConfigureAwait(false);
    }

    private static int IndexOfCrlfCrlf(byte[] b, int from, int len)
    {
        for (int i = from; i + 3 < len; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task Write(Stream stream, MiniHttpResponse r, CancellationToken ct, bool headOnly = false)
    {
        await stream.WriteAsync(Format(r, headOnly: true), ct).ConfigureAwait(false);
        if (!headOnly && r.Body.Length > 0) await stream.WriteAsync(r.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Status line + headers, followed by the body unless <paramref name="headOnly"/>.</summary>
    private static byte[] Format(MiniHttpResponse r, bool headOnly)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        if (r.Headers is not null)
            foreach (var kv in r.Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (headOnly || r.Body.Length == 0) return head;
        var all = new byte[head.Length + r.Body.Length];
        head.CopyTo(all, 0);
        r.Body.CopyTo(all, head.Length);
        return all;
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
