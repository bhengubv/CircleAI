using System.Runtime.CompilerServices;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="IVoiceTranscriber"/> implementation backed by whisper.cpp via
/// <see cref="WhisperInterop"/>. Lazy-loads the GGML model on first use and
/// provides both single-shot and streaming transcription.
/// </summary>
/// <remarks>
/// <para>
/// The whisper model is loaded once and reused across calls. Thread safety
/// is guaranteed via a <see cref="Lock"/> — whisper.cpp contexts are not
/// thread-safe, so concurrent calls are serialised.
/// </para>
/// <para>
/// Audio input must be PCM 16-bit, 16 kHz, mono (little-endian signed
/// short samples) as specified by <see cref="AudioFormat.Pcm16Mono16k"/>.
/// </para>
/// <para>
/// DO NOT WIRE THIS UP. It is not merely unused, it is unsafe to use, and the
/// two are easy to confuse for one another. <see cref="WhisperInterop"/>
/// declares <c>whisper_full_params</c> by hand and that declaration has drifted
/// from the struct whisper.cpp actually compiles: it still carries
/// <c>speed_up</c>, deleted upstream in v1.6; it declares
/// <c>suppress_regex</c> as a single byte where native has an eight-byte
/// pointer; and it adds a <c>temperature_inc_count</c> field that does not
/// exist at all. Every field past that point is written at the wrong offset —
/// setting <c>detect_language</c> lands inside the <c>language</c> POINTER.
/// That is a native memory corruption with no exception and no log line, of
/// exactly the kind that surfaces as a crash somewhere unrelated.
/// </para>
/// <para>
/// <see cref="WhisperNetTranscriber"/> is the one that runs. It rides
/// Whisper.net, which owns the marshalling and moves with the native library it
/// ships, so there is no layout to keep in step by hand.
/// </para>
/// </remarks>
[Obsolete(
    "Unsafe: WhisperInterop's hand-written whisper_full_params no longer matches " +
    "whisper.cpp's layout, so setting fields corrupts native memory silently. " +
    "Use WhisperNetTranscriber, which is what actually runs.")]
public sealed class WhisperTranscriber : IVoiceTranscriber
{
    private readonly string _modelPath;
    private readonly int _threads;
    private readonly Lock _gate = new();
    private IntPtr _ctx;
    private bool _disposed;

    /// <summary>
    /// Initialise a new whisper transcriber.
    /// </summary>
    /// <param name="modelPath">
    /// Absolute path to a whisper GGML model file (e.g. <c>ggml-base.bin</c>).
    /// </param>
    /// <param name="threads">
    /// Number of CPU threads to use for inference. Defaults to
    /// <c>Environment.ProcessorCount / 2</c> (clamped to at least 1).
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="modelPath"/> is null or whitespace.
    /// </exception>
    public WhisperTranscriber(string modelPath, int threads = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _modelPath = modelPath;
        _threads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Converts the PCM 16-bit buffer to float32, runs <c>whisper_full</c>,
    /// and concatenates all resulting segments. Language is auto-detected
    /// and returned as a BCP-47 code.
    /// </remarks>
    public Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        // Run on the thread pool to avoid blocking the caller.
        return Task.Run(() => TranscribeCore(pcmAudio, ct), ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Accumulates audio chunks into an internal buffer. Every
    /// <c>AccumulationThresholdBytes</c> bytes (roughly 2 seconds of audio),
    /// a partial transcription is emitted. When the input stream completes
    /// the final transcription is yielded with <see cref="PartialTranscription.IsFinal"/>
    /// set to <c>true</c>.
    /// </remarks>
    public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioChunks);

        // ~2 seconds of 16 kHz mono 16-bit audio = 64000 bytes.
        const int AccumulationThresholdBytes = 64_000;

        using var buffer = new MemoryStream();
        int sinceLastEmit = 0;

        await foreach (var chunk in audioChunks.WithCancellation(ct).ConfigureAwait(false))
        {
            if (chunk.Length == 0) continue;

            buffer.Write(chunk.Span);
            sinceLastEmit += chunk.Length;

            if (sinceLastEmit >= AccumulationThresholdBytes)
            {
                sinceLastEmit = 0;
                var accumulated = buffer.ToArray();
                var result = await Task.Run(
                    () => TranscribeCore(accumulated, ct), ct).ConfigureAwait(false);

                yield return new PartialTranscription(result.Text, IsFinal: false, result.Confidence);
            }
        }

        // Final transcription over the complete buffer.
        if (buffer.Length > 0)
        {
            var finalAudio = buffer.ToArray();
            var final_ = await Task.Run(
                () => TranscribeCore(finalAudio, ct), ct).ConfigureAwait(false);

            yield return new PartialTranscription(final_.Text, IsFinal: true, final_.Confidence);
        }
        else
        {
            yield return new PartialTranscription(string.Empty, IsFinal: true, 0f);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        lock (_gate)
        {
            if (_ctx != IntPtr.Zero)
            {
                WhisperInterop.whisper_free(_ctx);
                _ctx = IntPtr.Zero;
            }
        }

        return ValueTask.CompletedTask;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Ensure the whisper context is loaded. Thread-safe via <see cref="_gate"/>.
    /// </summary>
    private IntPtr EnsureContext()
    {
        if (_ctx != IntPtr.Zero) return _ctx;

        lock (_gate)
        {
            if (_ctx != IntPtr.Zero) return _ctx;

            WhisperInterop.EnsureResolverRegistered();

            var ctxParams = WhisperInterop.whisper_context_default_params();
            var ptr = WhisperInterop.whisper_init_from_file_with_params(_modelPath, ctxParams);

            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Failed to load whisper model from '{_modelPath}'. " +
                    "Verify the model file exists and the whisper native library " +
                    $"({WhisperInterop.LibraryName}) is available for the current platform " +
                    $"({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}).");
            }

            _ctx = ptr;
            return _ctx;
        }
    }

    /// <summary>
    /// Core transcription logic. Converts PCM16 to float32, runs whisper,
    /// reads segments. Must not be called concurrently (serialised via Lock
    /// on the context).
    /// </summary>
    private unsafe TranscriptionResult TranscribeCore(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (pcmAudio.Length < 2)
            return new TranscriptionResult(string.Empty, 0f, "und");

        var ctx = EnsureContext();

        // Convert PCM 16-bit signed short samples to float32 in [-1, 1].
        var shorts = PcmBytesToShorts(pcmAudio.Span);
        var floats = new float[shorts.Length];
        for (int i = 0; i < shorts.Length; i++)
        {
            floats[i] = shorts[i] / 32768f;
        }

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();

            var fullParams = WhisperInterop.whisper_full_default_params(WhisperSamplingStrategy.Greedy);
            fullParams.n_threads = _threads;
            fullParams.print_realtime = 0;
            fullParams.print_progress = 0;
            fullParams.print_timestamps = 0;
            fullParams.print_special = 0;
            fullParams.single_segment = 0;
            fullParams.detect_language = 1;

            int result;
            fixed (float* pSamples = floats)
            {
                result = WhisperInterop.whisper_full(ctx, fullParams, pSamples, floats.Length);
            }

            if (result != 0)
            {
                return new TranscriptionResult(string.Empty, 0f, "und");
            }

            // Read all segments.
            int nSegments = WhisperInterop.whisper_full_n_segments(ctx);
            if (nSegments <= 0)
            {
                return new TranscriptionResult(string.Empty, 0f, "und");
            }

            var textBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < nSegments; i++)
            {
                var segText = WhisperInterop.GetSegmentText(ctx, i);
                textBuilder.Append(segText);
            }

            // Detect language.
            int langId = WhisperInterop.whisper_lang_auto_detect(ctx, 0, _threads);
            string langCode = WhisperInterop.GetLanguageString(langId);

            string text = textBuilder.ToString().Trim();
            // Whisper does not expose per-segment confidence in the C API,
            // so we report 1.0 for non-empty results.
            float confidence = string.IsNullOrWhiteSpace(text) ? 0f : 1f;

            return new TranscriptionResult(text, confidence, langCode);
        }
    }

    /// <summary>
    /// Reinterpret a byte span of little-endian signed 16-bit PCM samples
    /// as a <c>short[]</c>.
    /// </summary>
    private static short[] PcmBytesToShorts(ReadOnlySpan<byte> pcmBytes)
    {
        int sampleCount = pcmBytes.Length / 2;
        var samples = new short[sampleCount];
        var sourceShorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcmBytes);
        sourceShorts[..sampleCount].CopyTo(samples);
        return samples;
    }
}
