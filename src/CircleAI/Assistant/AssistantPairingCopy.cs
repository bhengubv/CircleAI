// AssistantPairingCopy.cs
//
// The words for "half of this is missing", where both halves of the build can see them.
//
// ONE OWNER, AND IT HAD TO MOVE TO BE ONE. These sentences started in
// ServicePairing, which is Android-only - so the screen that shows them could not
// reference it, and the obvious fix was to retype them in the component. Two copies
// of a sentence drift the first time somebody improves one of them, and then a
// person is told about two different things that are the same thing.
//
// HERE RATHER THAN IN THE CLIENT, because CircleAI.Assistant has no project
// references at all and every head can see it. Strings need nothing else.

namespace CircleAI.Assistant;

/// <summary>What to call the missing half of Circle AI, and what to say about it.</summary>
public static class AssistantPairingCopy
{
    /// <summary>What the person is being asked to add, in their words.</summary>
    /// <remarks>
    /// NOT "THE SERVICE", WHICH MEANS NOTHING TO ANYBODY. It is the part that does
    /// the thinking, it is large because the models are in it, and it has no icon -
    /// all three are things somebody would reasonably want to know before agreeing
    /// to a download, and all three are true.
    /// </remarks>
    public const string Name = "Circle AI's brain";

    /// <summary>Why it is a separate thing at all, in one sentence.</summary>
    public const string Why =
        "Circle AI comes in two parts. This one is the screens; the other is the brain " +
        "that answers you, and it is large because the models live in it. " +
        "It runs on the phone with no signal and no account, and it has no icon of its own.";

    /// <summary>How to get it, on a phone with nothing else on it.</summary>
    /// <remarks>
    /// THE ONLY ROUTE THAT NEEDS NOTHING. Said plainly because the person reading it
    /// may have no data left, and every other answer quietly assumes they do. A
    /// store needs a store and a signal; a download needs a signal; the mesh needs
    /// AetherNetService, which is another install on a phone that has nothing - the
    /// same problem one step along. Somebody handing over the file needs none of it.
    /// </remarks>
    public const string HowToGet =
        "Anyone who already has Circle AI can send it to you from their phone, over " +
        "Bluetooth if that is all you have. No shop, no account, no data.";
}
