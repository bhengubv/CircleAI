// ModelPrerequisites.cs
//
// What a model needs that is not itself.
//
// A VOICE CAN NEED SOMETHING THAT IS NOT A VOICE. The Japanese voice, JSUT-VITS,
// speaks Open JTalk phoneme ids - so without the 104 MB naist-jdic dictionary it
// is 144 MB of weights that cannot phonemise a single sentence. The dictionary
// is catalogued separately and deliberately: it is shared by every Japanese
// voice, so it is catalogued once rather than duplicated into each.
//
// THE RULE EXISTED IN ONE CODE PATH AND THE WRONG ONE. CircleAITtsProbe - a
// diagnostic screen - fetched the dictionary, and its own comment explains why
// that mattered: "Cataloguing it was not enough. The entry existed, the
// phonemiser knew where a downloaded copy would live, and NOTHING EVER FETCHED
// IT." CircleAISpeaker, which is the thing that actually speaks, mentions the
// dictionary nowhere at all.
//
// So on a phone set to Japanese the speaker downloaded the voice, asked
// OpenJTalkPhonemizer.Open for a phonemiser, and got null - because Open returns
// null when the dictionary directory is not there. The probe could speak
// Japanese and the app could not.
//
// One fact with two owners, where the second owner did not know it was one. The
// rule lives here now, in the catalogue layer, and every download path asks.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Core.Models;

/// <summary>What a catalogued model needs alongside it to be usable.</summary>
public static class ModelPrerequisites
{
    /// <summary>The Japanese pronunciation dictionary, catalogued once.</summary>
    public const string OpenJTalkDictionary = "OpenJTalk-Dic-ja";

    /// <summary>
    /// The architecture that means "this voice speaks Open JTalk phoneme ids".
    /// </summary>
    public const string OpenJTalkDriven = "vits-espnet";

    /// <summary>
    /// Names of the other catalogue entries <paramref name="entry"/> cannot work
    /// without. Empty for almost everything.
    /// </summary>
    /// <remarks>
    /// KEYED ON ARCHITECTURE, WHICH IS WHAT THE PROBE WANTED AND COULD NOT HAVE.
    /// <c>vits-espnet</c> IS the statement "driven by Open JTalk phonemes", so a
    /// future ESPnet voice in any language pulls the right prerequisite without
    /// anybody remembering to come back here. That was impossible while
    /// <c>ModelEntry</c> dropped the registry's Architecture field on
    /// deserialise, which is why the probe tested the entry NAME instead.
    /// <para>
    /// The name test is kept as a fallback, for an entry whose architecture is
    /// blank. Losing a prerequisite silently costs somebody a voice that will
    /// not speak; carrying a redundant check costs a string comparison.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> For(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var openJTalk =
            string.Equals(entry.Architecture, OpenJTalkDriven, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(entry.Architecture)
                && entry.Name.StartsWith("JSUT", StringComparison.OrdinalIgnoreCase));

        // A model is never its own prerequisite. The dictionary itself is
        // catalogued as a Phonemizer, not as a voice, but a future entry that
        // shares the architecture marker would otherwise ask for itself.
        if (openJTalk &&
            !string.Equals(entry.Name, OpenJTalkDictionary, StringComparison.OrdinalIgnoreCase))
            return [OpenJTalkDictionary];

        return [];
    }

    /// <summary>
    /// The prerequisite entries for <paramref name="entry"/>, resolved against
    /// the catalogue. Anything the catalogue does not name is skipped.
    /// </summary>
    /// <remarks>
    /// SKIPPED RATHER THAN THROWN. A prerequisite that has been renamed out of
    /// the catalogue must not stop a voice downloading - the voice may still be
    /// usable, and a hard failure here would take a working capability away over
    /// a bookkeeping error. The absence is worth logging; it is not worth
    /// refusing.
    /// </remarks>
    public static IReadOnlyList<ModelEntry> Resolve(
        ModelEntry entry, IReadOnlyList<ModelEntry>? catalogue)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (catalogue is null || catalogue.Count == 0) return [];

        var wanted = For(entry);
        if (wanted.Count == 0) return [];

        return [.. catalogue.Where(c => wanted.Any(
            w => string.Equals(w, c.Name, StringComparison.OrdinalIgnoreCase)))];
    }
}
