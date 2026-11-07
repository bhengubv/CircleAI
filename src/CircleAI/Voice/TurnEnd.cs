// TurnEnd.cs
//
// WHETHER SOMEBODY HAS FINISHED IS A QUESTION ABOUT THEIR WORDS AS WELL AS THEIR VOLUME.
//
// A gate hears quiet. It cannot tell the quiet after "What is the capital of Peru" from the quiet
// after "What is the", and on the P30 on 2026-11-07 a pause of a second lost the end of five
// questions out of 122. So the words get a say: a sentence that stops on a word that cannot end one
// is given longer, and the quiet that ends a sentence that can end is left as it was.

using System;
using System.Collections.Generic;

namespace CircleAI.Voice;

/// <summary>What the words so far say about whether the person has finished.</summary>
public static class TurnEnd
{
    // Words a sentence does not stop on: what they lead to has not been said yet.
    private static readonly HashSet<string> LeadOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "to", "in", "on", "at", "for", "with", "from", "by", "about", "into", "than",
        "and", "or", "but", "so", "because", "if", "when", "while", "that", "which", "who", "whose", "where",
        "my", "your", "his", "her", "its", "our", "their", "this", "these", "those",
        "is", "are", "was", "were", "am", "be", "been", "do", "does", "did", "can", "could", "will", "would", "should",
        "what", "what's", "how", "why", "um", "uh", "er", "erm", "hmm", "like", "very", "really", "just",
    };

    /// <summary>Whether <paramref name="words"/> stop on a word that leaves the sentence open.</summary>
    public static bool LooksUnfinished(string? words)
    {
        if (string.IsNullOrWhiteSpace(words)) return false;

        var text = words.AsSpan().TrimEnd(" .,?!;:-…\"'");
        if (text.IsEmpty) return false;

        var start = text.LastIndexOfAny(" \t\n,.");
        var last = text[(start + 1)..].ToString();
        if (LeadOn.Contains(last)) return true;

        // "What is the tallest" has no noun: a superlative straight after "the" is going somewhere.
        if (last.Length > 4 && last.EndsWith("est", StringComparison.OrdinalIgnoreCase))
        {
            var before = text[..Math.Max(0, start)].TrimEnd();
            return before.EndsWith(" the", StringComparison.OrdinalIgnoreCase) || before.Equals("the", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
