// HttpLoopbackEndpoint.cs
//
// Tiny HTTP server that exposes a AIService to other processes on the
// same host (e.g. a CircleOS keyboard talking to a CircleOS background
// service). Implementation is built on System.Net.HttpListener so we have
// zero ASP.NET Core dependency and stay portable across Linux / macOS /
// Windows / Android.
//
// Routes:
//   POST /butler/ask    -> { "question": string }                              -> text/plain
//   POST /butler/chat   -> { "messages": [...], "options": {...} }             -> { "content": string }
//   POST /butler/stream -> { "messages": [...], "options": {...} }             -> text/event-stream
//   POST /butler/tool   -> { "toolName": string, "arguments": {...} }          -> ToolResult JSON
//
// Authentication is a shared-secret header `X-Butler-Token`. Configure via
// AIOptions.LoopbackToken; when not set, the endpoint generates a
// random token at startup and exposes it via Token.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using CircleAI.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Hosting.Endpoints;

/// <summary>
/// Loopback HTTP transport for <see cref="IAIService"/>. Binds only to
/// <c>127.0.0.1</c> so we never expose Butler on the network.
/// </summary>
public sealed class HttpLoopbackEndpoint : IAIEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AIOptions _options;
    private readonly ILogger<HttpLoopbackEndpoint> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoop;
    private IAIService? _service;
    private string? _token;
    private int _boundPort;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Constructs the endpoint without starting it. Call <see cref="StartAsync"/>
    /// to bind and begin serving.
    /// </summary>
    public HttpLoopbackEndpoint(AIOptions options, ILogger<HttpLoopbackEndpoint>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<HttpLoopbackEndpoint>.Instance;
    }

    /// <summary>
    /// Port the listener is currently bound to. <c>0</c> when not started.
    /// </summary>
    public int BoundPort => _boundPort;

    /// <summary>
    /// Effective shared-secret token. <c>null</c> when not started.
    /// </summary>
    public string? Token => _token;

    /// <inheritdoc />
    public async Task StartAsync(IAIService service, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(service);
        if (_started) return;

        _service = service;
        _token = string.IsNullOrEmpty(_options.LoopbackToken)
            ? AIOptions.GenerateRandomToken()
            : _options.LoopbackToken;

        var configuredPort = _options.LoopbackPort;

        // AUTO-ASSIGNED PORTS RACE, so retry them. PickFreeLoopbackPort finds a
        // free port, releases it, and HttpListener claims it a moment later -
        // and in that window anything else on the host can take it first. On a
        // busy machine (a full test run binding dozens of loopback endpoints)
        // that window is hit often enough to fail one run in several. A fresh
        // pick almost never collides twice.
        //
        // A CONFIGURED port is different: if the caller asked for 8080 and 8080
        // is taken, that is a real error they need to see, not a race to paper
        // over - so it is tried once and allowed to throw.
        var autoAssigned = configuredPort <= 0;
        var attempts = autoAssigned ? 5 : 1;
        HttpListener? listener = null;

        for (var attempt = 1; ; attempt++)
        {
            var port = autoAssigned ? PickFreeLoopbackPort() : configuredPort;
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
                _boundPort = port;
                listener = candidate;
                break;
            }
            catch (HttpListenerException ex)
            {
                candidate.Close();
                if (attempt >= attempts)
                    throw new InvalidOperationException(
                        $"Failed to start loopback HTTP listener on port {port}. " +
                        "On Windows this may require URL ACL configuration; consider using port 0 to let the OS assign a port.",
                        ex);
                // Auto-assigned and lost the race: pick a different port and try again.
            }
        }

        _listener = listener;
        _serverCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
        _started = true;

        _logger.LogInformation("Butler HTTP loopback endpoint listening on http://127.0.0.1:{Port}", _boundPort);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_started) return;
        _started = false;

        try { _serverCts?.Cancel(); } catch (ObjectDisposedException) { /* CTS already disposed — nothing to do */ }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Butler loopback: listener already closed during StopAsync.");
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-supplied cancellation; not fatal.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Butler loopback: accept loop faulted on shutdown.");
            }
        }

        _listener?.Close();
        _listener = null;
        _serverCts?.Dispose();
        _serverCts = null;
        _acceptLoop = null;
        _service = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // DisposeAsync must not throw — swallow but log at debug level.
            _logger.LogDebug(ex, "Butler loopback: StopAsync threw during DisposeAsync.");
        }
    }

    // ------------------------------------------------------------------
    // Accept loop
    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Butler accept loop hit an unexpected error.");
                continue;
            }

            // Fire-and-forget: each request runs on its own Task so the accept loop is not blocked.
            // HandleRequestAsync is fully exception-guarded — unhandled throws cannot escape it.
            _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (!Authorise(context))
            {
                await WritePlainAsync(context, 401, "unauthorised").ConfigureAwait(false);
                return;
            }

            // QUESTIONS AS RECORDS, the same door the phone's apps use: GET, POST and
            // DELETE on /v1, before the POST-only gate the /butler routes keep.
            if ((context.Request.Url?.AbsolutePath ?? string.Empty).StartsWith("/v1/", StringComparison.Ordinal))
            {
                await HandleV1Async(context, ct).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainAsync(context, 405, "method not allowed").ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            switch (path)
            {
                case "/butler/ask":
                    await HandleAskAsync(context, ct).ConfigureAwait(false);
                    break;
                case "/butler/chat":
                    await HandleChatAsync(context, ct).ConfigureAwait(false);
                    break;
                case "/butler/stream":
                    await HandleStreamAsync(context, ct).ConfigureAwait(false);
                    break;
                case "/butler/tool":
                    await HandleToolAsync(context, ct).ConfigureAwait(false);
                    break;
                default:
                    await WritePlainAsync(context, 404, "not found").ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Either the request was cancelled or the endpoint is shutting
            // down — either way we just close the response and move on.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Butler request handler failed.");
            try
            {
                await WritePlainAsync(context, 500, "internal error").ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                _logger.LogDebug(writeEx, "Butler loopback: could not send 500 response — client likely disconnected.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Routes
    // ------------------------------------------------------------------

    private async Task HandleAskAsync(HttpListenerContext context, CancellationToken ct)
    {
        var body = await ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        var payload = Deserialise<AskPayload>(body);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Question))
        {
            await WritePlainAsync(context, 400, "missing 'question'").ConfigureAwait(false);
            return;
        }

        // THROUGH THE QUEUE WHEN THERE IS ONE: durable and in line like every other
        // door, so this old route cannot jump the queue or lose a question.
        if (Asks is { } asks)
        {
            var caller = CallerKey(context);
            var admission = asks.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                caller, null, string.Empty, CircleAI.Assistant.Requests.AskLane.Foreground, payload.Question!));
            if (!admission.Accepted || admission.Record is null)
            {
                await WritePlainAsync(context, 429, admission.Refusal ?? "busy").ConfigureAwait(false);
                return;
            }
            var done = await asks.WaitAsync(caller, admission.Record.Id, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            await WritePlainAsync(context, 200, done?.State == CircleAI.Assistant.Requests.AskState.Succeeded
                ? done.Text : done?.Sentence ?? CircleAI.Assistant.Requests.AskSentences.StillLoading).ConfigureAwait(false);
            return;
        }

        var answer = await RequireService().AskAsync(payload.Question, ct).ConfigureAwait(false);
        await WritePlainAsync(context, 200, answer).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // /v1 - questions as records, readiness, health, quality
    // ------------------------------------------------------------------

    /// <summary>The request coordinator. When set, /v1/asks and /butler/ask go through it.</summary>
    public CircleAI.Assistant.Requests.IAskFront? Asks { get; set; }

    /// <summary>What the service reports about itself, for /v1/status.</summary>
    public CircleAI.Assistant.Lifecycle.IReadinessTracker? Readiness { get; set; }

    /// <summary>Health facts for /v1/health.</summary>
    public Func<IReadOnlyList<KeyValuePair<string, string>>>? HealthFacts { get; set; }

    /// <summary>Runs the recorded questions for /v1/quality, as the given caller.</summary>
    public Func<string, CancellationToken, Task<CircleAI.Assistant.Quality.QualityReport>>? Quality { get; set; }

    private static readonly JsonSerializerOptions V1Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // WHO IS ASKING, as far as a loopback caller can be told apart: everyone who holds
    // the token is trusted alike, and the header only keeps their conversations apart.
    private static string CallerKey(HttpListenerContext context)
    {
        var name = context.Request.Headers["X-Circle-Caller"];
        return "loopback:" + (string.IsNullOrWhiteSpace(name) ? "default" : name!.Trim());
    }

    private sealed class SubmitPayload
    {
        public string? ClientRequestId { get; set; }
        public string? SessionId { get; set; }
        public string? Message { get; set; }
        public string? Question { get; set; }
        public string? Lane { get; set; }
        public bool Spoken { get; set; }
    }

    private async Task HandleV1Async(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        var method = context.Request.HttpMethod.ToUpperInvariant();
        var query = context.Request.QueryString;
        var caller = CallerKey(context);

        if (path == "/v1/status" && method == "GET")
        {
            if (Readiness is not { } readiness) { await WritePlainAsync(context, 404, "no status").ConfigureAwait(false); return; }
            var now = readiness.Current;
            if (long.TryParse(query["after"], out var after) && now.Seq <= after)
                await WriteV1Async(context, 200, new { unchanged = true, seq = now.Seq }).ConfigureAwait(false);
            else
                await WriteV1Async(context, 200, now).ConfigureAwait(false);
            return;
        }

        if (path == "/v1/health" && method == "GET")
        {
            var facts = HealthFacts?.Invoke() ?? Array.Empty<KeyValuePair<string, string>>();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in facts) map[k] = v;
            await WriteV1Async(context, 200, map).ConfigureAwait(false);
            return;
        }

        if (path == "/v1/quality" && method == "POST")
        {
            if (Quality is not { } quality) { await WritePlainAsync(context, 404, "no quality run").ConfigureAwait(false); return; }
            await WriteV1Async(context, 200, await quality(caller, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        if (Asks is not { } asks) { await WritePlainAsync(context, 404, "no questions here").ConfigureAwait(false); return; }

        if (path == "/v1/asks" && method == "POST")
        {
            var body = Deserialise<SubmitPayload>(await ReadBodyAsync(context.Request, ct).ConfigureAwait(false));
            if (body is null || string.IsNullOrWhiteSpace(body.Message))
            {
                await WritePlainAsync(context, 400, "missing 'message'").ConfigureAwait(false);
                return;
            }
            var lane = Enum.TryParse<CircleAI.Assistant.Requests.AskLane>(body.Lane, true, out var l) && l != CircleAI.Assistant.Requests.AskLane.Assistant
                ? l : CircleAI.Assistant.Requests.AskLane.Foreground;
            var admission = asks.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                caller, body.ClientRequestId, body.SessionId ?? string.Empty, lane, body.Message!, body.Question, body.Spoken));
            if (!admission.Accepted || admission.Record is null)
                await WriteV1Async(context, 429, new { error = admission.Refusal, kind = admission.Kind }).ConfigureAwait(false);
            else
                await WriteV1Async(context, 202, new { ask = admission.Record, position = admission.Position, duplicate = admission.Duplicate }).ConfigureAwait(false);
            return;
        }

        if (path == "/v1/asks" && method == "GET")
        {
            var limit = int.TryParse(query["limit"], out var n) ? n : 20;
            await WriteV1Async(context, 200, asks.List(caller, limit, query["open"] == "1")).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/v1/asks/", StringComparison.Ordinal))
        {
            var id = path["/v1/asks/".Length..];
            if (method == "DELETE")
            {
                asks.Cancel(caller, id);
                var gone = asks.Status(caller, id);
                if (gone is null) await WritePlainAsync(context, 404, "no such ask").ConfigureAwait(false);
                else await WriteV1Async(context, 200, gone).ConfigureAwait(false);
                return;
            }
            if (method == "GET")
            {
                var after = long.TryParse(query["after"], out var a) ? a : -1;
                var wait = int.TryParse(query["wait"], out var w) ? Math.Clamp(w, 0, 25_000) : 0;
                var ask = after >= 0 && wait > 0
                    ? await asks.WaitForChangeAsync(caller, id, after, TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false)
                    : asks.Status(caller, id);
                if (ask is null) { await WritePlainAsync(context, 404, "no such ask").ConfigureAwait(false); return; }
                var since = int.TryParse(query["since"], out var s) ? Math.Clamp(s, 0, ask.Text.Length) : 0;
                await WriteV1Async(context, 200, new { ask = ask with { Text = ask.Text[since..] }, textOffset = since, textLength = ask.Text.Length, position = asks.PositionOf(id) }).ConfigureAwait(false);
                return;
            }
        }

        await WritePlainAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private static async Task WriteV1Async(HttpListenerContext context, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), V1Json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private async Task HandleChatAsync(HttpListenerContext context, CancellationToken ct)
    {
        var service = RequireService();
        var body = await ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        var payload = Deserialise<ChatPayload>(body);
        if (payload?.Messages is null || payload.Messages.Count == 0)
        {
            await WritePlainAsync(context, 400, "missing 'messages'").ConfigureAwait(false);
            return;
        }

        var messages = payload.Messages.ConvertAll(m => new ChatMessage(m.Role ?? "user", m.Content ?? string.Empty));
        var options = payload.Options?.ToGenerationOptions();
        var content = await service.ChatAsync(messages, options, ct).ConfigureAwait(false);

        await WriteJsonAsync(context, 200, new ChatResponse { Content = content }).ConfigureAwait(false);
    }

    private async Task HandleStreamAsync(HttpListenerContext context, CancellationToken ct)
    {
        var service = RequireService();
        var body = await ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        var payload = Deserialise<ChatPayload>(body);
        if (payload?.Messages is null || payload.Messages.Count == 0)
        {
            await WritePlainAsync(context, 400, "missing 'messages'").ConfigureAwait(false);
            return;
        }

        var messages = payload.Messages.ConvertAll(m => new ChatMessage(m.Role ?? "user", m.Content ?? string.Empty));
        var options = payload.Options?.ToGenerationOptions();

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.SendChunked = true;

        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        try
        {
            await foreach (var piece in service.StreamAsync(messages, options, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                // Emit data in the standard SSE framing. Encode the payload
                // as JSON so newlines / quotes inside the chunk don't break
                // the framing.
                await writer.WriteAsync("data: ").ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(piece, JsonOpts)).ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }

            // Closing event so clients know we're done cleanly.
            await writer.WriteAsync("event: done").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteAsync("data: {}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.OutputStream.Close(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Butler loopback: stream output already closed."); }
        }
    }

    private async Task HandleToolAsync(HttpListenerContext context, CancellationToken ct)
    {
        var service = RequireService();
        var body = await ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        var payload = Deserialise<ToolPayload>(body);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ToolName))
        {
            await WritePlainAsync(context, 400, "missing 'toolName'").ConfigureAwait(false);
            return;
        }

        var args = payload.Arguments ?? new Dictionary<string, object?>();
        var invocation = new ToolInvocation
        {
            ToolName = payload.ToolName,
            Arguments = args,
        };
        var result = await service.InvokeToolAsync(invocation, ct).ConfigureAwait(false);
        await WriteJsonAsync(context, result.Success ? 200 : 502, result).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private bool Authorise(HttpListenerContext context)
    {
        var token = _token;
        if (string.IsNullOrEmpty(token)) return false;

        var supplied = context.Request.Headers["X-Butler-Token"];
        return !string.IsNullOrEmpty(supplied)
            && CryptographicEquals(supplied, token);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private IAIService RequireService()
    {
        return _service
            ?? throw new InvalidOperationException("HttpLoopbackEndpoint has no service bound.");
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(
            request.InputStream,
            request.ContentEncoding ?? Encoding.UTF8,
            leaveOpen: false);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static T? Deserialise<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WritePlainAsync(HttpListenerContext context, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.OutputStream.Close(); } catch { /* already closed */ }
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, int status, T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.OutputStream.Close(); } catch { /* already closed */ }
        }
    }

    private static int PickFreeLoopbackPort()
    {
        // Bind a TCP listener to port 0, read the assigned port, release it.
        // There's a tiny race window between releasing and HttpListener
        // grabbing it, but on a single host this is acceptable for our use.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    // ------------------------------------------------------------------
    // wire payloads
    // ------------------------------------------------------------------

    private sealed class AskPayload
    {
        public string? Question { get; set; }
    }

    internal sealed class ChatMessagePayload
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    internal sealed class GenerationOptionsPayload
    {
        public int? MaxTokens { get; set; }
        public float? Temperature { get; set; }
        public float? TopP { get; set; }
        public int? TopK { get; set; }
        public int? Seed { get; set; }
        public string[]? StopSequences { get; set; }

        public GenerationOptions ToGenerationOptions()
        {
            var defaults = new GenerationOptions();
            return new GenerationOptions
            {
                MaxTokens = MaxTokens ?? defaults.MaxTokens,
                Temperature = Temperature ?? defaults.Temperature,
                TopP = TopP ?? defaults.TopP,
                TopK = TopK ?? defaults.TopK,
                Seed = Seed,
                StopSequences = StopSequences,
            };
        }
    }

    private sealed class ChatPayload
    {
        public List<ChatMessagePayload>? Messages { get; set; }
        public GenerationOptionsPayload? Options { get; set; }
    }

    private sealed class ChatResponse
    {
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ToolPayload
    {
        public string? ToolName { get; set; }
        public Dictionary<string, object?>? Arguments { get; set; }
    }
}
