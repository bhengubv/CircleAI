// HeuristicKnowledgeGraphExtractor.cs
//
// (M1) A model-free IKnowledgeGraphExtractor. It reads one conversation turn and
// links the content words it mentions to the memory they came from — a plain,
// general rule applied to every turn, with no per-case hand-wiring and no world
// knowledge. Two memories that mention the same word become connected through it,
// so a later question can reach an older memory across turns.
//
// This is the offline counterpart to LlmKnowledgeGraphExtractor (which uses the
// model): same interface, no network — the graph still fills up when no model is
// available, just more coarsely.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion;

/// <summary>(M1) Model-free extractor: links a turn's content words to their memory.</summary>
public sealed class HeuristicKnowledgeGraphExtractor : IKnowledgeGraphExtractor
{
    private const float DefaultConfidence = 0.6f;

    // Common function words carry no association — drop them so links form on
    // meaningful words (names, places, symptoms, things), not "the" and "my".
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","if","is","are","was","were","be","been","being",
        "to","of","in","on","at","for","with","from","by","as","into","about","over","under",
        "my","your","our","their","his","her","its","this","that","these","those",
        "i","you","he","she","it","we","they","me","him","them","us",
        "do","does","did","done","have","has","had","will","would","can","could","should",
        "shall","may","might","must","not","no","yes","so","than","then","there","here",
        "how","why","what","when","where","who","which","whom",
        "am","get","got","really","just","very","much","many","some","any","all",
    };

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<KnowledgeTriple>> ExtractFromTurnAsync(
        string userText, string assistantText, string? sourceEpisodeId, CancellationToken ct = default)
    {
        // The memory node is identified by the source id when given, else the user's
        // words — so recall can hand back the memory it came from.
        var memory = string.IsNullOrWhiteSpace(sourceEpisodeId) ? userText : sourceEpisodeId!;
        if (string.IsNullOrWhiteSpace(memory))
            return new ValueTask<IReadOnlyList<KnowledgeTriple>>(Array.Empty<KnowledgeTriple>());

        var words = ContentWords(userText + " " + assistantText);
        var triples = new List<KnowledgeTriple>(words.Count * 2);
        var now = DateTimeOffset.UtcNow;
        foreach (var w in words)
        {
            // Two-way so a walk can go word → memory → word → memory across turns.
            triples.Add(new KnowledgeTriple(memory, "mentions", w, sourceEpisodeId, DefaultConfidence, now));
            triples.Add(new KnowledgeTriple(w, "seenin", memory, sourceEpisodeId, DefaultConfidence, now));
        }
        return new ValueTask<IReadOnlyList<KnowledgeTriple>>(triples);
    }

    private static IReadOnlyList<string> ContentWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in text.ToLowerInvariant().Split(
                     new[] { ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '\'', '"', '(', ')', '-', '/' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3 || Stop.Contains(raw)) continue;
            if (seen.Add(raw)) result.Add(raw);
        }
        return result;
    }
}
