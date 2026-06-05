// ServerSentEventsWriter.cs
//
// Tiny SSE writer that frames a JSON payload as a "data: <json>\n\n"
// chunk and flushes the response stream immediately. We don't use
// System.Net.Http.SSE because we're the server and the HttpResponse
// surface is already available.

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CircleAI.Inference.Server.Streaming;

/// <summary>
/// Writes SSE-framed JSON chunks to an HTTP response body. Each call to
/// <see cref="WriteAsync"/> emits one frame and flushes; the caller is
/// responsible for any framing terminator (e.g. <c>[DONE]</c>).
/// </summary>
public sealed class ServerSentEventsWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpResponse _response;

    /// <summary>
    /// Construct a writer for <paramref name="response"/>. The response
    /// is set up for SSE (Content-Type, no buffering) on first write.
    /// </summary>
    public ServerSentEventsWriter(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _response = response;
    }

    /// <summary>
    /// Write a single SSE frame: <c>data: &lt;json&gt;\n\n</c> followed by a flush.
    /// </summary>
    public async Task WriteAsync<T>(T payload, CancellationToken ct)
    {
        EnsureHeaders();
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Write the OpenAI terminator <c>data: [DONE]\n\n</c>.</summary>
    public async Task WriteTerminatorAsync(CancellationToken ct)
    {
        EnsureHeaders();
        var bytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await _response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private void EnsureHeaders()
    {
        if (_response.HasStarted) return;
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers["Content-Type"]  = "text/event-stream; charset=utf-8";
        _response.Headers["Cache-Control"] = "no-cache, no-store";
        _response.Headers["Connection"]    = "keep-alive";
        _response.Headers["X-Accel-Buffering"] = "no"; // tell nginx not to buffer
    }
}
