// DeepgramSpeechRecognizer.cs
//
// (3.3.0) ISpeechRecognizer backed by Deepgram's /v1/listen endpoint.
// Single-shot HTTP POST with raw PCM (encoding=linear16) — same path
// the Patter Python SDK uses.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Deepgram-backed <see cref="ISpeechRecognizer"/>.</summary>
public sealed class DeepgramSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly DeepgramOptions _options;
    private readonly ILogger _logger;

    public DeepgramSpeechRecognizer(HttpClient http, DeepgramOptions options, ILogger<DeepgramSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "deepgram";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured) return Empty();

        var path = $"/v1/listen?model={Uri.EscapeDataString(_options.Model)}&encoding=linear16&sample_rate={sampleRateHz}&channels=1&punctuate=true";
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            path += $"&language={Uri.EscapeDataString(languageHint)}";
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(audioPcm16Mono.ToArray()),
        };
        msg.Content!.Headers.ContentType = new MediaTypeHeaderValue("audio/raw");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deepgram returned {Status}", resp.StatusCode);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        // Response shape: results.channels[0].alternatives[0].transcript
        if (!doc.RootElement.TryGetProperty("results", out var results)) return Empty();
        if (!results.TryGetProperty("channels", out var channels) || channels.GetArrayLength() == 0) return Empty();
        var firstChannel = channels[0];
        if (!firstChannel.TryGetProperty("alternatives", out var alts) || alts.GetArrayLength() == 0) return Empty();
        var firstAlt = alts[0];

        var text = firstAlt.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";

        var segments = new List<TranscribedSegment>();
        if (firstAlt.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in words.EnumerateArray())
            {
                segments.Add(new TranscribedSegment(
                    Text:       w.TryGetProperty("word",       out var ww) ? ww.GetString() ?? "" : "",
                    Offset:     TimeSpan.FromSeconds(w.TryGetProperty("start",      out var ss) ? ss.GetDouble() : 0d),
                    Duration:   TimeSpan.FromSeconds(w.TryGetProperty("end",        out var ee) ? ee.GetDouble() - (w.TryGetProperty("start", out var s2) ? s2.GetDouble() : 0d) : 0d),
                    Language:   languageHint,
                    Confidence: w.TryGetProperty("confidence", out var cc) ? (float)cc.GetDouble() : 0f));
            }
        }

        var duration = doc.RootElement.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("duration", out var d)
            ? TimeSpan.FromSeconds(d.GetDouble())
            : TimeSpan.Zero;

        return new TranscriptionResult(text, languageHint, segments, duration);
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, Array.Empty<TranscribedSegment>(), TimeSpan.Zero);
}
