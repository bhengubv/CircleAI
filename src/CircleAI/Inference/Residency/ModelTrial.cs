// ModelTrial.cs
//
// WHAT HAPPENED WHEN THIS PHONE ACTUALLY TRIED IT.
//
// Every gate in this product so far has been built from predictions - a
// MinRamGb somebody typed into JSON, an arithmetic rule over it, and a constant
// chosen to stop the last crash. On 2026-10-10 that approach ended with a
// 3.87 GB handset running a 0.6B model, because each failure made the rule
// stricter and nothing ever made it looser.
//
// The asymmetry is the defect. CrashVerdict records "this killed me" and
// nothing records "this worked, and it cost X". A system that can only learn
// no converges on doing nothing.
//
// So this is the other half: a dated, device-local record of an ATTEMPT, with
// the figures that were true at the time. It is evidence, not metadata - and
// unlike MinRamGb it cannot be wrong about this phone, because this phone is
// where it came from.

using System;

namespace CircleAI.Inference.Residency;

/// <summary>How an attempt to run a model ended.</summary>
public enum TrialOutcome
{
    /// <summary>Never attempted here.</summary>
    Untried,

    /// <summary>It loaded and answered. The only outcome that is evidence of capability.</summary>
    Loaded,

    /// <summary>
    /// The process died trying - OOM, the OS reclaiming, a native abort.
    /// </summary>
    /// <remarks>
    /// NOT PERMANENT ON ITS OWN. A kill at 400 MB free says nothing about the
    /// same model at 2 GB free, and treating one as the other is how a phone
    /// talks itself down to the smallest model it owns. Two kills is a verdict;
    /// one is a bad moment.
    /// </remarks>
    Killed,

    /// <summary>It ran, and was too slow to be worth it.</summary>
    /// <remarks>
    /// A SEPARATE OUTCOME FROM Killed BECAUSE IT NEEDS THE OPPOSITE RESPONSE.
    /// A killed model might work when the phone is quiet; a model that answers
    /// at 0.3 tokens/second will be just as useless then. Speed failures are
    /// durable in a way memory failures are not.
    /// </remarks>
    TooSlow,

    /// <summary>Refused before it was tried - it could never fit this device.</summary>
    Refused,
}

/// <summary>One attempt by THIS device to run one model, and what it cost.</summary>
/// <param name="ModelId">The model attempted.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="At">When, so a stale trial can be re-run after the device changes.</param>
/// <param name="FreeRamGbAtStart">
/// What was actually free when the attempt began. The single most important
/// field: it is what makes a failure conditional rather than final.
/// </param>
/// <param name="PeakResidentGb">
/// The most memory it held, measured. Zero when the attempt never got far
/// enough to measure - which is itself information, and must not be read as
/// "it cost nothing".
/// </param>
/// <param name="TokensPerSecond">Observed decode rate; zero if it never answered.</param>
/// <remarks>
/// FOUR FACTS, NOT ONE SCALAR. MinRamGb tried to answer "can I own this" and
/// "can I load this right now" with a single number and was wrong in both
/// directions on the same day. These are separate because they are separate
/// questions, and each one is observable.
/// </remarks>
public sealed record ModelTrial(
    string ModelId,
    TrialOutcome Outcome,
    DateTimeOffset At,
    double FreeRamGbAtStart,
    double PeakResidentGb = 0,
    double TokensPerSecond = 0)
{
    /// <summary>Evidence that this device can run it.</summary>
    public bool Worked => Outcome == TrialOutcome.Loaded;

    /// <summary>
    /// Evidence that it failed for a reason that might not hold next time.
    /// </summary>
    /// <remarks>
    /// Only a memory kill is conditional. TooSlow and Refused are properties of
    /// the pairing, not of the moment.
    /// </remarks>
    public bool FailedForNow => Outcome == TrialOutcome.Killed;
}
