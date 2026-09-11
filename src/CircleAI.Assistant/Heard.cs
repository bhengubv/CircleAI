// Heard.cs
//
// What the transcriber gave back, and whether any of it was somebody speaking.
//
// HERE RATHER THAN IN THE ANDROID HEAD BECAUSE IT HAS TO BE TESTABLE. These are
// three pure string decisions and they were private inside DeviceConversation,
// which only compiles for Android - so the rules that decide whether the
// assistant answers at all could not be pinned by a single test. They are the
// rules most worth pinning: they are what stands between a mis-hear and a
// confidently wrong answer.

using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Samples.It;

/// <summary>Turning a raw transcript into something worth answering.</summary>
public static class Heard
{
    /// <summary>
    /// What was actually SAID, or null when the transcriber only heard noise.
    /// </summary>
    /// <remarks>
    /// WHISPER LABELS NON-SPEECH RATHER THAN RETURNING NOTHING. A quiet room
    /// comes back as "[BLANK_AUDIO]", a radio in the background as "[Music]", and
    /// those are not empty strings - so they sail through an IsNullOrWhiteSpace
    /// check and straight into whatever asked for the text. Pressing "Say it"
    /// next to a television would have written [Music] onto somebody's CV as the
    /// kind of work they are looking for.
    /// <para>
    /// Only WHOLE bracketed tokens go. Somebody saying "forklift (code 14)" keeps
    /// their brackets; what is removed is the transcriber talking about the audio
    /// instead of transcribing it. If nothing survives, nothing was said.
    /// </para>
    /// </remarks>
    public static string? Speech(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;

        // THE WHOLE UTTERANCE IN BRACKETS IS AN ANNOTATION, WHATEVER THE
        // BRACKETS. Two measured facts pull in opposite directions here:
        //
        //   "forklift (code 14)"   - a person talking; the brackets are theirs
        //   "(Bell)"               - Whisper labelling a sound, on a Redmi 12
        //                            on 2026-09-09, and it was answered with
        //                            "I am ready for further questions"
        //
        // Stripping every bracket ate the first; stripping only square ones let
        // the second through. What separates them is not the bracket shape but
        // whether anything was said OUTSIDE the brackets. Square groups are
        // always the transcriber's ([BLANK_AUDIO], [Music]) and go wherever they
        // sit; a round group goes only when it is all there is.
        var trimmed = heard.Trim();
        if (Regex.IsMatch(trimmed, @"^\([^)]*\)[.!?]*$")) return null;

        var stripped = Regex.Replace(trimmed, @"\[[^\]]*\]", " ").Trim();

        // Punctuation on its own is not speech either: silence often comes back
        // as a lone full stop once the tag is gone.
        if (!stripped.Any(char.IsLetterOrDigit)) return null;

        var tidy = Regex.Replace(stripped, @"\s+", " ");
        return Garbled(tidy) ? null : tidy;
    }

    /// <summary>
    /// Whether this is the transcriber failing rather than somebody speaking.
    /// </summary>
    /// <remarks>
    /// A MODEL HANDED NOISE DOES NOT RETURN NOTHING, IT INVENTS. Two signatures
    /// were measured on 2026-09-09, one on each test phone, from the same build:
    ///
    ///   P30      "Placeculeeai."                       - one run-together token
    ///   Redmi 12 "Heset Lea. Heset Lea. Heset Lea. …"  - one fragment, fifteen times
    ///
    /// Neither is speech, and the second is Whisper's well-known repetition loop.
    /// What made them matter is what happened next: the first was passed to the
    /// answering model, which produced "I cannot place your cryptocurrency. It is
    /// illegal to exchange money online and carries significant legal risks." A
    /// confident, fabricated statement of law, built out of thirteen characters
    /// of noise, and spoken aloud. An assistant may mishear; it may not invent
    /// legal advice to cover for having misheard.
    /// <para>
    /// True here means the caller says "I did not catch that", which is the
    /// honest answer and the one a person can act on.
    /// </para>
    /// <para>
    /// Deliberately narrow. Both tests fire only when the WHOLE utterance is the
    /// degenerate shape, so a real answer that happens to be one word ("yes",
    /// "stop") is untouched, and a meeting that genuinely repeats a phrase keeps
    /// everything around it.
    /// </para>
    /// </remarks>
    public static bool Garbled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // NO LENGTH RULE. There was one here - a single token of ten characters
        // or more was called garbage, on the reasoning that "Placeculeeai." is
        // not a word. Neither is it a rule: "Johannesburg" is twelve letters, and
        // a one-word answer to an assistant is the most ordinary thing there is.
        // The test caught it on the first run.
        //
        // The honest position is that this function CANNOT tell an invented word
        // from a real one without a dictionary, and pretending otherwise costs
        // real answers to catch a fault that is fixed properly upstream - the
        // turn no longer records the tail of the wake phrase, so "Placeculeeai."
        // has no way to be produced in the first place. A safety net that eats
        // "Johannesburg" is worse than the hole it covers.

        // THE REPETITION LOOP, WHICH IS UNAMBIGUOUS. Split on sentence ends and
        // see whether one fragment is nearly all of it. No real utterance says
        // the same short thing four times and nothing else.
        var parts = text.Split(['.', '!', '?'], System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0)
                        .ToList();

        if (parts.Count < 4) return false;

        var commonest = parts
            .GroupBy(p => p, System.StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();

        return commonest.Count() >= parts.Count * 0.8;
    }

    // NO MARKER STRIPPING HERE ANY MORE. For a day this also removed an "IT! > "
    // console marker from the front of every reply, because ItSession put one
    // there and it was reaching the caption and the voice. That was the wrong
    // layer: the marker was the OLD BRAND of a product now called Circle AI,
    // and stripping it downstream left the native head reading it aloud and the
    // model seeing it in its own history. ItSession no longer emits it, so there
    // is nothing to strip - one owner, none of it a prompt marker.
}
