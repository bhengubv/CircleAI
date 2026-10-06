#nullable enable

// KokoroTtsEngine.cs
//
// Runs a Kokoro (StyleTTS2) voice through ONNX Runtime.
//
// This is a THIRD input layout beside the two OnnxTtsEngine already serves
// (Piper and MMS/sherpa), and it is different enough to deserve its own class
// rather than a third branch inside that one: the tokeniser is a fixed 115-entry
// IPA vocabulary rather than the voice's own phoneme map, the voice identity
// arrives as a 256-float STYLE VECTOR read from a separate file rather than a
// speaker id, and the output is 24 kHz rather than 16/22.05.
//
// Why bother: Kokoro is Apache-2.0 and one 82-156 MB model carries Hindi,
// Japanese, Spanish, French, Portuguese, Italian and Chinese. For Hindi
// specifically it was the only permissively-licensed option found — every Hindi
// voice in the Piper catalogue is CC-BY-NC-SA or under a research-only licence.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="ITtsEngine"/> for Kokoro / StyleTTS2 ONNX voices.
/// </summary>
public sealed class KokoroTtsEngine : ITtsEngine, ITtsFrontEndDiagnostics, IDisposable
{
    private const string InputIdsName = "input_ids";
    private const string StyleName    = "style";
    private const string SpeedName    = "speed";

    /// <summary>Kokoro emits 24 kHz, unlike the 16/22.05 kHz VITS voices.</summary>
    public const int KokoroSampleRate = 24000;

    /// <summary>Style dimensions per row in a voice pack.</summary>
    private const int StyleDims = 256;

    /// <summary>
    /// Rows in a voice pack. The row is chosen by token count, so this is also a
    /// hard ceiling on how many phonemes one call may synthesise.
    /// </summary>
    private const int MaxStyleRows = 510;

    private readonly string _modelPath;
    private readonly IPhonemizer _phonemizer;
    private readonly IReadOnlyDictionary<string, long> _vocab;
    private readonly float[] _voicePack;          // MaxStyleRows * StyleDims
    private readonly object _gate = new();

    private InferenceSession? _session;
    private bool _disposed;

    public float Speed { get; set; } = 1.0f;

    /// <inheritdoc />
    public int LastSkippedCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> LastSkippedSymbols { get; private set; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> LastApproximatedSymbols => Array.Empty<string>();

    /// <summary>
    /// Number of phonemes the last call dropped because the utterance was longer
    /// than a voice pack can style. Non-zero means audible truncation.
    /// </summary>
    public int LastTruncatedPhonemes { get; private set; }

    private KokoroTtsEngine(
        string modelPath, IPhonemizer phonemizer,
        IReadOnlyDictionary<string, long> vocab, float[] voicePack)
    {
        _modelPath = modelPath;
        _phonemizer = phonemizer;
        _vocab = vocab;
        _voicePack = voicePack;
    }

    /// <summary>
    /// Builds an engine from a Kokoro directory: the model, its
    /// <c>tokenizer.json</c>, and one voice pack from <c>voices/</c>.
    /// </summary>
    /// <param name="phonemizer">
    /// Must yield IPA. Kokoro's vocabulary is IPA symbols, not letters — feeding
    /// it graphemes produces confident nonsense rather than an error.
    /// </param>
    public static KokoroTtsEngine FromDirectory(
        string directory, string voiceName, IPhonemizer phonemizer, string modelStem = "kokoro")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ArgumentNullException.ThrowIfNull(phonemizer);

        var model = OnnxSessionFactory.PickModelFile(directory, modelStem);
        if (!File.Exists(model))
            throw new FileNotFoundException($"Kokoro model not found: {model}", model);

        var tokenizer = Path.Combine(directory, "tokenizer.json");
        if (!File.Exists(tokenizer))
            throw new FileNotFoundException(
                $"Kokoro needs tokenizer.json beside the model (its IPA vocabulary): {tokenizer}",
                tokenizer);

        var voice = Path.Combine(directory, "voices", voiceName + ".bin");
        if (!File.Exists(voice))
            throw new FileNotFoundException(
                $"voice pack '{voiceName}' not found: {voice}", voice);

        return new KokoroTtsEngine(model, phonemizer, LoadVocab(tokenizer), LoadVoicePack(voice));
    }

    /// <summary>Reads the 115-symbol IPA vocabulary out of <c>tokenizer.json</c>.</summary>
    private static IReadOnlyDictionary<string, long> LoadVocab(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJson));
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("vocab", out var vocab))
            throw new InvalidDataException($"no model.vocab in {tokenizerJson}");

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in vocab.EnumerateObject())
            if (p.Value.TryGetInt64(out var id)) map[p.Name] = id;

        if (map.Count == 0) throw new InvalidDataException($"empty vocab in {tokenizerJson}");
        return map;
    }

    /// <summary>
    /// Reads a voice pack: <see cref="MaxStyleRows"/> rows of
    /// <see cref="StyleDims"/> little-endian float32.
    /// </summary>
    private static float[] LoadVoicePack(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = MaxStyleRows * StyleDims * sizeof(float);
        if (bytes.Length != expected)
            throw new InvalidDataException(
                $"voice pack {Path.GetFileName(path)} is {bytes.Length} bytes, expected {expected} " +
                $"({MaxStyleRows}x{StyleDims} float32)");

        var floats = new float[MaxStyleRows * StyleDims];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private InferenceSession EnsureSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _session ??= OnnxSessionFactory.Open(_modelPath);
        }
    }

    public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken cancellationToken = default)
        => Task.Run(() => Synthesise(text, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await SynthesiseAsync(text, cancellationToken).ConfigureAwait(false);
        if (r.AudioData.Length > 0) yield return r.AudioData;
    }

    private TtsSynthesisResult Synthesise(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastTruncatedPhonemes = 0;

        if (string.IsNullOrWhiteSpace(text))
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, KokoroSampleRate, 1, 16);

        var ids = Tokenise(text);
        if (ids.Count == 0)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, KokoroSampleRate, 1, 16);

        var session = EnsureSession();

        // The model expects a boundary token at each end; the STYLE row, however,
        // is chosen by the count WITHOUT those — mixing the two up shifts every
        // utterance onto a neighbouring style and quietly changes the delivery.
        var styleRow = Math.Min(ids.Count, MaxStyleRows - 1);

        var tokens = new long[ids.Count + 2];
        for (int i = 0; i < ids.Count; i++) tokens[i + 1] = ids[i];

        var inputIds = new DenseTensor<long>(tokens, new[] { 1, tokens.Length });
        var style = new DenseTensor<float>(new[] { 1, StyleDims });
        for (int i = 0; i < StyleDims; i++) style[0, i] = _voicePack[styleRow * StyleDims + i];

        // float32 even on the fp16 build — the graph rejects float16 here.
        var speed = new DenseTensor<float>(new[] { Math.Max(0.1f, Speed) }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(StyleName, style),
            NamedOnnxValue.CreateFromTensor(SpeedName, speed),
        };

        using var results = session.Run(inputs);
        var waveform = results.First().AsTensor<float>();

        return new TtsSynthesisResult(ToPcm16(waveform), KokoroSampleRate, 1, 16);
    }

    /// <summary>Text → IPA → vocabulary ids, recording anything unmapped.</summary>
    private List<long> Tokenise(string text)
    {
        var phonemes = _phonemizer.Phonemize(text);
        var ids = new List<long>(phonemes.Count);
        var dropped = new List<string>();

        foreach (var p in phonemes)
        {
            // espeak emits stress marks and separators Kokoro's vocabulary does
            // carry, so look the symbol up as-is before giving up on it.
            if (_vocab.TryGetValue(p, out var id)) { ids.Add(id); continue; }

            // A multi-character cluster still maps one symbol at a time.
            var mapped = false;
            foreach (var ch in p)
            {
                if (_vocab.TryGetValue(ch.ToString(), out var cid)) { ids.Add(cid); mapped = true; }
            }
            if (!mapped && !dropped.Contains(p)) dropped.Add(p);
        }

        LastSkippedCount = dropped.Count;
        LastSkippedSymbols = dropped;

        // A voice pack has no style row beyond MaxStyleRows, so anything longer
        // cannot be spoken in one call. Report it rather than silently cutting —
        // the caller should be splitting into sentences (PhrasedTtsEngine).
        if (ids.Count > MaxStyleRows - 2)
        {
            LastTruncatedPhonemes = ids.Count - (MaxStyleRows - 2);
            ids.RemoveRange(MaxStyleRows - 2, LastTruncatedPhonemes);
        }
        return ids;
    }

    /// <summary>
    /// float waveform → little-endian PCM16, clamped.
    /// </summary>
    /// <remarks>
    /// Kokoro overshoots full scale — a measured peak of 1.008 on a two-word
    /// utterance. Scaling that without clamping wraps the sample to the opposite
    /// rail and produces a loud click exactly where the audio is loudest.
    /// </remarks>
    private static byte[] ToPcm16(Tensor<float> waveform)
    {
        var n = (int)waveform.Length;
        var pcm = new byte[n * 2];
        var i = 0;
        foreach (var sample in waveform)
        {
            var clamped = Math.Clamp(sample, -1.0f, 1.0f);
            var v = (short)Math.Round(clamped * short.MaxValue);
            pcm[i++] = (byte)(v & 0xFF);
            pcm[i++] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
