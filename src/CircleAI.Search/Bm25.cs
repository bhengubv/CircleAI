// Bm25.cs
//
// Ranking text by relevance, with no model and no network.
//
// SearchScoring.SimpleRelevance IS NOT BM25, THOUGH THE FILE SAYS "BM25-style".
// It sums a raw term-frequency RATE:
//
//     foreach (var q in queryTokens) score += TermFrequency(q, docTokens);
//     // TermFrequency = occurrences / document length
//
// TWO FAULTS, MEASURED RATHER THAN ASSUMED. The first draft of this comment
// listed three and two of them were wrong: because that TF is a rate, it
// already normalises for length and already saturates. Running both scorers
// over the same documents is what settled it, and it is worth writing down that
// the confident version was the inaccurate one.
//
//   * A WORD IN EVERY DOCUMENT COUNTS AS MUCH AS A RARE ONE. There is no
//     inverse document frequency at all, so searching a transcript for
//     "the meeting" ranks on "the". Measured: over three documents the naive
//     scorer picks the one with the most "the"s; BM25 picks the one containing
//     the rare word. This is the big one.
//
//   * LENGTH IS OVER-NORMALISED, WHICH BURIES THE REAL ANSWER. Dividing by
//     length entirely is b = 1.0, harsher than BM25's 0.75, so a long document
//     that genuinely discusses the subject loses to a short one that merely
//     mentions it. Measured: a 700-word meeting transcript discussing the
//     clinic appointment five times loses to the two-word note "clinic
//     elsewhere" under the naive scorer, and wins under BM25.
//
// What the naive scorer does NOT get wrong, contrary to the first draft here:
// repetition. Ten occurrences in ten words scores the same as one in one - the
// rate saturates trivially. BM25 is slightly LESS saturating (1.23x), not more.
//
// BM25 is forty years old, it is what every lexical search engine actually
// uses, and it is arithmetic - no model, no network, no download.
//
// WHICH IS THE POINT ON THIS PRODUCT. The embedding path needs a model nobody
// has catalogued yet, so semantic search cannot run on any handset today. This
// runs on a phone with nothing installed at all.

using System;
using System.Collections.Generic;

namespace CircleAI.Search;

/// <summary>Okapi BM25 relevance scoring.</summary>
public static class Bm25
{
    /// <summary>
    /// Term-frequency saturation. Higher means repetition keeps counting.
    /// </summary>
    /// <remarks>
    /// 1.2 is the long-standing default. It is NOT what fixes the naive scorer -
    /// that scorer already saturates, being a rate - but it is what keeps BM25
    /// from the opposite error: without it, raw counts would make a long
    /// repetitive document beat a short precise one.
    /// </remarks>
    public const double K1 = 1.2;

    /// <summary>
    /// How much document length is normalised away. 0 ignores length, 1 divides
    /// it out entirely.
    /// </summary>
    /// <remarks>
    /// 0.75 is the standard middle, and the middle is the whole point here.
    /// SimpleRelevance divides by length outright, which is b = 1.0 - and
    /// measured, that is what makes a two-word note beat a seven-hundred-word
    /// transcript that actually discusses the subject. None of it, b = 0, would
    /// let length alone win instead.
    /// </remarks>
    public const double B = 0.75;

    /// <summary>
    /// How much a term narrows the search: high for rare, near zero for common.
    /// </summary>
    /// <param name="documentsWithTerm">How many documents contain it.</param>
    /// <param name="totalDocuments">How many documents there are.</param>
    /// <remarks>
    /// THE PIECE THE NAIVE SCORER HAD NO EQUIVALENT OF. Without it "the meeting"
    /// ranks on "the", because the commonest word in the corpus contributes as
    /// much as the one that identifies the answer.
    /// <para>
    /// The +1 inside the logarithm is the standard guard: the classic form goes
    /// NEGATIVE for a term appearing in more than half the corpus, which would
    /// let a common word actively push a document DOWN the ranking - worse than
    /// ignoring it. With the +1 a term in every document contributes ~0, which
    /// is the honest answer: it tells you nothing.
    /// </para>
    /// </remarks>
    public static double InverseDocumentFrequency(int documentsWithTerm, int totalDocuments)
    {
        if (totalDocuments <= 0) return 0;

        var n = Math.Clamp(documentsWithTerm, 0, totalDocuments);
        return Math.Log(((totalDocuments - n + 0.5) / (n + 0.5)) + 1.0);
    }

    /// <summary>
    /// One term's contribution to a document's score.
    /// </summary>
    /// <param name="termCount">Times the term occurs in this document.</param>
    /// <param name="documentLength">Tokens in this document.</param>
    /// <param name="averageLength">Mean tokens per document in the corpus.</param>
    /// <param name="idf">The term's <see cref="InverseDocumentFrequency"/>.</param>
    public static double Score(
        int termCount, int documentLength, double averageLength, double idf)
    {
        if (termCount <= 0) return 0;

        // A corpus of identical-length documents, or one document: length
        // normalisation has nothing to say, so it must not divide by zero.
        var norm = averageLength > 0 ? documentLength / averageLength : 1.0;

        var numerator = termCount * (K1 + 1);
        var denominator = termCount + (K1 * (1 - B + (B * norm)));

        return denominator <= 0 ? 0 : idf * (numerator / denominator);
    }

    /// <summary>
    /// Score one document against a query, given corpus statistics.
    /// </summary>
    /// <param name="queryTerms">Tokenised query. Repeats count once.</param>
    /// <param name="documentTerms">Tokenised document.</param>
    /// <param name="documentsWithTerm">
    /// For each query term, how many documents in the corpus contain it.
    /// </param>
    /// <param name="totalDocuments">Documents in the corpus.</param>
    /// <param name="averageLength">Mean document length, in tokens.</param>
    public static double ScoreDocument(
        IReadOnlyCollection<string> queryTerms,
        IReadOnlyList<string> documentTerms,
        IReadOnlyDictionary<string, int> documentsWithTerm,
        int totalDocuments,
        double averageLength)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(documentTerms);
        ArgumentNullException.ThrowIfNull(documentsWithTerm);

        if (queryTerms.Count == 0 || documentTerms.Count == 0) return 0;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in documentTerms)
            counts[t] = counts.GetValueOrDefault(t) + 1;

        double total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in queryTerms)
        {
            // A QUERY TERM COUNTS ONCE HOWEVER OFTEN IT IS TYPED. "invoice
            // invoice invoice" is one person being emphatic, not a request to
            // triple that term's weight.
            if (!seen.Add(term)) continue;
            if (!counts.TryGetValue(term, out var count)) continue;

            var idf = InverseDocumentFrequency(
                documentsWithTerm.GetValueOrDefault(term), totalDocuments);

            total += Score(count, documentTerms.Count, averageLength, idf);
        }

        return total;
    }
}
