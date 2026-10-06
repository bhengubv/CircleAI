using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>
/// Kyutai Pocket-TTS: an autoregressive flow-matching voice that clones a
/// speaker from a few seconds of reference audio and runs on a phone CPU.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS NOT A VITS, AND IT DOES NOT FIT <see cref="OnnxTtsEngine"/>. Every
/// other voice in the catalogue is one graph: ids in, waveform out. This is
/// five graphs and a decode loop, and the loop carries 18 language-model states
/// plus 56 decoder states from step to step. Bolting it onto the single-graph
/// engine would have meant a second engine wearing the first one's interface,
/// so it is its own class and shares only <see cref="ITtsEngine"/>.
/// </para>
/// <para>
/// The pipeline, in the order it must run:
/// </para>
/// <list type="number">
///   <item><b>encoder</b> — reference audio → 1024-d latents. This is the VOICE.</item>
///   <item><b>lm_main</b> primed with those latents, sequence length ZERO.</item>
///   <item><b>text_conditioner</b> — token ids → 1024-d embeddings.</item>
///   <item><b>lm_main</b> primed with those, sequence length ZERO again.</item>
///   <item>then per frame: <b>lm_main</b> → conditioning + eos, <b>lm_flow</b>
///         → one flow step, <b>decoder</b> → 1920 audio samples.</item>
/// </list>
/// <para>
/// THE VOICE AND THE TEXT GO IN THROUGH THE SAME INPUT. It is called
/// <c>text_embeddings</c>, but the backbone simply concatenates it in front of
/// the audio sequence, so it is a prefix, not a text channel. Priming with
/// speaker latents through it is how cloning works. Skip that step and the
/// model is unconditioned: it emits EOS after two frames and 0.16 s of near
/// silence — which reads exactly like a broken export.
/// </para>
/// <para>
/// NaN MEANS "BEGINNING OF SEQUENCE". The first autoregressive step feeds a
/// [1,1,32] tensor of NaN, which the graph rewrites to its learned BOS
/// embedding. Feeding zeros instead is silently wrong — zeros are a valid
/// latent, so the model starts mid-utterance rather than at the start.
/// </para>
/// <para>
/// EOS DOES NOT END GENERATION. The logit crossing its threshold records WHERE
/// the end is; the loop then runs on for a few more frames, because the decoder
/// is streaming and the tail of the last word has not come out yet. Stopping on
/// the first EOS clips the final consonant off every sentence.
/// </para>
/// <para>
/// ONE FLOW STEP. Pocket-TTS uses Lagrangian self-distillation, so the
/// flow integrates in a single step: draw noise, ask the flow net for the
/// direction at s=0 → t=1, add it. That is what makes it cheap enough to matter
/// on a phone; a many-step sampler would put it back in the same cost class as
/// everything it beats.
/// </para>
/// </remarks>
public sealed class PocketTtsEngine : ITtsEngine, IDisposable
{
    /// <summary>Mimi's sample rate. Latents run at 12.5 Hz, so 1920 samples per frame.</summary>
    public const int SampleRate = 24000;

    private const int LatentDim = 32;
    private const int PrefixDim = 1024;

    // Reference defaults (pocket_tts/default_parameters.py). Temperature is a
    // standard deviation squared: the noise is drawn at sqrt(temperature).
    private const float Temperature = 0.7f;
    private const float EosThreshold = -4.0f;
    private const float NoiseClamp = 10.0f;

    private readonly InferenceSession _textConditioner;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _lmMain;
    private readonly InferenceSession _lmFlow;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceUnigram _tokeniser;
    private readonly float[] _voiceLatents;      // [T, 1024], flattened
    private readonly int _voiceFrames;
    private readonly Random _rng;
    private bool _disposed;

    private PocketTtsEngine(
        InferenceSession textConditioner, InferenceSession encoder, InferenceSession lmMain,
        InferenceSession lmFlow, InferenceSession decoder, SentencePieceUnigram tokeniser,
        float[] voiceLatents, int voiceFrames, int seed)
    {
        _textConditioner = textConditioner;
        _encoder = encoder;
        _lmMain = lmMain;
        _lmFlow = lmFlow;
        _decoder = decoder;
        _tokeniser = tokeniser;
        _voiceLatents = voiceLatents;
        _voiceFrames = voiceFrames;
        _rng = new Random(seed);
    }

    /// <summary>
    /// Open a Pocket-TTS bundle and clone the voice in <paramref name="referenceWavPath"/>.
    /// </summary>
    /// <param name="bundleDirectory">Folder holding the five .onnx graphs and the two tokeniser JSONs.</param>
    /// <param name="referenceWavPath">A few seconds of the voice to speak in. Any rate; resampled to 24 kHz mono.</param>
    /// <param name="seed">Fixed by default so a given sentence sounds the same twice.</param>
    public static PocketTtsEngine Create(string bundleDirectory, string referenceWavPath, int seed = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceWavPath);

        // int8 where it exists: lm_main is 302 MB in float and 76 MB quantised,
        // and the float build does not fit a mid-range phone's budget alongside
        // everything else CircleAI already holds open.
        var textConditioner = Open(bundleDirectory, "text_conditioner.onnx");
        var encoder = Open(bundleDirectory, "encoder.onnx");
        var lmMain = Open(bundleDirectory, "lm_main.int8.onnx", "lm_main.onnx");
        var lmFlow = Open(bundleDirectory, "lm_flow.int8.onnx", "lm_flow.onnx");
        var decoder = Open(bundleDirectory, "decoder.int8.onnx", "decoder.onnx");

        var tokeniser = SentencePieceUnigram.Load(
            Path.Combine(bundleDirectory, "vocab.json"),
            Path.Combine(bundleDirectory, "token_scores.json"));

        var audio = WavIo.ReadMono24k(referenceWavPath);
        var audioTensor = new DenseTensor<float>(audio, [1, 1, audio.Length]);
        using var encoded = encoder.Run([NamedOnnxValue.CreateFromTensor("audio", audioTensor)]);
        var latents = encoded.First(v => v.Name == "latents").AsTensor<float>();
        var frames = latents.Dimensions[^2];

        return new PocketTtsEngine(textConditioner, encoder, lmMain, lmFlow, decoder,
                                   tokeniser, latents.ToArray(), frames, seed);
    }

    private static InferenceSession Open(string dir, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return new InferenceSession(path, SessionOptions());
        }
        throw new FileNotFoundException(
            $"Pocket-TTS bundle at '{dir}' has none of: {string.Join(", ", candidates)}.");
    }

    /// <summary>
    /// Thread and optimisation settings, set explicitly rather than defaulted.
    /// </summary>
    /// <remarks>
    /// THE DEFAULT IS NOT NECESSARILY EVERY CORE. ONNX Runtime picks its own
    /// intra-op thread count, and on a phone that decision is made without
    /// knowing this is an autoregressive loop that will run the same graph
    /// hundreds of times in a row. Pocket-TTS measured 8,066 ms per second of
    /// speech on the P30 Lite before this — eight times slower than real time,
    /// on a model whose entire selling point is running in real time on a CPU.
    ///
    /// FOUR, NOT EIGHT. The Kirin 710 is big.LITTLE: four Cortex-A73 and four
    /// A53. Spreading a latency-bound graph across all eight puts a quarter of
    /// each step on the slow cores and makes every step wait for them, so the
    /// count is the number of BIG cores, not the number of cores.
    /// </remarks>
    private static SessionOptions SessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
        InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
    };

    /// <inheritdoc />
    public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, SampleRate, 1, 16));

        // Off the calling thread: this is an autoregressive loop, tens of
        // milliseconds per frame, and callers on a UI thread would freeze.
        return Task.Run(() => Generate(text, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Frame-at-a-time streaming is the whole point of a streaming decoder,
        // but the honest version of it needs the loop restructured around an
        // async channel. Until then this yields the finished buffer once rather
        // than pretending to stream.
        var result = await SynthesiseAsync(text, cancellationToken).ConfigureAwait(false);
        if (!result.AudioData.IsEmpty) yield return result.AudioData;
    }

    private TtsSynthesisResult Generate(string text, CancellationToken ct)
    {
        var (prepared, framesAfterEos) = PrepareText(text);
        var ids = _tokeniser.Encode(prepared);
        if (ids.Count == 0)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, SampleRate, 1, 16);

        var lmState = InitialState(_lmMain, "sequence", "text_embeddings");
        var decState = InitialState(_decoder, "latent");
        try
        {
            // Allocated ONCE and reused for every frame — see StateSlot for why
            // per-step allocation is what made this eight times slower than real
            // time on the phone.
            var conditioning = new float[PrefixDim];
            var noise = new float[LatentDim];
            var flow = new float[LatentDim];
            var sequence = new float[LatentDim];
            var empty = Array.Empty<float>();

            // 1. The voice. Sequence length zero: only the state matters here.
            RunLanguageModel(lmState, empty, _voiceLatents, _voiceFrames, conditioning, out _);

            // 2. The text, through the same prefix input.
            var idArray = ids.Select(i => (long)i).ToArray();
            var idTensor = new DenseTensor<long>(idArray, [1, idArray.Length]);
            using var conditioned = _textConditioner.Run(
                [NamedOnnxValue.CreateFromTensor("token_ids", idTensor)]);
            var textEmbeddings = conditioned.First(v => v.Name == "embeddings").AsTensor<float>().ToArray();
            RunLanguageModel(lmState, empty, textEmbeddings, ids.Count, conditioning, out _);

            // 3. Autoregressive generation. NaN is BOS.
            Array.Fill(sequence, float.NaN);

            var audio = new List<float>(SampleRate * 8);
            var maxFrames = (int)(ids.Count / 3.0 * 12.5) + 80;
            int? eosFrame = null;

            for (var step = 0; step < maxFrames; step++)
            {
                ct.ThrowIfCancellationRequested();

                RunLanguageModel(lmState, sequence, empty, 0, conditioning, out var eosLogit);

                if (eosLogit > EosThreshold && eosFrame is null) eosFrame = step;
                if (eosFrame is not null && step >= eosFrame + framesAfterEos) break;

                DrawNoise(noise);
                RunFlow(conditioning, noise, flow);
                for (var i = 0; i < LatentDim; i++) sequence[i] = noise[i] + flow[i];

                RunDecoder(decState, sequence, audio);
            }

            return new TtsSynthesisResult(WavIo.ToPcm16(audio), SampleRate, 1, 16);
        }
        finally
        {
            foreach (var slot in lmState.Values) slot.Dispose();
            foreach (var slot in decState.Values) slot.Dispose();
        }
    }

    /// <summary>
    /// One <c>lm_main</c> step. <paramref name="sequence"/> is either empty
    /// (priming) or exactly one latent; the prefix is either the voice, the
    /// text, or empty.
    /// </summary>
    /// <summary>Output names, cached: ORT takes them per call and they never change.</summary>
    private string[]? _lmOutputs;
    private string[]? _decoderOutputs;
    private readonly RunOptions _runOptions = new();

    /// <summary>
    /// One <c>lm_main</c> step. <paramref name="sequence"/> is either empty
    /// (priming) or exactly one latent; the prefix is either the voice, the
    /// text, or empty.
    /// </summary>
    private void RunLanguageModel(
        Dictionary<string, StateSlot> state, float[] sequence, float[] prefix, int prefixFrames,
        float[] conditioning, out float eosLogit)
    {
        _lmOutputs ??= _lmMain.OutputNames.ToArray();
        var seqFrames = sequence.Length / LatentDim;

        using var sequenceValue = OrtValue.CreateTensorValueFromMemory(
            sequence, [1, seqFrames, LatentDim]);
        using var prefixValue = OrtValue.CreateTensorValueFromMemory(
            prefix, [1, prefixFrames, PrefixDim]);

        var inputs = new Dictionary<string, OrtValue>(state.Count + 2, StringComparer.Ordinal)
        {
            ["sequence"] = sequenceValue,
            ["text_embeddings"] = prefixValue,
        };
        foreach (var (name, slot) in state) inputs[name] = slot.Value;

        using var results = _lmMain.Run(_runOptions, inputs, _lmOutputs);
        for (var i = 0; i < _lmOutputs.Length; i++)
        {
            var name = _lmOutputs[i];
            if (name == "conditioning")
                results[i].GetTensorDataAsSpan<float>().CopyTo(conditioning);
            else if (name == "eos_logit")
                _lastEos = results[i].GetTensorDataAsSpan<float>()[0];
            else if (name.StartsWith("out_state_", StringComparison.Ordinal))
                state[string.Concat("state_", name.AsSpan("out_state_".Length))].Absorb(results[i]);
        }
        eosLogit = _lastEos;
    }

    private float _lastEos;

    private void RunFlow(float[] conditioning, float[] noise, float[] into)
    {
        // ONE step: s = 0, t = 1, and the result is noise + direction.
        using var c = OrtValue.CreateTensorValueFromMemory(conditioning, [1, PrefixDim]);
        using var sv = OrtValue.CreateTensorValueFromMemory(_zero, [1, 1]);
        using var tv = OrtValue.CreateTensorValueFromMemory(_one, [1, 1]);
        using var x = OrtValue.CreateTensorValueFromMemory(noise, [1, LatentDim]);

        using var results = _lmFlow.Run(_runOptions,
            new Dictionary<string, OrtValue>(StringComparer.Ordinal)
            { ["c"] = c, ["s"] = sv, ["t"] = tv, ["x"] = x },
            ["flow_dir"]);
        results[0].GetTensorDataAsSpan<float>().CopyTo(into);
    }

    private readonly float[] _zero = [0f];
    private readonly float[] _one = [1f];

    private void RunDecoder(Dictionary<string, StateSlot> state, float[] latent, List<float> audio)
    {
        _decoderOutputs ??= _decoder.OutputNames.ToArray();

        using var latentValue = OrtValue.CreateTensorValueFromMemory(latent, [1, 1, LatentDim]);
        var inputs = new Dictionary<string, OrtValue>(state.Count + 1, StringComparer.Ordinal)
        {
            ["latent"] = latentValue,
        };
        foreach (var (name, slot) in state) inputs[name] = slot.Value;

        using var results = _decoder.Run(_runOptions, inputs, _decoderOutputs);
        for (var i = 0; i < _decoderOutputs.Length; i++)
        {
            var name = _decoderOutputs[i];
            if (name == "audio_frame")
            {
                foreach (var sample in results[i].GetTensorDataAsSpan<float>()) audio.Add(sample);
            }
            else if (name.StartsWith("out_state_", StringComparison.Ordinal))
            {
                state[string.Concat("state_", name.AsSpan("out_state_".Length))].Absorb(results[i]);
            }
        }
    }

    /// <summary>Zero/false state of exactly the shape each graph declares.</summary>
    private static Dictionary<string, StateSlot> InitialState(InferenceSession session, params string[] skip)
    {
        var state = new Dictionary<string, StateSlot>(StringComparer.Ordinal);
        foreach (var (name, meta) in session.InputMetadata)
        {
            if (skip.Contains(name, StringComparer.Ordinal)) continue;
            state[name] = StateSlot.Zero(meta);
        }
        return state;
    }

    /// <summary>
    /// One state slot, held in a REUSED buffer so a step costs no allocation.
    /// </summary>
    /// <remarks>
    /// THIS IS THE PERFORMANCE OF THE WHOLE ENGINE. The language model carries
    /// six [2,1,1000,16,64] caches — 49 MB — and the decoder four more at 16 MB,
    /// and the graphs hand ALL of it back on EVERY frame. Marshalling that
    /// through managed tensors copied it three times per direction and allocated
    /// 65 MB per frame; across a two-second sentence that is gigabytes of memcpy
    /// and enough garbage to keep a phone's collector busy for the entire
    /// utterance. Measured on the P30 Lite: 8,066 ms per second of speech.
    ///
    /// So the buffer is allocated ONCE per slot and the output is copied
    /// straight into it — one copy, no allocation, nothing for the GC. Threading
    /// was tried first and made it WORSE (8,736 ms/s on four cores), which is
    /// the tell that this was never compute-bound.
    ///
    /// THE SHAPES ARE NOT ALL FIXED. Several decoder slots grow for the first
    /// few frames before settling, so a changed shape reallocates rather than
    /// truncating — silently keeping a stale buffer would desynchronise that
    /// layer from the position it indexes.
    ///
    /// AND THE TYPES ARE NOT ALL FLOAT: the LM mixes in int64 step counters and
    /// the decoder bool warm-up flags. Reading a counter as float would reset it
    /// to zero and quietly detach the KV cache from the position it addresses.
    /// </remarks>
    private sealed class StateSlot : IDisposable
    {
        private Array _buffer;
        private long[] _shape;
        private OrtValue _value;
        private readonly TensorElementType _type;

        private StateSlot(TensorElementType type, Array buffer, long[] shape)
        {
            _type = type;
            _buffer = buffer;
            _shape = shape;
            _value = Wrap(type, buffer, shape);
        }

        public OrtValue Value => _value;

        public static StateSlot Zero(NodeMetadata meta)
        {
            // A negative (symbolic) dimension in a state slot means "nothing
            // cached yet", which is length zero — not an error and not a 1.
            var shape = meta.Dimensions.Select(d => (long)(d < 0 ? 0 : d)).ToArray();
            var count = (int)shape.Aggregate(1L, (a, b) => a * b);
            return new StateSlot(meta.ElementDataType, Allocate(meta.ElementDataType, count), shape);
        }

        /// <summary>Copy a produced state into this slot's buffer for the next step.</summary>
        public void Absorb(OrtValue produced)
        {
            var info = produced.GetTensorTypeAndShape();
            var shape = info.Shape;
            var count = (int)shape.Aggregate(1L, (a, b) => a * b);

            if (!shape.AsSpan().SequenceEqual(_shape))
            {
                _value.Dispose();
                _buffer = Allocate(_type, count);
                _shape = shape.ToArray();
                _value = Wrap(_type, _buffer, _shape);
            }

            switch (_type)
            {
                case TensorElementType.Float:
                    produced.GetTensorDataAsSpan<float>().CopyTo(((float[])_buffer).AsSpan(0, count));
                    break;
                case TensorElementType.Int64:
                    produced.GetTensorDataAsSpan<long>().CopyTo(((long[])_buffer).AsSpan(0, count));
                    break;
                case TensorElementType.Bool:
                    produced.GetTensorDataAsSpan<bool>().CopyTo(((bool[])_buffer).AsSpan(0, count));
                    break;
                default:
                    throw new NotSupportedException($"Pocket-TTS state type {_type} is not handled.");
            }
        }

        private static Array Allocate(TensorElementType type, int count) => type switch
        {
            TensorElementType.Float => new float[count],
            TensorElementType.Int64 => new long[count],
            TensorElementType.Bool  => new bool[count],
            _ => throw new NotSupportedException($"Pocket-TTS state type {type} is not handled."),
        };

        private static OrtValue Wrap(TensorElementType type, Array buffer, long[] shape) => type switch
        {
            TensorElementType.Float => OrtValue.CreateTensorValueFromMemory((float[])buffer, shape),
            TensorElementType.Int64 => OrtValue.CreateTensorValueFromMemory((long[])buffer, shape),
            TensorElementType.Bool  => OrtValue.CreateTensorValueFromMemory((bool[])buffer, shape),
            _ => throw new NotSupportedException($"Pocket-TTS state type {type} is not handled."),
        };

        public void Dispose() => _value.Dispose();
    }

    private void DrawNoise(float[] into)
    {
        var std = MathF.Sqrt(Temperature);
        for (var i = 0; i < into.Length; i++)
        {
            // Box–Muller: the reference draws from a truncated normal, and the
            // clamp is what keeps a rare tail sample from producing a burst of
            // noise in the middle of a word.
            double u1, u2;
            do { u1 = _rng.NextDouble(); } while (u1 <= double.Epsilon);
            u2 = _rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            into[i] = Math.Clamp((float)z * std, -NoiseClamp, NoiseClamp);
        }
    }

    /// <summary>
    /// The reference's own text preparation, and the frames-after-EOS it implies.
    /// </summary>
    /// <remarks>
    /// Short inputs need a longer tail: the decoder is streaming and the last
    /// word is still coming out when EOS fires, and there is proportionally
    /// more of it left in a four-word sentence than a forty-word one.
    /// </remarks>
    private static (string Text, int FramesAfterEos) PrepareText(string text)
    {
        var t = text.Trim().Replace("\n", " ").Replace("\r", " ").Replace("  ", " ");
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var framesAfterEos = (words <= 4 ? 3 : 1) + 2;

        if (!char.IsUpper(t[0])) t = char.ToUpperInvariant(t[0]) + t[1..];
        if (char.IsLetterOrDigit(t[^1])) t += ".";
        return (t, framesAfterEos);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textConditioner.Dispose();
        _encoder.Dispose();
        _lmMain.Dispose();
        _lmFlow.Dispose();
        _decoder.Dispose();
    }
}
