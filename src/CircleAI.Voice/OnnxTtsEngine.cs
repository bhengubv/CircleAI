using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="ITtsEngine"/> implementation that runs a VITS/Piper-style
/// text-to-speech model through ONNX Runtime.
/// </summary>
/// <remarks>
/// <para>
/// PHONEME-DRIVEN when a Piper <c>.onnx.json</c> config is present (the correct
/// path): text → phonemes (<see cref="IPhonemizer"/>) → token ids
/// (<see cref="PiperVoiceConfig.PhonemesToIds"/>, the model's real
/// <c>phoneme_id_map</c> in Piper's BOS/pad/EOS layout) → waveform. Sample rate
/// and the noise/length/noise_w scales come from the config, not from guesses.
/// </para>
/// <para>
/// This replaced a stub that mapped each TEXT character to <c>codepoint+1</c> and
/// interleaved zeros — feeding the model ids it was never trained on, so it
/// produced silence/garbage. That is why "OnnxTtsEngine cannot speak" was true.
/// </para>
/// <para>
/// When NO config sidecar is found the engine falls back to the legacy
/// character-level tokeniser for generic non-Piper models — clearly a fallback,
/// not the intended path.
/// </para>
/// </remarks>
public sealed class OnnxTtsEngine : ITtsEngine, ITtsFrontEndDiagnostics, IDisposable
{
    private readonly string _modelPath;
    private readonly int _sampleRate;
    private readonly PiperVoiceConfig? _config;
    private readonly IPhonemizer _phonemizer;
    private readonly Lock _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    /// <summary>
    /// The name of the ONNX model input that receives token IDs.
    /// Standard for VITS-style models.
    /// </summary>
    private const string InputName = "input";

    /// <summary>
    /// The name of the ONNX model input that receives input lengths.
    /// Required by VITS-style models for batched inference.
    /// </summary>
    private const string InputLengthsName = "input_lengths";

    /// <summary>
    /// The name of the ONNX model input that receives scales
    /// (noise_scale, length_scale, noise_scale_w).
    /// </summary>
    private const string ScalesName = "scales";

    /// <summary>
    /// The name of the ONNX model output that contains the waveform.
    /// </summary>
    private const string OutputName = "output";

    /// <summary>
    /// Token-id input on sherpa-onnx / MMS VITS exports. Those graphs name the
    /// ids <c>x</c> and the length <c>x_length</c>, and take the three inference
    /// scales as SEPARATE scalar tensors rather than one <c>scales[3]</c>.
    /// </summary>
    private const string MmsInputName = "x";

    /// <summary>Sequence-length input on sherpa-onnx / MMS VITS exports.</summary>
    private const string MmsInputLengthsName = "x_length";

    /// <summary>MeloTTS spells the same input <c>x_lengths</c>, with an s.</summary>
    private const string MeloInputLengthsName = "x_lengths";

    /// <summary>
    /// Parallel tone ids, declared by MeloTTS-family voices. Supplied by an
    /// <see cref="IToneSource"/> phonemizer; absent voices never see it.
    /// </summary>
    private const string TonesName = "tones";

    /// <summary>
    /// HuggingFace-transformers VITS names its inputs <c>input_ids</c> and
    /// <c>attention_mask</c>, and bakes the noise/length scales into the config
    /// rather than exposing them. A model exported straight from <c>VitsModel</c>
    /// arrives in this shape — which is how MMS ships the languages that have no
    /// pre-converted ONNX, Amharic and Tigrinya among them.
    /// </summary>
    private const string HfInputIdsName = "input_ids";
    private const string HfAttentionMaskName = "attention_mask";

    /// <summary>Noise-scale scalar input on sherpa-onnx / MMS VITS exports.</summary>
    private const string MmsNoiseScaleName = "noise_scale";

    /// <summary>Length-scale scalar input on sherpa-onnx / MMS VITS exports.</summary>
    private const string MmsLengthScaleName = "length_scale";

    /// <summary>Noise-w scalar input on sherpa-onnx / MMS VITS exports.</summary>
    private const string MmsNoiseWName = "noise_scale_w";

    /// <summary>Speaker-id input on multi-speaker Coqui VITS exports.</summary>
    private const string SpeakerIdName = "sid";

    /// <summary>Language-id input on multi-lingual Coqui VITS exports.</summary>
    private const string LanguageIdName = "langid";

    private bool _hasSpeakerId;
    private bool _hasLanguageId;

    /// <summary>
    /// Speaker to synthesise as, for multi-speaker voices. Ignored by models that
    /// declare no <c>sid</c> input.
    /// </summary>
    public long SpeakerId { get; set; }

    /// <summary>
    /// Language to synthesise in, for multi-lingual voices — e.g. the 11-language
    /// SA VITS uses 0=afr 1=eng 2=nbl 3=nso 4=sot 5=ssw 6=tsn 7=tso 8=ven 9=xho
    /// 10=zul. Ignored by models that declare no <c>langid</c> input.
    /// </summary>
    public long LanguageId { get; set; }

    /// <summary>
    /// Overrides the model's own <c>length_scale</c> for the next synthesis.
    /// Larger is slower. Null uses whatever the voice's config declares.
    /// </summary>
    /// <remarks>
    /// This exists for very short utterances. A VITS voice predicts duration from
    /// context, and given a single word with no sentence around it — which is what
    /// a code-switched name like "CircleAI" becomes once it is cut out of its
    /// isiZulu sentence to be said in English — it rushes the word into a mumble.
    /// Lengthening a short span is the standard mitigation. It changes nothing for
    /// ordinary sentences, which is why it is opt-in per call rather than a new
    /// default.
    /// </remarks>
    public float? LengthScaleOverride { get; set; }

    /// <summary>
    /// Overrides the model's <c>noise_w</c> (duration-predictor noise) for the next
    /// synthesis. Lower is steadier. Null uses the voice's own value.
    /// </summary>
    /// <remarks>
    /// Duration noise is what gives a long sentence natural variation; on a
    /// one-word utterance it is just jitter with nothing to average out, and it
    /// makes a short span land differently every time it is spoken.
    /// </remarks>
    public float? NoiseWOverride { get; set; }

    /// <summary>
    /// Extra silent tokens to place at the start of each utterance, whose audio is
    /// then trimmed away. Zero (the default) changes nothing.
    /// </summary>
    /// <remarks>
    /// The token layout is <c>[BOS, PAD, char, PAD, char, …, EOS]</c>, and the
    /// duration predictor sets a length for every one of those with nothing to its
    /// left to condition on. With one utterance per sentence it over-lengthens the
    /// opening every time — heard as the first syllable of each sentence being
    /// dragged.
    ///
    /// Padding the front gives that stretch somewhere inert to land: it happens on
    /// silence instead of on the first real sound, and silence is safe to cut
    /// because its boundary is unambiguous. The alternative — trimming into actual
    /// speech — risks clipping the very syllable being rescued.
    /// </remarks>
    public int LeadInPads { get; set; }

    /// <summary>
    /// Overrides the model's <c>noise_scale</c> for the next synthesis. Lower is
    /// cleaner and flatter; null uses the voice's own value.
    /// </summary>
    /// <remarks>
    /// This is the breathiness control, and it is a different thing from
    /// <see cref="NoiseWOverride"/>: noise_w perturbs how LONG each sound is,
    /// noise_scale perturbs the sound ITSELF. When a voice is asked for a language
    /// its speaker embedding has no evidence for, the model is uncertain about the
    /// waveform rather than the timing, and that uncertainty comes out as breath —
    /// heard as speaking through a gust of wind. Turning this down trades some
    /// natural variation for a steadier sound, which is the right trade on a span
    /// that would otherwise be noise.
    /// </remarks>
    public float? NoiseScaleOverride { get; set; }

    /// <summary>
    /// How many symbols the last synthesis could not map, and so did not speak.
    /// Anything above zero means the audio is missing sound that the text asked
    /// for; treat a non-trivial count as a broken front-end, not a rounding error.
    /// </summary>
    public int LastSkippedCount { get; private set; }

    // Implements ITtsFrontEndDiagnostics — see that interface for why this is not
    // merely a debug counter.

    /// <summary>
    /// The distinct symbols dropped by the last synthesis — the actionable half of
    /// <see cref="LastSkippedCount"/>, since it names what to add to the map.
    /// </summary>
    public IReadOnlyList<string> LastSkippedSymbols { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Symbols folded to a near-equivalent because this voice has no token for
    /// them — e.g. Sepedi <c>š</c> or Tshivenda <c>ṱ ḓ ṋ</c> on a voice whose
    /// vocabulary omits them.
    /// </summary>
    public IReadOnlyList<string> LastApproximatedSymbols { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// True when the loaded graph uses the sherpa-onnx / MMS input signature
    /// rather than Piper's. Decided from the model's own input metadata when the
    /// session is created, so one engine serves both families without a flag.
    /// </summary>
    private bool _useMmsLayout;

    /// <summary>
    /// Piper-style engine. Loads the <c>&lt;model&gt;.onnx.json</c> sidecar for the
    /// phoneme map, sample rate and inference scales.
    /// </summary>
    /// <param name="modelPath">Absolute path to the ONNX model file.</param>
    /// <param name="phonemizer">
    /// Text → phonemes. Defaults to <see cref="PassthroughPhonemizer"/> (input is
    /// already phoneme symbols). For arbitrary English on an espeak-type voice,
    /// pass an <see cref="EspeakPhonemizer"/>.
    /// </param>
    /// <param name="config">
    /// Explicit config; when null the sidecar next to <paramref name="modelPath"/>
    /// is loaded if present.
    /// </param>
    public OnnxTtsEngine(string modelPath, IPhonemizer? phonemizer, PiperVoiceConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        _modelPath = modelPath;
        _config = config ?? PiperVoiceConfig.TryLoadForModel(modelPath);
        _phonemizer = phonemizer ?? new PassthroughPhonemizer();
        _sampleRate = _config?.SampleRate ?? 22_050;
    }

    /// <summary>
    /// Legacy generic constructor: no config, character-level tokeniser, explicit
    /// sample rate. Retained for non-Piper models; the Piper path above is the
    /// intended one.
    /// </summary>
    /// <param name="modelPath">Absolute path to the ONNX model file.</param>
    /// <param name="sampleRate">Output sample rate in Hz. Must match the model.</param>
    public OnnxTtsEngine(string modelPath, int sampleRate = 24_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _modelPath = modelPath;
        _config = null;
        _phonemizer = new PassthroughPhonemizer();
        _sampleRate = sampleRate;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tokenises the input text, runs ONNX inference, converts the output
    /// waveform to 16-bit PCM, and returns the full audio buffer.
    /// </remarks>
    public Task<TtsSynthesisResult> SynthesiseAsync(
        string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new TtsSynthesisResult(
                ReadOnlyMemory<byte>.Empty, _sampleRate, 1, 16));
        }

        return Task.Run(() => SynthesiseCore(text, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Splits the input text on sentence boundaries and synthesises each
    /// sentence independently, yielding PCM chunks as they become available.
    /// This enables low-latency playback that begins before the full text
    /// has been processed.
    /// </remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var sentences = SplitSentences(text);

        foreach (var sentence in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sentence))
                continue;

            var result = await Task.Run(
                () => SynthesiseCore(sentence.Trim(), cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (result.AudioData.Length > 0)
            {
                yield return result.AudioData;
            }
        }
    }

    /// <summary>
    /// Release the ONNX session and associated native resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Ensure the ONNX inference session is loaded. Thread-safe via
    /// <see cref="_gate"/>.
    /// </summary>
    private InferenceSession EnsureSession()
    {
        if (_session is not null) return _session;

        lock (_gate)
        {
            if (_session is not null) return _session;

            if (!File.Exists(_modelPath))
            {
                throw new InvalidOperationException(
                    $"ONNX TTS model not found at '{_modelPath}'. " +
                    "Provide a valid VITS/Kokoro ONNX model file.");
            }

            // Same session handling as every other engine: reuse an ORT-optimised
            // copy when there is one, write one when there is not. Re-optimising a
            // large graph on a phone costs minutes, and this path serves most voices.
            _session = OnnxSessionFactory.Open(_modelPath);

            // Which VITS export is this? Piper names the ids "input" and takes a
            // single scales[3]; sherpa-onnx/MMS names them "x"/"x_length" with the
            // scales as separate scalars. Ask the graph rather than assume.
            _useMmsLayout = _session.InputMetadata.ContainsKey(MmsInputName);

            // Multi-speaker / multi-lingual Coqui exports add these; a model that
            // declares them will not run unless they are supplied.
            _hasSpeakerId = _session.InputMetadata.ContainsKey(SpeakerIdName);
            _hasLanguageId = _session.InputMetadata.ContainsKey(LanguageIdName);

            return _session;
        }
    }

    /// <summary>
    /// Core synthesis: tokenise -> infer -> convert waveform to PCM16.
    /// </summary>
    private TtsSynthesisResult SynthesiseCore(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var session = EnsureSession();

        // Phoneme path when a Piper config is present (correct); character path
        // otherwise (legacy generic fallback).
        long[] tokens;
        float noiseScale, lengthScale, noiseW;
        if (_config is { HasPhonemeMap: true })
        {
            var phonemes = _phonemizer.Phonemize(text);
            tokens = _config.PhonemesToIds(
                phonemes, out var skipped, out var droppedSymbols, out var approxSymbols);
            LastApproximatedSymbols = approxSymbols;

            // A dropped symbol makes no sound, so nothing downstream can reveal it:
            // the audio is merely shorter, and every acoustic metric still passes.
            // This went unnoticed until a listener heard the missing syllables. Record
            // it so the front-end can be inspected instead of inferred.
            LastSkippedCount = skipped;
            LastSkippedSymbols = droppedSymbols;
            noiseScale  = _config.NoiseScale;
            lengthScale = _config.LengthScale;
            noiseW      = _config.NoiseW;
        }
        else
        {
            tokens = TokeniseText(text);
            noiseScale = 0.667f; lengthScale = 1.0f; noiseW = 0.8f;
        }

        // Caller overrides win, whichever path produced the defaults above.
        if (LengthScaleOverride is { } ls) lengthScale = ls;
        if (NoiseWOverride is { } nw) noiseW = nw;
        if (NoiseScaleOverride is { } ns) noiseScale = ns;

        // A map of only BOS+pad+EOS (e.g. all phonemes unknown) is not speech.
        if (tokens.Length <= 3)
        {
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, _sampleRate, 1, 16);
        }

        // Give the opening stretch somewhere inert to land — see LeadInPads. The
        // pads go in after BOS, so the utterance still starts the way the model
        // expects; only the run of silence before the first real sound grows.
        if (LeadInPads > 0 && tokens.Length > 1)
        {
            var padded = new long[tokens.Length + LeadInPads];
            padded[0] = tokens[0];                                   // BOS
            for (var i = 0; i < LeadInPads; i++) padded[1 + i] = PadTokenId;
            Array.Copy(tokens, 1, padded, 1 + LeadInPads, tokens.Length - 1);
            tokens = padded;
        }

        // Build input tensors.
        // Input: token IDs as int64 [1, sequence_length]
        var inputTensor = new DenseTensor<long>(new[] { 1, tokens.Length });
        for (int i = 0; i < tokens.Length; i++)
        {
            inputTensor[0, i] = tokens[i];
        }

        // Input lengths: [1] containing the sequence length
        var inputLengths = new DenseTensor<long>(new[] { 1 });
        inputLengths[0] = tokens.Length;

        // Feed the scales in the shape this export actually declares: three
        // separate scalars for sherpa-onnx/MMS, one scales[3] for Piper.
        List<NamedOnnxValue> inputs;
        if (_session.InputMetadata.ContainsKey(HfInputIdsName))
        {
            // transformers VITS: ids + a mask of ones. No scales — the model holds
            // them. Detected from the graph's own metadata, like the other layouts,
            // so a model that declares this shape is simply served correctly rather
            // than being asked for inputs it does not have.
            var mask = new DenseTensor<long>(new[] { 1, tokens.Length });
            for (int i = 0; i < tokens.Length; i++) mask[0, i] = 1;

            inputs =
            [
                NamedOnnxValue.CreateFromTensor(HfInputIdsName, inputTensor),
                NamedOnnxValue.CreateFromTensor(HfAttentionMaskName, mask),
            ];
        }
        else if (_useMmsLayout)
        {
            inputs =
            [
                NamedOnnxValue.CreateFromTensor(MmsInputName, inputTensor),
                NamedOnnxValue.CreateFromTensor(
                    _session.InputMetadata.ContainsKey(MmsInputLengthsName)
                        ? MmsInputLengthsName : MeloInputLengthsName, inputLengths),
                NamedOnnxValue.CreateFromTensor(MmsNoiseScaleName, Scalar(noiseScale)),
                NamedOnnxValue.CreateFromTensor(MmsLengthScaleName, Scalar(lengthScale)),
                NamedOnnxValue.CreateFromTensor(MmsNoiseWName, Scalar(noiseW))
            ];

            // MeloTTS carries TONE as a channel of its own, parallel to the
            // phonemes, rather than as symbols inside them: its lexicon gives
            // "一 y i 1 1" — phonemes y,i and tones 1,1. Cantonese takes the other
            // approach and writes tone into the phoneme string (˥), which is why
            // it needs none of this. A tone array that drifts out of step with the
            // phonemes would not fail; it would mispronounce every syllable after
            // the drift, in a language where tone IS the word.
            if (_session.InputMetadata.ContainsKey(TonesName))
            {
                var tones = new DenseTensor<long>(new[] { 1, tokens.Length });
                var supplied = (_phonemizer as IToneSource)?.LastTones;
                for (int i = 0; i < tokens.Length; i++)
                    tones[0, i] = supplied is not null && i < supplied.Count ? supplied[i] : 0;
                inputs.Add(NamedOnnxValue.CreateFromTensor(TonesName, tones));
            }

            if (_session.InputMetadata.ContainsKey(SpeakerIdName))
            {
                var sid = new DenseTensor<long>(new[] { 1 });
                sid[0] = SpeakerId;
                inputs.Add(NamedOnnxValue.CreateFromTensor(SpeakerIdName, sid));
            }
        }
        else
        {
            var scales = new DenseTensor<float>(new[] { 3 });
            scales[0] = noiseScale;
            scales[1] = lengthScale;
            scales[2] = noiseW;

            inputs =
            [
                NamedOnnxValue.CreateFromTensor(InputName, inputTensor),
                NamedOnnxValue.CreateFromTensor(InputLengthsName, inputLengths),
                NamedOnnxValue.CreateFromTensor(ScalesName, scales)
            ];
        }

        if (_hasSpeakerId)
        {
            var sid = new DenseTensor<long>(new[] { 1 });
            sid[0] = SpeakerId;
            inputs.Add(NamedOnnxValue.CreateFromTensor(SpeakerIdName, sid));
        }

        if (_hasLanguageId)
        {
            var langId = new DenseTensor<long>(new[] { 1 });
            langId[0] = LanguageId;
            inputs.Add(NamedOnnxValue.CreateFromTensor(LanguageIdName, langId));
        }

        // Run inference.
        float[] waveform;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();

            using var results = session.Run(inputs);
            var outputTensor = results.First();
            var outputData = outputTensor.AsTensor<float>();

            waveform = new float[outputData.Length];
            int idx = 0;
            foreach (var sample in outputData)
            {
                waveform[idx++] = sample;
            }
        }

        // Cut the silence the lead-in pads produced. Only ever trims a leading run
        // that is genuinely quiet, so if the model put speech there — nothing to
        // stretch, no drag to absorb — this does nothing and takes nothing.
        if (LeadInPads > 0) waveform = TrimLeadingSilence(waveform);

        // Convert float waveform [-1, 1] to 16-bit signed PCM.
        var pcmBytes = FloatWaveformToPcm16(waveform);

        return new TtsSynthesisResult(pcmBytes, _sampleRate, 1, 16);
    }

    /// <summary>
    /// A one-element float tensor, the shape sherpa-onnx/MMS exports declare for
    /// each individual inference scale.
    /// </summary>
    private static DenseTensor<float> Scalar(float value)
    {
        var t = new DenseTensor<float>(new[] { 1 });
        t[0] = value;
        return t;
    }

    /// <summary>
    /// Character-level tokenisation fallback. Maps each character to its
    /// Unicode code point. Real production models typically require a
    /// phonemizer or model-specific vocabulary lookup.
    /// </summary>
    /// <remarks>
    /// The first token is a BOS (beginning of sequence) marker (0) and the
    /// last is an EOS (end of sequence) marker (0). Blank tokens (0) are
    /// inserted between each character for VITS-style models that expect
    /// interleaved blanks.
    /// </remarks>
    internal static long[] TokeniseText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        // VITS models expect: [BOS, blank, char, blank, char, ..., blank, EOS]
        // where blank = 0, BOS = 0, EOS = 0, and char tokens start at some offset.
        // We use a simple mapping: char code point + 1 (to avoid collision with blank).
        var result = new List<long>(text.Length * 2 + 2) { 0 }; // BOS / blank

        foreach (char c in text)
        {
            result.Add(c + 1); // character token
            result.Add(0);      // inter-character blank
        }

        return result.ToArray();
    }

    /// <summary>The id of the PAD token, or 0 when the voice declares none.</summary>
    private long PadTokenId => _config?.PadId ?? 0;

    /// <summary>
    /// Drops a leading run of near-silence, keeping a short natural head.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose. It stops at the first sample above the threshold
    /// and leaves 20 ms in front of it, because cutting flush to the first sample
    /// of speech clips the attack — which would trade a dragged syllable for a
    /// chopped one.
    /// </remarks>
    private float[] TrimLeadingSilence(float[] waveform, float threshold = 0.01f)
    {
        var first = 0;
        while (first < waveform.Length && MathF.Abs(waveform[first]) < threshold) first++;
        if (first >= waveform.Length) return waveform;      // all quiet: leave it alone

        var keepBefore = _sampleRate / 50;                  // 20 ms of run-up
        var start = Math.Max(0, first - keepBefore);
        if (start == 0) return waveform;

        var trimmed = new float[waveform.Length - start];
        Array.Copy(waveform, start, trimmed, 0, trimmed.Length);
        return trimmed;
    }

    /// Convert a float waveform (values in [-1, 1]) to 16-bit signed PCM
    /// as a byte array (little-endian).
    /// </summary>
    private static byte[] FloatWaveformToPcm16(ReadOnlySpan<float> waveform)
    {
        var pcm = new byte[waveform.Length * 2];
        for (int i = 0; i < waveform.Length; i++)
        {
            // Clamp to [-1, 1] then scale to short range.
            float sample = Math.Clamp(waveform[i], -1f, 1f);
            short value = (short)(sample * 32767f);
            // Little-endian: low byte first.
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>
    /// Split text into sentences on common sentence-ending punctuation.
    /// Preserves the delimiter at the end of each chunk.
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
                // Include the delimiter in the sentence.
                var sentence = text[start..(i + 1)];
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }
                start = i + 1;
            }
        }

        // Remainder after the last delimiter.
        if (start < text.Length)
        {
            var remainder = text[start..];
            if (!string.IsNullOrWhiteSpace(remainder))
            {
                sentences.Add(remainder);
            }
        }

        return sentences;
    }
}
