// CartesiaSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by Cartesia Sonic /v1/tts/bytes.
// Bearer auth + Cartesia-Version header.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Cartesia Sonic-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class CartesiaSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly CartesiaTtsOptions _options;
    private readonly ILogger _logger;

    public CartesiaSpeechSynthesizer(HttpClient http, CartesiaTtsOptions options, ILogger<CartesiaSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "cartesia-tts";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoiceId : voiceId;

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/tts/bytes");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Headers.Add("Cartesia-Version", _options.CartesiaVersion);
        msg.Content = JsonContent.Create(new
        {
            model_id      = _options.Model,
            transcript    = text,
            voice         = new { mode = "id", id = voice },
            output_format = new
            {
                container   = _options.OutputContainer,
                encoding    = _options.OutputEncoding,
                sample_rate = _options.PcmSampleRateHz,
            },
            language = languageHint ?? "en",
        });

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cartesia TTS returned {Status}", resp.StatusCode);
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
