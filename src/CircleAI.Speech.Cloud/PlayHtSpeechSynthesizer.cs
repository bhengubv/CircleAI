// PlayHtSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by Play.HT streaming TTS
// /api/v2/tts/stream. Returns raw PCM-16 audio when output_format=raw.

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Play.HT-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class PlayHtSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly PlayHtOptions _options;
    private readonly ILogger _logger;

    public PlayHtSpeechSynthesizer(HttpClient http, PlayHtOptions options, ILogger<PlayHtSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "playht";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.UserId);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoice : voiceId;

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v2/tts/stream");
        msg.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");
        msg.Headers.Add("X-USER-ID",    _options.UserId);
        msg.Headers.Add("Accept",       "audio/raw");
        msg.Content = JsonContent.Create(new
        {
            text             = text,
            voice            = voice,
            voice_engine     = _options.Model,
            output_format    = "raw",
            sample_rate      = _options.PcmSampleRateHz,
            language         = languageHint ?? "english",
        });

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Play.HT returned {Status}", resp.StatusCode);
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
