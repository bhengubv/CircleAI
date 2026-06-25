// SearchPrimitives.cs
//
// (3.3.0) Top-up: shared search-relevance helpers (BM25-style scoring,
// query tokenisation) over the existing Search package surface.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Search;

public static class SearchTokenisation
{
    public static IReadOnlyList<string> Tokenise(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return text.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')', '[', ']', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
    }
}

public static class SearchScoring
{
    public static double TermFrequency(string term, IReadOnlyList<string> docTokens)
    {
        if (docTokens is null) throw new ArgumentNullException(nameof(docTokens));
        if (docTokens.Count == 0) return 0;
        var c = 0;
        foreach (var t in docTokens) if (string.Equals(t, term, StringComparison.Ordinal)) c++;
        return (double)c / docTokens.Count;
    }

    public static double SimpleRelevance(IReadOnlyList<string> queryTokens, IReadOnlyList<string> docTokens)
    {
        ArgumentNullException.ThrowIfNull(queryTokens);
        ArgumentNullException.ThrowIfNull(docTokens);
        if (queryTokens.Count == 0 || docTokens.Count == 0) return 0;
        double score = 0;
        foreach (var q in queryTokens) score += TermFrequency(q, docTokens);
        return score;
    }
}
