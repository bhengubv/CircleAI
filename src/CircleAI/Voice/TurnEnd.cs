// TurnEnd.cs
//
// WHETHER SOMEBODY HAS FINISHED IS A QUESTION ABOUT THEIR WORDS AS WELL AS THEIR VOLUME.
//
// A gate hears quiet. It cannot tell the quiet after "What is the capital of Peru" from the quiet
// after "What is the", and on the P30 on 2026-11-07 a pause of a second lost the end of five
// questions out of 122. So the words get a say: a sentence that stops on a word that cannot end one
// is given longer, and the quiet that ends a sentence that can end is left as it was.
//
// A WORD THAT SOMETIMES LEADS ON IS JUDGED BY WHAT CAME BEFORE IT. One list held them all, and on the
// same phone later that day "What could I get her?" kept the microphone 1,5 s past its last word,
// because "What is her" goes on. Getting this wrong is not even: a wrong hold is paid by every
// sentence that ends that way, a wrong close only by somebody who stopped that long on exactly that
// word, and they can say the rest in the follow-up. So where it could go either way, it closes.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Voice;

/// <summary>What the words so far say about whether the person has finished.</summary>
public static class TurnEnd
{
    // Words no sentence stops on: what they lead to has not been said yet.
    private static readonly HashSet<string> NeverLast = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "than", "and", "or", "but", "because", "if", "while", "whose",
        "my", "your", "our", "their", "its", "what's", "very", "just", "um", "uh", "er", "erm", "hmm",
    };

    // These stand last when what they point at was said at the front: "Where are you from?", but not
    // "How far is it from". "The" says it was not: in "What is the weather in" the front word is the answer
    // being asked for, not what "in" points at.
    private static readonly HashSet<string> Pointing = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "in", "on", "at", "for", "with", "from", "by", "about", "into", "like",
    };

    private static readonly HashSet<string> Fronted = new(StringComparer.OrdinalIgnoreCase) { "what", "who", "whom", "which", "where" };
    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

    // "Turn it on", "Go on", "Come in": no front word, and the sentence is whole.
    private static readonly HashSet<string> TakesAParticle = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "them", "him", "her", "me", "us", "go", "come", "hold", "carry", "keep", "move", "log", "sign",
    };

    // "What should I do?" has done its asking by the time it gets there; "What time does" has not started.
    private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "were", "am", "be", "been", "do", "does", "did", "can", "could", "will", "would", "should",
    };

    private static readonly HashSet<string> Asking = new(StringComparer.OrdinalIgnoreCase) { "what", "who", "whom", "which", "where", "when", "why", "how" };
    private static readonly HashSet<string> Subjects = new(StringComparer.OrdinalIgnoreCase) { "i", "you", "he", "she", "it", "we", "they" };
    private static readonly HashSet<string> Demonstratives = new(StringComparer.OrdinalIgnoreCase) { "this", "that", "these", "those" };

    private static readonly char[] Between = [' ', '\t', '\r', '\n', ',', '.'];
    private static readonly char[] Around = ['?', '!', ';', ':', '-', '…', '"', '\''];

    /// <summary>Whether <paramref name="words"/> stop on a word that leaves the sentence open.</summary>
    public static bool LooksUnfinished(string? words)
    {
        if (string.IsNullOrWhiteSpace(words)) return false;

        var said = words.Split(Between, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.Trim(Around))
                        .Where(w => w.Length > 0)
                        .ToArray();
        if (said.Length == 0) return false;

        var last = said[^1];
        var before = said.Length > 1 ? said[^2] : string.Empty;
        var earlier = said[..^1];
        bool Earlier(HashSet<string> these) => earlier.Any(w => these.Contains(Stem(w)));

        if (NeverLast.Contains(last)) return true;

        if (Pointing.Contains(last))
        {
            var pointsBack = Earlier(Fronted) && (last.Equals("like", StringComparison.OrdinalIgnoreCase) || !Earlier(Articles));
            var particle = last.ToLowerInvariant() is "on" or "in" && TakesAParticle.Contains(before);
            return !pointsBack && !particle;
        }

        if (Helpers.Contains(last))
        {
            var asked = Earlier(Helpers) && Earlier(Asking) && !Helpers.Contains(before);
            var answered = Subjects.Contains(before) && said.Length > 2;   // "Yes it is", where "It is" goes on
            return !asked && !answered;
        }

        // "Is that" goes on and "What is that?" does not; "What is her" goes on and "What could I get her?" does not.
        if (Demonstratives.Contains(last)) return Helpers.Contains(before) && !Earlier(Asking);
        if (last.ToLowerInvariant() is "her" or "his") return said.Length == 1 || Helpers.Contains(before);

        // "Why?" and "And how?" are whole; "Tell me what" is not. "Tell me why" is.
        if (Asking.Contains(last)) return said.Length > 2 && !last.Equals("why", StringComparison.OrdinalIgnoreCase);
        if (last.Equals("so", StringComparison.OrdinalIgnoreCase)) return said.Length < 3;   // "I think so", where "And so" goes on

        // "What is the tallest" has no noun: a superlative straight after "the" is going somewhere.
        return last.Length > 4 && last.EndsWith("est", StringComparison.OrdinalIgnoreCase)
            && before.Equals("the", StringComparison.OrdinalIgnoreCase);
    }

    // "What's" is "what" as far as these lists go, and the ears write either apostrophe.
    private static string Stem(string word)
    {
        var at = word.IndexOfAny(['\'', '’']);
        return at > 0 ? word[..at] : word;
    }
}
