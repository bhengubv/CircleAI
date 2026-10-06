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
// class. The model emits BPE tokens and the search looks for the keyword's token
// sequence, so "Hey B" is a line in a file rather than a training run on a GPU we
// do not have and would not use.
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
// WHERE THE BUG ACTUALLY WAS, recorded because the search is not where anyone
// would look and it is where four rewrites went. This decoder was written three
// times from an understanding of the algorithm — greedy, then a per-keyword
// score, then a beam with a hand-rolled position counter — and then a fourth time
// as a faithful port of the reference. None of them detected anything, and the
// reason was never in any of them:
//
//   KaldiFbank WAS SCALING THE AUDIO BY 32768. sherpa's normalize_samples = true
//   means "these samples are already in [-1, 1]", not "normalise them for me".
//   Read backwards it adds a constant 20.794 to every mel bin, which is a UNIFORM
//   offset — so the features keep their shape, their range and their contours, and
//   look right by every eye check. The zipformer just emitted blank forever.
//
// WHAT FOUND IT, after reasoning had failed repeatedly: running sherpa's own
// Python package on the same files as a greedy recogniser. It transcribed 6.6
// seconds of speech in full from THE SAME ONNX GRAPHS; the same loop here emitted
// not one token. That single comparison localised the fault to the input side in
// one step. The lesson is cheap to state and was expensive to learn — when an
// implementation and a reference disagree, run the reference, do not reason about
// it. It should be the first move, not the last.
//
// THE PORT WAS STILL WORTH DOING. Three real defects came out of it, each of
// which would have surfaced the moment the features were fixed:
//
//   THE MEAN WAS THE WRONG MEAN. sherpa stores exp(logprob) per token and takes
//   the ARITHMETIC mean of probabilities. Averaging log-probs and exponentiating
//   is the GEOMETRIC mean, which one weak token drags to nearly zero.
//
//   THE KEYWORD MUST NOT BE HANDED A CANDIDATE SLOT. Forcing each phrase's next
//   expected token into the candidate set every frame guarantees the phrase can
//   always complete — a search that cannot say no. Here top-k is GLOBAL over
//   (hypotheses x vocabulary) on acoustics alone, and a keyword survives only by
//   carrying a high enough hypothesis score from the boost it has already earned.
//
//   DETECTION IS TESTED ON THE LEADING HYPOTHESIS ONLY. Firing whenever ANY beam
//   entry finished a phrase is what turns a search into a rubber stamp.
//
// PROVEN, against sherpa-onnx 1.13.4 as the oracle on its own shipped audio:
//   0.wav -> LIGHT UP at p=0.741;  1.wav -> LOVELY CHILD 0.626, FOREVER 0.686
//   — the same three detections the oracle makes, and nothing else fires. Phrases
//   that are absent stall after one token at p≈0.02, so the 0.25 threshold sits in
//   a gap of more than thirty times, not on a knife edge.
//   Features bit-compatible with the C++ reference (KaldiFbankTests); three
//   graphs and 36 state tensors threaded across chunks at 20-24x realtime.

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
/// <param name="Boost">Per-token boost, or 0 to use the spotter's default.</param>
/// <param name="Threshold">Acceptance threshold, or 0 to use the spotter's default.</param>
public sealed record KwsKeyword(IReadOnlyList<int> Tokens, string Phrase,
                                float Boost = 0f, float Threshold = 0f);

/// <summary>A keyword was heard.</summary>
/// <param name="Phrase">The phrase, as written in keywords_raw.txt.</param>
/// <param name="AtFrame">Encoder frame index where the last token landed.</param>
/// <param name="Probability">Mean acoustic probability it scored, boost excluded.</param>
/// <param name="StartFrame">Encoder frame where the phrase's FIRST token landed.</param>
public sealed record KwsDetection(string Phrase, int AtFrame, double Probability, int StartFrame = -1)
{
    /// <summary>Milliseconds of audio per encoder frame — 4x subsampling of 10 ms hops.</summary>
    public const double MsPerFrame = 40.0;

    /// <summary>Where the phrase began, in milliseconds from the start of the stream.</summary>
    public double StartMs => (StartFrame < 0 ? AtFrame : StartFrame) * MsPerFrame;

    /// <summary>Where the phrase ended, in milliseconds from the start of the stream.</summary>
    public double EndMs => AtFrame * MsPerFrame;
}

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

    private long _processedLens;
    private readonly int _chunkFrames;     // 45  — what the encoder consumes
    private readonly int _advanceFrames;   // 32  — how far we move each call
    private readonly int _contextSize;     // 2   — decoder history width
    private readonly int _unkId;           // treated as blank, like sherpa

    private readonly List<float[]> _features = new();
    private int _featureCursor;
    private int _encoderFrame;
    private int _vocab;

    /// <summary>The last progress actually reported, so a still one is not repeated.</summary>
    private KwsProgress? _lastProgress;

    /// <summary>
    /// How much a mean probability has to improve to count as having moved.
    /// </summary>
    /// <remarks>
    /// Not zero: the mean drifts in the last decimal place as blanks accumulate
    /// behind a matched prefix, and reporting that would restore the flood this
    /// is here to stop while looking like real movement. A thousandth is below
    /// what the traces print and far below what any threshold is set to.
    /// </remarks>
    private const double ProgressEpsilon = 0.001;

    /// <summary>Blank in a k2 transducer is always id 0.</summary>
    private const int Blank = 0;

    /// <summary>Raised when a keyword is heard.</summary>
    public event EventHandler<KwsDetection>? Detected;

    /// <summary>
    /// Raised for every token the leading hypothesis emits, with its text.
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
    /// Raised as the leading hypothesis walks into a keyword.
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
    /// <param name="preferQuantized">
    /// Use the int8 graphs when the bundle ships both. Smaller and faster; the
    /// probabilities come out flatter, which costs separation.
    /// </param>
    public ZipformerKwsSpotter(string directory, string? keywordsFile = null, bool preferQuantized = false)
    {
        // WHICH GRAPH GETS PICKED IS A DECISION, NOT AN ACCIDENT. These bundles
        // ship both float and int8 copies of all three models, and the int8 name
        // sorts FIRST, so "take the first file matching *encoder*" quietly ran the
        // quantized stack. It loads, it runs, it is fast — and its output
        // distribution is flat enough that keyword tokens sat around p=0.01 where
        // the float graphs put them above 0.3. Nothing errors; the wake word
        // simply never fires. Float is the default and int8 is opt-in.
        string Find(string contains, string ext = ".onnx")
        {
            var all = Directory.GetFiles(directory, "*" + ext)
                .Where(p => Path.GetFileName(p).Contains(contains, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (all.Count == 0) throw new FileNotFoundException($"no *{contains}*{ext} in {directory}");

            bool IsInt8(string p) => Path.GetFileName(p).Contains("int8", StringComparison.OrdinalIgnoreCase);
            return all.FirstOrDefault(p => IsInt8(p) == preferQuantized) ?? all[0];
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

        _states = new DenseTensor<float>[_stateInNames.Length];
        for (var i = 0; i < _stateInNames.Length; i++)
        {
            var dims = inputs[_stateInNames[i]].Dimensions.Select(d => d < 0 ? 1 : d).ToArray();
            if (_stateInNames[i] != "processed_lens")
                _states[i] = new DenseTensor<float>(dims);      // zeros = fresh stream
        }

        _fbank = new KaldiFbank();
        var tokensPath = Path.Combine(directory, "tokens.txt");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(tokensPath))
        {
            var sp = line.LastIndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(line[(sp + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                continue;
            _tokenText[tid] = line[..sp];
            ids[line[..sp]] = tid;
        }

        // <unk> is scored as blank rather than as a token, so a garbled frame
        // neither advances a keyword nor breaks one.
        _unkId = ids.TryGetValue("<unk>", out var unk) ? unk : -1;

        _keywords = LoadKeywords(keywordsFile ?? Path.Combine(directory, "keywords.txt"), ids, _tokenText);
    }

    /// <summary>The phrases this spotter is listening for.</summary>
    public IReadOnlyList<string> Keywords => _keywords.Select(k => k.Phrase).ToList();

    /// <summary>How many tokens spell a registered phrase; 0 if it is not one.</summary>
    /// <remarks>
    /// SO A REFUSAL CAN SAY "8 OF 8". A veto is the nearest miss there is - every
    /// token matched - but the rejection carries only the phrase, and a screen
    /// reporting it needs the same denominator the partial matches use, or "all
    /// of it" and "one of eight" arrive in different units.
    /// </remarks>
    public int TokenCountOf(string phrase) =>
        _keywords.FirstOrDefault(k => string.Equals(k.Phrase, phrase, StringComparison.Ordinal))
            ?.Tokens.Count ?? 0;

    /// <summary>
    /// Registered phrases that can never fire, each with the shorter phrase that
    /// swallows it. Empty is the healthy case.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than silently tolerated. A wake phrase that cannot fire
    /// looks identical, from outside, to one the model simply is not hearing —
    /// and someone would reasonably spend a day on the audio before suspecting
    /// the keyword list. Check it after construction and say something.
    /// </remarks>
    public IReadOnlyList<(string Phrase, string ShadowedBy)> ShadowedKeywords =>
        Graph.ShadowedPhrases;

    private static int MetaInt(IReadOnlyDictionary<string, string> m, string key, int fallback) =>
        m.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>
    /// Reads sherpa's keywords.txt: tokens, then optional <c>:boost</c>,
    /// <c>#threshold</c> and <c>@phrase</c>.
    /// </summary>
    /// <remarks>
    /// The human-readable phrase comes from <c>@</c>, else from the matching
    /// <c>*_raw.txt</c>, else from gluing the tokens back together. The raw file
    /// is looked up FROM THE KEYWORDS FILE'S OWN NAME rather than always taking
    /// the bundle's keywords_raw.txt — those two only line up when the caller is
    /// using the bundle's default keyword list, and when they don't, every
    /// detection gets confidently mislabelled with someone else's phrase.
    /// </remarks>
    private static List<KwsKeyword> LoadKeywords(
        string keywordsPath, Dictionary<string, int> ids, Dictionary<int, string> tokenText)
    {
        var rawPath = Path.Combine(
            Path.GetDirectoryName(keywordsPath) ?? ".",
            Path.GetFileNameWithoutExtension(keywordsPath) + "_raw" + Path.GetExtension(keywordsPath));
        var raw = File.Exists(rawPath) ? File.ReadAllLines(rawPath) : Array.Empty<string>();

        var list = new List<KwsKeyword>();
        var i = 0;

        foreach (var line in File.ReadLines(keywordsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var seq = new List<int>();
            var phrase = string.Empty;
            float boost = 0, threshold = 0;
            var ok = true;

            foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ids.TryGetValue(w, out var id)) { seq.Add(id); continue; }
                switch (w[0])
                {
                    case ':': boost     = Num(w); break;
                    case '#': threshold = Num(w); break;
                    case '@': phrase    = w[1..].Replace('_', ' '); break;
                    // A token that is not in the vocabulary would silently become a
                    // keyword that can never match, so the line is dropped instead.
                    default: ok = false; break;
                }
                if (!ok) break;
            }

            if (phrase.Length == 0)
                phrase = i < raw.Length && raw[i].Trim().Length > 0
                    ? raw[i].Trim()
                    // "▁ L IGHT ▁UP" -> "LIGHT UP": the sentencepiece marker IS
                    // the word boundary, so the tokens rebuild the phrase exactly.
                    : string.Concat(seq.Select(t => tokenText.GetValueOrDefault(t, "")))
                            .Replace('▁', ' ').Trim();

            if (ok && seq.Count > 0) list.Add(new KwsKeyword(seq, phrase, boost, threshold));
            i++;
        }

        if (list.Count == 0)
            throw new InvalidOperationException(
                $"no usable keywords in {keywordsPath} — every line had a token outside the vocabulary");
        return list;

        static float Num(string w) =>
            float.TryParse(w[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    /// <summary>Feeds audio. Float samples in [-1, 1] at 16 kHz.</summary>
    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        _fbank.AcceptWaveform(samples);
        Drain();
    }

    /// <summary>Marks the end of the audio, releasing the final frames.</summary>
    /// <remarks>
    /// PADS WITH SILENCE FIRST, and that is not a nicety. The encoder only runs on
    /// FULL 45-frame chunks, so the last part of an utterance is never decoded at
    /// all; worse, a keyword is only accepted once TRAILING BLANKS follow it, so a
    /// phrase spoken at the very end of a recording gets matched and then thrown
    /// away for want of the silence after it. Measured: "Hey B" reached all three
    /// of its tokens and never fired. sherpa's own command-line tool pads with
    /// exactly this trailing silence, which is why it fired on the same file.
    /// <para>
    /// It costs nothing live — a microphone keeps delivering — but a file-based
    /// check without it quietly under-reports, which is the worst kind of test.
    /// </para>
    /// </remarks>
    public void Flush()
    {
        Span<float> silence = stackalloc float[1600];       // 100 ms
        for (var i = 0; i < 5; i++) _fbank.AcceptWaveform(silence);
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

        Decode(encOut, frames, width);
    }

    /// <summary>Beam width — sherpa's max_active_paths.</summary>
    public int BeamSize { get; init; } = 4;

    /// <summary>Log-prob bonus for a token that advances a keyword (keywords_score).</summary>
    /// <remarks>
    /// THE reason this works where greedy did not. The model is trained to sit on
    /// blank, so a keyword's tokens never win an unbiased argmax. The boost is not
    /// a thumb on the scale for a wrong answer — it is a LOAN that keeps the
    /// keyword path alive in the beam long enough to be judged on its acoustics,
    /// repaid in full by <see cref="KwsContextGraph"/> the moment the path falls
    /// off the phrase.
    /// </remarks>
    public double KeywordBoost { get; init; } = 1.0;

    /// <summary>Mean acoustic probability the phrase must reach to fire.</summary>
    /// <remarks>
    /// Judged on RAW log-probs. If the bonus that made the path visible also
    /// counted towards accepting it, every keyword would fire on silence.
    /// </remarks>
    /// <summary>The acceptance threshold this spotter was measured at.</summary>
    /// <remarks>
    /// 0.20, LOWERED FROM 0.25 ON EVIDENCE THIS FILE WAS ALREADY CARRYING.
    ///
    /// <para>
    /// The note here used to say, correctly, that "Hey B" scores 0.24 to 0.34
    /// through the air on a P30 - and then set the gate at 0.25, which is INSIDE
    /// that range. The bottom of the observed spread was on the wrong side of
    /// the bar, so the quietest third of real utterances were always going to be
    /// refused. Not unreliably: arithmetically, for anyone who speaks slightly
    /// softly or slightly further away.
    /// </para>
    /// <para>
    /// Measured on a P30 on 2026-09-06, one person, one room:
    /// </para>
    /// <para>
    ///   fired    0.297  0.369  0.371  0.386
    ///   refused  0.246  - three tokens of three matched, missed by 0.004
    /// </para>
    /// <para>
    /// That last one is the whole argument. The model recognised the entire
    /// phrase and the gate turned it away by four thousandths, which from the
    /// outside is indistinguishable from a phone that did not hear you. A
    /// threshold that rejects a complete, correct match is not a safety margin,
    /// it is a fault.
    /// </para>
    /// <para>
    /// 0.20 SITS BELOW EVERY REAL UTTERANCE SEEN AND WELL ABOVE THE NOISE. In a
    /// quiet room the spotter's partial sightings score 0 to 0.013 - an order of
    /// magnitude below this - so the gap being bought here is between "somebody
    /// spoke softly" and "a chair moved", not between speech and silence.
    /// </para>
    /// <para>
    /// A lower gate does mean more false wakes, and that is what the confirmer
    /// is for. It is worth saying that the confirmer is the RIGHT place to pay
    /// this cost: stage two judges whether the phrase opened an utterance, which
    /// is a question about intent, where the threshold only ever knew about
    /// loudness.
    /// </para>
    /// <para>
    /// STILL A CONSTANT, AND STILL THE WRONG SHAPE OF ANSWER. One number for
    /// every phone, every voice and every room is what put the bar inside the
    /// range in the first place. WakeCalibration now records what each phone
    /// actually hears; when a device has enough of its own evidence this should
    /// come from that file rather than from here.
    /// </para>
    /// </remarks>
    public const double MeasuredThreshold = 0.20;

    /// <inheritdoc cref="MeasuredThreshold"/>
    public double Threshold { get; init; } = MeasuredThreshold;

    /// <summary>Blanks required after the phrase before it is called finished.</summary>
    public int TrailingBlanksRequired { get; init; } = 1;

    private KwsContextGraph? _graph;

    /// <summary>
    /// The keyword trie, built on first use.
    /// </summary>
    /// <remarks>
    /// Not built in the constructor because <see cref="KeywordBoost"/> and
    /// <see cref="Threshold"/> are init-only properties — an object initializer
    /// assigns them AFTER the constructor body has run, so a graph built there
    /// would silently bake in the defaults and ignore whatever the caller asked
    /// for.
    /// </remarks>
    private KwsContextGraph Graph => _graph ??= new KwsContextGraph(
        _keywords.Select(k => k.Tokens).ToList(),
        (float)KeywordBoost, (float)Threshold,
        _keywords.Select(k => k.Boost).ToList(),
        _keywords.Select(k => k.Phrase).ToList(),
        _keywords.Select(k => k.Threshold).ToList());

    private sealed class Hyp
    {
        /// <summary>Tokens decoded so far, primed with the decoder's start context.</summary>
        public List<int> Ys = null!;

        /// <summary>Acoustic PROBABILITY (not log) of each token in <see cref="Ys"/>.</summary>
        public List<double> YsProbs = new();

        /// <summary>Encoder frame each token landed on, cleared alongside YsProbs.</summary>
        public List<int> Timestamps = new();

        public double LogProb;
        public KwsContextState State = null!;
        public int TrailingBlanks;

        /// <summary>Token appended this frame, or -1 for a blank.</summary>
        public int LastToken = -1;

        public Hyp Clone() => new()
        {
            Ys = new List<int>(Ys), YsProbs = new List<double>(YsProbs),
            Timestamps = new List<int>(Timestamps),
            LogProb = LogProb, State = State, TrailingBlanks = TrailingBlanks,
        };

        public string Key => string.Join("-", Ys);
    }

    private Dictionary<string, Hyp>? _hyps;

    /// <summary>
    /// The decoder's start context: <c>context_size</c> entries, all -1 but the
    /// last, which is blank.
    /// </summary>
    /// <remarks>
    /// The -1 is not a mistake and not a sentinel that gets filtered out — it is
    /// fed to the decoder verbatim, where ONNX Gather reads a negative index from
    /// the END of the embedding table. sherpa does exactly this, so the start
    /// context is the last vocabulary row, and priming with blanks instead would
    /// score every phrase's first token against a different conditioning.
    /// </remarks>
    private List<int> StartContext()
    {
        var ys = Enumerable.Repeat(-1, _contextSize).ToList();
        ys[^1] = Blank;
        return ys;
    }

    private Dictionary<string, Hyp> StartHyps()
    {
        var h = new Hyp { Ys = StartContext(), State = Graph.Root };
        return new Dictionary<string, Hyp> { [h.Key] = h };
    }

    /// <summary>
    /// Modified beam search with a context graph — a structural port of sherpa's
    /// TransducerKeywordDecoder::Decode.
    /// </summary>
    private void Decode(Tensor<float> encOut, int frames, int width)
    {
        _hyps ??= StartHyps();

        for (var t = 0; t < frames; t++, _encoderFrame++)
        {
            var enc = new DenseTensor<float>(new[] { 1, width });
            for (var d = 0; d < width; d++) enc[0, d] = encOut[0, t, d];

            var prev = _hyps.Values.ToList();

            // Two arrays, and the difference between them is the whole design:
            // ACOUSTIC is what a token is worth on this frame's audio and is what
            // the threshold later judges; RANKED adds the hypothesis's running
            // score, including every boost it has earned, and is what the search
            // ranks on. Mixing them up either hides the keyword from the beam or
            // lets the boost vote for its own acceptance.
            var acoustic = new float[prev.Count][];
            for (var i = 0; i < prev.Count; i++)
                acoustic[i] = LogSoftmax(JoinerLogits(enc, DecoderFor(prev[i].Ys)));
            _vocab = acoustic[0].Length;

            var ranked = new double[prev.Count * _vocab];
            for (var i = 0; i < prev.Count; i++)
                for (var v = 0; v < _vocab; v++)
                    ranked[i * _vocab + v] = acoustic[i][v] + prev[i].LogProb;

            // GLOBAL top-k over (hypotheses x vocabulary) — not top-k per
            // hypothesis, and with nothing seeded into the candidate set. A
            // keyword token gets considered because the hypothesis carrying it is
            // already scoring well, which is the only evidence that means anything.
            var next = new Dictionary<string, Hyp>();
            foreach (var k in TopK(ranked, BeamSize))
            {
                var i   = k / _vocab;
                var tok = k % _vocab;
                var h   = prev[i].Clone();
                var contextScore = 0f;

                if (tok != Blank && tok != _unkId)
                {
                    h.Ys.Add(tok);
                    h.YsProbs.Add(Math.Exp(acoustic[i][tok]));
                    h.Timestamps.Add(_encoderFrame);
                    h.TrailingBlanks = 0;
                    h.LastToken = tok;

                    (contextScore, h.State, _) = Graph.ForwardOneStep(h.State, tok);

                    // Back at the root means this token belonged to no phrase, so
                    // the decoder history starts over: a keyword's tokens must be
                    // scored as if the phrase were beginning, not conditioned on
                    // whatever sentence happened to precede it.
                    if (h.State.Token == -1)
                    {
                        h.Ys = StartContext();
                        h.YsProbs.Clear();
                        h.Timestamps.Clear();
                    }
                }
                else h.TrailingBlanks++;

                h.LogProb = ranked[k] + contextScore;

                // Same token sequence reached two ways is ONE hypothesis, and its
                // score is the log-sum of both routes. Merging by max instead
                // would under-count a sequence the model reached several ways;
                // not merging at all fills the beam with blank-extensions of the
                // same path and prunes the one genuinely distinct entry.
                if (next.TryGetValue(h.Key, out var seen)) seen.LogProb = LogAdd(seen.LogProb, h.LogProb);
                else next[h.Key] = h;
            }

            var best = next.Values.Aggregate((a, b) => b.LogProb > a.LogProb ? b : a);

            if (best.LastToken >= 0)
                TokenEmitted?.Invoke(this,
                    _tokenText.TryGetValue(best.LastToken, out var tx) ? tx : $"<{best.LastToken}>");

            // Progress reports the DEEPEST hypothesis in the beam, not the leader.
            // The leader is almost always the all-blank path — blank is the most
            // likely symbol on nearly every frame — so reporting it would say
            // "nothing happened" even while a phrase was being tracked. What a
            // threshold needs to be set from is how far the phrase actually got.
            if (KeywordProgress is not null)
            {
                var deepest = next.Values.Aggregate((a, b) =>
                    b.State.Level > a.State.Level ||
                    (b.State.Level == a.State.Level && b.LogProb > a.LogProb) ? b : a);

                // ONLY WHEN IT ACTUALLY MOVES.
                //
                // A partial hypothesis sits in the beam until it is pruned, and
                // this used to re-announce it on EVERY frame at the same score.
                // Downstream keeps the deepest sighting per window, so a phrase
                // that got 2 of 3 tokens once and then stopped went on being
                // reported as the closest thing heard, through minutes of
                // silence. Measured on a P30: closest="Hey B" p=0,345 in a
                // window whose peak amplitude was 0,042 - nobody was speaking,
                // and the log said somebody nearly had.
                //
                // That is not a sighting, it is an echo of one, and it makes the
                // only number available for tuning a threshold unusable: it
                // never falls, so it never distinguishes a near miss from a room
                // that has been empty for a minute.
                if (deepest.State.Level > 0)
                {
                    var p = MeanProbability(deepest, deepest.State.Level);
                    var moved = _lastProgress is not { } last
                        || !string.Equals(last.Phrase, deepest.State.PrefixPhrase, StringComparison.Ordinal)
                        || deepest.State.Level != last.Matched
                        || p > last.MeanProbability + ProgressEpsilon;

                    if (moved)
                    {
                        var report = new KwsProgress(
                            deepest.State.PrefixPhrase, deepest.State.Level,
                            deepest.State.PrefixLength, p);
                        _lastProgress = report;
                        KeywordProgress.Invoke(this, report);
                    }
                }
                else _lastProgress = null;
            }

            // Only the LEADING hypothesis can trigger. Any beam entry finishing a
            // phrase is not evidence the phrase was said — it is evidence the
            // search considered it.
            var (matched, endState) = Graph.IsMatched(best.State);
            if (matched && endState is not null && best.TrailingBlanks > TrailingBlanksRequired)
            {
                var p = MeanProbability(best, endState.Level);
                if (p >= endState.AcThreshold)
                {
                    // The LAST `level` timestamps are the matched phrase — a
                    // hypothesis can carry earlier tokens that reached this node
                    // through a fail link, and those are not part of the keyword.
                    var start = best.Timestamps.Count >= endState.Level
                        ? best.Timestamps[^endState.Level]
                        : _encoderFrame;
                    Detected?.Invoke(this,
                        new KwsDetection(endState.Phrase, _encoderFrame, p, start));
                }

                // A JUDGED PHRASE IS SPENT, WHATEVER THE VERDICT - and this
                // is where the wake word used to go deaf.
                //
                // The reset used to sit inside the `if`, so a hypothesis
                // that COMPLETED the phrase and scored under the bar was
                // never cleared. It cannot fire, because its score is below
                // the threshold. It cannot be beaten, because it finished
                // the phrase and leads on log-probability while every
                // newcomer starts from nothing. And MeanProbability averages
                // only the MATCHED tokens, which are fixed once matched - so
                // more blanks can never raise it either.
                //
                // The result is a permanent leader that can never trigger,
                // and a phone that goes deaf until the app restarts.
                // Measured on a P30: twenty consecutive windows reporting an
                // identical 3/3 score, in silence and speech alike, peak
                // 0,055 to 0,37 while p did not move a digit.
                //
                // Resetting either way costs nothing - the phrase is
                // finished, so there is no partial progress left to protect.
                if (p < endState.AcThreshold)
                    VoiceTrace.Write(
                        $"kws: completed \"{endState.Phrase}\" at p={p:0.###} "
                        + $"(needs {endState.AcThreshold:0.###}) - beam reset");

                next = StartHyps();

                // The beam it described is gone, so the next sighting is a new
                // one however closely it resembles the last.
                _lastProgress = null;
            }

            _hyps = next;
        }
    }

    /// <summary>Mean acoustic probability of a phrase's tokens — arithmetic, on probabilities.</summary>
    /// <remarks>
    /// Deliberately divides by <paramref name="level"/> even when fewer
    /// probabilities were recorded, so a truncated history scores LOW rather than
    /// flattering itself on a short average.
    /// </remarks>
    private static double MeanProbability(Hyp h, int level)
    {
        if (level <= 0) return 0;
        double sum = 0;
        for (var i = 0; i < level && i < h.YsProbs.Count; i++) sum += h.YsProbs[i];
        return sum / level;
    }

    /// <summary>log(exp(x) + exp(y)), without leaving log space.</summary>
    private static double LogAdd(double x, double y)
    {
        if (x < y) (x, y) = (y, x);
        var diff = y - x;
        return diff < -36.04 ? x : x + Math.Log(1 + Math.Exp(diff));
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

    private static IEnumerable<int> TopK(double[] v, int k)
    {
        var idx = new int[v.Length];
        for (var i = 0; i < v.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => v[b].CompareTo(v[a]));
        return idx.Take(Math.Clamp(k, 1, v.Length));
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
        _hyps = null;                 // rebuilt on the next chunk
        _processedLens = 0;
        _encoderFrame = 0;
        _lastProgress = null;         // a new utterance owes nothing to the last
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
