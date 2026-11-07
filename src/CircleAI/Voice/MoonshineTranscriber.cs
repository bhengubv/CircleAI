// MoonshineTranscriber.cs
//
// EARS THAT WORK WHILE THE PERSON IS STILL TALKING.
//
// WHY THIS EXISTS. Whisper writes nothing until the clip is closed, so everything it does is added
// to the wait after the last word, and an assistant has nothing to start on until then. Moonshine's
// streaming models take the audio as it arrives: by the time somebody stops, the words before the
// last few are already written down, and the brain can be reading them.
//
// ON THE RUNTIME THE SERVICE ALREADY LOADS. Moonshine publishes a native library, an Android package
// and a C API. None of that is here: its graphs are ordinary ONNX Runtime files, so this is the
// driver alone, in C#, read from core/moonshine-streaming-model.cpp in moonshine-ai/moonshine. The
// models and that code are MIT, every streaming model at every size.
//
// THE PIPELINE, per stretch of audio:
//   frontend  80 ms of samples to 4 frames of features, carrying its own buffers between calls
//   encoder   a sliding window over the features; a frame is settled once 16 more follow it
//   adapter   settled frames to the decoder's memory, by position
//   cross_kv  that memory to the keys and values every decoder step attends to
//   decoder   tokens, one at a time, carrying its own keys and values
//
// WHAT WAS WRITTEN LAST TIME IS CHECKED, NOT WRITTEN AGAIN. Every update decodes from the start,
// because the memory it attends to has grown. So the previous words go through the decoder in ONE
// pass, it keeps the ones it still agrees with, and only what is new costs a step each. That is what
// makes the last update after the last word short.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="IVoiceTranscriber"/> over a Moonshine streaming model. Consumes PCM 16-bit 16 kHz mono,
/// as the interface specifies; <see cref="Begin"/> is the way in for audio that is still arriving.
/// </summary>
public sealed class MoonshineTranscriber : IVoiceTranscriber
{
    /// <summary>
    /// The frontend takes samples in multiples of this: 80 ms.
    /// </summary>
    /// <remarks>
    /// WHOLE CHUNKS ONLY, AND THAT IS THE GRAPH'S RULE, NOT A TIDINESS ONE. English ships graphs exported
    /// before 2026-08-23, which drop a remainder that is not a multiple of 80 samples and still report it
    /// as buffered, so the next chunk reads silence where the audio was. <see cref="Hearing"/> holds the
    /// odd samples back until a chunk is full.
    /// </remarks>
    private const int ChunkSamples = 1280;

    /// <summary>The decoder's memory runs at 50 frames a second.</summary>
    private const float SecondsPerFrame = 0.020f;

    /// <summary>
    /// The most tokens a second of speech is allowed to come to; past it the decoder is repeating itself.
    /// </summary>
    private const float MostTokensPerSecond = 6.5f;

    /// <summary>Frames of left context each encoder layer attends to.</summary>
    private const int LeftContextPerLayer = 16;

    /// <summary>How many frames to the left of the new ones the encoder is shown; null is the whole reach of its layers.</summary>
    public int? LeftContext { get; init; }

    /// <summary>How often a stream gets a new partial, in bytes of audio: half a second.</summary>
    private const int BytesPerUpdate = 16_000;

    /// <summary>The word-boundary mark in these vocabularies, U+2581.</summary>
    private const string WordMark = "▁";

    private static readonly string[] FrontendOutputs =
        ["features", "sample_buffer_out", "sample_len_out", "conv1_buffer_out", "conv2_buffer_out", "frame_count_out"];
    private static readonly string[] EncoderInputs = ["features"];
    private static readonly string[] EncoderOutputs = ["encoded"];
    private static readonly string[] AdapterInputs = ["encoded", "pos_offset"];
    private static readonly string[] AdapterOutputs = ["memory"];
    private static readonly string[] CrossInputs = ["memory"];
    private static readonly string[] CrossOutputs = ["k_cross", "v_cross"];
    private static readonly string[] DecoderInputs = ["token", "k_self", "v_self", "out_k_cross", "out_v_cross"];
    private static readonly string[] DecoderOutputs = ["logits", "out_k_self", "out_v_self"];

    private readonly string _language;
    private readonly ModelShape _shape;
    private readonly byte[][] _vocabulary;
    private readonly SessionOptions _options;
    private readonly RunOptions _run = new();
    private readonly InferenceSession _frontend;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _adapter;
    private readonly InferenceSession _crossKv;
    private readonly InferenceSession _decoder;

    /// <summary>
    /// The frontend's weights, handed to it on every call.
    /// </summary>
    /// <remarks>
    /// THEY SHIP AS A GRAPH OF THEIR OWN so they can stay int8 on disk; running it once dequantises them,
    /// and they go back in as inputs. Held open and pinned for as long as the model is, because a copy per
    /// chunk would be twelve copies of up to 30 MB a second.
    /// </remarks>
    private readonly OrtValue[] _weights;
    private readonly string[] _frontendInputs;

    /// <summary>Stands in for the decoder's keys and values before its first step.</summary>
    private readonly OrtValue _nothingYet;

    /// <param name="directory">The folder holding the model's files.</param>
    /// <param name="language">The language this model hears; it does not detect one.</param>
    /// <param name="threads">Threads for one operator; null is the factory's rule, half the cores.</param>
    /// <param name="tune">Last word on the session options, for a measurement.</param>
    public MoonshineTranscriber(string directory, string language = "en", int? threads = null,
                                Action<SessionOptions>? tune = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _language = language;
        _shape = ModelShape.Read(Path.Combine(directory, "streaming_config.json"));
        _vocabulary = ReadVocabulary(Path.Combine(directory, "tokenizer.bin"));

        _options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            InterOpNumThreads = 1,
            IntraOpNumThreads = threads ?? Math.Max(1, CircleAI.Core.DeviceContext.AllCores / 2),
        };
        tune?.Invoke(_options);

        _frontend = Open("frontend.model.ort");
        _encoder = Open("encoder.ort");
        _adapter = Open("adapter.ort");
        _crossKv = Open("cross_kv.ort");
        _decoder = Open("decoder_kv.ort");

        using (var weights = Open("frontend.weights.ort"))
        {
            var names = weights.OutputNames.ToArray();
            using var values = weights.Run(_run, [], [], names);

            _weights = new OrtValue[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                _weights[i] = OrtValue.CreateTensorValueFromMemory(
                    values[i].GetTensorDataAsSpan<float>().ToArray(), values[i].GetTensorTypeAndShape().Shape);
            }

            _frontendInputs =
            [
                "audio_chunk", "sample_buffer", "sample_len", "conv1_buffer", "conv2_buffer", "frame_count", .. names,
            ];
        }

        _nothingYet = OrtValue.CreateTensorValueFromMemory(
            new float[1], [_shape.Depth, 1, _shape.Heads, 0, _shape.HeadDim]);

        InferenceSession Open(string file) => OnnxSessionFactory.Create(Path.Combine(directory, file), _options);
    }

    /// <summary>Starts listening to one person saying one thing.</summary>
    public Hearing Begin() => new(this);

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null) =>
        Task.Run(() =>
        {
            using var hearing = Begin();
            hearing.Hear(pcmAudio.Span);
            return new TranscriptionResult(hearing.Finish(), 1f, _language);
        }, ct);

    /// <inheritdoc />
    public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var hearing = Begin();
        var sinceUpdate = 0;

        await foreach (var chunk in audioChunks.WithCancellation(ct).ConfigureAwait(false))
        {
            hearing.Hear(chunk.Span);
            sinceUpdate += chunk.Length;
            if (sinceUpdate < BytesPerUpdate) continue;

            sinceUpdate = 0;
            yield return new PartialTranscription(await Task.Run(hearing.Update, ct).ConfigureAwait(false), false, 1f);
        }

        yield return new PartialTranscription(await Task.Run(hearing.Finish, ct).ConfigureAwait(false), true, 1f);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _frontend.Dispose();
        _encoder.Dispose();
        _adapter.Dispose();
        _crossKv.Dispose();
        _decoder.Dispose();
        foreach (var weight in _weights) weight.Dispose();
        _nothingYet.Dispose();
        _options.Dispose();
        _run.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The words <paramref name="tokens"/> spell.</summary>
    internal string TextOf(IEnumerable<int> tokens) => TextOf(tokens, _vocabulary);

    /// <inheritdoc cref="TextOf(IEnumerable{int})"/>
    internal static string TextOf(IEnumerable<int> tokens, byte[][] vocabulary)
    {
        var bytes = new List<byte>();
        foreach (var token in tokens)
        {
            var piece = vocabulary[token];

            // A control token, not text.
            if (piece.Length > 2 && piece[0] == '<' && piece[^1] == '>') continue;
            bytes.AddRange(piece);
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Replace(WordMark, " ", StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Reads a <c>tokenizer.bin</c>: the bytes of every token in id order, each behind a one or two byte
    /// length, optionally after a header.
    /// </summary>
    internal static byte[][] ReadVocabulary(string file) => ParseVocabulary(File.ReadAllBytes(file));

    /// <inheritdoc cref="ReadVocabulary"/>
    internal static byte[][] ParseVocabulary(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> magic = [0xFF, 0xFF, (byte)'B', (byte)'T', (byte)'O', (byte)'K'];
        const int headerSize = 16;

        var offset = 0;
        var count = int.MaxValue;
        if (data.Length >= headerSize && data.StartsWith(magic))
        {
            count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
            offset = headerSize;
        }

        var tokens = new List<byte[]>();
        while (offset < data.Length && tokens.Count < count)
        {
            int first = data[offset++];
            if (first == 0)
            {
                tokens.Add([]);
                continue;
            }

            var length = first < 128 ? first : data[offset++] * 128 + first - 128;
            tokens.Add(data.Slice(offset, length).ToArray());
            offset += length;
        }

        return [.. tokens];
    }

    /// <summary>The figures a model's <c>streaming_config.json</c> gives.</summary>
    private sealed record ModelShape(
        int EncoderDim, int DecoderDim, int Depth, int Heads, int HeadDim, int VocabularySize,
        int Bos, int Eos, int Lookahead, int FrontendDim, int Conv1, int MostTokens)
    {
        public static ModelShape Read(string file)
        {
            using var config = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = config.RootElement;

            int Value(string name) => root.GetProperty(name).GetInt32();

            return new ModelShape(
                Value("encoder_dim"), Value("decoder_dim"), Value("depth"), Value("nheads"), Value("head_dim"),
                Value("vocab_size"), Value("bos_id"), Value("eos_id"), Value("total_lookahead"),
                Value("d_model_frontend"), Value("c1"),
                root.TryGetProperty("max_seq_len", out var most) ? most.GetInt32() : 448);
        }
    }

    /// <summary>One person saying one thing: audio in as it arrives, what they have said so far out.</summary>
    /// <remarks>
    /// ONE CALLER AT A TIME. Nothing here is shared with another <see cref="Hearing"/>, so two can run on
    /// one model, but a single one is a sequence of steps and takes them in order.
    /// </remarks>
    public sealed class Hearing : IHearing
    {
        private readonly MoonshineTranscriber _ears;
        private readonly ModelShape _shape;

        private float[] _samples = new float[ChunkSamples * 8];
        private int _sampleCount;

        private readonly float[] _sampleBuffer = new float[79];
        private readonly long[] _sampleLength = new long[1];
        private readonly float[] _conv1;
        private readonly float[] _conv2;
        private readonly long[] _frameCount = new long[1];

        private float[] _features = new float[1];
        private int _featureCount;
        private int _settled;

        private readonly long[] _position = new long[1];
        private float[] _memory = new float[1];
        private int _memoryLength;

        private IDisposableReadOnlyCollection<OrtValue>? _cross;
        private bool _crossIsCurrent;

        private IDisposableReadOnlyCollection<OrtValue>? _decoded;
        private OrtValue _keys;
        private OrtValue _values;

        private List<int> _written = [];
        private string _text = string.Empty;

        private readonly Stopwatch _frontendTime = new();
        private readonly Stopwatch _encoderTime = new();
        private readonly Stopwatch _crossTime = new();
        private readonly Stopwatch _decoderTime = new();
        private int _encoderFrames;
        private int _decoderPasses;

        internal Hearing(MoonshineTranscriber ears)
        {
            _ears = ears;
            _shape = ears._shape;
            _conv1 = new float[_shape.FrontendDim * 4];
            _conv2 = new float[_shape.Conv1 * 4];
            _keys = _values = ears._nothingYet;
        }

        /// <summary>The words so far.</summary>
        public string Text => _text;

        /// <summary>Audio heard so far.</summary>
        public TimeSpan Heard => TimeSpan.FromSeconds(_featureCount * (double)SecondsPerFrame);

        /// <summary>Where the time has gone so far, for the log.</summary>
        public string Spent =>
            $"frontend {_frontendTime.ElapsedMilliseconds} ms, "
            + $"encoder {_encoderTime.ElapsedMilliseconds} ms over {_encoderFrames} frames, "
            + $"cross {_crossTime.ElapsedMilliseconds} ms, "
            + $"decoder {_decoderTime.ElapsedMilliseconds} ms in {_decoderPasses} passes";

        /// <summary>Takes in audio, PCM 16-bit 16 kHz mono, in pieces of any size.</summary>
        public void Hear(ReadOnlySpan<byte> pcm16)
        {
            Fit(ref _samples, _sampleCount + pcm16.Length / 2);
            for (var i = 0; i + 1 < pcm16.Length; i += 2)
                _samples[_sampleCount++] = BinaryPrimitives.ReadInt16LittleEndian(pcm16[i..]) / 32768f;
        }

        /// <summary>Writes down what has settled, and returns the words so far.</summary>
        public string Update()
        {
            Frontend();
            if (Encode(final: false) > 0) Decode();
            return _text;
        }

        /// <summary>
        /// What the words would be if the audio ended here. The last frames go in without the 320 ms a
        /// frame is otherwise given, and come out again: they are read properly once what follows is heard.
        /// </summary>
        public string Peek()
        {
            Frontend();
            var (settled, memory, position) = (_settled, _memoryLength, _position[0]);
            if (Encode(final: true) > 0) Decode();
            (_settled, _memoryLength, _position[0]) = (settled, memory, position);
            _crossIsCurrent = false;
            return _text;
        }

        /// <summary>Writes down everything, the last frames included, and returns the words.</summary>
        public string Finish()
        {
            Frontend();
            if (Encode(final: true) > 0) Decode();
            return _text;
        }

        /// <summary>Everything waiting, in one call: its weights go in with every call, so calls are what cost.</summary>
        private void Frontend()
        {
            var whole = _sampleCount / ChunkSamples * ChunkSamples;
            if (whole == 0) return;

            _frontendTime.Start();

            var inputs = new OrtValue[6 + _ears._weights.Length];
            try
            {
                inputs[0] = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, _samples.AsMemory(0, whole), [1, whole]);
                inputs[1] = OrtValue.CreateTensorValueFromMemory(_sampleBuffer, [1, _sampleBuffer.Length]);
                inputs[2] = OrtValue.CreateTensorValueFromMemory(_sampleLength, [1]);
                inputs[3] = OrtValue.CreateTensorValueFromMemory(_conv1, [1, _shape.FrontendDim, 4]);
                inputs[4] = OrtValue.CreateTensorValueFromMemory(_conv2, [1, _shape.Conv1, 4]);
                inputs[5] = OrtValue.CreateTensorValueFromMemory(_frameCount, [1]);
                _ears._weights.CopyTo(inputs, 6);

                using var outputs = _ears._frontend.Run(_ears._run, _ears._frontendInputs, inputs, FrontendOutputs);

                var shape = outputs[0].GetTensorTypeAndShape().Shape;
                var frames = (int)shape[1];
                if (frames > 0)
                {
                    var dim = (int)shape[2];
                    Fit(ref _features, (_featureCount + frames) * dim);
                    outputs[0].GetTensorDataAsSpan<float>().CopyTo(_features.AsSpan(_featureCount * dim));
                    _featureCount += frames;
                }

                outputs[1].GetTensorDataAsSpan<float>().CopyTo(_sampleBuffer);
                _sampleLength[0] = outputs[2].GetTensorDataAsSpan<long>()[0];
                outputs[3].GetTensorDataAsSpan<float>().CopyTo(_conv1);
                outputs[4].GetTensorDataAsSpan<float>().CopyTo(_conv2);
                _frameCount[0] = outputs[5].GetTensorDataAsSpan<long>()[0];

                Array.Copy(_samples, whole, _samples, 0, _sampleCount - whole);
                _sampleCount -= whole;
            }
            finally
            {
                for (var i = 0; i < 6; i++) inputs[i]?.Dispose();
                _frontendTime.Stop();
            }
        }

        /// <summary>Moves what has settled into the decoder's memory; returns how many frames.</summary>
        private int Encode(bool final)
        {
            if (_featureCount == 0) return 0;

            var settled = final ? _featureCount : Math.Max(0, _featureCount - _shape.Lookahead);
            var fresh = settled - _settled;
            if (fresh <= 0) return 0;

            _encoderTime.Start();
            try
            {
                var dim = _shape.EncoderDim;
                var start = Math.Max(0, _settled - (_ears.LeftContext ?? LeftContextPerLayer * _shape.Depth));
                var window = _featureCount - start;
                _encoderFrames += window;

                using var features = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, _features.AsMemory(start * dim, window * dim), [1, window, dim]);
                using var encoded = _ears._encoder.Run(_ears._run, EncoderInputs, [features], EncoderOutputs);

                var slice = encoded[0].GetTensorDataAsSpan<float>().Slice((_settled - start) * dim, fresh * dim).ToArray();

                using var sliced = OrtValue.CreateTensorValueFromMemory(slice, [1, fresh, dim]);
                using var position = OrtValue.CreateTensorValueFromMemory(_position, [1]);
                using var adapted = _ears._adapter.Run(_ears._run, AdapterInputs, [sliced, position], AdapterOutputs);

                Fit(ref _memory, (_memoryLength + fresh) * _shape.DecoderDim);
                adapted[0].GetTensorDataAsSpan<float>().CopyTo(_memory.AsSpan(_memoryLength * _shape.DecoderDim));

                _memoryLength += fresh;
                _position[0] += fresh;
                _settled = settled;
                _crossIsCurrent = false;
                return fresh;
            }
            finally { _encoderTime.Stop(); }
        }

        private void Cross()
        {
            if (_crossIsCurrent) return;

            _crossTime.Start();
            try
            {
                using var memory = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, _memory.AsMemory(0, _memoryLength * _shape.DecoderDim),
                    [1, _memoryLength, _shape.DecoderDim]);

                var outputs = _ears._crossKv.Run(_ears._run, CrossInputs, [memory], CrossOutputs);
                _cross?.Dispose();
                _cross = outputs;
                _crossIsCurrent = true;
            }
            finally { _crossTime.Stop(); }
        }

        /// <summary>One pass through the decoder; the logits for every token given.</summary>
        private ReadOnlySpan<float> Step(long[] tokens)
        {
            _decoderPasses++;
            using var token = OrtValue.CreateTensorValueFromMemory(tokens, [1, tokens.Length]);

            var outputs = _ears._decoder.Run(
                _ears._run, DecoderInputs, [token, _keys, _values, _cross![0], _cross[1]], DecoderOutputs);

            // AFTER THE RUN: the keys and values it just read belong to the previous outputs.
            _decoded?.Dispose();
            _decoded = outputs;
            _keys = outputs[1];
            _values = outputs[2];
            return outputs[0].GetTensorDataAsSpan<float>();
        }

        private void Forget()
        {
            _keys = _values = _ears._nothingYet;
        }

        private static int Best(ReadOnlySpan<float> logits)
        {
            var best = 0;
            var top = logits[0];
            for (var i = 1; i < logits.Length; i++)
            {
                if (logits[i] <= top) continue;
                top = logits[i];
                best = i;
            }
            return best;
        }

        private void Decode()
        {
            if (_memoryLength == 0) return;

            Cross();

            _decoderTime.Start();
            try
            {
                var vocabulary = _shape.VocabularySize;
                var most = Math.Min((int)Math.Ceiling(_memoryLength * SecondsPerFrame * MostTokensPerSecond), _shape.MostTokens);
                var draft = _written;
                var written = new List<int>(draft.Count + 8);

                Forget();

                int next;
                if (draft.Count == 0)
                {
                    next = Best(Step([_shape.Bos]));
                }
                else
                {
                    var given = new long[draft.Count + 1];
                    given[0] = _shape.Bos;
                    for (var i = 0; i < draft.Count; i++) given[i + 1] = draft[i];

                    var logits = Step(given);

                    var agreed = 0;
                    while (agreed < draft.Count
                           && Best(logits.Slice(agreed * vocabulary, vocabulary)) == draft[agreed]) agreed++;

                    written.AddRange(draft.Take(agreed));

                    if (agreed == draft.Count)
                    {
                        next = Best(logits.Slice(agreed * vocabulary, vocabulary));
                    }
                    else
                    {
                        // ITS KEYS AND VALUES NOW HOLD WORDS IT DOES NOT AGREE WITH, so back to where it did.
                        Forget();
                        var kept = new long[agreed + 1];
                        Array.Copy(given, kept, kept.Length);
                        next = Best(Step(kept).Slice(agreed * vocabulary, vocabulary));
                    }
                }

                while (next != _shape.Eos && written.Count < most)
                {
                    written.Add(next);
                    next = Best(Step([next]));
                }

                _written = written;
                _text = Plain(_ears.TextOf(written));
            }
            finally { _decoderTime.Stop(); }
        }

        // THE MODEL READ OLD BOOKS: it writes "to-day" and "to-night", and "What's the date to-day?" is not
        // a question anything downstream was told to expect.
        private static string Plain(string text) => text
            .Replace("to-day", "today", StringComparison.OrdinalIgnoreCase)
            .Replace("to-night", "tonight", StringComparison.OrdinalIgnoreCase)
            .Replace("to-morrow", "tomorrow", StringComparison.OrdinalIgnoreCase);

        private static void Fit(ref float[] buffer, int length)
        {
            if (buffer.Length >= length) return;
            Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cross?.Dispose();
            _decoded?.Dispose();
        }
    }
}
