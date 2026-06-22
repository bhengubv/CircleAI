// OpenAiSpeechSynthesizer.cs
//
// (3.2.0) ISpeechSynthesizer backed by OpenAI's /v1/audio/speech
// endpoint. Lifted from Concierge's OpenAiVoiceRuntime but
// response_format is bumped to "pcm" so the bytes we return are real
// PCM-16 mono — honouring CircleAI.Speech.SynthesisResult's
// AudioPcm16Mono contract.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>
/// (3.2.0) <see cref="ISpeechSynthesizer"/> backed by OpenAI TTS.
/// Returns PCM-16 mono at <see cref="OpenAiVoiceOptions.PcmSampleRateHz"/>
/// (24 kHz per OpenAI's docs).
/// </summary>
public sealed class OpenAiSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly OpenAiVoiceOptions _options;
    private readonly ILogger _logger;

    public OpenAiSpeechSynthesizer(HttpClient http, OpenAiVoiceOptions options, ILogger<OpenAiSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string BackendId => "openai-tts";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured)
        {
            return Empty();
        }

        var resolvedVoice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoice : voiceId;

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Content = JsonContent.Create(new
        {
            model           = _options.SpeechModel,
            input           = text,
            voice           = resolvedVoice,
            response_format = "pcm",
        });

        using var response = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("OpenAI synthesis returned {Status}: {Body}", response.StatusCode, error);
            return Empty();
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // PCM-16 mono: each sample is 2 bytes. Duration = samples / rate.
        var samples  = bytes.Length / 2;
        var duration = TimeSpan.FromSeconds((double)samples / _options.PcmSampleRateHz);

        return new SynthesisResult(
            AudioPcm16Mono: bytes,
            SampleRateHz:   _options.PcmSampleRateHz,
            Duration:       duration);
    }

    private static SynthesisResult Empty() =>
        new(System.ReadOnlyMemory<byte>.Empty, SampleRateHz: 0, Duration: TimeSpan.Zero);
}
