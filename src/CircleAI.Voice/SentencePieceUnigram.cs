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
                var raw = s.Substring(start, i - start);
                foreach (var b in Encoding.UTF8.GetBytes(raw))
                {
                    if (_ids.TryGetValue($"<0x{b:X2}>", out var byteId)) reversed.Add(byteId);
                }
            }
            i = start;
        }

        reversed.Reverse();
        return reversed;
    }
}
