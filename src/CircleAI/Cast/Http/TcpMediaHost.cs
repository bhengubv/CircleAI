// TcpMediaHost.cs — (3.5.0) Minimal pure-managed HTTP/1.1 media host on TcpListener.
// Serves each published asset (bytes or file) at its own URL, with HTTP Range so a
// DLNA renderer can seek/stream. Deliberately NOT HttpListener: HttpListener needs a
// URL-ACL reservation for non-loopback prefixes on Windows (admin-only), which breaks
// unprivileged LAN serving. A raw TcpListener bound to the LAN IP works unprivileged
// on Windows, Linux and Android alike — the right fit for low-end de-Googled phones.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast.Http;

/// <summary>
/// LAN HTTP host for cast media. Bind to a specific local IPv4 (the one the renderer
/// can reach — see <c>CircleAI.Cast.Net.LocalAddress</c>) and publish assets on demand.
/// One host per bind address; the port is chosen by the OS (never guessed).
/// </summary>
public sealed class TcpMediaHost : ILocalMediaHost
{
    private sealed record Resource(string Mime, long Length, byte[]? Bytes, string? FilePath);

    private readonly IPAddress _bind;
    private readonly ConcurrentDictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _port;

    public string BackendId => "tcp-http";
    public bool IsRunning => _listener is not null;
    public Uri? BaseUrl => IsRunning ? new Uri($"http://{_bind}:{_port.ToString(CultureInfo.InvariantCulture)}/") : null;

    /// <summary>Create a host that will bind to <paramref name="bindAddress"/> when started.</summary>
    public TcpMediaHost(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        _bind = bindAddress;
    }

    public ValueTask StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_listener is not null) return ValueTask.CompletedTask;

            var listener = new TcpListener(_bind, 0);
            listener.Start();
            // NOTE: TcpListener.LocalEndpoint (lowercase 'p') is the public API; the OS
            // has assigned the ephemeral port by this point (post-Start).
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Uri> PublishAsync(CastMediaSource source, string mimeType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        if (!IsRunning) await StartAsync(ct).ConfigureAwait(false);

        var path = "/" + Guid.NewGuid().ToString("N") + GuessExtension(mimeType);
        Resource res = source switch
        {
            CastMediaSource.Bytes b => new Resource(mimeType, b.Data.Length, b.Data.ToArray(), null),
            CastMediaSource.File f  => new Resource(mimeType, new FileInfo(f.Path).Length, null, f.Path),
            CastMediaSource.Url     => throw new ArgumentException(
                "URL sources are already reachable; publish is only for bytes/file media.", nameof(source)),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        _resources[path] = res;
        return new Uri($"http://{_bind}:{_port.ToString(CultureInfo.InvariantCulture)}{path}");
    }

    public ValueTask UnpublishAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        _resources.TryRemove(url.AbsolutePath, out _);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            loop = _acceptLoop;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception) { /* shutdown races are expected */ }
        }
        _cts?.Dispose();
        _resources.Clear();
    }

    // ---- connection handling ------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                await ServeAsync(stream, request.Value.Method, request.Value.Path, request.Value.Range, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }
    }

    private static async Task<(string Method, string Path, string? Range)?> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int total = 0, headerEnd = -1;

        while (total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            headerEnd = IndexOfDoubleCrlf(buf, total);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return null;

        var head = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = head.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        string method = requestLine[0];
        string path = Uri.UnescapeDataString(requestLine[1]);
        string? range = null;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            if (line.AsSpan(0, c).Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                range = line[(c + 1)..].Trim();
        }
        return (method, path, range);
    }

    private static int IndexOfDoubleCrlf(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i] == (byte)'\n' && b[i - 1] == (byte)'\r' && b[i - 2] == (byte)'\n' && b[i - 3] == (byte)'\r')
                return i + 1;
        return -1;
    }

    private async Task ServeAsync(NetworkStream stream, string method, string path, string? rangeHeader, CancellationToken ct)
    {
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        bool isGet  = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        bool isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        if (!isGet && !isHead)
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", ct).ConfigureAwait(false);
            return;
        }
        if (!_resources.TryGetValue(path, out var res))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct).ConfigureAwait(false);
            return;
        }

        long start = 0, end = res.Length - 1;
        bool partial = res.Length > 0 && rangeHeader is not null && TryParseRange(rangeHeader, res.Length, out start, out end);
        long contentLength = res.Length == 0 ? 0 : end - start + 1;

        var sb = new StringBuilder(256);
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: ").Append(res.Mime).Append("\r\n");
        sb.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        if (partial)
            sb.Append("Content-Range: bytes ")
              .Append(start.ToString(CultureInfo.InvariantCulture)).Append('-')
              .Append(end.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(res.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("transferMode.dlna.org: ")
          .Append(res.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Interactive" : "Streaming")
          .Append("\r\n");
        sb.Append("contentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n");
        sb.Append("Server: CircleAI.Cast/3.5\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (isHead || contentLength == 0)
        {
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        if (res.Bytes is not null)
        {
            await stream.WriteAsync(res.Bytes.AsMemory((int)start, (int)contentLength), ct).ConfigureAwait(false);
        }
        else if (res.FilePath is not null)
        {
            await using var fs = new FileStream(
                res.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            fs.Seek(start, SeekOrigin.Begin);
            await CopyRangeAsync(fs, stream, contentLength, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task CopyRangeAsync(Stream src, Stream dst, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = await src.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
        }
    }

    private static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;

        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var spec = header[prefix.Length..];
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var startPart = spec[..dash].Trim();
        var endPart = spec[(dash + 1)..].Trim();
        int comma = endPart.IndexOf(','); // honour only the first range
        if (comma >= 0) endPart = endPart[..comma].Trim();

        if (startPart.Length == 0)
        {
            // suffix form: bytes=-N  => last N bytes
            if (!long.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(startPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                return false;
            if (endPart.Length == 0)
                end = length - 1;
            else if (!long.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
                return false;
        }

        if (start < 0 || end < start) return false;
        if (end > length - 1) end = length - 1;
        return start <= end;
    }

    private static async Task WriteStatusAsync(NetworkStream stream, int code, string reason, CancellationToken ct)
    {
        var msg = $"HTTP/1.1 {code.ToString(CultureInfo.InvariantCulture)} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(msg), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string GuessExtension(string mime) => mime.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        "video/mpeg" => ".mpg",
        "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/aac" => ".m4a",
        "audio/ogg" => ".ogg",
        "audio/wav" or "audio/x-wav" => ".wav",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin",
    };
}
