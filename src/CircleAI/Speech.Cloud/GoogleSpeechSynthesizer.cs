// GoogleSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by Google Cloud TTS v1
// /v1/text:synthesize. API-key auth; returns base64 LINEAR16 audio.

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Google-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class GoogleSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly GoogleTtsOptions _options;
    private readonly ILogger _logger;

    public GoogleSpeechSynthesizer(HttpClient http, GoogleTtsOptions options, ILogger<GoogleSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "google-tts";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoiceName : voiceId;
        var lang  = string.IsNullOrWhiteSpace(languageHint) ? _options.LanguageCode : languageHint;

        var body = $$"""
            {
              "input": { "text": {{JsonSerializer.Serialize(text)}} },
              "voice": {
                "languageCode": "{{lang}}",
                "name": "{{voice}}"
              },
              "audioConfig": {
                "audioEncoding": "LINEAR16",
                "sampleRateHertz": {{_options.PcmSampleRateHz}}
              }
            }
            """;

        var path = $"/v1/text:synthesize?key={Uri.EscapeDataString(_options.ApiKey!)}";
        using var resp = await _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google TTS returned {Status}", resp.StatusCode);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("audioContent", out var ac)) return Empty();
        var b64   = ac.GetString();
        if (string.IsNullOrEmpty(b64)) return Empty();

        var bytes = Convert.FromBase64String(b64);
        // Google returns a WAV envelope — strip it.
        var pcm   = StripWavHeader(bytes);
        var samples = pcm.Length / 2;
        return new SynthesisResult(
            AudioPcm16Mono: pcm,
            SampleRateHz:   _options.PcmSampleRateHz,
            Duration:       TimeSpan.FromSeconds((double)samples / _options.PcmSampleRateHz));
    }

    /// <summary>Strip a 44-byte WAV header if present.</summary>
    private static byte[] StripWavHeader(byte[] data)
    {
        if (data.Length > 44 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            var stripped = new byte[data.Length - 44];
            Array.Copy(data, 44, stripped, 0, stripped.Length);
            return stripped;
        }
        return data;
    }

    private static SynthesisResult Empty() =>
        new(ReadOnlyMemory<byte>.Empty, SampleRateHz: 0, Duration: TimeSpan.Zero);
}
