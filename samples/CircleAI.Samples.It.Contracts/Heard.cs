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

        // SQUARE BRACKETS ONLY, WHICH IS WHAT THE PROMISE ABOVE ACTUALLY NEEDS.
        // This matched round brackets too, so "forklift (code 14)" came back as
        // "forklift" - the doc comment has claimed the opposite since the day it
        // was written and no test ever asked. Whisper's annotations are square
        // ([BLANK_AUDIO], [Music], [Applause]); round brackets in a transcript
        // are far more often somebody actually talking.
        var stripped = Regex.Replace(heard, @"\[[^\]]*\]", " ").Trim();

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

    /// <summary>The answer without the console marker in front of it.</summary>
    /// <remarks>
    /// ItSession prefixes every reply with "IT! &gt; ", which made sense when a
    /// turn was a line in a console and makes none on a screen that already knows
    /// who is speaking. It reached the caption AND the voice: measured on a P30 on
    /// 2026-09-09 the first thing synthesised was a four-character chunk, so the
    /// assistant opened its mouth and said "IT!" before anything it had actually
    /// been asked.
    /// <para>
    /// Stripped where it is CONSUMED rather than where it is emitted, because the
    /// native head and the benchmark both parse that marker; a screen and a voice
    /// are the two places it is simply wrong.
    /// </para>
    /// <para>
    /// Leading only, so an answer that legitimately mentions the app's own name
    /// mid-sentence keeps it.
    /// </para>
    /// </remarks>
    public const string ConsoleMarker = "IT! > ";

    /// <inheritdoc cref="ConsoleMarker"/>
    public static string Answer(string? reply) =>
        reply is null ? ""
        : reply.StartsWith(ConsoleMarker, System.StringComparison.Ordinal)
            ? reply[ConsoleMarker.Length..]
            : reply;
}
