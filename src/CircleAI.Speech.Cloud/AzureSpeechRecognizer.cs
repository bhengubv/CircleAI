// AzureSpeechRecognizer.cs
//
// (3.3.0) ISpeechRecognizer backed by Microsoft Azure Cognitive
// Services Speech-to-Text REST endpoint.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Azure-backed <see cref="ISpeechRecognizer"/>.</summary>
public sealed class AzureSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly AzureSpeechOptions _options;
    private readonly ILogger _logger;

    public AzureSpeechRecognizer(HttpClient http, AzureSpeechOptions options, ILogger<AzureSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null && options.BaseAddress is not null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string BackendId    => "azure-stt";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && _options.BaseAddress is not null;

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured) return Empty();

        var lang = string.IsNullOrWhiteSpace(languageHint) ? _options.LanguageCode : languageHint;
        var path = $"/speech/recognition/conversation/cognitiveservices/v1?language={Uri.EscapeDataString(lang)}&format=detailed";

        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(audioPcm16Mono.ToArray()),
        };
        // Azure REST STT expects the codec sub-param + samplerate appended.
        msg.Content!.Headers.TryAddWithoutValidation(
            "Content-Type", $"audio/wav; codecs=audio/pcm; samplerate={sampleRateHz}");
        msg.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        msg.Headers.Add("Accept", "application/json");

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Azure STT returned {Status}", resp.StatusCode);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var status = doc.RootElement.TryGetProperty("RecognitionStatus", out var rs) ? rs.GetString() : null;
        if (status != "Success") return Empty();

        var text = doc.RootElement.TryGetProperty("DisplayText", out var dt) ? dt.GetString() ?? "" : "";

        // Azure returns offsets/durations in 100-nanosecond ticks (HNS).
        var offsetTicks   = doc.RootElement.TryGetProperty("Offset",   out var o) ? o.GetInt64() : 0L;
        var durationTicks = doc.RootElement.TryGetProperty("Duration", out var du) ? du.GetInt64() : 0L;
        var duration      = TimeSpan.FromTicks(durationTicks);

        var segment = new TranscribedSegment(
            Text:       text,
            Offset:     TimeSpan.FromTicks(offsetTicks),
            Duration:   duration,
            Language:   lang,
            Confidence: doc.RootElement.TryGetProperty("NBest", out var nb) && nb.GetArrayLength() > 0
                            && nb[0].TryGetProperty("Confidence", out var cc) ? (float)cc.GetDouble() : 0f);

        return new TranscriptionResult(text, lang, new[] { segment }, duration);
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, Array.Empty<TranscribedSegment>(), TimeSpan.Zero);
}
