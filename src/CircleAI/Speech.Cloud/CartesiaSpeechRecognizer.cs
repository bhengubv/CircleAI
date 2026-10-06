// CartesiaSpeechRecognizer.cs
//
// (3.3.0) ISpeechRecognizer backed by Cartesia's /v1/transcribe
// endpoint. Bearer auth + multipart upload of WAV-wrapped audio.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) Cartesia-backed <see cref="ISpeechRecognizer"/>.</summary>
public sealed class CartesiaSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly CartesiaSttOptions _options;
    private readonly ILogger _logger;

    public CartesiaSpeechRecognizer(HttpClient http, CartesiaSttOptions options, ILogger<CartesiaSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "cartesia-stt";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured) return Empty();

        var wav = WrapPcmAsWav(audioPcm16Mono, sampleRateHz);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/transcribe");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Headers.Add("Cartesia-Version", _options.CartesiaVersion);

        using var form = new MultipartFormDataContent();
        var audioPart = new ByteArrayContent(wav);
        audioPart.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(audioPart, "file", "audio.wav");
        form.Add(new StringContent(_options.Model), "model");
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            form.Add(new StringContent(languageHint), "language");
        }
        msg.Content = form;

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cartesia STT returned {Status}", resp.StatusCode);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : languageHint;
        var duration = doc.RootElement.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetDouble())
            : TimeSpan.Zero;

        return new TranscriptionResult(text, lang, Array.Empty<TranscribedSegment>(), duration);
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, Array.Empty<TranscribedSegment>(), TimeSpan.Zero);

    private static byte[] WrapPcmAsWav(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        const int channels = 1, bitsPerSample = 16;
        var byteRate   = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = channels * (bitsPerSample / 8);
        var dataSize   = pcm.Length;
        var chunkSize  = 36 + dataSize;
        var buffer     = new byte[44 + dataSize];
        var span       = buffer.AsSpan();
        span[0] = (byte)'R'; span[1] = (byte)'I'; span[2] = (byte)'F'; span[3] = (byte)'F';
        BitConverter.GetBytes(chunkSize).CopyTo(span[4..]);
        span[8] = (byte)'W'; span[9] = (byte)'A'; span[10] = (byte)'V'; span[11] = (byte)'E';
        span[12] = (byte)'f'; span[13] = (byte)'m'; span[14] = (byte)'t'; span[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(span[16..]);
        BitConverter.GetBytes((short)1).CopyTo(span[20..]);
        BitConverter.GetBytes((short)channels).CopyTo(span[22..]);
        BitConverter.GetBytes(sampleRate).CopyTo(span[24..]);
        BitConverter.GetBytes(byteRate).CopyTo(span[28..]);
        BitConverter.GetBytes((short)blockAlign).CopyTo(span[32..]);
        BitConverter.GetBytes((short)bitsPerSample).CopyTo(span[34..]);
        span[36] = (byte)'d'; span[37] = (byte)'a'; span[38] = (byte)'t'; span[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(span[40..]);
        pcm.Span.CopyTo(span[44..]);
        return buffer;
    }
}
