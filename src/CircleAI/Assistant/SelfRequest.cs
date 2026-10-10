// SelfRequest.cs
//
// "WHICH BRAIN ARE YOU USING?" AND "TRY THE BIG ONE AGAIN."
//
// A device that refuses a model has to be able to say so and be argued with, out
// loud, because voice is the primary surface and a person is not going to go
// looking for a settings page to repair an assistant.
//
// THE REFUSAL EXISTS AND HAD NO WAY BACK. CrashVerdict writes it when a load
// aborts the process - on a Circle OS device a 22.8 GB MoE took a dozen system apps
// down with it - and IModelCatalog.Pardon withdraws it. Nothing reached Pardon. So
// a device that refused something wrongly would refuse it forever, and the only
// recovery was deleting a database file over adb. That is not a product.
//
// IT MUST ANSWER WITH THE BRAIN DOWN, which is the whole reason it lives at this
// seam rather than in a prompt. Every question here is about the state of the
// model, and the state worth asking about is "it did not come up". Putting these
// to a language model would mean the one moment somebody needs an answer is the one
// moment there is nothing to answer with.
//
// NOT AN INTENT CLASSIFIER, and like Opener it must never become one. Three narrow
// shapes, each a thing a person says ABOUT the assistant rather than TO it. Anything
// else is a question and goes to the model.

using System;

namespace CircleAI.Assistant;

/// <summary>What somebody is asking about the assistant itself.</summary>
public enum SelfAsk
{
    /// <summary>Not about the assistant. Goes to the model like anything else.</summary>
    None = 0,

    /// <summary>"Which brain are you using?" / "Are you ready?"</summary>
    WhichBrain,

    /// <summary>"Try the big one again." — withdraw a refusal and use it.</summary>
    TryAgain,

    /// <summary>"Clear it." — delete the bytes of a model this phone cannot run.</summary>
    /// <remarks>
    /// THE ANSWER TO AN OFFER, NOT AN IDLE INSTRUCTION. Circle AI says there is a
    /// brain here it cannot run and how much room it takes; this is somebody saying
    /// go ahead. Nothing deletes without it.
    /// </remarks>
    ClearIt,
}

/// <summary>Recognises the few things a person says about the assistant's own state.</summary>
public static class SelfRequest
{
    /// <summary>
    /// Asking what it is running, or whether it is up.
    /// </summary>
    /// <remarks>
    /// EVERY ONE OF THESE IS A WHOLE UTTERANCE, not a substring. "Are you ready to
    /// book the table" is a question about a table; matching it here would answer
    /// the wrong thing with total confidence, which is worse than being slow.
    /// </remarks>
    private static readonly string[] Status =
    [
        "which brain are you using",
        "which brain are you on",
        "what brain are you using",
        "what brain is this",
        "what model are you using",
        "which model are you using",
        "are you ready",
        "are you ready yet",
        "how are you set up",
        "what are you running",
        "why are you slow",
    ];

    /// <summary>
    /// Telling it to use something it refused.
    /// </summary>
    /// <remarks>
    /// THE SPOKEN INSTRUCTION IS THE AUTHORISATION. announce-dont-confirm is a
    /// standing rule here: somebody who says "try the big one again" has decided,
    /// and asking them to confirm it is asking the same question twice. It announces
    /// what it did.
    /// </remarks>
    private static readonly string[] Retry =
    [
        "try the big one again",
        "try the big brain again",
        "try the bigger one again",
        "use the big one anyway",
        "use the big brain anyway",
        "try it again anyway",
        "try the big one",
        "use the big brain",
        "give the big one another go",
    ];

    /// <summary>
    /// Telling it to reclaim the space a model it cannot run is taking.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NOT "yes". A bare yes is the commonest word in a conversation
    /// and binding a deletion of twenty-two gigabytes to it would be indefensible -
    /// somebody agreeing with the previous sentence would lose a download. Every
    /// phrase here names the action.
    /// </remarks>
    private static readonly string[] Clear =
    [
        "clear it",
        "clear the big one",
        "clear the big brain",
        "get rid of it",
        "get rid of the big one",
        "delete it",
        "delete the big one",
        "free up the space",
        "yes clear it",
        "go ahead and clear it",
    ];

    /// <summary>
    /// Plain agreement - only ever meaningful while an offer is open.
    /// </summary>
    /// <remarks>
    /// THE ANSWER A PERSON ACTUALLY GIVES. The dead-weight offer was announced,
    /// and the only accepted replies were whole utterances like "clear it" and
    /// "delete the big one". Nobody answers an offer that way. They say "yes".
    ///
    /// Reported on 2026-10-10, within minutes of hearing it for the first time:
    /// "I have been responding but nothing happens."
    ///
    /// DANGEROUS WITHOUT THE GUARD, which is why it is a separate list behind a
    /// parameter. A bare "yes" with no offer open must never delete 2.85 GB -
    /// it could be the tail of any sentence a transcriber mangled. These count
    /// only when the assistant has just asked.
    /// </remarks>
    private static readonly string[] Agreement =
    [
        "yes", "yeah", "yep", "yup", "ok", "okay", "sure",
        "go ahead", "go on", "do it", "please do", "please", "yes please",
        "that is fine", "thats fine", "fine", "clear", "do that",
    ];

    /// <summary>What <paramref name="said"/> is asking about the assistant, if anything.</summary>
    /// <param name="said">What the person said.</param>
    /// <param name="anOfferIsOpen">
    /// True when the assistant has just offered to clear something and is
    /// waiting for an answer. Only then does plain agreement mean yes.
    /// </param>
    /// <remarks>
    /// MATCHED AS A WHOLE UTTERANCE AND THEN, FAILING THAT, LENIENTLY. The strict
    /// pass stays first and is unchanged: "are you ready to book the table" must
    /// not be heard as a question about the assistant.
    ///
    /// But strict-only is why the offer could not be answered. "clear it please",
    /// "yes clear it then" and "ok clear it" all failed an exact comparison
    /// against "clear it", and a person has no way to know which of ten phrasings
    /// the machine wants. So a CLEAR instruction is also recognised when the
    /// utterance is a short one that contains it - short, because "do not clear
    /// it" and "why would I clear it" are not instructions, and length is the
    /// cheap guard against a sentence that merely mentions it.
    /// </remarks>
    public static SelfAsk Of(string? said, bool anOfferIsOpen = false)
    {
        var text = Bare(said);
        if (text.Length == 0) return SelfAsk.None;

        foreach (var c in Clear)  if (text == c) return SelfAsk.ClearIt;
        foreach (var r in Retry)  if (text == r) return SelfAsk.TryAgain;
        foreach (var s in Status) if (text == s) return SelfAsk.WhichBrain;

        // Nothing matched exactly. Anything below here needs an open offer.
        if (!anOfferIsOpen) return SelfAsk.None;

        foreach (var a in Agreement) if (text == a) return SelfAsk.ClearIt;

        // "clear it please", "ok clear it then" - the instruction with the
        // politeness a person actually uses. Five words is enough for every
        // natural phrasing and short of anything that could be a refusal.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 5 && !Negated(words))
        {
            foreach (var c in Clear) if (text.Contains(c, StringComparison.Ordinal)) return SelfAsk.ClearIt;
        }

        return SelfAsk.None;
    }

    /// <summary>"do not clear it" is not an instruction to clear it.</summary>
    private static bool Negated(string[] words)
    {
        foreach (var w in words)
            if (w is "not" or "dont" or "never" or "no" or "cancel" or "stop" or "wait" or "why")
                return true;
        return false;
    }

    /// <summary>
    /// Lower-cased, stripped of the punctuation a transcriber adds and the wake
    /// phrase a person says first.
    /// </summary>
    /// <remarks>
    /// Whisper returns "Hey, B. Which brain are you using?" for one breath of
    /// speech, so a matcher that wants the bare sentence has to take the wake phrase
    /// and the full stops off first. This is the same problem Opener solves and it
    /// is solved the same way.
    /// </remarks>
    private static string Bare(string? said)
    {
        if (string.IsNullOrWhiteSpace(said)) return string.Empty;

        var span = said.AsSpan().Trim();
        var sb = new System.Text.StringBuilder(span.Length);
        foreach (var c in span)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) || c == '\'') { if (c != '\'') sb.Append(' '); }
            else sb.Append(' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var from = 0;

        // "hey b", "hey circle ai", "ok b" - the attention-getter, then the sentence.
        if (words.Length > 1 && (words[0] == "hey" || words[0] == "ok" || words[0] == "okay"))
        {
            from = 1;
            if (words.Length > from + 1 && (words[from] == "b" || words[from] == "bee")) from++;
            else if (words.Length > from + 2 && words[from] == "circle" && words[from + 1] == "ai") from += 2;
        }

        // "please" either end, and a trailing "now", are politeness rather than content.
        var to = words.Length;
        while (to > from && (words[to - 1] == "please" || words[to - 1] == "now")) to--;
        while (from < to && words[from] == "please") from++;

        return to <= from ? string.Empty : string.Join(' ', words, from, to - from);
    }
}
