using System.Text;
using System.Text.Json;

namespace CircleAI.Voice;

/// <summary>
/// SentencePiece unigram tokeniser — Viterbi over the piece lattice, with
/// byte fallback.
/// </summary>
/// <remarks>
/// <para>
/// Pocket-TTS ships its tokeniser as a pair of plain JSON files, <c>vocab.json</c>
/// (piece → id) and <c>token_scores.json</c> (piece → log probability), 4000
/// pieces between them. That is a unigram SentencePiece model with the training
/// apparatus stripped off, and encoding it is a shortest-path problem, not a
/// greedy scan: pick the segmentation of the string whose pieces sum to the
/// highest total score.
/// </para>
/// <para>
/// GREEDY LONGEST-MATCH IS THE WRONG ALGORITHM HERE, and it fails quietly —
/// it produces ids, the model speaks, and the words are subtly wrong. Unigram
/// scores are not monotone in piece length: a long piece can score worse than
/// the two short pieces covering the same span, which is exactly why the model
/// was trained with the scores in the first place. So this is a real Viterbi.
/// </para>
/// <para>
/// BYTE FALLBACK IS NOT OPTIONAL. The vocabulary carries <c>&lt;0x00&gt;</c>
/// through <c>&lt;0xFF&gt;</c> precisely so that a character no piece covers
/// still produces sound rather than vanishing. Dropping unknown characters is
/// the failure mode that made the 42 MMS voices speak fluent nonsense: the
/// audio stays plausible and merely says less than it was given.
/// </para>
/// <para>
/// The leading <c>▁</c> (U+2581) and the space→<c>▁</c> substitution are
/// SentencePiece's own convention, not a detail of this model — without them
/// every word after the first is treated as a continuation and the prosody is
/// wrong even when the phonemes are right.
/// </para>
/// </remarks>
public sealed class SentencePieceUnigram
{
    private readonly Dictionary<string, int> _ids;
    private readonly Dictionary<string, float> _scores;
    private readonly int _maxPieceLength;

    // What a character no piece covers becomes when the model has no byte pieces -
    // XLM-R's has none, so without this such a character would vanish.
    private int? _unknownId;

    /// <summary>Cost charged for falling back to raw bytes.</summary>
    /// <remarks>
    /// Any finite penalty works, because fallback only ever competes with "no
    /// path at all" — a character no piece covers has no alternative. It has to
    /// be worse than a real piece so the lattice never prefers it where a piece
    /// exists, and finite so a path always exists.
    /// </remarks>
    private const float FallbackPenalty = 10.0f;

    private SentencePieceUnigram(Dictionary<string, int> ids, Dictionary<string, float> scores)
    {
        _ids = ids;
        _scores = scores;
        _maxPieceLength = ids.Keys.Max(k => k.Length);
    }

    /// <summary>Number of pieces in the vocabulary.</summary>
    public int Count => _ids.Count;

    /// <summary>
    /// Load from a bundle's <c>vocab.json</c> and <c>token_scores.json</c>.
    /// </summary>
    public static SentencePieceUnigram Load(string vocabJsonPath, string scoresJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scoresJsonPath);

        var ids = JsonSerializer.Deserialize<Dictionary<string, int>>(
                      File.ReadAllText(vocabJsonPath, Encoding.UTF8))
                  ?? throw new InvalidDataException($"'{vocabJsonPath}' is not a piece→id map.");
        var scores = JsonSerializer.Deserialize<Dictionary<string, float>>(
                         File.ReadAllText(scoresJsonPath, Encoding.UTF8))
                     ?? throw new InvalidDataException($"'{scoresJsonPath}' is not a piece→score map.");

        if (ids.Count == 0)
            throw new InvalidDataException($"'{vocabJsonPath}' is empty.");

        return new SentencePieceUnigram(ids, scores);
    }

    /// <summary>Encode text to token ids.</summary>
    /// <summary>
    /// Reads a SentencePiece <c>.model</c> - the protobuf the trainer writes - keeping each
    /// normal, user-defined and byte piece with its score at its own index (OPEN-GAPS A7).
    /// </summary>
    /// <remarks>
    /// THE FILE A MODEL'S LISTING PINS. XLM-R's <c>tokenizer.json</c> is nine megabytes Hugging
    /// Face keeps outside LFS, so the catalogue could only hash it by downloading it; the
    /// <c>.model</c> beside it carries a published SHA-256. Control pieces never match text and
    /// stay out of the lattice; the unknown piece is what an uncovered character becomes.
    /// </remarks>
    public static SentencePieceUnigram FromModelProto(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        int? unknown = null;
        var index = 0;

        for (var pos = 0; pos < bytes.Length;)
        {
            var (field, wire) = Tag(bytes, ref pos);
            if (field == 1 && wire == 2)                       // ModelProto.pieces
            {
                var end = (int)Varint(bytes, ref pos) + pos;
                string? piece = null;
                float score = 0f;
                var type = 1L;                                  // NORMAL when unstated
                while (pos < end)
                {
                    var (f, w) = Tag(bytes, ref pos);
                    if (f == 1 && w == 2) { var n = (int)Varint(bytes, ref pos); piece = Encoding.UTF8.GetString(bytes, pos, n); pos += n; }
                    else if (f == 2 && w == 5) { score = BitConverter.ToSingle(bytes, pos); pos += 4; }
                    else if (f == 3 && w == 0) type = Varint(bytes, ref pos);
                    else Skip(bytes, ref pos, w);
                }
                pos = end;

                // NORMAL, USER_DEFINED and BYTE go in the lattice; UNKNOWN is remembered.
                if (piece is not null && type is 1 or 4 or 6) { ids.TryAdd(piece, index); scores[piece] = score; }
                else if (type == 2) unknown = index;
                index++;
            }
            else Skip(bytes, ref pos, wire);
        }

        if (ids.Count == 0) throw new InvalidDataException($"'{path}' holds no SentencePiece pieces.");
        return new SentencePieceUnigram(ids, scores) { _unknownId = unknown };
    }

    private static (int Field, int Wire) Tag(byte[] b, ref int pos)
    {
        var key = Varint(b, ref pos);
        return ((int)(key >> 3), (int)(key & 7));
    }

    private static long Varint(byte[] b, ref int pos)
    {
        long value = 0;
        for (var shift = 0; pos < b.Length; shift += 7)
        {
            var x = b[pos++];
            value |= (long)(x & 0x7F) << shift;
            if (x < 0x80) return value;
        }
        throw new InvalidDataException("A varint ran past the end of the SentencePiece model.");
    }

    private static void Skip(byte[] b, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: Varint(b, ref pos); break;
            case 1: pos += 8; break;
            case 2:
            {
                // NOT `pos += Varint(b, ref pos)`: that reads pos before the call moves it past
                // the length, and lands the length's own bytes short of the field's end.
                var length = (int)Varint(b, ref pos);
                pos += length;
                break;
            }
            case 5: pos += 4; break;
            default: throw new InvalidDataException($"Protobuf wire type {wire} in a SentencePiece model.");
        }
    }

    public IReadOnlyList<int> Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        // SentencePiece's own normalisation: NFKC, then spaces become U+2581,
        // with one prepended so the first word is marked as word-initial too.
        var s = "▁" + text.Normalize(NormalizationForm.FormKC).Replace(' ', '▁');
        var n = s.Length;

        const float Unreachable = -1e18f;
        var best = new float[n + 1];
        var fromIndex = new int[n + 1];
        var piece = new string?[n + 1];
        Array.Fill(best, Unreachable);
        best[0] = 0f;

        for (var i = 0; i < n; i++)
        {
            if (best[i] <= Unreachable / 2) continue;

            var limit = Math.Min(_maxPieceLength, n - i);
            for (var len = 1; len <= limit; len++)
            {
                var candidate = s.Substring(i, len);
                if (!_ids.ContainsKey(candidate)) continue;
                var score = best[i] + (_scores.TryGetValue(candidate, out var sc) ? sc : 0f);
                if (score > best[i + len])
                {
                    best[i + len] = score;
                    fromIndex[i + len] = i;
                    piece[i + len] = candidate;
                }
            }

            // Byte fallback for this ONE character, so no input is ever silent.
            // Surrogate pairs are taken whole — splitting one produces bytes
            // that are not valid UTF-8 for the character the user typed.
            var charLen = char.IsHighSurrogate(s[i]) && i + 1 < n ? 2 : 1;
            var end = i + charLen;
            var fallbackScore = best[i] - FallbackPenalty;
            if (fallbackScore > best[end])
            {
                best[end] = fallbackScore;
                fromIndex[end] = i;
                piece[end] = null;                 // null marks "emit as bytes"
            }
        }

        var reversed = new List<int>(n);
        for (var i = n; i > 0;)
        {
            var start = fromIndex[i];
            var p = piece[i];
            if (p is not null && _ids.TryGetValue(p, out var id))
            {
                reversed.Add(id);
            }
            else
            {
                // BACKWARDS, because this whole list is built backwards.
                //
                // The lattice is walked from the end, so `reversed` accumulates
                // in reverse and is flipped once at the bottom. A multi-byte
                // character appended in forward order therefore comes out
                // byte-reversed: é is UTF-8 C3 A9 and was being emitted A9 C3.
                // Nothing throws — those are real pieces with real ids — so the
                // model simply pronounces a different character, and only for
                // text outside ASCII, which is precisely the African and Asian
                // languages this catalogue exists for.
                var raw = s.Substring(start, i - start);
                var bytes = Encoding.UTF8.GetBytes(raw);
                var spoken = false;
                for (var b = bytes.Length - 1; b >= 0; b--)
                {
                    if (_ids.TryGetValue($"<0x{bytes[b]:X2}>", out var byteId)) { reversed.Add(byteId); spoken = true; }
                }
                if (!spoken && _unknownId is { } unknown) reversed.Add(unknown);
            }
            i = start;
        }

        reversed.Reverse();
        return reversed;
    }
}
