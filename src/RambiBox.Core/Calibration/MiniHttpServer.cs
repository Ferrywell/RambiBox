using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RambiBox.Core.Calibration;

internal sealed record MiniHttpRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers,
    byte[] Body, IPAddress Remote);

internal sealed record MiniHttpResponse(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static MiniHttpResponse Text(int status, string text) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// Minimal HTTP/1.1 server on a raw <see cref="TcpListener"/> (HttpListener would need an admin URL ACL to listen on
/// the LAN). One request per connection (Connection: close), bounded header/body sizes, read timeouts and a cap on
/// concurrent connections. Never throws out of its background tasks.
/// </summary>
internal sealed class MiniHttpServer : IDisposable
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxConcurrent = 16;

    private readonly Func<MiniHttpRequest, CancellationToken, Task<MiniHttpResponse>> _handler;
    private readonly Func<string, string, bool> _accepts;
    private readonly int _maxBodyBytes;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private int _activeTasks;

    /// <param name="accepts">(method, path) → whether the route exists; checked before reading a request body so
    /// unauthorized requests are rejected without accepting their upload.</param>
    public MiniHttpServer(Func<MiniHttpRequest, CancellationToken, Task<MiniHttpResponse>> handler,
        Func<string, string, bool> accepts, int maxBodyBytes)
    {
        _handler = handler;
        _accepts = accepts;
        _maxBodyBytes = maxBodyBytes;
    }

    public int Port { get; private set; }
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(120);

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

            if (!_slots.Wait(0))
            {
                client.Dispose(); // too many concurrent connections: drop
                continue;
            }
            Interlocked.Increment(ref _activeTasks);
            _ = Task.Run(async () =>
            {
                try { await Serve(client, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
                catch (Exception ex) { Log.Warn($"Calibration server: request failed: {ex.Message}"); }
                finally
                {
                    try { client.Dispose(); } catch { }
                    Interlocked.Decrement(ref _activeTasks);
                    _slots.Release();
                }
            });
        }
    }

    private async Task Serve(TcpClient client, CancellationToken serverCt)
    {
        client.NoDelay = true;
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !LanAddress.IsAllowedClient(remote.Address))
            return; // not from the local network: close without a word

        var stream = client.GetStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(HeaderTimeout);

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

        if (!_accepts(method, path))
        {
            await Write(stream, MiniHttpResponse.Text(404, "Not found"), cts.Token).ConfigureAwait(false);
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
                await Write(stream, MiniHttpResponse.Text(413, "Photo too large"), cts.Token).ConfigureAwait(false);
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

        var req = new MiniHttpRequest(method, path, query, headers, body, remote.Address);
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

    private static async Task Write(NetworkStream stream, MiniHttpResponse r, CancellationToken ct, bool headOnly = false)
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
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (!headOnly && r.Body.Length > 0) await stream.WriteAsync(r.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
