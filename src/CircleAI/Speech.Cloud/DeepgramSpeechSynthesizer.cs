// DeepgramSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by Deepgram Aura /v1/speak.
// "Token" auth scheme + JSON body { text }; encoding=linear16 returns
// raw PCM-16 mono at the requested sample rate.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Deepgram Aura-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class DeepgramSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly DeepgramTtsOptions _options;
    private readonly ILogger _logger;

    public DeepgramSpeechSynthesizer(HttpClient http, DeepgramTtsOptions options, ILogger<DeepgramSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "deepgram-aura";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.Voice : voiceId;
        var path  = $"/v1/speak?model={Uri.EscapeDataString(voice)}&encoding=linear16&sample_rate={_options.PcmSampleRateHz}";

        using var msg = new HttpRequestMessage(HttpMethod.Post, path);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);
        msg.Content = JsonContent.Create(new { text = text });

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deepgram Aura returned {Status}", resp.StatusCode);
            return Empty();
        }

        var bytes   = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var samples = bytes.Length / 2;
        return new SynthesisResult(
            AudioPcm16Mono: bytes,
            SampleRateHz:   _options.PcmSampleRateHz,
            Duration:       TimeSpan.FromSeconds((double)samples / _options.PcmSampleRateHz));
    }

    private static SynthesisResult Empty() =>
        new(ReadOnlyMemory<byte>.Empty, SampleRateHz: 0, Duration: TimeSpan.Zero);
}
