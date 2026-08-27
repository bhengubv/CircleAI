// CueExtractor.cs
//
// Turning what was said into what is remembered, with no model.
//
// THE FLOOR THAT ALWAYS WORKS. A model reads a conversation better than a list
// of phrases ever will, and a memory that only fills itself when a model is
// loaded does not fill itself on a phone in aeroplane mode. So this is the
// mechanism, not the degraded mode - the same call the store makes with FTS5
// and LIKE, and the same one the whole design makes about embeddings.
//
// IT KEEPS THE PERSON'S WORDS. Every atom is a sentence somebody actually said,
// lifted whole. Paraphrasing is where extraction starts inventing, and an
// invented memory is worse than an empty one because it is handed back with the
// same confidence as a true one.
//
// IT LISTENS TO THE PERSON, NOT TO THE ASSISTANT. What an assistant said it
// would do is a plan; what the person said is the requirement. Extracting from
// both would let the thing that was corrected file its own version of events
// alongside the correction - which is how a memory ends up agreeing with
// whoever spoke last.
//
// IT DOES NOT INVENT A SUBJECT. A wrong subject key is worse than none: it
// makes an atom findable in the wrong situation and invisible in the right one.
// The caller knows what it is doing and says so; this only files what it heard.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Memory;

/// <summary>Spots things worth remembering, from cues, with no model.</summary>
public sealed class CueExtractor : IAtomExtractor
{
    /// <inheritdoc />
    public string Name => "cues";

    // ------------------------------------------------------------------
    // The cues
    // ------------------------------------------------------------------
    //
    // Ordered by how little they leave open to interpretation. "never" at the
    // start of a sentence is a rule and nothing else; "use" could be anything,
    // which is why it scores where it does.

    /// <param name="AtStart">
    /// Whether the cue only counts at the start of a sentence.
    /// </param>
    /// <remarks>
    /// THE IMPERATIVES NEED IT AND THE REST DO NOT. Somebody stating a rule
    /// starts with it - "Never restart a device", "Always uninstall first" -
    /// while the same word mid-sentence is almost always narration: "the house
    /// always wins", "I would never have guessed". Being told again is the
    /// opposite: "dude, I told you this" is mid-sentence every time.
    /// </remarks>
    private sealed record Cue(
        string Phrase, AtomKind Kind, double Confidence,
        bool Failed = false, bool AtStart = false);

    private static readonly Cue[] Cues =
    [
        // A rule, stated. The least ambiguous thing a person says - as long as
        // it is the sentence's first word. See Cue.AtStart.
        new("never ",            AtomKind.Ruling,     0.92, AtStart: true),
        new("always ",           AtomKind.Ruling,     0.88, AtStart: true),
        new("do not ",           AtomKind.Ruling,     0.88, AtStart: true),
        new("don't ",            AtomKind.Ruling,     0.88, AtStart: true),
        new("must not ",         AtomKind.Ruling,     0.90, AtStart: true),
        new("stop ",             AtomKind.Ruling,     0.82, AtStart: true),
        new("we only ",          AtomKind.Ruling,     0.86),
        new("we never ",         AtomKind.Ruling,     0.90),
        new("we always ",        AtomKind.Ruling,     0.88),
        new("from now on",       AtomKind.Ruling,     0.90),

        // THE SAME RULES WITHOUT THE APOSTROPHE, because that is how people
        // type when they are annoyed - which is exactly when they are stating
        // the rule that was just broken.
        new("dont ",             AtomKind.Ruling,     0.88, AtStart: true),
        new("wont ",             AtomKind.Ruling,     0.84, AtStart: true),
        new("we dont ",          AtomKind.Ruling,     0.88),
        new("we wont ",          AtomKind.Ruling,     0.84),

        // A road tried and found closed. Worth as much as one that worked, and
        // it is the thing recall pushes to the top.
        new("did not work",      AtomKind.Decision,   0.88, Failed: true),
        new("didn't work",       AtomKind.Decision,   0.88, Failed: true),
        new("didnt work",        AtomKind.Decision,   0.88, Failed: true),
        new("does not work",     AtomKind.Decision,   0.88, Failed: true),
        new("doesn't work",      AtomKind.Decision,   0.88, Failed: true),
        new("doesnt work",       AtomKind.Decision,   0.88, Failed: true),
        new("never worked",      AtomKind.Decision,   0.86, Failed: true),
        new("still broken",      AtomKind.Decision,   0.86, Failed: true),
        new("that broke",        AtomKind.Decision,   0.84, Failed: true),
        new("it failed",         AtomKind.Decision,   0.84, Failed: true),

        // Being told again. The single highest-value thing in a transcript:
        // whatever follows has already cost somebody twice.
        new("i told you",        AtomKind.Ruling,     0.90),
        new("i already told",    AtomKind.Ruling,     0.90),
        new("i said ",           AtomKind.Ruling,     0.84),
        new("you keep ",         AtomKind.Ruling,     0.86),
        new("how many times",    AtomKind.Ruling,     0.88),

        // How somebody wants to be worked with.
        new("i prefer ",         AtomKind.Preference, 0.88),
        new("i'd rather ",       AtomKind.Preference, 0.86),
        new("i would rather ",   AtomKind.Preference, 0.86),
        new("i hate ",           AtomKind.Preference, 0.84),
        new("i want ",           AtomKind.Preference, 0.78),
        new("i like ",           AtomKind.Preference, 0.76),

        // Something settled.
        new("let's use ",        AtomKind.Decision,   0.84),
        new("lets use ",         AtomKind.Decision,   0.84),
        new("we'll use ",        AtomKind.Decision,   0.84),
        new("we will use ",      AtomKind.Decision,   0.84),
        new("we're going with",  AtomKind.Decision,   0.86),
        new("going with ",       AtomKind.Decision,   0.78),
        new("use ",              AtomKind.Decision,   0.66),
        new("the answer is",     AtomKind.Decision,   0.72),
    ];

    // A sentence this short is a reaction, not a requirement. "never mind",
    // "stop it", "I want that" carry a cue and no content, and filing them
    // fills the memory with things that match everything and mean nothing.
    private const int ShortestWorthKeeping = 20;

    // And one this long is a paragraph that happens to contain the word, not a
    // rule somebody stated. Keeping it would put a page into a recall budget
    // that holds 600 characters.
    private const int LongestWorthKeeping = 240;

    /// <inheritdoc />
    public IReadOnlyList<AtomCandidate> Extract(
        EpisodicMemoryEntry episode, string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var found = new List<AtomCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The person's turn only. See the header: extracting from the
        // assistant's lets the thing that was corrected file its own version.
        foreach (var sentence in Sentences(episode.UserText))
        {
            if (sentence.Length is < ShortestWorthKeeping or > LongestWorthKeeping) continue;

            var lowered = sentence.ToLowerInvariant();

            // The most specific cue wins. "i told you" and "you keep" often sit
            // in one sentence, and filing it twice makes one complaint look
            // like a pattern.
            var cue = Cues
                .Where(c => Position(lowered, c.Phrase) is var at && at >= 0 && (!c.AtStart || at == 0))
                .OrderByDescending(c => c.Confidence)
                .ThenByDescending(c => c.Phrase.Length)
                .FirstOrDefault();

            if (cue is null) continue;
            if (!seen.Add(Normalise(sentence))) continue;

            found.Add(new AtomCandidate(
                new MemoryAtom
                {
                    Kind          = cue.Kind,
                    Text          = sentence,
                    Subject       = subject ?? episode.AppContext,
                    Outcome       = cue.Failed  ? DecisionOutcome.Failed
                                  : cue.Kind == AtomKind.Decision ? DecisionOutcome.Resolved
                                  : null,
                    SourceEpisode = episode.Id,
                    RecordedAtUtc = episode.RecordedAtUtc,
                },
                Confidence: cue.Confidence,
                Cue: cue.Phrase.Trim(),
                Quote: sentence));
        }

        return found;
    }

    // ------------------------------------------------------------------
    // Text
    // ------------------------------------------------------------------

    /// <summary>
    /// Where a cue starts, or -1.
    /// </summary>
    /// <remarks>
    /// ON A WORD BOUNDARY, which is the difference between "never" and
    /// "whenever", and between "use " and "because ". A substring match here
    /// files the opposite of what was said often enough to matter.
    /// </remarks>
    private static int Position(string haystack, string needle)
    {
        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (at == 0 || !char.IsLetterOrDigit(haystack[at - 1])) return at;
            from = at + 1;
        }
        return -1;
    }

    /// <summary>Sentences, roughly - enough to lift one out whole.</summary>
    /// <remarks>
    /// Newlines end a sentence too: people write rules as bullet points far
    /// more often than they end them with a full stop.
    /// </remarks>
    private static IEnumerable<string> Sentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var ends = c is '\n' or '\r' or '?' or '!' ||
                       (c == '.' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])));

            if (!ends) continue;

            var sentence = text[start..i].Trim(' ', '\t', '-', '*', '>', '.', ',');
            if (sentence.Length > 0) yield return sentence;
            start = i + 1;
        }

        var last = text[start..].Trim(' ', '\t', '-', '*', '>', '.', ',');
        if (last.Length > 0) yield return last;
    }

    /// <summary>A form two ways of typing the same thing agree on.</summary>
    internal static string Normalise(string text) =>
        string.Join(" ", text
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim('.', ',', '!', '?', ';', ':', ' ');
}
