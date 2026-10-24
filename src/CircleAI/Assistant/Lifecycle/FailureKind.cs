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
// model is held against the model. A death nothing can explain, inside a load, used to
// be held against it too; now it holds the next load off instead (FailurePolicy.HoldOff).

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
    /// ONLY A CRASH STRIKES: the native worker dying while it held the model (loading,
    /// warming or generating) - that is the model or its runtime breaking. Everything
    /// else - Android stopping the service, a time limit, memory, damaged files, a
    /// missing permission, our own deadlines - is about the moment or the device, and
    /// refusing the model for it would refuse the only brain a phone can run.
    /// <para>
    /// A DEATH NOTHING CAN EXPLAIN NO LONGER STRIKES. It did, inside a load or warm-up,
    /// to keep the two-strike protection on a phone that cannot say why a process died
    /// - and that phone is exactly where it went wrong. Measured on the P30 on
    /// 2026-10-23, a fresh install: EMUI's iAware killed the service and the inference
    /// process together for memory ("iAwareF[LowMem]") twice while Qwen3.5-0.8B loaded.
    /// API 29 keeps no exit record, both deaths read Unknown, and the second refused the
    /// only brain on the phone for good; the question asked waited, unanswered, until it
    /// expired. A native crash still leaves evidence - an exit record from API 30, a
    /// signal in the crash buffer on API 29 - so it still strikes. A death with none
    /// holds the next load off instead: see <see cref="HoldOff"/>.
    /// </para>
    /// </remarks>
    public static bool CountsAgainstModel(FailureKind kind, WorkPhase phase) => kind switch
    {
        FailureKind.NativeWorkerCrashed =>
            phase is WorkPhase.Load or WorkPhase.Warm or WorkPhase.Prefill or WorkPhase.Decode,
        _ => false,
    };

    /// <summary>Whether a load that ended this way makes the next load of that model wait (<see cref="HoldOff"/>).</summary>
    /// <remarks>
    /// Memory, and deaths nothing can explain - which on the P30 are memory too. Not an
    /// update or a reboot: those were one moment, and the next load is fine.
    /// </remarks>
    public static bool HoldsOff(FailureKind kind, WorkPhase phase) =>
        (phase is WorkPhase.Load or WorkPhase.Warm) && (kind is FailureKind.Unknown or FailureKind.OutOfMemory);

    /// <summary>How long the next load of a model waits after loads that stopped this way.</summary>
    /// <param name="stoppedLoadsInARow">Loads of the model that stopped in a row; 0 is none.</param>
    /// <remarks>
    /// THE BRAKE THAT REPLACED THE REFUSAL. Without one, a phone killing the service for
    /// memory on every load is loaded into again at the next question - the P30 died
    /// twice in four minutes that way - and every kill takes other apps down with it. So
    /// each further try waits longer: 30 s, 2 min, 10 min, 30 min, then hourly. The count
    /// is kept on disk, because the death that matters takes the process with it; a clean
    /// load clears it.
    /// </remarks>
    public static TimeSpan HoldOff(int stoppedLoadsInARow) => stoppedLoadsInARow switch
    {
        <= 0 => TimeSpan.Zero,
        1    => TimeSpan.FromSeconds(30),
        2    => TimeSpan.FromMinutes(2),
        3    => TimeSpan.FromMinutes(10),
        4    => TimeSpan.FromMinutes(30),
        _    => TimeSpan.FromHours(1),
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
