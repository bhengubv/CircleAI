// PhraseInSpeech.cs
//
// DECIDING WHETHER SOMEBODY JUST SAID THE WAKE PHRASE, GIVEN A TRANSCRIPT.
//
// WHY A WAKE WORD IS BECOMING A TRANSCRIPT MATCH AT ALL. The product must let a
// person set ANY phrase, in ANY language, at runtime and with no training:
// Africa has roughly two thousand languages and South Africa twelve official
// ones, so one trained model per phrase does not scale and never will. That
// rules out the whole fixed-phrase family - openWakeWord, microWakeWord and the
// rest - however good they are at the four phrases they ship.
//
// sherpa-onnx's keyword spotter does take arbitrary text, which is why it was
// chosen. But its entire published shelf is 3 to 3.3 MILLION parameters, and
// measured on the owner's own voice on 2026-10-12 the English one found the
// phrase 4 times out of roughly twenty spoken close to the microphone, and
// never once at normal holding distance. There is no larger keyword model to
// upgrade to; 3.3M is the whole shelf.
//
// The same project publishes STREAMING ASR at 20M parameters under Apache-2.0,
// running on the ONNX Runtime this app already loads. Over that same recording
// it transcribed legibly - "hey circle a", "hay bob", "hey alexe hey alexa" -
// at 32x realtime. Six times the model, open vocabulary by construction, and
// nothing new to invent.
//
// WHICH MOVES THE HARD PART HERE. A keyword spotter answers yes or no. A
// transcript is fuzzy: it arrives upper case and unpunctuated, "Hey B!" comes
// back as "hay bee" or "hey be", and a streaming decoder revises what it said a
// moment ago. So the matching has to be forgiving enough to catch the phrase as
// spoken and strict enough not to wake on a sentence that merely contains it.
//
// IT IS PURE TEXT, ON PURPOSE. The thing that decides whether a person is
// answered is a string comparison, and it belongs somewhere both framework legs
// can assert it - not inside an Android-only capture loop, which is where every
// other rule in this feature hid while it was wrong.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CircleAI.Voice;

/// <summary>Whether a transcript contains the wake phrase.</summary>
public static class PhraseInSpeech
{
    /// <summary>Shortest word that may be matched with an error in it.</summary>
    /// <remarks>
    /// THREE, AND THE LINE IS WHERE IT IS BECAUSE OF WHAT THE MODEL DOES. It
    /// guesses vowels in short words - "hay" for "hey" - and that has to be
    /// forgiven, or the phrase is refused on the commonest mishearing there
    /// is. It also guesses whole words for a spoken letter - "bob", "bill",
    /// "be" for "B" - and that must NOT be forgiven, because nothing separates
    /// a correct "be" from a wrong "bob" except length and both are one or two
    /// letters on.
    /// <para>
    /// So anything shorter than this is matched exactly. It is also why a
    /// one-letter wake phrase cannot work at all - see the test named for it.
    /// </para>
    /// </remarks>
    public const int ShortestForgivable = 3;

    /// <summary>Strips a transcript or a phrase to comparable words.</summary>
    /// <remarks>
    /// A TRANSCRIPT AND A TYPED PHRASE ARE NEVER IN THE SAME SHAPE. The model
    /// emits upper case without punctuation, trained on GigaSpeech transcripts;
    /// a person types "Hey B!" with a capital and an exclamation mark that is
    /// part of the brand. Comparing those directly fails on every one of those
    /// differences and none of them is about what was said.
    /// <para>
    /// Diacritics are kept. Stripping them would be right for English and wrong
    /// for most of the languages this has to serve.
    /// </para>
    /// </remarks>
    public static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text!.Length);
        var space = true;   // leading separators collapse to nothing

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The phrase as whole words, ready to look for.</summary>
    public static string[] Words(string? phrase) =>
        Plain(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="said"/> ends with the phrase.</summary>
    /// <param name="said">What the recogniser has transcribed so far.</param>
    /// <param name="phrase">What the person set as their wake phrase.</param>
    /// <remarks>
    /// AT THE END, NOT ANYWHERE. A wake word fires when somebody has JUST said
    /// it, and a streaming transcript grows - so "hey b what is the weather"
    /// contains the phrase but the moment to wake has passed, and firing then
    /// would take the microphone away mid-question. Checking the tail also
    /// makes this cheap to call on every revision of a growing transcript.
    /// <para>
    /// WHOLE WORDS. A substring test wakes "hey b" on "they bought", which is
    /// the single most obvious way this could become worse than the keyword
    /// spotter it replaces.
    /// </para>
    /// </remarks>
    public static bool JustSaid(string? said, string? phrase)
    {
        var want = Words(phrase);
        if (want.Length == 0) return false;

        var heard = Words(said);
        if (heard.Length < want.Length) return false;

        for (var i = 0; i < want.Length; i++)
            if (!string.Equals(heard[heard.Length - want.Length + i], want[i], StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>
    /// Whether the phrase is there allowing for the recogniser mishearing it.
    /// </summary>
    /// <param name="said">What the recogniser transcribed.</param>
    /// <param name="phrase">The wake phrase.</param>
    /// <param name="slack">
    /// Edit distance allowed PER WORD, as a fraction of that word's length.
    /// </param>
    /// <remarks>
    /// BECAUSE A SPOKEN LETTER IS NOT A WORD. Measured on the owner's own voice:
    /// "Hey B!" came back from the 20M model as "hay bob", "hay bill", "hey be"
    /// and "b hey friend". An exact match catches almost none of that, and the
    /// person is saying their phrase perfectly clearly - the recogniser is
    /// guessing at a single syllable with no context to constrain it.
    /// <para>
    /// PER WORD RATHER THAN OVER THE WHOLE STRING, because a short word can
    /// afford almost no slack and a long one can afford more. One sixth lets
    /// "circle" lose a letter and keeps "b" exact, which is the right way round:
    /// the short word is the one carrying the identity.
    /// </para>
    /// <para>
    /// This is DELIBERATELY not the default. A fuzzy wake word wakes at things
    /// that merely sound close, and the cost of that is a phone that interrupts
    /// a conversation. Callers opt in, and the slack is theirs to set.
    /// </para>
    /// </remarks>
    public static bool NearlySaid(string? said, string? phrase, double slack = 1.0 / 6.0)
    {
        var want = Words(phrase);
        if (want.Length == 0) return false;

        var heard = Words(said);
        if (heard.Length < want.Length) return false;

        // THE PER-WORD RULE LIVES IN Forgives, which SaidAnywhere asks too.
        // Measured on the P30 at 04:05 on 2026-10-12: the phrase came back as
        // "ANY TIME NOW HAY B SIGNING OUT" and the match was refused on that
        // one vowel, which is what the rule exists to forgive.
        for (var i = 0; i < want.Length; i++)
            if (!Forgives(want[i], heard[heard.Length - want.Length + i], slack))
                return false;

        return true;
    }

    /// <summary>
    /// Whether the phrase is anywhere in one endpointed utterance, allowing for
    /// the recogniser mishearing it.
    /// </summary>
    /// <param name="said">One utterance, as decoded between two silences.</param>
    /// <param name="phrase">The wake phrase.</param>
    /// <param name="slack">Edit distance allowed per word, as a fraction.</param>
    /// <remarks>
    /// ANYWHERE, AND THAT IS A CHANGE OF MIND WITH A MEASUREMENT BEHIND IT.
    /// <para>
    /// <see cref="JustSaid"/> and <see cref="NearlySaid"/> deliberately match
    /// only at the END, and the reason was sound: the transcript they were
    /// written for GREW without bound, so "hey b what is the weather" contained
    /// the phrase long after the moment to wake had passed, and firing then
    /// would take the microphone away mid-question.
    /// </para>
    /// <para>
    /// That reason has since stopped applying. The wake loop now endpoints: the
    /// gate closes on a silence, the recogniser is asked to finish, and the
    /// transcript is forgotten - so what arrives here is ONE UTTERANCE of at
    /// most a few seconds, not a session. Inside that window there is no "the
    /// moment has passed", and insisting on the tail just throws away matches.
    /// </para>
    /// <para>
    /// MEASURED ON THE OWNER'S VOICE, 2026-10-12. Saying "Hey Circle AI" came
    /// back as one burst reading "HAY CIRCLED AY HAY CIRCLE A HEY CIRCLE AI".
    /// The phrase is in there twice over, and a tail-only rule depends on him
    /// stopping on exactly the right word.
    /// </para>
    /// <para>
    /// The protections that matter are kept: whole words, so "hey b" still does
    /// not fire on "they bought", and the per-word edit budget, so "hay bob"
    /// and "hay bill" still do not fire on "Hey B".
    /// </para>
    /// </remarks>
    public static bool SaidAnywhere(string? said, string? phrase, double slack = 1.0 / 6.0)
    {
        var want = Words(phrase);
        if (want.Length == 0) return false;

        var heard = Words(said);
        if (heard.Length < want.Length) return false;

        for (var start = 0; start + want.Length <= heard.Length; start++)
        {
            var all = true;
            for (var i = 0; i < want.Length && all; i++)
                all = Forgives(want[i], heard[start + i], slack);

            if (all) return true;
        }

        return false;
    }

    /// <summary>Whether one heard word is close enough to one wanted word.</summary>
    /// <remarks>
    /// ONE OWNER FOR THE PER-WORD RULE, because it is now asked by two matchers
    /// and the two must not drift. The last time a rule in this feature existed
    /// in two places, two keyword files held two different wake phrases.
    /// </remarks>
    private static bool Forgives(string want, string heard, double slack)
    {
        // ONE EDIT IS THE FLOOR FOR A REAL WORD, NOT A FRACTION OF IT.
        //
        // The fraction alone gave a three-letter word ZERO edits -
        // floor(3 x 1/6) = 0 - so "hay" could never match "hey", which is the
        // single most common thing this recogniser does.
        //
        // AND A ONE OR TWO LETTER WORD STILL GETS NOTHING, which is what keeps
        // this honest: "b" against "bob", "bill" or "d" must fail, and the
        // short word is usually the one carrying the identity.
        var allowed = want.Length >= ShortestForgivable
            ? Math.Max(1, (int)Math.Floor(want.Length * slack))
            : 0;

        return Distance(want, heard) <= allowed;
    }

    /// <summary>Levenshtein distance, bounded by the shorter of the two.</summary>
    /// <remarks>
    /// Two rows rather than a full matrix: the words here are a handful of
    /// characters, but this runs on every revision of a growing transcript on a
    /// phone, and an allocation per comparison is not free at that rate.
    /// </remarks>
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
