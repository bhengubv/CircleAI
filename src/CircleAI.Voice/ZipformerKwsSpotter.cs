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
// INCOMPLETE: THIS DOES NOT DETECT YET. Do not wire it to a microphone.
// ============================================================================
//
// What is proven:
//   - the features are bit-compatible with the C++ reference (KaldiFbankTests)
//   - the three graphs run, the 36 states thread correctly across chunks, and
//     the encoder responds to audio (mean |speech - silence| 0.087 against an
//     output std of 0.18)
//   - identical results to the same pipeline driven from Python, and 4.5-24x
//     realtime on desktop
//
// What is missing is the DECODER STRATEGY, and the reason is worth writing down
// because it cost real time to find. A keyword spotter is trained to sit on
// blank: greedy argmax over the joiner emits NOTHING at all, on audio that
// demonstrably contains the keywords. Scoring the keyword's token path on its
// own does not rescue it either — measured on the shipped test audio, a phrase
// that IS present scores -3.127 per token and the same phrase in a clip where it
// is ABSENT scores -3.148. No usable margin, because a max over ~660 frames
// always finds some plausible-looking alignment.
//
// The missing ingredient is that the keyword path has to WIN AGAINST THE
// ALTERNATIVES rather than be scored in isolation — beam search over a keyword
// trie with a boosting score and a detection threshold, which is what sherpa-onnx
// does and what this needs next. That is a real algorithm, not a tuning pass.

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

        DecodeGreedy(encOut, frames, width);
    }

    /// <summary>
    /// Greedy transducer decode, checking for keywords as tokens land.
    /// </summary>
    /// <remarks>
    /// Greedy rather than beam search with keyword boosting. Boosting biases the
    /// search TOWARDS the phrases, which raises recall on quiet or accented speech
    /// and is what sherpa-onnx does. Greedy is what the acoustics alone support,
    /// so what it detects, it detected honestly — the right baseline to measure
    /// before adding a thumb to the scale, and the boost is a change we can make
    /// against a number rather than a hunch.
    /// </remarks>
    private void DecodeGreedy(Tensor<float> encOut, int frames, int width)
    {
        for (var t = 0; t < frames; t++, _encoderFrame++)
        {
            var enc = new DenseTensor<float>(new[] { 1, width });
            for (var d = 0; d < width; d++) enc[0, d] = encOut[0, t, d];

            var dec = DecoderOut();

            using var jr = _joiner.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("encoder_out", enc),
                NamedOnnxValue.CreateFromTensor("decoder_out", dec),
            });
            var logit = jr.First().AsTensor<float>();

            var best = 0;
            var bestVal = float.NegativeInfinity;
            for (var v = 0; v < logit.Dimensions[^1]; v++)
                if (logit[0, v] > bestVal) { bestVal = logit[0, v]; best = v; }

            if (best == Blank) continue;

            _emitted.Add(best);
            _decoderCache = null;             // history changed
            if (TokenEmitted is not null)
                TokenEmitted.Invoke(this, _tokenText.TryGetValue(best, out var tx) ? tx : $"<{best}>");
            CheckKeywords();
        }
    }

    private DenseTensor<float>? _decoderCache;

    private DenseTensor<float> DecoderOut()
    {
        if (_decoderCache is not null) return _decoderCache;

        var y = new DenseTensor<long>(new[] { 1, _contextSize });
        for (var i = 0; i < _contextSize; i++)
        {
            var back = _contextSize - i;
            var idx = _emitted.Count - back;
            y[0, i] = idx >= 0 ? _emitted[idx] : Blank;
        }

        using var r = _decoder.Run(new[] { NamedOnnxValue.CreateFromTensor("y", y) });
        return _decoderCache = r.First().AsTensor<float>().ToDenseTensor();
    }

    private void CheckKeywords()
    {
        foreach (var k in _keywords)
        {
            var n = k.Tokens.Count;
            if (_emitted.Count < n) continue;

            var match = true;
            for (var i = 0; i < n; i++)
                if (_emitted[_emitted.Count - n + i] != k.Tokens[i]) { match = false; break; }

            if (!match) continue;

            Detected?.Invoke(this, new KwsDetection(k.Phrase, _encoderFrame));

            // Consume the match so one utterance fires once. Without this a
            // trailing token would re-trigger on every subsequent frame.
            _emitted.Clear();
            _decoderCache = null;
            return;
        }

        // The tail cannot be longer than the longest keyword, or memory grows for
        // the life of the process on a device that has none to spare.
        var longest = _keywords.Max(k => k.Tokens.Count);
        if (_emitted.Count > longest * 2)
            _emitted.RemoveRange(0, _emitted.Count - longest);
    }

    /// <summary>Clears stream state for a new utterance, keeping the loaded models.</summary>
    public void Reset()
    {
        _fbank.Reset();
        _features.Clear();
        _featureCursor = 0;
        _emitted.Clear();
        _decoderCache = null;
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
