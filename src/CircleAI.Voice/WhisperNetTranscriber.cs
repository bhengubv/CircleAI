#nullable enable

// WhisperNetTranscriber.cs
//
// The IVoiceTranscriber that actually runs. The hand-rolled WhisperInterop
// P/Invokes whisper.dll — a native library that ships NOWHERE in this repo or
// via NuGet, so it throws DllNotFoundException on every platform (the same
// defect class the MNN packaging gap was). This implementation rides Whisper.net
// instead, which bundles the whisper.cpp native library per-RID as a NuGet
// package — so ASR works out of the box, no hand-built native to ABI-match.
//
// Proven end to end by tools/stt-hear: ggml-tiny transcribed the JFK sample
// verbatim on Windows, 2026-07-21. whisper.cpp is MIT / de-Googled and reads the
// exact ggml model the registry catalogues.

using System.Runtime.CompilerServices;
using Whisper.net;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="IVoiceTranscriber"/> backed by Whisper.net (whisper.cpp). Consumes
/// PCM 16-bit 16 kHz mono, as the interface specifies.
/// </summary>
public sealed class WhisperNetTranscriber : IVoiceTranscriber
{
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private bool _disposed;

    /// <param name="modelPath">Path to a whisper.cpp ggml model (e.g. ggml-tiny.bin).</param>
    /// <param name="language">
    /// BCP-47 language, or <c>"auto"</c> to detect. Default <c>"auto"</c>.
    /// </param>
    public WhisperNetTranscriber(string modelPath, string language = "auto")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Whisper ggml model not found at '{modelPath}'. The registry catalogues " +
                "Whisper-tiny-ggml (Source=HuggingFace) — download it first.", modelPath);

        // Loads the model once; reused across calls. Native lib comes from the
        // Whisper.net.Runtime NuGet package, so there is no DllNotFoundException.
        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var samples = Pcm16ToFloat(pcmAudio.Span);
        if (samples.Length == 0)
            return new TranscriptionResult(string.Empty, 0f, "und");

        await using var processor = _factory.CreateBuilder()
            .WithLanguage(_language)
            .Build();

        var text = new System.Text.StringBuilder();
        double probSum = 0;
        int segCount = 0;
        string lang = _language == "auto" ? "und" : _language;

        await foreach (var seg in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            text.Append(seg.Text);
            probSum += seg.Probability;
            segCount++;
            if (!string.IsNullOrWhiteSpace(seg.Language)) lang = seg.Language;
        }

        var confidence = segCount > 0 ? (float)(probSum / segCount) : 0f;
        return new TranscriptionResult(text.ToString().Trim(), confidence, lang);
    }

    /// <summary>
    /// How much new audio must arrive before a partial re-decode. Lower feels
    /// more live but costs a full decode each time; the whole buffer is
    /// re-decoded, so cost grows with utterance length.
    /// </summary>
    public double PartialIntervalSeconds { get; init; } = 1.0;

    /// <summary>
    /// Longest audio a partial will re-decode. Past this only the final decode
    /// runs, so a long monologue cannot drag the device into decoding a
    /// minute of audio every second.
    /// </summary>
    public double MaxPartialSeconds { get; init; } = 30.0;

    /// <inheritdoc />
    /// <remarks>
    /// Whisper is not natively a streaming recogniser — it decodes a whole
    /// utterance. This emits real partials anyway, by re-decoding the buffer so
    /// far each time roughly <see cref="PartialIntervalSeconds"/> of new audio
    /// arrives, then one authoritative <c>IsFinal</c> result at end of stream.
    /// <para>
    /// Two honest consequences. Partials can CHANGE as more context arrives —
    /// Whisper may revise earlier words — so a UI must replace the displayed
    /// text, never append to it. And this trades CPU for latency: each partial
    /// is a full decode of everything heard so far, which is why
    /// <see cref="MaxPartialSeconds"/> exists. Callers that cannot afford that
    /// should segment with a VAD and call <see cref="TranscribeAsync"/> per
    /// utterance instead.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int bytesPerSecond = 16_000 * 2;          // PCM16 16 kHz mono
        var partialEvery = (long)(PartialIntervalSeconds * bytesPerSecond);
        var partialCeiling = (long)(MaxPartialSeconds * bytesPerSecond);

        using var buffer = new MemoryStream();
        var lastPartialAt = 0L;
        var lastText = string.Empty;

        await foreach (var chunk in audioChunks.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer.Write(chunk.Span);

            if (buffer.Length - lastPartialAt < partialEvery) continue;
            if (buffer.Length > partialCeiling) continue;

            lastPartialAt = buffer.Length;

            TranscriptionResult interim;
            try
            {
                interim = await TranscribeAsync(buffer.ToArray(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A failed partial is cosmetic — keep buffering so the FINAL
                // decode still gets every sample. Dropping the utterance here
                // would lose audio the user already spoke.
                continue;
            }

            // Suppress no-change partials: Whisper often returns identical text
            // for a second of trailing silence, and a UI that redraws on every
            // emission would flicker for no reason.
            if (string.IsNullOrWhiteSpace(interim.Text) || interim.Text == lastText) continue;

            lastText = interim.Text;
            yield return new PartialTranscription(interim.Text, IsFinal: false, interim.Confidence);
        }

        var result = await TranscribeAsync(buffer.ToArray(), ct).ConfigureAwait(false);
        yield return new PartialTranscription(result.Text, IsFinal: true, result.Confidence);
    }

    /// <summary>little-endian PCM16 → float[-1,1].</summary>
    internal static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        int n = pcm.Length / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            f[i] = s / 32768f;
        }
        return f;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
