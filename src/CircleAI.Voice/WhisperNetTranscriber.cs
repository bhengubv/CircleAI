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

    /// <inheritdoc />
    /// <remarks>
    /// Whisper is not a true streaming recogniser — it needs a whole utterance.
    /// This buffers the incoming chunks and emits ONE final transcription when the
    /// stream ends. It is honestly not incremental; callers wanting partials must
    /// segment upstream (e.g. with a VAD) and call <see cref="TranscribeAsync"/>
    /// per utterance.
    /// </remarks>
    public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var buffer = new MemoryStream();
        await foreach (var chunk in audioChunks.WithCancellation(ct).ConfigureAwait(false))
            buffer.Write(chunk.Span);

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
