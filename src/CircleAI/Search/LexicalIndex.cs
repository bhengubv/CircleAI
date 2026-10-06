// LexicalIndex.cs
//
// Search across everything, on a phone with nothing installed.
//
// THE REGISTER SAID "UNBUILT, NOT UNWIRED" AND IT WAS RIGHT. CircleAI.Search
// held primitives - a tokeniser, a term-frequency helper, SIMD vector maths -
// and no index, no store and no query API. There was nothing to wire a screen
// to. CircleAI.Embeddings.TextEmbedder is real and MNN-backed and has never had
// a model to load, and no embedding model is catalogued, so the semantic path
// cannot run on any handset today.
//
// SO THIS IS THE PATH THAT NEEDS NO MODEL. Lexical search is arithmetic: split
// text into words, count them, rank with BM25. It works offline, on a handset
// with nothing downloaded, in the first second after install - the same argument
// that makes the procedural music bed worth having.
//
// IT IS NOT SEMANTIC AND MUST NOT BE SOLD AS SUCH. It cannot know that "car"
// and "vehicle" are the same thing, and when an embedding model is finally
// catalogued the two belong side by side: lexical is exact and cheap, semantic
// is fuzzy and expensive, and the honest ranking uses both. Until then this is
// what actually runs.
//
// ONE INDEX OVER MANY SOURCES, which is what "across everything" means. A
// memory episode, a transcript line, a document and a note are all just text
// with an origin, so the index carries a Source rather than one per store -
// otherwise every caller merges result lists by hand and each does it
// differently.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CircleAI.Search;

/// <summary>Where a searchable piece of text came from.</summary>
/// <param name="Kind">"memory", "transcript", "document" - the caller's word.</param>
/// <param name="Id">The caller's own identifier, so it can find the thing again.</param>
public readonly record struct SearchOrigin(string Kind, string Id);

/// <summary>One thing the index can find.</summary>
/// <param name="Origin">Where it came from.</param>
/// <param name="Text">The words.</param>
/// <param name="When">
/// When it happened, where that is known. Used only to break ties.
/// </param>
public sealed record SearchDocument(SearchOrigin Origin, string Text, DateTimeOffset? When = null);

/// <summary>A match, and why it matched.</summary>
/// <param name="Document">What was found.</param>
/// <param name="Score">BM25 relevance. Comparable within one query only.</param>
/// <param name="Matched">
/// Which query terms this document actually contained — so a screen can say why
/// a result is there, and a person can see that their rarest word was ignored.
/// </param>
public sealed record SearchHit(SearchDocument Document, double Score, IReadOnlyList<string> Matched);

/// <summary>A BM25 index over text from any number of sources.</summary>
/// <remarks>
/// IN MEMORY AND REBUILT, NOT PERSISTED. The corpus this serves is a phone's own
/// memory, transcripts and documents - thousands of short texts, not millions -
/// and an on-disk inverted index would be a second store to keep in step with
/// the first, which is the failure mode this codebase keeps finding. Rebuilding
/// from the real stores means the index can never disagree with them.
/// </remarks>
public sealed class LexicalIndex
{
    private readonly List<SearchDocument> _documents = [];
    private readonly List<IReadOnlyList<string>> _tokens = [];

    /// <summary>How many documents contain a given term.</summary>
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _totalTokens;

    /// <summary>How many documents are indexed.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _documents.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Add one document. Returns false when there is nothing to index.</summary>
    public bool Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.Text)) return false;

        var tokens = SearchTokenisation.Tokenise(document.Text);
        if (tokens.Count == 0) return false;

        _lock.EnterWriteLock();
        try
        {
            _documents.Add(document);
            _tokens.Add(tokens);
            _totalTokens += tokens.Count;

            // DISTINCT PER DOCUMENT. Document frequency counts documents, not
            // occurrences - a word used nine times in one document is still one
            // document, and counting it nine times would make a common word look
            // rare and invert the ranking.
            foreach (var term in tokens.Distinct(StringComparer.Ordinal))
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;

            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Add many. Returns how many were indexed.</summary>
    public int AddRange(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return documents.Count(Add);
    }

    /// <summary>Forget everything.</summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _documents.Clear();
            _tokens.Clear();
            _documentFrequency.Clear();
            _totalTokens = 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// The best matches for a query, most relevant first.
    /// </summary>
    /// <param name="query">What was typed. Tokenised the same way documents are.</param>
    /// <param name="take">How many to return.</param>
    /// <param name="kinds">
    /// Limit to these origin kinds, or null for all of them. "Search my
    /// transcripts" and "search everything" are the same call.
    /// </param>
    /// <remarks>
    /// A DOCUMENT MATCHING NOTHING IS NOT A RESULT. Everything scoring zero is
    /// dropped rather than returned at the bottom: a list that always has ten
    /// rows teaches people that the bottom rows are noise, and then they stop
    /// reading the top ones.
    /// </remarks>
    public IReadOnlyList<SearchHit> Search(
        string? query, int take = 10, IReadOnlyCollection<string>? kinds = null)
    {
        if (string.IsNullOrWhiteSpace(query) || take <= 0) return [];

        var terms = SearchTokenisation.Tokenise(query);
        if (terms.Count == 0) return [];

        _lock.EnterReadLock();
        try
        {
            if (_documents.Count == 0) return [];

            var average = (double)_totalTokens / _documents.Count;
            var hits = new List<SearchHit>();

            for (var i = 0; i < _documents.Count; i++)
            {
                var document = _documents[i];

                if (kinds is { Count: > 0 } &&
                    !kinds.Contains(document.Origin.Kind, StringComparer.OrdinalIgnoreCase))
                    continue;

                var score = Bm25.ScoreDocument(
                    terms, _tokens[i], _documentFrequency, _documents.Count, average);

                if (score <= 0) continue;

                var present = _tokens[i].ToHashSet(StringComparer.Ordinal);
                var matched = terms.Distinct(StringComparer.Ordinal)
                                   .Where(present.Contains)
                                   .ToList();

                hits.Add(new SearchHit(document, score, matched));
            }

            // Newest first when relevance ties, because on a phone the tie is
            // usually between two records of the same recurring thing and the
            // recent one is the one being asked about.
            return [.. hits
                .OrderByDescending(h => h.Score)
                .ThenByDescending(h => h.Document.When ?? DateTimeOffset.MinValue)
                .Take(take)];
        }
        finally { _lock.ExitReadLock(); }
    }
}
