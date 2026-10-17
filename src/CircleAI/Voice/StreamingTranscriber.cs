// StreamingTranscriber.cs
//
// A STREAMING ZIPFORMER TRANSDUCER, DECODED GREEDILY INSTEAD OF AGAINST A
// KEYWORD TRIE.
//
// WHY THIS EXISTS. The wake phrase must be settable to ANY text, in ANY
// language, at runtime and without training - Africa has roughly two thousand
// languages and South Africa twelve official ones, so one trained model per
// phrase does not scale and never will. That rules out the entire fixed-phrase
// family however good it is at the four phrases it ships.
//
// sherpa-onnx's keyword spotter does take arbitrary text, and it is what the
// product has used. But the whole published keyword shelf is 3 to 3.3 MILLION
// parameters - there is no larger model to move to, that is the shelf - and
// measured on the owner's own voice on 2026-10-12 the English one found his
// phrase four times out of roughly twenty spoken close to the microphone, and
// never once at normal holding distance.
//
// The same project publishes STREAMING ASR at 20M parameters under Apache-2.0
// (stated in the model card, not inferred). Over that same recording it
// transcribed legibly - "hey circle a", "hay bob", "hey alexe hey alexa" - at
// 30x realtime, and finding a phrase in a transcript is open-vocabulary by
// construction. Six times the model, the same ONNX runtime the app already
// loads, and nothing invented.
//
// AND ALMOST NOTHING HERE IS NEW. A keyword spotter and a streaming recogniser
// are the same transducer: encoder with cached state, decoder over the last two
// tokens, joiner, argmax. ZipformerKwsSpotter already does every one of those
// and discovers its state tensors from the graph, so this is that loop with the
// context graph and its beam search taken out. The feature frontend, the chunk
// arithmetic and the state handling are deliberately identical, because they
// were the parts that took longest to get right.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>Turns a stream of audio into text, as it arrives.</summary>
public sealed class StreamingTranscriber : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joiner;

    private readonly KaldiFbank _fbank = new();
    private readonly List<float[]> _features = new();
    private int _featureCursor;

    // Bumped by Reset, so Drain can tell that a wake handler reset the stream
    // from inside the decode it is in the middle of. See Drain.
    private int _generation;

    private readonly string[] _stateInNames;
    private readonly string[] _stateOutNames;

    /// <summary>The carried encoder state, one entry per state input.</summary>
    /// <remarks>
    /// TYPED FROM THE GRAPH, NOT FROM A NAME, AND THAT IS A BUG FIXED RATHER
    /// THAN A STYLE CHOICE. ZipformerKwsSpotter keeps its states as
    /// DenseTensor&lt;float&gt; and special-cases the one int64 it knows about by
    /// the name "processed_lens". The 20M streaming model carries int64 state
    /// under different names, so that code threw
    /// "Tensor element data type discovered: Float metadata expected: Int64"
    /// on the first chunk. Reading the element type off InputMetadata works for
    /// both bundles and for the next one.
    /// </remarks>
    private readonly object[] _states;

    private readonly int _chunkFrames;
    private readonly int _advanceFrames;
    private readonly int _contextSize;

    private readonly Dictionary<int, string> _tokenText = new();
    private readonly List<int> _hyp = new();
    private readonly StringBuilder _said = new();
    private readonly Dictionary<(int, int), DenseTensor<float>> _decCache = new();

    /// <summary>The blank symbol. Zero for every transducer in this family.</summary>
    private const int Blank = 0;

    /// <summary>What has been transcribed since the last <see cref="Reset"/>.</summary>
    public string Text => _said.ToString();

    /// <summary>Raised whenever the transcript grows.</summary>
    /// <remarks>
    /// EVERY REVISION, NOT EVERY UTTERANCE. A wake word has to fire the moment
    /// the phrase has been said, not when the speaker stops - waiting for an
    /// endpoint would add the whole of the question to the latency, which is
    /// the complaint this work started from.
    /// </remarks>
    public event EventHandler<string>? Grew;

    /// <summary>True when the quantized encoder was loaded.</summary>
    /// <inheritdoc cref="ZipformerKwsSpotter.EncoderIsQuantized"/>
    public bool EncoderIsQuantized { get; }

    /// <summary>Loads a sherpa-onnx streaming transducer bundle.</summary>
    /// <param name="directory">Extracted model directory.</param>
    /// <param name="preferQuantized">int8 where the bundle ships both.</param>
    public StreamingTranscriber(string directory, bool preferQuantized = false)
    {
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        var files = Directory.GetFiles(directory, "*.onnx");

        // THE SAME PICKER THE KEYWORD SPOTTER USES, and for the same reason: its
        // predecessor silently fell back to the int8 encoder whenever a bundle
        // shipped only one precision, which is how a guard written against a
        // known-bad configuration spent four months enforcing it.
        var enc = ZipformerKwsSpotter.PickGraph(files, "encoder", preferQuantized);
        var dec = ZipformerKwsSpotter.PickGraph(files, "decoder", preferQuantized);
        var joi = ZipformerKwsSpotter.PickGraph(files, "joiner", preferQuantized);

        EncoderIsQuantized = enc.IsQuantized;
        if (!enc.AsAsked)
        {
            VoiceTrace.Write(
                $"asr: wanted the {(preferQuantized ? "int8" : "float")} encoder and this bundle has only "
                + $"{Path.GetFileName(enc.Path)} - running {(enc.IsQuantized ? "QUANTIZED" : "float")}.");
        }

        _encoder = new InferenceSession(enc.Path, opts);
        _decoder = new InferenceSession(dec.Path, opts);
        _joiner  = new InferenceSession(joi.Path, opts);

        var meta = _encoder.ModelMetadata.CustomMetadataMap;
        _chunkFrames   = MetaInt(meta, "T", 45);
        _advanceFrames = MetaInt(meta, "decode_chunk_len", 32);
        _contextSize   = MetaInt(_decoder.ModelMetadata.CustomMetadataMap, "context_size", 2);

        // Shapes come off the graph, so a newer model cannot silently mismatch.
        var inputs = _encoder.InputMetadata;
        _stateInNames = inputs.Keys.Where(k => k != "x").ToArray();
        _stateOutNames = _stateInNames
            .Select(n => n == "processed_lens" ? "new_processed_lens"
                       : n == "embed_states"   ? "new_embed_states"
                       : "new_" + n)
            .ToArray();

        _states = new object[_stateInNames.Length];
        for (var i = 0; i < _stateInNames.Length; i++)
        {
            var stateMeta = inputs[_stateInNames[i]];
            var dims = stateMeta.Dimensions.Select(d => d < 0 ? 1 : d).ToArray();

            // Zeros are a fresh stream for both kinds: a zeroed cache and a
            // processed-length of nothing processed.
            _states[i] = stateMeta.ElementType == typeof(long)
                ? new DenseTensor<long>(dims)
                : new DenseTensor<float>(dims);
        }

        foreach (var line in File.ReadLines(Path.Combine(directory, "tokens.txt")))
        {
            var sp = line.LastIndexOf(' ');
            if (sp <= 0) continue;
            if (int.TryParse(line[(sp + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                _tokenText[id] = line[..sp];
        }

        StartHypothesis();
    }

    private static int MetaInt(IReadOnlyDictionary<string, string> meta, string key, int fallback) =>
        meta.TryGetValue(key, out var v)
        && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private void StartHypothesis()
    {
        _hyp.Clear();
        for (var i = 0; i < _contextSize; i++) _hyp.Add(Blank);
    }

    /// <summary>Feeds audio. Float samples in [-1, 1] at 16 kHz.</summary>
    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        _fbank.AcceptWaveform(samples);
        Drain();
    }

    /// <summary>Ends the audio, releasing the final partial chunk.</summary>
    /// <remarks>
    /// PADS WITH SILENCE, for the reason the keyword spotter documents: the
    /// encoder only runs on FULL chunks, so without this the last words of a
    /// recording are never decoded at all. It costs nothing live, where a
    /// microphone keeps delivering, and a file-based measurement without it
    /// under-reports - which is the worst kind of wrong a test can be.
    /// </remarks>
    public void Flush()
    {
        Span<float> silence = stackalloc float[1600];
        for (var i = 0; i < 5; i++) _fbank.AcceptWaveform(silence);
        _fbank.Flush();
        Drain();
    }

    /// <summary>Starts a new stream, keeping the loaded models.</summary>
    /// <remarks>
    /// IT USED TO CLEAR THE TRANSCRIPT AND NOTHING ELSE, which is the whole of
    /// why this feature spent a day producing nonsense.
    /// <para>
    /// The body was <c>_said.Clear(); StartHypothesis();</c> - the text and the
    /// decoder's token history. It never touched <c>_states</c>, which is the
    /// ENCODER's cache and the actual stream. So the wake loop's "restart the
    /// stream on every burst", written precisely to stop a cut stream being
    /// decoded as continuous audio, restarted nothing: the encoder carried its
    /// state across every silence exactly as before, and the only thing that
    /// changed was that the garbage it produced was now printed from an empty
    /// string. A method named Reset that resets a third of the object is worse
    /// than no method, because every caller believes it.
    /// </para>
    /// <para>
    /// Measured consequence, P30, 2026-10-12: six minutes of a person talking
    /// decoded to "TABY A B", and half an hour of a quiet room decoded to "YOU
    /// SEE THERE YOU ARE TO YOU WHOM MOVED YOU THE DAYS FOLLOWED UNDER A
    /// SPARECT". Both are a transducer reading a stream that does not exist.
    /// </para>
    /// <para>
    /// Called at the start of every burst, and the moment a wake fires -
    /// otherwise the phrase stays in the transcript and every later frame
    /// matches it again.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        _said.Clear();
        StartHypothesis();

        // THE STREAM ITSELF, which is what this method is named after.
        _fbank.Reset();
        _features.Clear();
        _featureCursor = 0;
        ZeroStates();
        _generation++;
    }

    /// <summary>Returns the encoder cache to "nothing has been heard".</summary>
    /// <remarks>
    /// ZEROED IN PLACE RATHER THAN REALLOCATED. The shapes came off the graph
    /// at construction and cannot change; allocating a fresh set of tensors per
    /// burst would hand the collector several megabytes a minute on a phone
    /// that has 3,87 GB and a full swap.
    /// <para>
    /// Zeros ARE a fresh stream for both kinds here, as the constructor's own
    /// comment records: a zeroed cache, and a processed-length of nothing
    /// processed.
    /// </para>
    /// </remarks>
    private void ZeroStates()
    {
        foreach (var s in _states)
        {
            switch (s)
            {
                case DenseTensor<float> f: f.Buffer.Span.Clear(); break;
                case DenseTensor<long> l: l.Buffer.Span.Clear(); break;
            }
        }
    }

    private void Drain()
    {
        // TAKE EVERY READY FRAME, THEN LET THE EXTRACTOR FORGET IT.
        //
        // TWO BUGS LIVED IN THE LINE THIS REPLACES, and they had one cause: it
        // read `for (f = _features.Count; f < _fbank.FramesReady; f++)`, which
        // treats the number of frames held here as an ABSOLUTE index into the
        // extractor. That is only true while the extractor never forgets
        // anything and never renumbers.
        //
        // THE FIRST BUG. Flush() pads with silence and recounts WITH a mirrored
        // tail, so FramesReady jumps; the next AcceptWaveform recounts WITHOUT
        // it and FramesReady drops back BELOW _features.Count. From that moment
        // the two numbering schemes disagree and every frame fetched afterwards
        // is the wrong frame. Measured on the owner's voice on 2026-10-12:
        // calling Flush at each utterance boundary took the wake count from
        // four to two, and on a phone it would have gone deaf at the first
        // silence - which is a fix that breaks the thing it was fixing.
        //
        // THE SECOND BUG. Nothing was ever dropped, from either side.
        // KaldiFbank.Consume exists for exactly this and its own documentation
        // says so - "so a long session does not grow forever" - and it had ZERO
        // callers in the repository. The wake loop runs for ever by
        // construction: the service process was measured at 904 MB.
        //
        // Both go away by not keeping an absolute index at all. Each pass takes
        // what is ready, hands those frames to Consume, and the extractor keeps
        // only the samples the next window needs.
        var ready = _fbank.FramesReady;
        for (var f = 0; f < ready; f++) _features.Add(_fbank.GetFrame(f));
        if (ready > 0) _fbank.Consume(ready);

        while (_featureCursor + _chunkFrames <= _features.Count)
        {
            // A WAKE HANDLER CAN RESET US FROM INSIDE THIS CALL.
            //
            // RunChunk decodes, decoding raises Grew, and the wake loop's Grew
            // handler calls Reset the moment it matches a phrase - so by the
            // time this returns, _features may have been emptied and the cursor
            // put back to zero underneath this loop. Carrying on then advances
            // a cursor into a list that no longer has anything in it, which
            // threw ArgumentException out of RemoveRange the first time a real
            // wake fired after Reset started clearing the stream.
            //
            // Re-entrancy through an event is not something a caller can be
            // asked to avoid: resetting on a match is exactly right, and it has
            // to be allowed to happen at the moment of the match. So the
            // generation counter notices and this pass simply stops; the next
            // block of audio starts the new stream cleanly.
            var generation = _generation;
            RunChunk(_featureCursor);
            if (_generation != generation) return;

            _featureCursor += _advanceFrames;

            // AND DROP WHAT NO FUTURE CHUNK CAN REACH. Chunks overlap - 45
            // frames wide, advancing 32 - so trimming to the new cursor keeps
            // the overlap and throws away only what is finished with. Without
            // this, _features is the same unbounded list in a different coat.
            var drop = Math.Min(_featureCursor, _features.Count);
            if (drop > 0)
            {
                _features.RemoveRange(0, drop);
                _featureCursor -= drop;
            }
        }
    }

    private void RunChunk(int from)
    {
        var dim = _fbank.Dimension;
        var x = new DenseTensor<float>(new[] { 1, _chunkFrames, dim });
        for (var t = 0; t < _chunkFrames; t++)
        {
            var frame = _features[from + t];
            for (var d = 0; d < dim; d++) x[0, t, d] = frame[d];
        }

        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", x) };
        for (var i = 0; i < _stateInNames.Length; i++)
        {
            feeds.Add(_states[i] is DenseTensor<long> l
                ? NamedOnnxValue.CreateFromTensor(_stateInNames[i], l)
                : NamedOnnxValue.CreateFromTensor(_stateInNames[i], (DenseTensor<float>)_states[i]));
        }

        using var results = _encoder.Run(feeds);
        var byName = results.ToDictionary(r => r.Name, r => r);

        var encOut = byName["encoder_out"].AsTensor<float>();
        var frames = encOut.Dimensions[1];
        var width = encOut.Dimensions[2];

        for (var i = 0; i < _stateInNames.Length; i++)
        {
            // A COPY WE OWN. The results are disposed with this block, so a
            // reference into them is a state tensor pointing at freed memory on
            // the next chunk.
            _states[i] = _states[i] is DenseTensor<long>
                ? byName[_stateOutNames[i]].AsTensor<long>().ToDenseTensor()
                : byName[_stateOutNames[i]].AsTensor<float>().ToDenseTensor();
        }

        Decode(encOut, frames, width);
    }

    /// <summary>
    /// Greedy transducer decoding — the whole difference from the keyword spotter.
    /// </summary>
    /// <remarks>
    /// NO BEAM AND NO CONTEXT GRAPH, AND THAT IS THE POINT. The spotter needs
    /// both because it is hunting for one phrase the model is not inclined to
    /// emit, so it keeps several hypotheses alive and pays a boost to tokens
    /// that advance the keyword. Here the model is simply asked what it heard,
    /// and the answer is the argmax: open vocabulary comes for free, and so
    /// does a far cheaper decode.
    /// </remarks>
    private void Decode(Tensor<float> encOut, int frames, int width)
    {
        var grew = false;

        for (var t = 0; t < frames; t++)
        {
            var enc = new DenseTensor<float>(new[] { 1, width });
            for (var d = 0; d < width; d++) enc[0, d] = encOut[0, t, d];

            // UP TO A FEW SYMBOLS PER FRAME. A transducer may emit more than one
            // token on a frame; without the loop a fast speaker loses syllables,
            // and the bound stops a degenerate model looping for ever on one.
            for (var emitted = 0; emitted < 4; emitted++)
            {
                var logits = JoinerLogits(enc, DecoderFor(_hyp));

                var best = 0;
                for (var v = 1; v < logits.Length; v++)
                    if (logits[v] > logits[best]) best = v;

                if (best == Blank) break;

                _hyp.Add(best);
                if (_tokenText.TryGetValue(best, out var piece))
                {
                    _said.Append(piece.Replace("▁", " "));
                    grew = true;
                }
            }
        }

        if (grew) Grew?.Invoke(this, _said.ToString());
    }

    private float[] JoinerLogits(DenseTensor<float> enc, DenseTensor<float> dec)
    {
        using var r = _joiner.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("encoder_out", enc),
            NamedOnnxValue.CreateFromTensor("decoder_out", dec),
        });
        var t = r.First().AsTensor<float>();
        var v = new float[t.Dimensions[^1]];
        for (var i = 0; i < v.Length; i++) v[i] = t[0, i];
        return v;
    }

    /// <summary>Decoder output for the current hypothesis, cached on its tail.</summary>
    /// <remarks>
    /// The decoder only sees context_size tokens, so two hypotheses ending the
    /// same way share an answer. On a greedy decode that turns most frames into
    /// a dictionary lookup, which is what keeps this affordable on a phone.
    /// </remarks>
    private DenseTensor<float> DecoderFor(List<int> tokens)
    {
        var key = (tokens[^2], tokens[^1]);
        if (_decCache.TryGetValue(key, out var cached)) return cached;

        var y = new DenseTensor<long>(new[] { 1, _contextSize });
        for (var i = 0; i < _contextSize; i++)
            y[0, i] = tokens[tokens.Count - _contextSize + i];

        using var r = _decoder.Run(new[] { NamedOnnxValue.CreateFromTensor("y", y) });
        var d = r.First().AsTensor<float>().ToDenseTensor();
        if (_decCache.Count < 4096) _decCache[key] = d;
        return d;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _joiner.Dispose();
    }
}
