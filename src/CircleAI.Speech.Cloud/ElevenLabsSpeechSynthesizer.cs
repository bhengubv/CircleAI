// ElevenLabsSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by ElevenLabs /v1/text-to-speech.
// xi-api-key header; output_format=pcm_24000 returns raw PCM-16 mono.

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) ElevenLabs-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class ElevenLabsSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly ElevenLabsOptions _options;
    private readonly ILogger _logger;

    public ElevenLabsSpeechSynthesizer(HttpClient http, ElevenLabsOptions options, ILogger<ElevenLabsSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "elevenlabs";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoiceId : voiceId;
        var rate  = ParsePcmRate(_options.OutputFormat, fallback: _options.PcmSampleRateHz);

        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format={_options.OutputFormat}");
        msg.Headers.Add("xi-api-key", _options.ApiKey);
        msg.Content = JsonContent.Create(new
        {
            text     = text,
            model_id = _options.Model,
        });

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("ElevenLabs returned {Status}", resp.StatusCode);
            return Empty();
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var samples = bytes.Length / 2;
        return new SynthesisResult(
            AudioPcm16Mono: bytes,
            SampleRateHz:   rate,
            Duration:       TimeSpan.FromSeconds((double)samples / rate));
    }

    private static int ParsePcmRate(string outputFormat, int fallback)
    {
        // Format: pcm_22050 / pcm_24000 / pcm_44100 / pcm_16000
        var m = Regex.Match(outputFormat, @"pcm_(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var r) ? r : fallback;
    }

    private static SynthesisResult Empty() =>
        new(ReadOnlyMemory<byte>.Empty, SampleRateHz: 0, Duration: TimeSpan.Zero);
}
