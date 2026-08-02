#nullable enable

// ZipformerKwsSpotter.cs
//
// Streaming keyword spotting on a zipformer2 transducer — the engine behind
// "Hey B" without a round trip through full speech recognition.
//
// WHY THIS AND NOT KwsWakeWordDetector. That one runs a single-graph classifier:
// one ONNX, a softmax, pick the target class. It is the right runtime for a
// Speech-Commands-shaped model and cannot run this one at all. This is three
// graphs — encoder, decoder, joiner — with 36 recurrent state tensors threaded
// through every chunk.
//
// THE PROPERTY THAT MADE IT WORTH THE WORK: keywords are TEXT, not a trained
// class. The model emits BPE tokens and we look for the keyword's token sequence
// in the stream, so "Hey B" is a line in a file rather than a training run on a
// GPU we do not have and would not use.
//
// Streaming shape, from the model's own metadata rather than assumed:
//   T = 45                each encoder call sees 45 feature frames
//   decode_chunk_len = 32 and advances 32, so 13 frames overlap as right context
//   encoder_out [1,8,320] 32 frames in, 8 out — the zipformer downsamples by 4
//
// The 36 state tensors are allocated by READING THE GRAPH's input shapes, not by
// hardcoding a table of them. A hardcoded table is a silent liability the first
// time the model is updated — wrong-shaped zeros do not throw, they just make the
// thing deaf.
//
// ============================================================================
// INCOMPLETE: RUNS AND COMPLETES KEYWORDS, BUT DOES NOT DISCRIMINATE YET.
// Do not wire it to a microphone.
// ============================================================================
//
// GROUND TRUTH, established by running sherpa-onnx's own Python package on the
// same files: it detects LIGHT UP in 0.wav and LOVELY CHILD + FOREVER in 1.wav.
// So the model, the audio and the keyword encodings are all correct, and any
// remaining gap is in this implementation. That check is worth more than any
// amount of reasoning about it and should be the first move next time.
//
// PROVEN HERE:
//   - features bit-compatible with the C++ reference (KaldiFbankTests)
//   - three graphs, 36 state tensors threaded across chunks, encoder responds to
//     audio; identical to the same pipeline driven from Python; 17-24x realtime
//   - modified beam search with keyword boosting now COMPLETES phrases at
//     sherpa's own default boost of 1.0
//
// TWO REAL BUGS FIXED ON THE WAY, both silent:
//
//   THE MEAN WAS THE WRONG MEAN. sherpa stores exp(logprob) per token and takes
//   the ARITHMETIC mean of probabilities. Averaging log-probs and exponentiating
//   is the GEOMETRIC mean, which one weak token drags to nearly zero — it read
//   0.013 against a 0.25 threshold, so nothing could ever fire.
//
//   THE DECODER HISTORY MUST RESET WITH THE MATCH. A keyword's tokens are scored
//   conditioned on blank and then on its own prefix, not on the sentence that
//   happened to precede it. Carrying the surrounding speech made keyword tokens
//   so unlikely that the path needed EIGHT times the published boost to survive;
//   with the reset it survives at 1.0.
//
// WHAT IS STILL WRONG: separation. On 0.wav the phrase that IS present completes
// at mean probability 0.124, and a phrase that is ABSENT completes at 0.106 —
// against a 0.25 threshold neither fires, and the two are too close to separate
// by moving the threshold. Something in the search still differs from sherpa;
// candidates are the global top-k over (hypotheses x vocab) rather than per
// hypothesis, hypothesis merging by log-add rather than max, and the terminal
// node_score bonus on completion. The next session should diff against the C++
// decoder frame by frame rather than reason about it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>One keyword: what to listen for, and what to call it when heard.</summary>
/// <param name="Tokens">The BPE token ids that spell the phrase.</param>
/// <param name="Phrase">Human-readable form, surfaced on detection.</param>
public sealed record KwsKeyword(IReadOnlyList<int> Tokens, string Phrase);

/// <summary>A keyword was heard.</summary>
/// <param name="Phrase">The phrase, as written in keywords_raw.txt.</param>
/// <param name="AtFrame">Encoder frame index where the last token landed.</param>
public sealed record KwsDetection(string Phrase, int AtFrame);

/// <summary>
/// Streaming keyword spotter over a sherpa-onnx zipformer2 KWS bundle.
/// </summary>
public sealed class ZipformerKwsSpotter : IDisposable
{
    private readonly InferenceSession _encoder, _decoder, _joiner;
    private readonly KaldiFbank _fbank;
    private readonly List<KwsKeyword> _keywords;
    private readonly Dictionary<int, string> _tokenText = new();

    private readonly string[] _stateInNames, _stateOutNames;
    private readonly DenseTensor<float>[] _states;
    private readonly int[] _stateDims;

    private long _processedLens;
    private readonly int _chunkFrames;     // 45  — what the encoder consumes
    private readonly int _advanceFrames;   // 32  — how far we move each call
    private readonly int _contextSize;     // 2   — decoder history width

    private readonly List<float[]> _features = new();
    private int _featureCursor;

    /// <summary>Token ids emitted so far, for keyword matching.</summary>
    private readonly List<int> _emitted = new();
    private int _encoderFrame;

    /// <summary>Blank in a k2 transducer is always id 0.</summary>
    private const int Blank = 0;

    /// <summary>Raised when a keyword is heard.</summary>
    public event EventHandler<KwsDetection>? Detected;

    /// <summary>
    /// Raised for every token the decoder emits, with its text.
    /// </summary>
    /// <remarks>
    /// Diagnostics, and the only honest way to tell a BROKEN PIPELINE from a
    /// decode that simply did not pick the keyword. Both look like silence from
    /// the outside; only the token stream distinguishes "the model heard the words
    /// and we failed to match them" from "the model heard noise".
    /// </remarks>
    public event EventHandler<string>? TokenEmitted;

    /// <summary>How far a keyword has been matched, and how well it scores.</summary>
    /// <param name="Phrase">Which keyword.</param>
    /// <param name="Matched">Tokens matched so far.</param>
    /// <param name="Total">Tokens in the phrase.</param>
    /// <param name="MeanProbability">Mean acoustic probability, boost excluded.</param>
    public sealed record KwsProgress(string Phrase, int Matched, int Total, double MeanProbability);

    /// <summary>
    /// Raised as a keyword is partially matched.
    /// </summary>
    /// <remarks>
    /// This is how a threshold gets set from evidence instead of taste: it shows
    /// the score a phrase ACTUALLY reaches on real speech, so
    /// <see cref="Threshold"/> can be placed between the hits and the misses
    /// rather than at whatever number the upstream project happened to pick.
    /// </remarks>
    public event EventHandler<KwsProgress>? KeywordProgress;

    /// <summary>Loads a bundle directory (encoder/decoder/joiner + tokens + keywords).</summary>
    /// <param name="directory">Extracted bundle path.</param>
    /// <param name="keywordsFile">Defaults to keywords.txt beside the models.</param>
    public ZipformerKwsSpotter(string directory, string? keywordsFile = null)
    {
        string Find(string contains, string ext = ".onnx")
        {
            var f = Directory.GetFiles(directory, "*" + ext)
                .FirstOrDefault(p => Path.GetFileName(p).Contains(contains, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"no *{contains}*{ext} in {directory}");
            return f;
        }

        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _encoder = new InferenceSession(Find("encoder"), opts);
        _decoder = new InferenceSession(Find("decoder"), opts);
        _joiner  = new InferenceSession(Find("joiner"),  opts);

        // From the model, not from memory.
        var meta = _encoder.ModelMetadata.CustomMetadataMap;
        _chunkFrames   = MetaInt(meta, "T", 45);
        _advanceFrames = MetaInt(meta, "decode_chunk_len", 32);
        _contextSize   = MetaInt(_decoder.ModelMetadata.CustomMetadataMap, "context_size", 2);

        // Every encoder input except the features is a state tensor. Shapes come
        // straight off the graph so an updated model cannot silently mismatch.
        var inputs = _encoder.InputMetadata;
        _stateInNames = inputs.Keys.Where(k => k != "x").ToArray();
        _stateOutNames = _stateInNames
            .Select(n => n == "processed_lens" ? "new_processed_lens"
                       : n == "embed_states"   ? "new_embed_states"
                       : "new_" + n)
            .ToArray();

        _states    = new DenseTensor<float>[_stateInNames.Length];
        _stateDims = new int[_stateInNames.Length];
        for (var i = 0; i < _stateInNames.Length; i++)
        {
            var dims = inputs[_stateInNames[i]].Dimensions.Select(d => d < 0 ? 1 : d).ToArray();
            _stateDims[i] = dims.Length;
            if (_stateInNames[i] != "processed_lens")
                _states[i] = new DenseTensor<float>(dims);      // zeros = fresh stream
        }

        _fbank = new KaldiFbank();
        var tokensPath = Path.Combine(directory, "tokens.txt");
        foreach (var line in File.ReadLines(tokensPath))
        {
            var sp = line.LastIndexOf(' ');
            if (sp > 0 && int.TryParse(line[(sp + 1)..], out var tid)) _tokenText[tid] = line[..sp];
        }

        _keywords = LoadKeywords(keywordsFile ?? Path.Combine(directory, "keywords.txt"),
                                 Path.Combine(directory, "keywords_raw.txt"),
                                 tokensPath);
    }

    /// <summary>The phrases this spotter is listening for.</summary>
    public IReadOnlyList<string> Keywords => _keywords.Select(k => k.Phrase).ToList();

    private static int MetaInt(IReadOnlyDictionary<string, string> m, string key, int fallback) =>
        m.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static List<KwsKeyword> LoadKeywords(string keywordsPath, string rawPath, string tokensPath)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(tokensPath))
        {
            var sp = line.LastIndexOf(' ');
            if (sp <= 0) continue;
            if (int.TryParse(line[(sp + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                ids[line[..sp]] = id;
        }

        var raw = File.Exists(rawPath) ? File.ReadAllLines(rawPath) : Array.Empty<string>();
        var list = new List<KwsKeyword>();
        var i = 0;
        foreach (var line in File.ReadLines(keywordsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var toks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var seq = new List<int>();
            var ok = true;
            foreach (var t in toks)
            {
                // A token that is not in the vocabulary would silently become a
                // keyword that can never match, so it is dropped loudly instead.
                if (!ids.TryGetValue(t, out var id)) { ok = false; break; }
                seq.Add(id);
            }
            var phrase = i < raw.Length ? raw[i].Trim() : string.Join(" ", toks);
            if (ok && seq.Count > 0) list.Add(new KwsKeyword(seq, phrase));
            i++;
        }
        if (list.Count == 0)
            throw new InvalidOperationException(
                $"no usable keywords in {keywordsPath} — every line had a token missing from {tokensPath}");
        return list;
    }

    /// <summary>Feeds audio. Float samples in [-1, 1] at 16 kHz.</summary>
    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        _fbank.AcceptWaveform(samples);
        Drain();
    }

    /// <summary>Marks the end of the audio, releasing the final frames.</summary>
    public void Flush()
    {
        _fbank.Flush();
        Drain();
    }

    private void Drain()
    {
        for (var f = _features.Count; f < _fbank.FramesReady; f++)
            _features.Add(_fbank.GetFrame(f));

        while (_featureCursor + _chunkFrames <= _features.Count)
        {
            RunChunk(_featureCursor);
            _featureCursor += _advanceFrames;
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
            if (_stateInNames[i] == "processed_lens")
            {
                var pl = new DenseTensor<long>(new[] { 1 });
                pl[0] = _processedLens;
                feeds.Add(NamedOnnxValue.CreateFromTensor("processed_lens", pl));
            }
            else feeds.Add(NamedOnnxValue.CreateFromTensor(_stateInNames[i], _states[i]));
        }

        using var results = _encoder.Run(feeds);
        var byName = results.ToDictionary(r => r.Name, r => r);

        var encOut = byName["encoder_out"].AsTensor<float>();   // [1, U, 320]
        var frames = encOut.Dimensions[1];
        var width  = encOut.Dimensions[2];

        for (var i = 0; i < _stateInNames.Length; i++)
        {
            if (_stateInNames[i] == "processed_lens")
            {
                _processedLens = byName[_stateOutNames[i]].AsTensor<long>()[0];
                continue;
            }
            // ToDenseTensor gives a COPY we own. The result is disposed with the
            // Run() block, so holding a reference into it would be a state tensor
            // pointing at freed memory on the next chunk.
            _states[i] = byName[_stateOutNames[i]].AsTensor<float>().ToDenseTensor();
        }

        DecodeBeam(encOut, frames, width);
    }

    /// <summary>Beam width — sherpa's max_active_paths.</summary>
    public int BeamSize { get; init; } = 4;

    /// <summary>Log-prob bonus for a token that advances a keyword (keywords_score).</summary>
    /// <remarks>
    /// THE reason this works where greedy did not. The model is trained to sit on
    /// blank, so a keyword's tokens never win an unbiased argmax. The boost is not
    /// a thumb on the scale for a wrong answer — it is what keeps the keyword path
    /// ALIVE IN THE BEAM long enough to be judged on its acoustics, which is what
    /// <see cref="Threshold"/> then does, with the boost removed.
    /// </remarks>
    public double KeywordBoost { get; init; } = 1.0;

    /// <summary>Mean acoustic probability the phrase must reach to fire.</summary>
    /// <remarks>
    /// Judged on RAW log-probs. If the bonus that made the path visible also
    /// counted towards accepting it, every keyword would fire on silence.
    /// </remarks>
    public double Threshold { get; init; } = 0.25;

    /// <summary>Blanks required after the phrase before it is called finished.</summary>
    public int TrailingBlanksRequired { get; init; } = 1;

    private sealed class Hyp
    {
        public List<int> Tokens = new() { Blank, Blank };
        public double    LogProb;
        public int[]     Pos    = Array.Empty<int>();      // tokens matched, per keyword
        public double[]  AcSum  = Array.Empty<double>();   // sum of PROBABILITIES, per keyword
        public int       Blanks;
        public int       Pending = -1;                     // matched, awaiting trailing blanks

        public Hyp Clone() => new()
        {
            Tokens = new List<int>(Tokens), LogProb = LogProb,
            Pos = (int[])Pos.Clone(), AcSum = (double[])AcSum.Clone(),
            Blanks = Blanks, Pending = Pending,
        };
    }

    private List<Hyp>? _beam;

    /// <summary>Modified beam search over the keyword set — sherpa's algorithm.</summary>
    /// <remarks>
    /// Greedy emitted nothing on this model, and scoring a keyword's path in
    /// isolation separated nothing (measured on the shipped audio: present
    /// -3.127/token, absent -3.148). Both fail the same way — the keyword never
    /// has to COMPETE. Here it does: boosted enough to hold a beam slot, then
    /// accepted only if its unboosted acoustics clear the threshold.
    /// </remarks>
    private void DecodeBeam(Tensor<float> encOut, int frames, int width)
    {
        var nk = _keywords.Count;
        _beam ??= new List<Hyp> { new() { Pos = new int[nk], AcSum = new double[nk] } };

        for (var t = 0; t < frames; t++, _encoderFrame++)
        {
            var enc = new DenseTensor<float>(new[] { 1, width });
            for (var d = 0; d < width; d++) enc[0, d] = encOut[0, t, d];

            var next = new List<Hyp>();
            foreach (var h in _beam)
            {
                var logp = LogSoftmax(JoinerLogits(enc, DecoderFor(h.Tokens)));

                // The best few tokens PLUS every keyword's next expected token.
                // Without the second half a keyword token can be pruned before the
                // boost ever reaches it — which is precisely the bug being fixed.
                var cand = new HashSet<int> { Blank };
                foreach (var k in TopK(logp, BeamSize)) cand.Add(k);
                for (var k = 0; k < nk; k++)
                    if (h.Pos[k] < _keywords[k].Tokens.Count) cand.Add(_keywords[k].Tokens[h.Pos[k]]);

                foreach (var tok in cand)
                {
                    var n = h.Clone();
                    var boost = 0.0;

                    if (tok == Blank)
                    {
                        n.Blanks++;
                        if (n.Pending >= 0 && n.Blanks > TrailingBlanksRequired)
                        {
                            Detected?.Invoke(this, new KwsDetection(
                                _keywords[n.Pending].Phrase, _encoderFrame));
                            n.Pending = -1;
                            Array.Clear(n.Pos); Array.Clear(n.AcSum);
                        }
                    }
                    else
                    {
                        n.Blanks = 0;
                        n.Tokens.Add(tok);
                        TokenEmitted?.Invoke(this,
                            _tokenText.TryGetValue(tok, out var tx) ? tx : $"<{tok}>");

                        for (var k = 0; k < nk; k++)
                        {
                            var kw = _keywords[k].Tokens;
                            if (n.Pos[k] < kw.Count && kw[n.Pos[k]] == tok)
                            {
                                boost = KeywordBoost;
                                // exp() FIRST, then average — the arithmetic mean
                                // of probabilities, which is what sherpa compares
                                // against the threshold. Averaging the LOG-probs
                                // and exponentiating gives the geometric mean, and
                                // that is a different and far smaller number: one
                                // weak token drags it to nearly zero. Measured
                                // wrong-way-round it read 0.013 against a 0.25
                                // threshold and nothing ever fired.
                                n.AcSum[k] += Math.Exp(logp[tok]);
                                n.Pos[k]++;
                                KeywordProgress?.Invoke(this, new KwsProgress(
                                    _keywords[k].Phrase, n.Pos[k], kw.Count,
                                    n.AcSum[k] / n.Pos[k]));
                                if (n.Pos[k] == kw.Count)
                                {
                                    if (n.AcSum[k] / kw.Count >= Threshold) n.Pending = k;
                                    n.Pos[k] = 0; n.AcSum[k] = 0;
                                }
                            }
                            else if (n.Pos[k] > 0)
                            {
                                // Mismatch restarts the phrase. A full trie would
                                // fall back to the longest matching prefix; across a
                                // handful of short wake phrases that costs a
                                // re-detect a syllable later, not a miss.
                                n.Pos[k] = 0; n.AcSum[k] = 0;
                            }
                        }

                        // DECODER HISTORY IS RESET WITH THE MATCH, exactly as
                        // sherpa does it. A keyword's tokens must be scored as if
                        // the phrase were starting — conditioned on blank, then on
                        // its own first token — not on whatever sentence happened
                        // to precede it. Carrying the surrounding speech into the
                        // decoder is why the keyword's probabilities were so low
                        // that it needed four times the published boost to survive.
                        if (n.Pos.All(v => v == 0) && n.Pending < 0)
                            n.Tokens = new List<int> { Blank, Blank };
                        {
                        }
                    }

                    n.LogProb = h.LogProb + logp[tok] + boost;
                    next.Add(n);
                }
            }

            // MERGE BY TOKEN SEQUENCE, then prune. This is the "modified" in
            // modified beam search and it is not an optimisation — it is what
            // makes the search work at all.
            //
            // Blank does not extend the sequence, so every blank-extension of a
            // hypothesis is the SAME hypothesis. Without merging they are four
            // separate entries, they fill the beam with copies of each other, and
            // the keyword path — the one genuinely distinct sequence — is pruned
            // every single frame. Measured before this fix: keywords reached 1-2
            // of their tokens and never finished.
            var merged = new Dictionary<string, Hyp>();
            foreach (var h in next)
            {
                var key = string.Join(",", h.Tokens);
                if (merged.TryGetValue(key, out var seen))
                {
                    // Same sequence reached two ways: keep the better score, and
                    // the further-advanced keyword state with it.
                    if (h.LogProb > seen.LogProb) merged[key] = h;
                }
                else merged[key] = h;
            }

            next = merged.Values.ToList();
            next.Sort((a, b) => b.LogProb.CompareTo(a.LogProb));
            if (next.Count > BeamSize) next.RemoveRange(BeamSize, next.Count - BeamSize);

            // Re-base to the leader, or an always-on stream walks the scores to
            // -infinity over hours and every comparison becomes meaningless.
            var top = next[0].LogProb;
            foreach (var x in next)
            {
                x.LogProb -= top;
                if (x.Tokens.Count > 64) x.Tokens.RemoveRange(0, x.Tokens.Count - 8);
            }
            _beam = next;
        }
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

    private static float[] LogSoftmax(float[] logits)
    {
        var max = logits.Max();
        double sum = 0;
        for (var i = 0; i < logits.Length; i++) sum += Math.Exp(logits[i] - max);
        var logSum = Math.Log(sum);
        var o = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++) o[i] = (float)(logits[i] - max - logSum);
        return o;
    }

    private static IEnumerable<int> TopK(float[] v, int k)
    {
        var idx = new int[v.Length];
        for (var i = 0; i < v.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => v[b].CompareTo(v[a]));
        return idx.Take(k);
    }

    private readonly Dictionary<(int, int), DenseTensor<float>> _decCache = new();

    /// <summary>Decoder output for a hypothesis, cached on its last two tokens.</summary>
    /// <remarks>
    /// The decoder only sees <c>context_size</c> tokens, so two hypotheses ending
    /// the same way share an answer. With a beam of 4 that removes most of the
    /// decoder calls, which is what keeps this affordable on a phone.
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

    /// <summary>Clears stream state for a new utterance, keeping the loaded models.</summary>
    public void Reset()
    {
        _fbank.Reset();
        _features.Clear();
        _featureCursor = 0;
        _beam = null;                 // rebuilt on the next chunk
        _processedLens = 0;
        _encoderFrame = 0;
        for (var i = 0; i < _stateInNames.Length; i++)
            if (_stateInNames[i] != "processed_lens")
                _states[i] = new DenseTensor<float>(_states[i].Dimensions.ToArray());
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _joiner.Dispose();
    }
}
