// ConsultEscalation.cs
//
// (3.3.0) Consult escalation: AI pauses the call, contacts a human
// expert out-of-band (chat / webhook / phone), conveys the question,
// receives an answer, and reads it back to the caller. Different from
// warm transfer — the caller stays with the AI, the human just
// answers behind the scenes.

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Question the AI asks a human expert.</summary>
/// <param name="CallId">Source call id for the audit trail.</param>
/// <param name="Question">Plain-English question text.</param>
/// <param name="ContextJson">Structured context (caller intent, last few utterances, customer record).</param>
/// <param name="Urgency">"normal" / "high".</param>
public sealed record ConsultRequest(string CallId, string Question, string ContextJson, string Urgency = "normal");

/// <summary>(3.3.0) Human reply.</summary>
public sealed record ConsultAnswer(string Answer, bool Confidence /* true = expert confirmed */, string? Notes = null);

/// <summary>(3.3.0) Channel for asking a human expert.</summary>
public interface IConsultChannel
{
    string Name { get; }
    ValueTask<ConsultAnswer?> AskAsync(ConsultRequest request, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>(3.3.0) Default escalation driver: try channels in order until one returns within the timeout.</summary>
public sealed class ConsultEscalator
{
    private readonly IConsultChannel[] _channels;
    private readonly ILogger _logger;

    public ConsultEscalator(IConsultChannel[] channels, ILogger<ConsultEscalator>? logger = null)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _logger   = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>(3.3.0) Walk channels in order; first one to return a non-null answer wins.</summary>
    public async ValueTask<ConsultAnswer?> EscalateAsync(
        ConsultRequest    request,
        TimeSpan          timeoutPerChannel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var channel in _channels)
        {
            try
            {
                var answer = await channel.AskAsync(request, timeoutPerChannel, ct).ConfigureAwait(false);
                if (answer is not null)
                {
                    _logger.LogInformation("Consult {Call} answered by {Channel}", request.CallId, channel.Name);
                    return answer;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Consult channel {Channel} threw", channel.Name);
            }
        }
        return null;
    }
}

/// <summary>(3.3.0) HTTP webhook channel — POSTs the request, expects a JSON reply.</summary>
public sealed class HttpWebhookConsultChannel : IConsultChannel
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _name;

    public HttpWebhookConsultChannel(HttpClient http, Uri endpoint, string name = "webhook")
    {
        _http     = http     ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _name     = name;
    }

    public string Name => _name;

    public async ValueTask<ConsultAnswer?> AskAsync(ConsultRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(request),
            };
            using var resp = await _http.SendAsync(msg, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false),
                cancellationToken: linked.Token).ConfigureAwait(false);

            var root = doc.RootElement;
            var answer = root.TryGetProperty("answer", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(answer)) return null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.True;
            var notes      = root.TryGetProperty("notes", out var n) ? n.GetString() : null;
            return new ConsultAnswer(answer, confidence, notes);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
