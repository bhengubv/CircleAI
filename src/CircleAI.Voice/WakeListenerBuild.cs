// WakeListenerBuild.cs
//
// What a running wake listener was built from, so it can be asked whether it has
// gone stale.
//
// WHY THIS EXISTS. A wake listener is built ONCE, from a keywords file, and then
// holds that phrase as a compiled graph for the life of the process. Nothing
// re-reads the file. So "what the phone listens for" had three owners:
//
//   1. the settings table          wake.chosen.en   = "Hey Circle AI"
//   2. the keywords file on disk   wake-en.txt      = "Hey Circle AI"
//   3. the graph inside the spotter                 = "Hey B"
//
// The screen showed (1). The write-through fixed (2). Nobody owned (3), and (3)
// is the only one the microphone actually consults.
//
// MEASURED, NOT REASONED. On a P30 on 2026-09-06, "Hey Circle AI" was chosen in
// Settings; the screen said so, the file said so, and the log said
// `kws: 'en' listens for "Hey Circle AI" (8 tokens, Good)`. Six minutes of
// somebody saying "Hey Circle AI" into it produced:
//
//     closest="Hey B" 2/3 tokens p=0,033 (threshold 0,2)
//
// Three tokens. The phone had been renamed everywhere except in the one place
// that hears.
//
// THE STALENESS TOKEN USED TO BE THE LANGUAGE, which is a PROXY for the phrase
// and a coarser one. Changing English to Japanese rebuilt the listener; changing
// "Hey B" to "Hey Circle AI" did not, because both are English. A proxy that is
// coarser than the fact will always have a case it cannot see. This records the
// fact: the file the graph was compiled from, and what was in it.

using System;
using System.IO;
using System.Security.Cryptography;

namespace CircleAI.Voice;

/// <summary>
/// What a wake listener was built from — the language and the keywords file —
/// and enough of that file to notice when it changes underneath.
/// </summary>
public readonly record struct WakeListenerBuild
{
    /// <summary>Nothing has been built yet.</summary>
    public static WakeListenerBuild None => default;

    /// <summary>
    /// Recorded when the requested keywords file is not there.
    /// </summary>
    /// <remarks>
    /// DISTINCT FROM "COULD NOT TELL", which is null. A file that is absent is a
    /// fact worth holding: when it later appears - the owner opens the phrase
    /// screen for the first time on a phone that had been running the bundle's
    /// own keywords - that IS a change, and the listener has to be rebuilt to
    /// pick it up.
    /// </remarks>
    public const string Absent = "absent";

    private WakeListenerBuild(string? language, string? keywords, string? contents)
    {
        Language = language;
        Keywords = keywords;
        Contents = contents;
    }

    /// <summary>The language the listener was built for.</summary>
    public string? Language { get; }

    /// <summary>The keywords file it was asked to use, or null for the bundle's own.</summary>
    public string? Keywords { get; }

    /// <summary>A fingerprint of that file as it was when the listener was built.</summary>
    public string? Contents { get; }

    /// <summary>Record what a listener has just been built from.</summary>
    /// <param name="language">The language the listener was built for.</param>
    /// <param name="keywords">
    /// The file the caller ASKED for, not the one that was resolved after
    /// falling back. The request is what a later caller can repeat and compare
    /// against; the resolution cannot be, because a fallback to the bundle's own
    /// keywords would then read as a permanent change and rebuild on every start.
    /// </param>
    public static WakeListenerBuild Of(string? language, string? keywords)
        => new(language, keywords, Fingerprint(keywords));

    /// <summary>
    /// True when what is on disk no longer matches what this was built from.
    /// </summary>
    /// <remarks>
    /// SILENCE IS NOT STALENESS. If the file cannot be read at all right now -
    /// permissions, a half-written save, storage unmounted - this answers false
    /// and leaves the running listener alone. Rebuilding stops the microphone and
    /// loads a model, and doing that on a transient read error would turn a
    /// hiccup into a phone that stops answering. Leaving the previous wake word
    /// running is the safe direction, which is the same choice
    /// <c>ResidentWakeWord.KeywordsFor</c> already makes.
    /// </remarks>
    public bool IsStaleFor(string? language, string? keywords)
    {
        if (!string.Equals(Language, language, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(Keywords, keywords, StringComparison.Ordinal)) return true;

        var now = Fingerprint(keywords);
        return now is not null && !string.Equals(Contents, now, StringComparison.Ordinal);
    }

    /// <summary>What is in the file, short enough to hold and exact enough to compare.</summary>
    /// <remarks>
    /// THE CONTENTS, NOT THE TIMESTAMP. A keywords file holds one phrase and is
    /// rewritten in full every time somebody chooses; length and modified-time
    /// can both repeat across two different phrases of the same size written in
    /// the same instant, and the failure that would cause is exactly the one this
    /// type exists to end. Hashing a file this small costs nothing worth counting.
    /// </remarks>
    private static string? Fingerprint(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            if (!File.Exists(path)) return Absent;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..16];
        }
        catch
        {
            // Could not tell. See IsStaleFor: this is deliberately not "changed".
            return null;
        }
    }
}
