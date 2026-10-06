// AzureSpeechSynthesizer.cs
//
// (3.3.0) ISpeechSynthesizer backed by Azure Cognitive Services TTS.
// SSML body + X-Microsoft-OutputFormat=raw-24khz-16bit-mono-pcm.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Azure-backed <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class AzureSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _http;
    private readonly AzureTtsOptions _options;
    private readonly ILogger _logger;

    public AzureSpeechSynthesizer(HttpClient http, AzureTtsOptions options, ILogger<AzureSpeechSynthesizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null && options.BaseAddress is not null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string BackendId    => "azure-tts";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && _options.BaseAddress is not null;

    public async ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default)
    {
        if (!IsConfigured) return Empty();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoiceName : voiceId;
        var lang  = string.IsNullOrWhiteSpace(languageHint) ? _options.LanguageCode : languageHint;
        var rate  = _options.PcmSampleRateHz;

        var ssml = $"""
            <speak version='1.0' xml:lang='{lang}'>
              <voice name='{voice}'>{System.Net.WebUtility.HtmlEncode(text)}</voice>
            </speak>
            """;

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/cognitiveservices/v1")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        msg.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        msg.Headers.Add("X-Microsoft-OutputFormat", $"raw-{rate / 1000}khz-16bit-mono-pcm");
        msg.Headers.Add("User-Agent", "CircleAI");

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Azure TTS returned {Status}", resp.StatusCode);
            return Empty();
        }

        var bytes   = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var samples = bytes.Length / 2;
        return new SynthesisResult(
            AudioPcm16Mono: bytes,
            SampleRateHz:   rate,
            Duration:       TimeSpan.FromSeconds((double)samples / rate));
    }

    private static SynthesisResult Empty() =>
        new(ReadOnlyMemory<byte>.Empty, SampleRateHz: 0, Duration: TimeSpan.Zero);
}
