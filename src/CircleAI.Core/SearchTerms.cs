// SearchTerms.cs
//
// The words in a question that are worth searching for.
//
// ONE OWNER FOR A LIST THAT WAS ABOUT TO HAVE TWO. The skill store learned, on
// a P30, that OR-matching every word of a question against a corpus turns "no
// match" into "junk match": "Name three colours" hit 1,082 of 1,378 skills
// because "name" is everywhere, and the phone was handed web-design skills for
// a question about colours. It grew a private stopword list to stop that.
//
// Then the SAME failure turned up in the memory store, from the other side:
// a lone fact - "Never deploy with -t:Install..." - was offered for "What is
// the capital of France" and "What is 12 times 8", because its text shares the
// words "the" and "time(s)" with them, and the atom store's term splitter
// filtered nothing shorter than two letters. Measured 2026-09-14; the answer
// rambled and the user reported accuracy as very low.
//
// Two stores, one problem, one list. It lives here, in the assembly both
// already reference, rather than being copied - a copied stopword list is a
// second owner that drifts, which is the exact defect this codebase keeps
// finding. It does not do linguistics: it exists to stop a common word
// standing in for a match, and dropping a genuine word costs one term of an OR.

namespace CircleAI.Core;

/// <summary>Turning a question into the terms worth matching on.</summary>
public static class SearchTerms
{
    /// <summary>
    /// English function words that carry no subject and must not stand in for a
    /// match.
    /// </summary>
    /// <remarks>
    /// SMALL AND ENGLISH-ONLY ON PURPOSE. A query made only of these matches
    /// nothing, deliberately - the caller's no-match path is better than a match
    /// on "do you". It is not a linguistics project; it is the words that were
    /// actually seen causing spurious matches on a phone, plus their obvious
    /// neighbours.
    /// </remarks>
    public static readonly IReadOnlySet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "by", "can", "could",
        "did", "do", "does", "for", "from", "get", "give", "had", "has", "have",
        "how", "i", "if", "in", "is", "it", "its", "make", "may", "me", "my", "of",
        "on", "or", "please", "should", "show", "so", "some", "tell", "that", "the",
        "their", "them", "then", "there", "these", "they", "this", "to", "us",
        "was", "we", "were", "what", "when", "where", "which", "who", "why",
        "will", "with", "would", "you", "your",
    };

    /// <summary>The characters a question is split on before matching.</summary>
    public static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '*', '\''];

    /// <summary>
    /// The searchable words of <paramref name="query"/>: split, trimmed, longer
    /// than one letter, and not a stopword.
    /// </summary>
    /// <remarks>
    /// Order is preserved and duplicates are kept, because a ranker downstream
    /// may weight repetition; de-duplicating is the caller's business if it
    /// wants to. Capped by <paramref name="max"/> so one pasted paragraph cannot
    /// build a hundred-term OR.
    /// </remarks>
    public static List<string> Significant(string? query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        return [.. query
            .Split(Separators, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 1 && !Stopwords.Contains(t))
            .Take(max)];
    }
}
