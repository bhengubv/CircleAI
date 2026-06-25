// AssemblyAiSpeechRecognizer.cs
//
// (3.3.0) ISpeechRecognizer backed by AssemblyAI. Two-step flow:
// upload bytes → POST /v2/transcript with upload_url → poll until
// status=completed. Bytes are wrapped as WAV for compatibility.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.3.0) AssemblyAI-backed <see cref="ISpeechRecognizer"/>.</summary>
public sealed class AssemblyAiSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _http;
    private readonly AssemblyAiOptions _options;
    private readonly ILogger _logger;

    public AssemblyAiSpeechRecognizer(HttpClient http, AssemblyAiOptions options, ILogger<AssemblyAiSpeechRecognizer>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.BaseAddress;
    }

    public string BackendId    => "assemblyai";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default)
    {
        if (!IsConfigured) return Empty();

        // 1) Upload audio.
        var wav = WrapPcmAsWav(audioPcm16Mono, sampleRateHz);
        using var uploadMsg = new HttpRequestMessage(HttpMethod.Post, "/v2/upload")
        {
            Content = new ByteArrayContent(wav),
        };
        uploadMsg.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadMsg.Headers.Add("Authorization", _options.ApiKey);

        using var uploadResp = await _http.SendAsync(uploadMsg, ct).ConfigureAwait(false);
        if (!uploadResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("AssemblyAI upload returned {Status}", uploadResp.StatusCode);
            return Empty();
        }
        using var uploadDoc = await JsonDocument.ParseAsync(
            await uploadResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var uploadUrl = uploadDoc.RootElement.TryGetProperty("upload_url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(uploadUrl)) return Empty();

        // 2) Submit transcript job.
        var body = new StringBuilder("{");
        body.Append($"\"audio_url\":\"{uploadUrl}\",");
        body.Append($"\"speech_model\":\"{_options.SpeechModel}\"");
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            body.Append($",\"language_code\":\"{languageHint}\"");
        }
        body.Append('}');

        using var submitMsg = new HttpRequestMessage(HttpMethod.Post, "/v2/transcript")
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
        };
        submitMsg.Headers.Add("Authorization", _options.ApiKey);

        using var submitResp = await _http.SendAsync(submitMsg, ct).ConfigureAwait(false);
        if (!submitResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("AssemblyAI submit returned {Status}", submitResp.StatusCode);
            return Empty();
        }
        using var submitDoc = await JsonDocument.ParseAsync(
            await submitResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var transcriptId = submitDoc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(transcriptId)) return Empty();

        // 3) Poll until completed (max 60 attempts of 500 ms = 30 s).
        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct).ConfigureAwait(false);

            using var pollMsg = new HttpRequestMessage(HttpMethod.Get, $"/v2/transcript/{transcriptId}");
            pollMsg.Headers.Add("Authorization", _options.ApiKey);

            using var pollResp = await _http.SendAsync(pollMsg, ct).ConfigureAwait(false);
            if (!pollResp.IsSuccessStatusCode) continue;

            using var pollDoc = await JsonDocument.ParseAsync(
                await pollResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);

            var status = pollDoc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "completed")
            {
                var text = pollDoc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var lang = pollDoc.RootElement.TryGetProperty("language_code", out var lc) ? lc.GetString() : languageHint;
                var duration = pollDoc.RootElement.TryGetProperty("audio_duration", out var ad) && ad.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(ad.GetDouble())
                    : TimeSpan.Zero;

                var segments = new List<TranscribedSegment>();
                if (pollDoc.RootElement.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in words.EnumerateArray())
                    {
                        var start = w.TryGetProperty("start", out var ws) ? ws.GetDouble() / 1000d : 0d;
                        var end   = w.TryGetProperty("end",   out var we) ? we.GetDouble() / 1000d : start;
                        segments.Add(new TranscribedSegment(
                            Text:       w.TryGetProperty("text",       out var wt) ? wt.GetString() ?? "" : "",
                            Offset:     TimeSpan.FromSeconds(start),
                            Duration:   TimeSpan.FromSeconds(Math.Max(0, end - start)),
                            Language:   lang,
                            Confidence: w.TryGetProperty("confidence", out var wc) ? (float)wc.GetDouble() : 0f));
                    }
                }

                return new TranscriptionResult(text, lang, segments, duration);
            }
            if (status == "error")
            {
                var err = pollDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                _logger.LogWarning("AssemblyAI transcript error: {Error}", err);
                return Empty();
            }
        }

        _logger.LogWarning("AssemblyAI transcript {Id} timed out after 30 s", transcriptId);
        return Empty();
    }

    private static TranscriptionResult Empty() =>
        new(string.Empty, null, Array.Empty<TranscribedSegment>(), TimeSpan.Zero);

    private static byte[] WrapPcmAsWav(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        const int channels = 1, bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = channels * (bitsPerSample / 8);
        var dataSize = pcm.Length;
        var chunkSize = 36 + dataSize;
        var buffer = new byte[44 + dataSize];
        var span = buffer.AsSpan();
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
