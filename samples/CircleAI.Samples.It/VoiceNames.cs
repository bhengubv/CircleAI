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
    /// <summary>The multi-speaker, multi-lingual South African voice.</summary>
    public const string Preferred = "Vits-11ZA";

    /// <summary>The English voice.</summary>
    public const string English = "Piper-en_US-lessac-medium";
}
