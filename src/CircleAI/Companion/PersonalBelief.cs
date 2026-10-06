// PersonalBelief.cs
//
// (M2) Memory integrity, part one: attribution. Every belief carries WHOSE fact it
// is — the user's own (Self), someone else's (Other), or a general fact (World).
// The highest-harm rule in the whole system lives here: a fact about a third party
// ("my mother is diabetic") must never be recorded as a fact about the user.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion;

/// <summary>(M2) Whose fact a belief is about.</summary>
public enum Attribution { Self, Other, World }

/// <summary>(M2) A single attributed belief, with provenance and confidence.</summary>
public sealed record PersonalBelief(
    Attribution Attribution, string Subject, string Predicate, string Object,
    float Confidence, string? Source, DateTimeOffset RecordedAtUtc);

/// <summary>(M2) Turns a sentence into attributed beliefs.</summary>
public interface IBeliefExtractor
{
    ValueTask<IReadOnlyList<PersonalBelief>> ExtractAsync(
        string text, string? source, CancellationToken ct = default);
}

/// <summary>
/// (M2) Model-free belief extractor with attribution discipline. Coarse by design —
/// the model-based extractor is far more precise — but it never collapses "my mother"
/// into "me". Attribution is decided by the sentence's leading subject.
/// </summary>
public sealed class HeuristicBeliefExtractor : IBeliefExtractor
{
    private static readonly HashSet<string> Relations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mother","father","mom","mum","dad","sister","brother","wife","husband","son","daughter",
        "aunt","uncle","grandmother","grandfather","granny","grandpa","gran","nan","friend",
        "colleague","boss","neighbour","neighbor","cousin","partner","girlfriend","boyfriend",
    };
    private static readonly HashSet<string> Possessive = new(StringComparer.OrdinalIgnoreCase)
    { "my","her","his","their","our" };
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","be","been","am","to","of","in","on","at","and","or",
        "but","with","has","have","had","that","this","it","as","for","really","very","just","now",
    };

    public ValueTask<IReadOnlyList<PersonalBelief>> ExtractAsync(
        string text, string? source, CancellationToken ct = default)
    {
        var result = new List<PersonalBelief>();
        if (string.IsNullOrWhiteSpace(text))
            return new ValueTask<IReadOnlyList<PersonalBelief>>(result);

        var tokens = text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')' },
                   StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count == 0)
            return new ValueTask<IReadOnlyList<PersonalBelief>>(result);

        Attribution attribution;
        string subject;
        var skip = new HashSet<int>();   // subject tokens, excluded from the object

        if (tokens.Count >= 2 && Possessive.Contains(tokens[0]) && Relations.Contains(tokens[1]))
        {
            // "my mother ..." → someone else
            attribution = Attribution.Other; subject = tokens[1]; skip.Add(0); skip.Add(1);
        }
        else if (Relations.Contains(tokens[0]))
        {
            attribution = Attribution.Other; subject = tokens[0]; skip.Add(0);
        }
        else if (tokens[0] is "i" or "i'm" or "im" or "me" ||
                 tokens[0].Equals("my", StringComparison.OrdinalIgnoreCase))
        {
            // "I ..." or "my <non-relation> ..." → the user
            attribution = Attribution.Self; subject = "user"; skip.Add(0);
        }
        else
        {
            attribution = Attribution.World; subject = tokens[0];
        }

        var obj = string.Join(' ', tokens.Where((t, i) =>
            !skip.Contains(i) && t.Length >= 3 && !Stop.Contains(t) && !Relations.Contains(t)));
        if (string.IsNullOrWhiteSpace(obj))
            return new ValueTask<IReadOnlyList<PersonalBelief>>(result);

        result.Add(new PersonalBelief(attribution, subject, "isAbout", obj, 0.6f, source, DateTimeOffset.UtcNow));
        return new ValueTask<IReadOnlyList<PersonalBelief>>(result);
    }
}
