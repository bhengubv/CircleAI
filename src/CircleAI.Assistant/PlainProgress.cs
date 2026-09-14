// PlainProgress.cs
//
// Engine log lines, in words a person can read.
//
// THE VOICE PIPELINE REPORTS TO ITSELF. Its progress lines are for whoever is
// debugging it - "voice-under-test=af_ZA-google-nasional", "engine WARM in
// 812ms", "phones=41", "prereq espeak-ng-data" - and one head put them straight
// into a row subtitle on the screen. Somebody choosing a language watched their
// phone print internal diagnostics at them for forty seconds.
//
// The other head translated each one into a phase and had done since it was
// written, in a sample that is being retired.
//
// ORDER MATTERS AND IS NOT ALPHABETICAL. "voice-under-test" has to be tested
// before "voice" or every voice line collapses into "found the voice"; the
// percentage test comes first because a line carrying a percentage is progress
// whatever else it says.
//
// WHAT IT IS NOT: a log. The raw line still belongs in logcat, where the person
// debugging the pipeline is looking. This is what the SCREEN says.

namespace CircleAI.Assistant;

/// <summary>Turning a pipeline's own progress into something worth reading.</summary>
public static class PlainProgress
{
    /// <summary>One short phrase for one engine line.</summary>
    /// <remarks>
    /// A PHASE, NOT A TRANSLATION. Several different lines mean "it is loading
    /// the voice" and collapsing them is the point: a subtitle that changes every
    /// 200 ms is as unreadable as no subtitle at all, and what somebody wants to
    /// know is which of four or five stages it has reached.
    /// </remarks>
    public static string Say(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0) return "getting ready";

        // FIRST: anything carrying a percentage IS the download, whatever words
        // are around it.
        if (s.Contains('%', StringComparison.Ordinal)) return "downloading… " + s;

        if (Starts(s, "prereq")) return "fetching what it needs";
        if (Starts(s, "sideload")) return "importing the voice";
        if (Starts(s, "downloaded")) return "downloaded — loading";

        // BEFORE the bare "voice" test, or every voice line collapses into
        // "found the voice".
        if (Starts(s, "voice-under-test")) return "getting ready";
        if (Starts(s, "voice")) return "found the voice";

        if (Starts(s, "engine"))
            return s.Contains("WARM", StringComparison.Ordinal) ? "voice ready" : "loading the voice";

        if (Starts(s, "phones")) return "sounding out the words";
        if (Starts(s, "saying it")) return "saying it";
        if (Starts(s, "respelt")) return "saying it";
        if (Starts(s, "synthesised")) return "ready to play";

        // Pocket-TTS reports in words already, so it passes through whole.
        if (Starts(s, "pocket:")) return s;

        // ANYTHING UNRECOGNISED IS STILL PROGRESS. Showing the raw line here
        // would put the diagnostics back on screen for exactly the lines nobody
        // thought about, which is where they would be least expected.
        return "getting ready";
    }

    private static bool Starts(string s, string prefix)
        => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
