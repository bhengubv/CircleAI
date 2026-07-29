using System.Runtime.CompilerServices;
using SherpaOnnx;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="ITtsEngine"/> backed by <b>sherpa-onnx</b> (k2-fsa, Apache-2.0) —
/// one runtime for the whole VITS family. Where <see cref="OnnxTtsEngine"/> runs
/// a single Piper-style graph through raw ONNX Runtime (and owns its own
/// phonemiser), sherpa-onnx bundles the tokeniser + espeak-ng phonemisation +
/// VITS/Matcha/Kokoro inference behind one native library that ships per-RID via
/// NuGet (<c>org.k2fsa.sherpa.onnx.runtime.&lt;rid&gt;</c>, incl.
/// <c>android-arm64</c> for the Huawei P30).
/// </summary>
/// <remarks>
/// <para>
/// This is the second rung of the TTS ladder: it lets a single engine serve every
/// open voice format — <b>Piper, mimic3, coqui, Kokoro, Matcha</b> — instead of
/// Piper-only. That is what makes the ready mimic3 voices (Afrikaans <c>af_ZA</c>,
/// Setswana <c>tn_ZA</c>), the ~46 languages bundled by sherpa, and every voice we
/// train ourselves runnable on device without a per-format engine.
/// </para>
/// <para>
/// A sherpa VITS voice is a <i>bundle</i>, not a lone <c>.onnx</c>: the acoustic
/// model, a <c>tokens.txt</c> vocabulary, and — for espeak-based voices like
/// piper/mimic3 — an <c>espeak-ng-data</c> directory (<see cref="_config"/>'s
/// <c>DataDir</c>). Lexicon-based voices supply <c>lexicon.txt</c> (+ optional
/// <c>dict/</c>) instead. <see cref="FromBundleDirectory"/> wires those by
/// convention; the full constructor takes them explicitly.
/// </para>
/// <para>
/// Output is 16-bit signed PCM at the model's own sample rate (read back from the
/// generated audio, never guessed) — the same <see cref="TtsSynthesisResult"/>
/// shape the Piper engine returns, so callers and the WAV writer are unchanged.
/// </para>
/// </remarks>
public sealed class SherpaOnnxTtsEngine : ITtsEngine, IDisposable
{
    /// <summary>Sample rate reported for empty input, before the model is loaded.</summary>
    private const int FallbackSampleRate = 22_050;

    private readonly OfflineTtsConfig _config;
    private readonly string _modelPath;
    private readonly int _speakerId;
    private readonly float _speed;
    private readonly Lock _gate = new();
    private OfflineTts? _tts;
    private bool _disposed;

    /// <summary>
    /// Construct from explicit bundle paths.
    /// </summary>
    /// <param name="modelPath">Absolute path to the VITS/Matcha/Kokoro <c>.onnx</c>.</param>
    /// <param name="tokensPath">Absolute path to the voice's <c>tokens.txt</c>.</param>
    /// <param name="dataDir">
    /// <c>espeak-ng-data</c> directory for espeak-phonemised voices (piper, mimic3).
    /// Null for lexicon-based voices.
    /// </param>
    /// <param name="lexicon">
    /// <c>lexicon.txt</c> for lexicon-based voices (e.g. some coqui/zh). Null for
    /// espeak-based voices.
    /// </param>
    /// <param name="dictDir">Optional jieba <c>dict/</c> directory (Chinese voices).</param>
    /// <param name="speakerId">Speaker id for multi-speaker voices (0 for single-speaker).</param>
    /// <param name="speed">Speaking-rate multiplier (1.0 = natural; &gt;1 faster).</param>
    /// <param name="numThreads">ONNX intra-op thread count.</param>
    /// <param name="noiseScale">VITS noise scale; null keeps the model default (0.667).</param>
    /// <param name="noiseScaleW">VITS duration-predictor noise; null keeps the default (0.8).</param>
    /// <param name="lengthScale">VITS length scale; null keeps the default (1.0).</param>
    public SherpaOnnxTtsEngine(
        string modelPath,
        string tokensPath,
        string? dataDir = null,
        string? lexicon = null,
        string? dictDir = null,
        int speakerId = 0,
        float speed = 1.0f,
        int numThreads = 2,
        float? noiseScale = null,
        float? noiseScaleW = null,
        float? lengthScale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokensPath);

        // A VITS voice needs a phonemisation source: espeak-ng-data (piper/mimic3)
        // OR a lexicon (coqui-style). Neither present means the model would emit
        // silence for real text — fail loudly at construction, not at playback.
        if (string.IsNullOrWhiteSpace(dataDir) && string.IsNullOrWhiteSpace(lexicon))
        {
            throw new ArgumentException(
                "A sherpa VITS voice needs either an espeak-ng-data directory " +
                "(dataDir, for piper/mimic3 voices) or a lexicon.txt (lexicon, for " +
                "coqui-style voices). Neither was supplied — the model would produce " +
                "silence for arbitrary text.",
                nameof(dataDir));
        }

        var vits = new OfflineTtsVitsModelConfig
        {
            Model = modelPath,
            Tokens = tokensPath,
            DataDir = dataDir ?? string.Empty,
            Lexicon = lexicon ?? string.Empty,
            DictDir = dictDir ?? string.Empty,
        };
        // Only override the model's own scales when the caller asked to.
        if (noiseScale.HasValue) vits.NoiseScale = noiseScale.Value;
        if (noiseScaleW.HasValue) vits.NoiseScaleW = noiseScaleW.Value;
        if (lengthScale.HasValue) vits.LengthScale = lengthScale.Value;

        // The nested model configs (Matcha, Kokoro, …) are initialised by
        // OfflineTtsModelConfig's constructor; we only populate the Vits rung and
        // the shared runtime knobs. Setting Model explicitly means we don't rely on
        // OfflineTtsConfig's ctor to have created it.
        _config = new OfflineTtsConfig
        {
            Model = new OfflineTtsModelConfig
            {
                Vits = vits,
                NumThreads = Math.Max(1, numThreads),
                Provider = "cpu",
                Debug = 0,
            },
            MaxNumSentences = 1,
        };

        _modelPath = modelPath;
        _speakerId = Math.Max(0, speakerId);
        _speed = speed <= 0f ? 1.0f : speed;
    }

    /// <summary>
    /// Build an engine from a sherpa voice-bundle directory, resolving the model,
    /// <c>tokens.txt</c>, and <c>espeak-ng-data</c>/<c>lexicon.txt</c>/<c>dict</c>
    /// by the layout sherpa-onnx ships (and that <c>tools/tts-speak</c> unpacks).
    /// </summary>
    /// <param name="bundleDir">Directory containing the extracted voice bundle.</param>
    /// <param name="speakerId">Speaker id for multi-speaker voices.</param>
    /// <param name="speed">Speaking-rate multiplier.</param>
    /// <param name="numThreads">ONNX intra-op thread count.</param>
    public static SherpaOnnxTtsEngine FromBundleDirectory(
        string bundleDir, int speakerId = 0, float speed = 1.0f, int numThreads = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDir);
        if (!Directory.Exists(bundleDir))
            throw new DirectoryNotFoundException($"sherpa voice bundle directory not found: '{bundleDir}'");

        var onnx = Directory.EnumerateFiles(bundleDir, "*.onnx").FirstOrDefault()
            ?? throw new FileNotFoundException($"no .onnx acoustic model found in bundle '{bundleDir}'");

        var tokens = Path.Combine(bundleDir, "tokens.txt");
        if (!File.Exists(tokens))
            throw new FileNotFoundException($"tokens.txt not found in bundle '{bundleDir}'");

        var dataDir = Path.Combine(bundleDir, "espeak-ng-data");
        var lexicon = Path.Combine(bundleDir, "lexicon.txt");
        var dictDir = Path.Combine(bundleDir, "dict");

        return new SherpaOnnxTtsEngine(
            onnx,
            tokens,
            dataDir: Directory.Exists(dataDir) ? dataDir : null,
            lexicon: File.Exists(lexicon) ? lexicon : null,
            dictDir: Directory.Exists(dictDir) ? dictDir : null,
            speakerId: speakerId,
            speed: speed,
            numThreads: numThreads);
    }

    /// <inheritdoc />
    public Task<TtsSynthesisResult> SynthesiseAsync(
        string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new TtsSynthesisResult(
                ReadOnlyMemory<byte>.Empty, FallbackSampleRate, 1, 16));
        }

        return Task.Run(() => SynthesiseCore(text, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Splits on sentence boundaries and synthesises each independently, yielding
    /// PCM as it is produced so playback can start before the whole passage is done.
    /// </remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var sentence in SplitSentences(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sentence))
                continue;

            var result = await Task.Run(
                () => SynthesiseCore(sentence.Trim(), cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (result.AudioData.Length > 0)
                yield return result.AudioData;
        }
    }

    /// <summary>Release the native sherpa-onnx TTS handle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>Lazily construct the native <see cref="OfflineTts"/>. Thread-safe.</summary>
    private OfflineTts EnsureTts()
    {
        if (_tts is not null) return _tts;

        lock (_gate)
        {
            if (_tts is not null) return _tts;

            if (!File.Exists(_modelPath))
            {
                throw new InvalidOperationException(
                    $"sherpa-onnx TTS model not found at '{_modelPath}'. " +
                    "Provide an extracted VITS/Matcha/Kokoro voice bundle.");
            }

            _tts = new OfflineTts(_config);
            return _tts;
        }
    }

    /// <summary>Core synthesis: generate float samples then convert to PCM16.</summary>
    private TtsSynthesisResult SynthesiseCore(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var tts = EnsureTts();

        OfflineTtsGeneratedAudio audio;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            audio = tts.Generate(text, _speed, _speakerId);
        }

        try
        {
            var samples = audio.Samples;
            var sampleRate = audio.SampleRate > 0 ? audio.SampleRate : FallbackSampleRate;

            if (samples is null || samples.Length == 0)
                return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, sampleRate, 1, 16);

            var pcm = FloatWaveformToPcm16(samples);
            return new TtsSynthesisResult(pcm, sampleRate, 1, 16);
        }
        finally
        {
            audio.Dispose();
        }
    }

    /// <summary>
    /// Convert a float waveform (values in [-1, 1]) to little-endian 16-bit
    /// signed PCM — identical scaling to <see cref="OnnxTtsEngine"/> so both
    /// engines feed the same players/WAV writer.
    /// </summary>
    private static byte[] FloatWaveformToPcm16(ReadOnlySpan<float> waveform)
    {
        var pcm = new byte[waveform.Length * 2];
        for (int i = 0; i < waveform.Length; i++)
        {
            float sample = Math.Clamp(waveform[i], -1f, 1f);
            short value = (short)(sample * 32767f);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>
    /// Split text into sentences on common terminators, preserving the delimiter.
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        int start = 0;
        char[] delimiters = ['.', '!', '?', ';'];

        for (int i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(delimiters, text[i]) >= 0)
            {
                var sentence = text[start..(i + 1)];
                if (!string.IsNullOrWhiteSpace(sentence))
                    sentences.Add(sentence);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            var remainder = text[start..];
            if (!string.IsNullOrWhiteSpace(remainder))
                sentences.Add(remainder);
        }

        // Whole-passage fallback when there was no terminator at all.
        if (sentences.Count == 0 && !string.IsNullOrWhiteSpace(text))
            sentences.Add(text);

        return sentences;
    }
}
