// FailureKind.cs
//
// WHY SOMETHING STOPPED, AS ONE ANSWER EVERY SURFACE SHARES.
//
// Before this there were three answers and none of them was a kind. The brain host
// wrote free text into a Status string ("failed to load: ..."), the link put a raw
// exception message into LinkTurnReply.Error, and CrashVerdict turned every death
// that left a breadcrumb into a strike against the model - so on the P30 (API 29,
// no ApplicationExitInfo) an `adb install -r` during a load, a reboot and the low-
// memory killer all counted against Qwen3.5 exactly as a native abort would, and two
// of them refused the only brain the phone could run.
//
// A KIND IS WHAT DECIDES WHAT HAPPENS NEXT, and the rules are in FailurePolicy, in one
// place: Android stopping the service or a platform time limit is never the model's
// fault; running out of memory means "not now", not "defective"; damaged files are
// repaired, not refused; only a crash of the native worker while it was working on a
// model - or a death nothing can explain, inside a load - is held against the model.

using System;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Why a piece of work stopped. Appended, never renumbered: it crosses the link by name.</summary>
public enum FailureKind
{
    /// <summary>Nothing could tell what happened. Treated cautiously, never as certain.</summary>
    Unknown = 0,

    /// <summary>
    /// Android ended the process on purpose: a reboot, an app update, the person
    /// stopping it, a permission change, or a process it depended on dying.
    /// </summary>
    AndroidStopped = 1,

    /// <summary>A platform time budget ran out - a foreground-service time limit or an ANR.</summary>
    AndroidTimeout = 2,

    /// <summary>The phone ran out of memory: the low-memory killer, or a load refused for memory.</summary>
    OutOfMemory = 3,

    /// <summary>The inference worker died by a signal or a native abort while it was working.</summary>
    NativeWorkerCrashed = 4,

    /// <summary>A model file is missing, truncated or fails its hash.</summary>
    ModelFilesDamaged = 5,

    /// <summary>A permission or link grant the work needs is not held.</summary>
    PermissionMissing = 6,

    /// <summary>There is no usable model on this device yet.</summary>
    NotInstalled = 7,

    /// <summary>The caller, or the service on its behalf, cancelled the work.</summary>
    Cancelled = 8,

    /// <summary>One of our own deadlines ran out - waiting in the queue, or generating.</summary>
    TimedOut = 9,

    /// <summary>Refused at the door: the queue was full, the question too long, or not permitted.</summary>
    Rejected = 10,

    /// <summary>A managed exception in our own code, with no platform cause behind it.</summary>
    ManagedError = 11,

    /// <summary>
    /// We ended the worker on purpose, to prove a question survives it - the soak's fault.
    /// </summary>
    /// <remarks>
    /// NOT <see cref="TimedOut"/>, WHICH IT WAS. The test kill borrowed the watchdog's "we
    /// ended it" flag, so on 2026-10-17 at hour 8 of the soak the question it interrupted
    /// was failed as a hung answer - never retried - instead of being put back in line,
    /// which is the one thing the fault exists to prove.
    /// </remarks>
    FaultInjected = 12,
}

/// <summary>Which part of the work was running when it stopped.</summary>
public enum WorkPhase
{
    /// <summary>Nothing was running.</summary>
    None = 0,

    /// <summary>The process was starting up.</summary>
    Startup = 1,

    /// <summary>Checking that a model's files are whole.</summary>
    Verify = 2,

    /// <summary>Reading a model's weights into memory.</summary>
    Load = 3,

    /// <summary>The first prefill after a load.</summary>
    Warm = 4,

    /// <summary>Reading a question (prompt processing).</summary>
    Prefill = 5,

    /// <summary>Writing an answer (token generation).</summary>
    Decode = 6,

    /// <summary>Fetching a model.</summary>
    Download = 7,

    /// <summary>Loaded and waiting for work.</summary>
    Idle = 8,
}

/// <summary>How sure the classification is.</summary>
public enum Certainty
{
    /// <summary>No evidence either way.</summary>
    Unknown = 0,

    /// <summary>Read from circumstantial evidence (memory readings, timing, boot counts).</summary>
    Inferred = 1,

    /// <summary>Android, or our own code, said so directly.</summary>
    Certain = 2,
}

/// <summary>One classified failure.</summary>
/// <param name="Kind">Why it stopped.</param>
/// <param name="Certainty">How sure that is.</param>
/// <param name="Phase">What was running.</param>
/// <param name="ModelId">The model involved, when one was.</param>
/// <param name="Evidence">What the classification was read from - short, text-free of anything a person said.</param>
/// <param name="Detail">One plain line for a log or a developer.</param>
/// <param name="At">When it was noticed.</param>
public sealed record Failure(
    FailureKind Kind,
    Certainty Certainty,
    WorkPhase Phase,
    string? ModelId,
    string Evidence,
    string Detail,
    DateTimeOffset At)
{
    /// <summary>Whether this failure is held against <see cref="ModelId"/>. See <see cref="FailurePolicy"/>.</summary>
    public bool CountsAgainstModel => FailurePolicy.CountsAgainstModel(Kind, Phase);

    /// <summary>Whether the same work may simply be tried again later.</summary>
    public bool Retryable => FailurePolicy.Retryable(Kind);
}

/// <summary>What each kind of failure means for the model and for the work. One owner.</summary>
public static class FailurePolicy
{
    /// <summary>
    /// Whether a failure of this kind, in this phase, is a strike against the model.
    /// </summary>
    /// <remarks>
    /// ONLY TWO THINGS STRIKE. A native crash of the worker while it held the model
    /// (loading, warming or generating) - that is the model or its runtime breaking -
    /// and a death nobody can explain that happened inside a load or warm-up, which
    /// keeps the two-strike protection CrashVerdict was written for on a phone that
    /// cannot say why a process died. Everything else - Android stopping the
    /// service, a time limit, memory, damaged files, a missing permission, our own
    /// deadlines - is about the moment or the device, and refusing the model for it
    /// would refuse the only brain a phone can run.
    /// </remarks>
    public static bool CountsAgainstModel(FailureKind kind, WorkPhase phase) => kind switch
    {
        FailureKind.NativeWorkerCrashed =>
            phase is WorkPhase.Load or WorkPhase.Warm or WorkPhase.Prefill or WorkPhase.Decode,
        FailureKind.Unknown =>
            phase is WorkPhase.Load or WorkPhase.Warm,
        _ => false,
    };

    /// <summary>Whether work that failed this way may be tried again without anyone deciding to.</summary>
    public static bool Retryable(FailureKind kind) => kind switch
    {
        FailureKind.AndroidStopped      => true,
        FailureKind.AndroidTimeout      => true,
        FailureKind.OutOfMemory         => true,
        FailureKind.NativeWorkerCrashed => true,   // bounded by the attempt count, and by the strike rule
        FailureKind.Unknown             => true,
        FailureKind.ModelFilesDamaged   => true,   // after the repair
        FailureKind.FaultInjected       => true,   // what the fault is there to test
        _                               => false,
    };
}
