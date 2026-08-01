#nullable enable

// PersonalRespellings.cs
//
// The respelling table each person's CircleAI builds for itself, by listening.
//
// The shipped table is a starting point, not an answer: it holds the words usage
// has already settled (esemese) and some spellings we guessed (wotsapha). What a
// particular person actually says is something only they can teach, and they teach
// it simply by talking — when they use a borrowed word, the transcriber writes it
// down in their own orthography, and that transcript IS the respelling.
//
// ADOPTION IS DELIBERATELY SLOW, and the reason is accent. The first few hearings
// of a word scatter: the same speaker varies with what surrounds it, and a shared
// phone has several speakers. Three that agree could still be one person's range.
// So five must AGREE with one another before anything changes — and a sixth then
// checks the change against a version we have already altered:
//
//   1-4   listening; nothing changes
//   5     adopt, and start saying it their way
//   6     the check. Agrees -> locked. Disagrees -> we were wrong, revert
//
// That last step is what makes this safe to run unattended. Adoption is a
// hypothesis with a test after it, not a leap.
//
// Nothing here leaves the device, and nothing is shared between people. Two
// CircleAIs that have heard different speakers will pronounce the same word
// differently, permanently. That is the intent.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Voice;

/// <summary>Where a word has got to in the learning process.</summary>
public enum LearningState
{
    /// <summary>Still listening. Nothing has changed how the word is spoken.</summary>
    Listening,

    /// <summary>Five hearings agreed; the new spelling is in use and awaiting its check.</summary>
    Adopted,

    /// <summary>The check passed. This is how the word is said for this person.</summary>
    Confirmed,
}

/// <summary>What has been learned about one word.</summary>
/// <param name="Word">The written form, as it appears in text.</param>
/// <param name="Spelling">The spelling in use, or null while still listening.</param>
/// <param name="State">How far along the word is.</param>
/// <param name="Candidates">Each candidate spelling and how many hearings agreed on it.</param>
public sealed record LearnedWord(
    string Word,
    string? Spelling,
    LearningState State,
    IReadOnlyDictionary<string, int> Candidates);

/// <summary>
/// Learns how this person says borrowed words, from ordinary use.
/// </summary>
public sealed class PersonalRespellings
{
    /// <summary>Hearings that must AGREE before a spelling is adopted.</summary>
    /// <remarks>
    /// Five, not three. Hearings one to three vary with accent — the same speaker
    /// shifts depending on the surrounding words, and a shared phone carries
    /// several voices. By four and five a real case exists. Counting only hearings
    /// that agree with EACH OTHER is what makes the number mean something: five
    /// scattered forms are noise and must never accumulate into a decision.
    /// </remarks>
    public const int AdoptAfter = 5;

    /// <summary>
    /// How different a heard form may be and still count as the same word.
    /// </summary>
    /// <remarks>
    /// Expressed as a share of the word's length. "wayufayu" against "wayifayi" is
    /// two vowels in eight characters — plainly the same word said differently.
    /// Something wildly different is a different word altogether, picked up because
    /// the person was talking about something else, and learning from it would
    /// teach nonsense.
    /// </remarks>
    public const double MaxDifference = 0.40;

    private readonly Dictionary<string, Entry> _words = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards the table: the ear writes it while the mouth reads it.</summary>
    /// <remarks>
    /// These are genuinely different threads — a transcript arrives on the voice
    /// loop and lands in <see cref="Observe"/> at the same moment synthesis is
    /// asking <see cref="Respell"/> how to say a word. An unguarded dictionary
    /// resized mid-read does not return a wrong answer, it corrupts or throws, and
    /// it would do so rarely enough to be untraceable.
    /// </remarks>
    private readonly object _gate = new();

    private sealed class Entry
    {
        public Dictionary<string, int> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Spelling { get; set; }
        public LearningState State { get; set; } = LearningState.Listening;
    }

    /// <summary>How this person says <paramref name="word"/>, or null if not yet learned.</summary>
    public string? Respell(string word)
    {
        lock (_gate)
            return _words.TryGetValue(word, out var e)
                   && e.State is LearningState.Adopted or LearningState.Confirmed
                ? e.Spelling
                : null;
    }

    /// <summary>Everything learned so far, for a "words your CircleAI knows" view.</summary>
    public IReadOnlyList<LearnedWord> All()
    {
        lock (_gate)
            return _words.Select(kv => new LearnedWord(kv.Key, kv.Value.Spelling, kv.Value.State,
                                                       new Dictionary<string, int>(kv.Value.Candidates)))
                         .ToList();
    }

    /// <summary>
    /// Records one hearing of <paramref name="word"/> spoken as <paramref name="heard"/>.
    /// </summary>
    /// <param name="word">The written form, as it appears in text.</param>
    /// <param name="heard">How the transcriber wrote what they actually said.</param>
    /// <param name="currentSpelling">
    /// What we say today — shipped, derived or already learned. A hearing that
    /// merely agrees with what we already do teaches nothing new.
    /// </param>
    /// <returns>True when this hearing changed how the word will be spoken.</returns>
    public bool Observe(string word, string heard, string? currentSpelling = null)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(heard)) return false;

        word = word.Trim();
        heard = heard.Trim();

        lock (_gate) return ObserveLocked(word, heard, currentSpelling);
    }

    private bool ObserveLocked(string word, string heard, string? currentSpelling)
    {
        var entry = _words.TryGetValue(word, out var e) ? e : _words[word] = new Entry();
        var reference = entry.Spelling ?? currentSpelling ?? word;

        // Too far from the word to be that word: the speaker was saying something
        // else and this hearing is not evidence about anything.
        if (!IsSameWord(reference, heard)) return false;

        // THE CHECK. A word adopted last time is now being said our new way; this
        // hearing is the test of whether we got it right.
        if (entry.State == LearningState.Adopted)
        {
            if (Agrees(entry.Spelling!, heard))
            {
                entry.State = LearningState.Confirmed;
                return false;                       // confirmed, but nothing changed
            }

            // We were wrong. Undo it and let the evidence rebuild — including this
            // hearing, which is evidence for something else.
            entry.Candidates.Remove(entry.Spelling!);
            entry.Spelling = null;
            entry.State = LearningState.Listening;
        }

        // They said it the way we already say it. That is agreement, not a lesson —
        // and counting it would build a personal entry that overrides the shipped
        // spelling with an identical one, for no reason.
        var effective = entry.Spelling ?? currentSpelling;
        if (Agrees(effective, heard)) return false;

        var count = entry.Candidates.TryGetValue(heard, out var n) ? n + 1 : 1;
        entry.Candidates[heard] = count;

        if (count < AdoptAfter) return false;

        entry.Spelling = heard;
        entry.State = LearningState.Adopted;
        return true;
    }

    /// <summary>
    /// Learns from one thing the person said, as the transcriber wrote it down.
    /// </summary>
    /// <param name="transcript">What they said, in their own language's spelling.</param>
    /// <param name="currentSpellings">
    /// The words we can respell and how we say each today — shipped, derived or
    /// already learned. Only these are looked for; the rest of the sentence is
    /// their own language and has nothing to teach us about borrowings.
    /// </param>
    /// <returns>The words whose spelling changed because of this utterance.</returns>
    /// <remarks>
    /// This is where the design pays off: the person is not correcting anything or
    /// completing a task. They asked their phone to do something, and the
    /// transcriber wrote a borrowed word the way THEY say it. That transcript is
    /// the lesson, and it costs them nothing.
    ///
    /// isiZulu glues its prefixes on — "nge-wotsapha", "i-esemese" — so a token is
    /// compared on the part after the last hyphen. Comparing the whole thing would
    /// make every prefixed mention look like a different word.
    /// </remarks>
    public IReadOnlyList<string> LearnFrom(string? transcript, IReadOnlyDictionary<string, string> currentSpellings)
    {
        if (string.IsNullOrWhiteSpace(transcript) || currentSpellings.Count == 0)
            return Array.Empty<string>();

        var tokens = transcript
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '"' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Contains('-') ? t[(t.LastIndexOf('-') + 1)..] : t)
            .Where(t => t.Length > 1)
            .ToList();
        if (tokens.Count == 0) return Array.Empty<string>();

        var changed = new List<string>();
        lock (_gate)
        foreach (var (word, spelling) in currentSpellings)
        {
            // The nearest token to how we say it today. Nearest, not merely close:
            // a sentence can hold two similar words and picking the wrong one would
            // teach the wrong lesson.
            var reference = Respell(word) ?? spelling;
            var best = tokens
                .Select(t => (Token: t, Distance: EditDistance(t.ToLowerInvariant(), reference.ToLowerInvariant())))
                .OrderBy(x => x.Distance)
                .First();

            var allowed = Math.Max(reference.Length, best.Token.Length) * MaxDifference;
            if (best.Distance > allowed) continue;          // that word was not said

            if (Observe(word, best.Token, spelling)) changed.Add(word);
        }
        return changed;
    }

    /// <summary>Forgets a word, so it falls back to the shipped or derived spelling.</summary>
    public void Forget(string word) { lock (_gate) _words.Remove(word); }

    // ── keeping it between sessions ──────────────────────────────────────────
    //
    // A year of learning that vanishes on restart is not learning. This is the
    // person's own file, on their own device: it is never uploaded, never shared,
    // and never merged with anybody else's. Two people's files will disagree about
    // the same word, and that is the whole point.

    private sealed record Snapshot(string Word, string? Spelling, LearningState State,
                                   Dictionary<string, int> Candidates);

    /// <summary>Writes everything learned to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Written to a temporary file and moved into place. A phone loses power mid-
    /// write, and a half-written table would be read back as corrupt and thrown
    /// away — losing months of listening to save a few milliseconds.
    /// </remarks>
    public void Save(string path)
    {
        List<Snapshot> snapshot;
        lock (_gate)
            snapshot = _words.Select(kv =>
                new Snapshot(kv.Key, kv.Value.Spelling, kv.Value.State,
                             new Dictionary<string, int>(kv.Value.Candidates))).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, json);
        System.IO.File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a table back, or returns an empty one when there is none.</summary>
    /// <remarks>
    /// An unreadable file yields an empty table rather than an exception. Losing
    /// the learning is bad; refusing to start because of it is worse, and the
    /// person can simply teach it again by talking.
    /// </remarks>
    public static PersonalRespellings Load(string path)
    {
        var table = new PersonalRespellings();
        try
        {
            if (!System.IO.File.Exists(path)) return table;

            var snapshot = System.Text.Json.JsonSerializer
                .Deserialize<List<Snapshot>>(System.IO.File.ReadAllText(path));
            if (snapshot is null) return table;

            foreach (var s in snapshot)
            {
                var entry = new Entry { Spelling = s.Spelling, State = s.State };
                foreach (var (k, v) in s.Candidates) entry.Candidates[k] = v;
                table._words[s.Word] = entry;
            }
        }
        catch { /* unreadable: start over rather than refuse to start */ }
        return table;
    }

    private static bool Agrees(string? a, string b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Are these two spellings the same word, said differently?</summary>
    private static bool IsSameWord(string a, string b)
    {
        if (Agrees(a, b)) return true;
        var longest = Math.Max(a.Length, b.Length);
        if (longest == 0) return false;
        return (double)EditDistance(a.ToLowerInvariant(), b.ToLowerInvariant()) / longest <= MaxDifference;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
