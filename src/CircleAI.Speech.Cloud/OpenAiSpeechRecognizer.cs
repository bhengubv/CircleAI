// OpenAiSpeechRecognizer.cs
//
// (3.2.0) ISpeechRecognizer backed by OpenAI's Whisper
// /v1/audio/transcriptions endpoint. Lifted from Concierge's
// OpenAiVoiceRuntime — same multipart form upload, adapted to
// CircleAI.Speech's ReadOnlyMemory<byte> + sample-rate-aware contract.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>
/// (3.2.0) <see cref="ISpeechRecognizer"/> backed by OpenAI Whisper.
/// Fail-soft: empty <see cref="OpenAiVoiceOptions.ApiKey"/> returns an
/// empty <see cref="TranscriptionResult"/> rather than throwing, so a
/// fallback router can move on.
/// </summary>
public sealed class OpenAiSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly OpenAiVoiceOptions _options;
    private readonly ILogger _logger;

    public OpenAiSpeechRecognizer(HttpClient http, OpenAiVoiceOptions options, ILogger<OpenAiSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string BackendId => "openai-whisper";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured)
        {
            return Empty();
        }

        // Wrap PCM bytes in a WAV header so Whisper accepts them.
        // Whisper's documented inputs: mp3 / mp4 / mpeg / mpga / m4a /
        // wav / webm. PCM-without-header is not on the list, so we
        // build a minimal WAV envelope here.
        var wavBytes = WrapPcmAsWav(audioPcm16Mono, sampleRateHz);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var form = new MultipartFormDataContent();
        var audioPart = new ByteArrayContent(wavBytes);
        audioPart.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(audioPart, "file", "audio.wav");
        form.Add(new StringContent(_options.TranscriptionModel), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            form.Add(new StringContent(languageHint), "language");
        }
        msg.Content = form;

        using var response = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("OpenAI transcription returned {Status}: {Body}", response.StatusCode, error);
            return Empty();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var text = doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        var language = doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()
            : null;
        var duration = doc.RootElement.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetDouble())
            : TimeSpan.Zero;

        var segments = new System.Collections.Generic.List<TranscribedSegment>();
        if (doc.RootElement.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                var segText  = s.TryGetProperty("text",  out var st) ? st.GetString() ?? "" : "";
                var segStart = s.TryGetProperty("start", out var ss) ? ss.GetDouble() : 0d;
                var segEnd   = s.TryGetProperty("end",   out var se) ? se.GetDouble() : segStart;
                segments.Add(new TranscribedSegment(
                    Text:       segText,
                    Offset:     TimeSpan.FromSeconds(segStart),
                    Duration:   TimeSpan.FromSeconds(Math.Max(0, segEnd - segStart)),
                    Language:   language,
                    Confidence: 0f));
            }
        }

        return new TranscriptionResult(text, language, segments, duration);
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, System.Array.Empty<TranscribedSegment>(), TimeSpan.Zero);

    private static byte[] WrapPcmAsWav(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        // 44-byte WAV header for 16-bit mono PCM.
        const int channels = 1;
        const int bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = channels * (bitsPerSample / 8);
        var dataSize = pcm.Length;
        var chunkSize = 36 + dataSize;

        var buffer = new byte[44 + dataSize];
        var span = buffer.AsSpan();

        // "RIFF"
        span[0] = (byte)'R'; span[1] = (byte)'I'; span[2] = (byte)'F'; span[3] = (byte)'F';
        BitConverter.GetBytes(chunkSize).CopyTo(span[4..]);
        span[8] = (byte)'W'; span[9] = (byte)'A'; span[10] = (byte)'V'; span[11] = (byte)'E';
        // "fmt "
        span[12] = (byte)'f'; span[13] = (byte)'m'; span[14] = (byte)'t'; span[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(span[16..]);                              // Subchunk1Size
        BitConverter.GetBytes((short)1).CopyTo(span[20..]);                        // PCM = 1
        BitConverter.GetBytes((short)channels).CopyTo(span[22..]);
        BitConverter.GetBytes(sampleRate).CopyTo(span[24..]);
        BitConverter.GetBytes(byteRate).CopyTo(span[28..]);
        BitConverter.GetBytes((short)blockAlign).CopyTo(span[32..]);
        BitConverter.GetBytes((short)bitsPerSample).CopyTo(span[34..]);
        // "data"
        span[36] = (byte)'d'; span[37] = (byte)'a'; span[38] = (byte)'t'; span[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(span[40..]);
        pcm.Span.CopyTo(span[44..]);

        return buffer;
    }
}
