// ToolIntent.cs
//
// The engine deciding, for the few questions where it plainly can, which tool a
// turn needs — so a 0.6B that will not emit a <tool_call> still gets one run.
//
// Measured on a P30 (see circleai-06b-wont-toolcall): the 0.6B, offered a
// calculator, did not call it; the same is true of every tool. Arithmetic side-
// stepped that by answering in the engine (ArithmeticIntent). This does the same
// for the two tools whose intent is recognisable from the words alone: the
// battery (device state the model cannot know) and a live web search (facts the
// model's weights cannot hold). When one of these is plainly asked, the engine
// runs the tool itself and hands the model the result to answer from.
//
// CONSERVATIVE, LIKE ArithmeticIntent. A false positive is worse than a false
// negative here: a battery reading nobody asked for is odd, and a web search
// leaves the phone uninvited. So it fires only on clear intent, and everything
// else falls through to the model exactly as before.

using System;

namespace CircleAI.Assistant;

/// <summary>What a question plainly needs a tool for.</summary>
public enum ToolNeed
{
    /// <summary>Nothing recognisable — let the model answer.</summary>
    None,

    /// <summary>The device's battery charge — state the model cannot know.</summary>
    Battery,

    /// <summary>Something true right now — weather, news — that needs the internet.</summary>
    Web,
}

/// <summary>Recognises the few questions that plainly need a specific tool.</summary>
public static class ToolIntent
{
    /// <summary>Classify <paramref name="question"/>, or <see cref="ToolNeed.None"/>.</summary>
    public static ToolNeed Classify(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return ToolNeed.None;

        // Padded so a phrase match is word-bounded: " weather " never fires inside
        // "whether", " news " never inside "newsagent".
        var q = " " + question!.ToLowerInvariant().Trim() + " ";

        if (IsBattery(q)) return ToolNeed.Battery;
        if (IsWeb(q)) return ToolNeed.Web;
        return ToolNeed.None;
    }

    // "battery" is unambiguous device state. "charge" alone is not ("charge my
    // account", "in charge"), so it is deliberately left out — a missed
    // "is my phone charged" just runs the model, which is safe.
    private static bool IsBattery(string q) => q.Contains(" battery", StringComparison.Ordinal);

    // Only clear "needs the internet, now" intent — the honest bar for a tool that
    // LEAVES THE PHONE. Weather and news are things the model's weights cannot
    // hold; the explicit web phrases are the person asking for a search outright.
    // Bare "search" is excluded — it also means "search my memory / my files".
    private static bool IsWeb(string q) =>
        q.Contains(" weather ", StringComparison.Ordinal)
        || q.Contains(" forecast ", StringComparison.Ordinal)
        || q.Contains(" news ", StringComparison.Ordinal)
        || q.Contains(" latest news", StringComparison.Ordinal)
        || q.Contains(" headlines ", StringComparison.Ordinal)
        || q.Contains(" search the web", StringComparison.Ordinal)
        || q.Contains(" web search", StringComparison.Ordinal)
        || q.Contains(" search online", StringComparison.Ordinal)
        || q.Contains(" look it up online", StringComparison.Ordinal)
        || q.Contains(" on the internet", StringComparison.Ordinal);
}
