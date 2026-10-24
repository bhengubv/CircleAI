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

    // ── the brain could not start: FINAL sentences, which promise nothing later ──

    /// <summary>No brain on the phone it can run, and nothing will change that by itself.</summary>
    public const string NoBrainItCanRun =
        "I can't answer that. There is no brain on this phone I can run. Say “try the big one again” and I'll give it another go.";

    /// <summary>Setup is still bringing the brain, and the question's wait is used up.</summary>
    public const string StillSettingUp =
        "My brain is still being set up on this phone, so I can't answer that yet. Ask me again in a few minutes.";

    /// <summary>The phone is too short of memory to start the brain.</summary>
    public const string TooShortOfMemory =
        "Your phone is too short of memory for my brain right now, so I couldn't answer that. Close a few apps and ask me again.";

    /// <summary>Android kept stopping the brain while it started.</summary>
    public const string StoppedWhileStarting =
        "Your phone stopped me while I was getting my brain ready, so I couldn't answer that. Ask me again in a moment.";

    /// <summary>Damaged files are still being repaired.</summary>
    public const string StillRepairing =
        "Part of my brain is still being repaired, so I couldn't answer that. Ask me again in a few minutes.";

    /// <summary>The sentence for a failure kind.</summary>
    public static string For(FailureKind kind) => kind switch
    {
        FailureKind.AndroidStopped      => PausedByAndroid,
        FailureKind.AndroidTimeout      => PausedByAndroid,
        FailureKind.OutOfMemory         => WaitingForMemory,
        FailureKind.NativeWorkerCrashed => WorkerCrashed,
        FailureKind.FaultInjected       => WorkerCrashed,
        FailureKind.ModelFilesDamaged   => RepairingFiles,
        FailureKind.PermissionMissing   => NeedsPermission,
        FailureKind.NotInstalled        => NotSetUpYet,
        FailureKind.Cancelled           => Cancelled,
        FailureKind.TimedOut            => TimedOut,
        FailureKind.Rejected            => Busy,
        _                               => CouldNotAnswer,
    };

    /// <summary>
    /// The sentence a question ENDS with when the brain could not start for it.
    /// </summary>
    /// <param name="kind">Why the brain could not start.</param>
    /// <param name="nothingComing">Nothing will change by itself - every model here is refused.</param>
    /// <remarks>
    /// NOT THE WAITING SENTENCES ABOVE, which promise an answer later ("and I'll answer
    /// then"). A waiting question's sentence never reaches the app - it shows only an
    /// answer, or the sentence of a question that has ended - so on the P30 on 2026-10-23
    /// the person watched typing dots for three minutes and was told nothing. These end
    /// the question, so they say why and what to do, and promise nothing.
    /// </remarks>
    public static string BrainCouldNotStart(FailureKind kind, bool nothingComing = false) => kind switch
    {
        FailureKind.NotInstalled      => nothingComing ? NoBrainItCanRun : StillSettingUp,
        FailureKind.OutOfMemory       => TooShortOfMemory,
        FailureKind.AndroidStopped    => StoppedWhileStarting,
        FailureKind.AndroidTimeout    => StoppedWhileStarting,
        FailureKind.Unknown           => StoppedWhileStarting,   // a load that stopped with nothing to say why
        FailureKind.ModelFilesDamaged => StillRepairing,
        _                             => For(kind),
    };
}
