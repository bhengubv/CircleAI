// GoogleSpeechRecognizer.cs
//
// (3.3.0) ISpeechRecognizer backed by Google Cloud Speech-to-Text v1.
// Uses API-key auth (?key=…); audio is base64'd LINEAR16 mono.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Google-backed <see cref="ISpeechRecognizer"/>.</summary>
public sealed class GoogleSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly GoogleSpeechOptions _options;
    private readonly ILogger _logger;

    public GoogleSpeechRecognizer(HttpClient http, GoogleSpeechOptions options, ILogger<GoogleSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "google-stt";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured) return Empty();

        var lang = string.IsNullOrWhiteSpace(languageHint) ? _options.LanguageCode : languageHint;
        var audioB64 = Convert.ToBase64String(audioPcm16Mono.Span);

        var body = $$"""
            {
              "config": {
                "encoding": "LINEAR16",
                "sampleRateHertz": {{sampleRateHz}},
                "languageCode": "{{lang}}",
                "enableWordTimeOffsets": true,
                "enableWordConfidence": true
              },
              "audio": { "content": "{{audioB64}}" }
            }
            """;

        var path = $"/v1/speech:recognize?key={Uri.EscapeDataString(_options.ApiKey!)}";
        using var resp = await _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google STT returned {Status}", resp.StatusCode);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        // Pick the top alternative across results.
        var allText = new StringBuilder();
        var segments = new List<TranscribedSegment>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("alternatives", out var alts) || alts.GetArrayLength() == 0) continue;
                var alt = alts[0];
                if (allText.Length > 0) allText.Append(' ');
                allText.Append(alt.TryGetProperty("transcript", out var tx) ? tx.GetString() ?? "" : "");

                if (alt.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in words.EnumerateArray())
                    {
                        var start = ParseSeconds(w, "startTime");
                        var end   = ParseSeconds(w, "endTime");
                        segments.Add(new TranscribedSegment(
                            Text:       w.TryGetProperty("word",       out var ww) ? ww.GetString() ?? "" : "",
                            Offset:     TimeSpan.FromSeconds(start),
                            Duration:   TimeSpan.FromSeconds(Math.Max(0, end - start)),
                            Language:   lang,
                            Confidence: w.TryGetProperty("confidence", out var wc) ? (float)wc.GetDouble() : 0f));
                    }
                }
            }
        }

        return new TranscriptionResult(allText.ToString(), lang, segments, TimeSpan.Zero);
    }

    private static double ParseSeconds(JsonElement el, string property)
    {
        // Google encodes durations as e.g. "1.500s".
        if (!el.TryGetProperty(property, out var p)) return 0d;
        var s = p.GetString();
        if (string.IsNullOrWhiteSpace(s)) return 0d;
        if (s.EndsWith("s")) s = s[..^1];
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0d;
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, Array.Empty<TranscribedSegment>(), TimeSpan.Zero);
}
