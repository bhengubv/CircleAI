// WolverineSentences.cs
//
// WHAT WOLVERINE TELLS THE PERSON, IN ONE PLACE (S6).
//
// "The person is told when something they would notice changed": a smaller brain, a shorter
// conversation, a briefer think, a slower way of computing, a download fetched again, and that
// reports are on. Each is said once through CircleNeuronLinkService.Decided - the service's one
// way of saying things, which a linked app polls and speaks - and in the voice AssistantPersona
// sets: plain, first person, what it did and what it means for them. A knob nobody would notice
// (working memory held at fewer bits) is changed quietly and only logged.
//
// One owner, because the same fact with two owners always ends up with two answers.

namespace CircleAI.Assistant;

/// <summary>The sentences Wolverine says when it changes something a person would notice.</summary>
public static class WolverineSentences
{
    /// <summary>It stepped down to a smaller brain for a while.</summary>
    public const string SmallerBrain =
        "I've moved to a smaller brain for a while, because the bigger one ran into trouble on this phone. I'll try the bigger one again later.";

    /// <summary>It holds a shorter conversation for now.</summary>
    public const string ShorterContext =
        "I'm keeping our conversations a little shorter for now, so I don't run out of room on this phone.";

    /// <summary>It thinks for less before answering.</summary>
    public const string TighterThinking =
        "I'll think a little less before I answer for now, so you always get an answer.";

    /// <summary>It runs on fewer cores.</summary>
    public const string FewerThreads =
        "I'm thinking on fewer of this phone's cores for now, to stay steady. Answers may be a little slower.";

    /// <summary>It switched to kernels without vector instructions.</summary>
    public const string ScalarKernels =
        "I've switched to a slower but safer way of working, because the fast one isn't supported on this phone. Answers may take longer.";

    /// <summary>A model file arrived damaged and is being fetched again.</summary>
    public const string Redownloading =
        "Something I downloaded arrived damaged, so I'm fetching it again.";

    /// <summary>That reports go upstream - said once, at the first start.</summary>
    public const string ReportsOn =
        "When something goes wrong I send an anonymous report so it can be fixed on every phone. Nothing you say is ever in it, and you can switch the reports off.";

    /// <summary>That reports are off, after the person switched them off.</summary>
    public const string ReportsOff = "I've stopped sending reports, and deleted the ones that were waiting.";
}
