// AskSentences.cs
//
// WHAT A PERSON IS TOLD WHEN A QUESTION IS NOT ANSWERED YET, OR AT ALL.
//
// The link used to put the raw exception message into the reply - "The operation was
// canceled." is what a person read when memory pressure cancelled their answer - and
// every non-answer from a cold start was the same CouldNotStart sentence, whether the
// model was still loading, missing, or broken. Each reason has its own sentence now,
// and the record carries the kind, so an app can do better than read it out.

using CircleAI.Assistant.Lifecycle;

namespace CircleAI.Assistant.Requests;

/// <summary>Plain sentences for why a question is waiting or was not answered.</summary>
public static class AskSentences
{
    /// <summary>The model is loading; the question waits in line.</summary>
    public const string StillLoading = "I'm still getting my brain ready - I'll answer in a moment.";

    /// <summary>No model on the phone yet.</summary>
    public const string NotSetUpYet = "My brain isn't on this phone yet. Open Circle AI to finish setting me up, and I'll answer then.";

    /// <summary>Android stopped the work; it resumes by itself.</summary>
    public const string PausedByAndroid = "Your phone paused me for a moment. I'll pick your question up again as soon as I can.";

    /// <summary>Not enough free memory right now.</summary>
    public const string WaitingForMemory = "Your phone is short of memory right now. I'll answer as soon as some frees up.";

    /// <summary>Damaged files are being fetched again.</summary>
    public const string RepairingFiles = "Part of my brain needs repairing. I'm fixing it, and I'll answer when it's done.";

    /// <summary>A permission or grant is needed first.</summary>
    public const string NeedsPermission = "I need your permission for that first. Open Circle AI to allow it.";

    /// <summary>Too many questions waiting.</summary>
    public const string Busy = "I've got a few questions to get through first. Ask me again in a moment.";

    /// <summary>The question is longer than the service accepts.</summary>
    public const string TooLong = "That's too long for me to take in at once. Could you ask it in a shorter way?";

    /// <summary>It ran out of time.</summary>
    public const string TimedOut = "That took me too long, so I stopped. Could you ask me again?";

    /// <summary>The engine crashed and could not recover in time.</summary>
    public const string WorkerCrashed = "Something went wrong while I was thinking about that. Could you ask me again?";

    /// <summary>The caller withdrew it.</summary>
    public const string Cancelled = "Okay, I've stopped.";

    /// <summary>It waited too long to still be wanted.</summary>
    public const string Expired = "I couldn't get to that in time. Ask me again whenever you like.";

    /// <summary>Anything else.</summary>
    public const string CouldNotAnswer = "I couldn't answer that just now. Could you ask me again?";

    /// <summary>The sentence for a failure kind.</summary>
    public static string For(FailureKind kind) => kind switch
    {
        FailureKind.AndroidStopped      => PausedByAndroid,
        FailureKind.AndroidTimeout      => PausedByAndroid,
        FailureKind.OutOfMemory         => WaitingForMemory,
        FailureKind.NativeWorkerCrashed => WorkerCrashed,
        FailureKind.ModelFilesDamaged   => RepairingFiles,
        FailureKind.PermissionMissing   => NeedsPermission,
        FailureKind.NotInstalled        => NotSetUpYet,
        FailureKind.Cancelled           => Cancelled,
        FailureKind.TimedOut            => TimedOut,
        FailureKind.Rejected            => Busy,
        _                               => CouldNotAnswer,
    };
}
