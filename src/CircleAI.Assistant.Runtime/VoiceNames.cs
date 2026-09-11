// VoiceNames.cs
//
// The catalogue IDs of the voices this sample asks for, and nothing else.
//
// They lived on ItSpeaker, which is the speech ENGINE and is compiled only when
// voice is switched on. FirstRun — shared with the console head, always
// compiled — names those voices while listing what a first run should fetch, so
// the chat-only Android build failed with "the type or namespace name 'Voice'
// does not exist in the namespace 'CircleAI.Samples.It'". That error reads as a
// missing using; it was a missing Compile item, for a dependency that should
// never have existed. A screen naming a download does not need the engine that
// plays it.
//
// ItSpeaker takes its constants from here, so each name is still defined once.

namespace CircleAI.Samples.It;

/// <summary>Catalogue IDs of the voices this sample asks for.</summary>
public static class VoiceNames
{
    /// <summary>The multi-speaker voice covering the South African languages.</summary>
    /// <remarks>
    /// IT WAS CALLED "Preferred", AND THAT NAME WAS THE BUG. Preferred by whom?
    /// It is fetched on every handset in the world, so a phone set to Japanese
    /// downloaded English and eleven South African languages and no Japanese
    /// voice at all - and the loading screen then presented that as what the
    /// device could do. The reasoning for the pair is sound and technical
    /// (see FirstRun.Plan: this voice is grapheme-driven and right for the South
    /// African languages, structurally wrong for English) but "preferred" quietly
    /// turned a home-market default into a claim about everybody.
    /// <para>
    /// Named for what it IS. What a given phone should also fetch is now decided
    /// from that phone's own language - see <see cref="FirstRun.WantedFor"/>.
    /// </para>
    /// </remarks>
    public const string SouthAfrican = "Vits-11ZA";

    /// <summary>The English voice.</summary>
    public const string English = "Piper-en_US-lessac-medium";
}
