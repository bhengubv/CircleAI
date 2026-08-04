#nullable enable

// SentencePieceTokenizer.cs
//
// Turns typed text into the token ids a keyword spotter matches on, which is what
// makes "set your own wake phrase" possible at all.
//
// WE WERE ALREADY SHIPPING THE FILE AND COULD NOT READ IT. Every KWS bundle
// carries bpe.model — 245 KB of it — and nothing in the codebase opened it, so
// the headline property of this whole approach ("keywords are TEXT, not a trained
// class") stopped at the edge of a text file a developer edits by hand. A person
// could not name their own phrase, which is the difference between a wake word
// and A wake word.
//
// NO DEPENDENCY. The model is a protobuf holding one message per piece — the
// string, a float score and a type — and the scores are negative, meaning this is
// a UNIGRAM model rather than merge-rank BPE. Encoding is therefore a Viterbi
// pass picking the segmentation with the best total score, which is about forty
// lines. Taking a native sentencepiece binding for that would add a platform
// matrix to an Android app to avoid writing a dynamic program.
//
// VERIFIED AGAINST THE REAL THING rather than assumed: SentencePieceTokenizerTests
// pins the output against Google's own sentencepiece on the shipped model.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CircleAI.Voice;

/// <summary>How a piece is used, mirroring sentencepiece's own enum.</summary>
public enum SentencePieceKind
{
    Normal = 1,
    Unknown = 2,
    Control = 3,
    UserDefined = 4,
    Byte = 5,
    Unused = 6,
}

/// <summary>One entry of a sentencepiece vocabulary.</summary>
public sealed record SentencePiece(string Piece, float Score, SentencePieceKind Kind, int Id);

/// <summary>
/// Reads a sentencepiece model and segments text into its pieces.
/// </summary>
public sealed class SentencePieceTokenizer
{
    /// <summary>The word-boundary marker sentencepiece uses. U+2581, not an underscore.</summary>
    public const char WordStart = '▁';

    private readonly Dictionary<string, SentencePiece> _byPiece;
    private readonly float _unknownPenalty;

    /// <summary>Every piece, in id order.</summary>
    public IReadOnlyList<SentencePiece> Pieces { get; }

    /// <summary>
    /// True when the vocabulary is upper-case, so input must be folded to match.
    /// </summary>
    /// <remarks>
    /// DETECTED, NOT CONFIGURED. This model was trained on GigaSpeech transcripts,
    /// which are upper-case, so its pieces are "▁THE" and "IGHT" — feed it
    /// "circle" and every piece misses and the phrase silently becomes unknowns.
    /// Asking the caller to know that is asking them to know a property of a file
    /// they did not make, so it is read off the vocabulary instead.
    /// </remarks>
    public bool VocabularyIsUpperCase { get; }

    public SentencePieceTokenizer(string modelPath)
        : this(File.ReadAllBytes(modelPath)) { }

    public SentencePieceTokenizer(ReadOnlySpan<byte> model)
    {
        var pieces = ReadPieces(model);
        Pieces = pieces;

        _byPiece = new Dictionary<string, SentencePiece>(StringComparer.Ordinal);
        foreach (var p in pieces) _byPiece.TryAdd(p.Piece, p);

        // Worse than any real piece, so a segmentation covering the text with
        // known pieces always wins over one that gives up.
        _unknownPenalty = pieces.Count > 0 ? pieces.Min(p => p.Score) - 10f : -100f;

        var lower = 0;
        var upper = 0;
        foreach (var p in pieces)
        {
            if (p.Kind != SentencePieceKind.Normal) continue;
            foreach (var c in p.Piece)
            {
                if (char.IsLower(c)) lower++;
                else if (char.IsUpper(c)) upper++;
            }
        }
        VocabularyIsUpperCase = upper > lower * 8;
    }

    /// <summary>Segments text into pieces, best-scoring segmentation first.</summary>
    /// <param name="text">Plain text, e.g. "hey circle".</param>
    /// <returns>The pieces, in order. Unknown spans come back as single characters.</returns>
    public IReadOnlyList<string> Encode(string text)
    {
        var norm = Normalise(text);
        if (norm.Length == 0) return Array.Empty<string>();

        // Viterbi over the string: best[i] is the score of the best segmentation
        // of the first i characters, and back[i] the length of the piece that ends
        // there. Longest piece bounds the inner loop so this stays linear-ish.
        var n = norm.Length;
        var best = new float[n + 1];
        var back = new int[n + 1];
        for (var i = 1; i <= n; i++) best[i] = float.NegativeInfinity;

        var longest = _byPiece.Count == 0 ? 1 : _byPiece.Keys.Max(k => k.Length);

        for (var end = 1; end <= n; end++)
        {
            for (var len = 1; len <= Math.Min(longest, end); len++)
            {
                var start = end - len;
                if (float.IsNegativeInfinity(best[start])) continue;

                var span = norm.Substring(start, len);
                float score;
                if (_byPiece.TryGetValue(span, out var piece) &&
                    piece.Kind is SentencePieceKind.Normal or SentencePieceKind.UserDefined)
                    score = piece.Score;
                else if (len == 1)
                    score = _unknownPenalty;        // a single character always has a way through
                else
                    continue;

                var total = best[start] + score;
                if (total > best[end]) { best[end] = total; back[end] = len; }
            }
        }

        var outp = new List<string>();
        for (var at = n; at > 0;)
        {
            var len = Math.Max(1, back[at]);
            outp.Add(norm.Substring(at - len, len));
            at -= len;
        }
        outp.Reverse();
        return outp;
    }

    /// <summary>True when every piece of the text is in the vocabulary.</summary>
    /// <remarks>
    /// The check a wake-phrase UI needs. A phrase containing a piece the model has
    /// never seen cannot ever be matched, and failing loudly at the moment someone
    /// types it beats a wake word that simply never works.
    /// </remarks>
    public bool CanRepresent(string text, out IReadOnlyList<string> unknown)
    {
        var bad = Encode(text).Where(p => !_byPiece.ContainsKey(p)).Distinct().ToList();
        unknown = bad;
        return bad.Count == 0;
    }

    /// <summary>sentencepiece's normalisation: spaces become the marker, and one is prefixed.</summary>
    private string Normalise(string text)
    {
        var s = text.Trim();
        if (VocabularyIsUpperCase) s = s.ToUpperInvariant();

        var sb = new StringBuilder(s.Length + 1);
        sb.Append(WordStart);
        var lastWasSpace = true;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(WordStart);
                lastWasSpace = true;
            }
            else { sb.Append(c); lastWasSpace = false; }
        }
        return sb.ToString();
    }

    // ── the smallest protobuf reader that does this job ─────────────────────
    //
    // ModelProto { repeated SentencePiece pieces = 1; ... }
    // SentencePiece { string piece = 1; float score = 2; Type type = 3; }
    //
    // Unknown fields are skipped by wire type, so a model carrying a trainer spec
    // or a normaliser blob — which every real one does — reads fine.

    private static List<SentencePiece> ReadPieces(ReadOnlySpan<byte> data)
    {
        var pieces = new List<SentencePiece>();
        var i = 0;
        while (i < data.Length)
        {
            if (!TryReadVarint(data, ref i, out var key)) break;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            if (field == 1 && wire == 2)
            {
                if (!TryReadVarint(data, ref i, out var len)) break;
                if (i + (int)len > data.Length) break;
                pieces.Add(ReadPiece(data.Slice(i, (int)len), pieces.Count));
                i += (int)len;
            }
            else if (!SkipField(data, ref i, wire)) break;
        }
        return pieces;
    }

    private static SentencePiece ReadPiece(ReadOnlySpan<byte> data, int id)
    {
        var text = string.Empty;
        var score = 0f;
        var kind = SentencePieceKind.Normal;
        var i = 0;

        while (i < data.Length)
        {
            if (!TryReadVarint(data, ref i, out var key)) break;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            switch (field, wire)
            {
                case (1, 2):
                    if (!TryReadVarint(data, ref i, out var len) || i + (int)len > data.Length) return
                        new SentencePiece(text, score, kind, id);
                    text = Encoding.UTF8.GetString(data.Slice(i, (int)len));
                    i += (int)len;
                    break;
                case (2, 5):
                    if (i + 4 > data.Length) return new SentencePiece(text, score, kind, id);
                    score = BitConverter.ToSingle(data.Slice(i, 4));
                    i += 4;
                    break;
                case (3, 0):
                    if (!TryReadVarint(data, ref i, out var t)) return new SentencePiece(text, score, kind, id);
                    kind = (SentencePieceKind)(int)t;
                    break;
                default:
                    if (!SkipField(data, ref i, wire)) return new SentencePiece(text, score, kind, id);
                    break;
            }
        }
        return new SentencePiece(text, score, kind, id);
    }

    private static bool SkipField(ReadOnlySpan<byte> data, ref int i, int wire)
    {
        switch (wire)
        {
            case 0: return TryReadVarint(data, ref i, out _);
            case 1: i += 8; return i <= data.Length;
            case 2:
                if (!TryReadVarint(data, ref i, out var len)) return false;
                i += (int)len; return i <= data.Length;
            case 5: i += 4; return i <= data.Length;
            default: return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (i < data.Length && shift < 64)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
