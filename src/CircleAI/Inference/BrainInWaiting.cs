// BrainInWaiting.cs
//
// THE BRAIN A DEVICE HAS CHOSEN AND CANNOT YET ANSWER WITH, AND WHO ANSWERS MEANWHILE.
//
// "RAM is the turbo, not the engine": a device whose engine computes from flash runs the best brain its flash holds
// (ModelFit.ReadFromFlash), however little of it RAM can keep. What makes such a brain answer is its index - "Download,
// Split, Store, Index, Revise" - and the owner said what building one is: "a background process for hours of course just
// like updating an OS is hours of time" (2026-11-13). An update is prepared behind a phone that goes on working, and
// takes over when it is ready.
//
// MEASURED ON THE P30 ON 2026-11-09, the 27B with no index: "7 tokens in 184232 ms (0.04 tok/s)" and "2 tokens in
// 148292 ms (0.01 tok/s)" - 26 to 74 seconds a token, 6.5 GB read from flash for each. The 2B beside it answers at
// 7 to 8 tokens a second. So until the best brain's index is complete the brain that has been answering goes on
// answering, and the best one is loaded while the phone is idle, where its index gets built (IndexingQueue). The same
// rule the service already keeps for a better model that has not loaded here yet: the working one answers until its
// replacement has passed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Store;

namespace CircleAI.Inference;

/// <summary>What a device does about the brain it chose: who answers, who waits, and what is said of it.</summary>
/// <param name="Answers">The brain that answers.</param>
/// <param name="Waiting">The chosen brain, when it waits on its index and is loaded while the device is idle; else null.</param>
/// <param name="WaitingFile">Its file, which the engine's record of its indexing is named for; null when none waits.</param>
/// <param name="Said">What is said of it, for the log; null when the chosen brain answers.</param>
public readonly record struct WaitingPlan(ModelEntry Answers, ModelEntry? Waiting, string? WaitingFile, string? Said);

/// <summary>What the service's clock does about the brain in waiting.</summary>
public enum SittingMove
{
    /// <summary>Nothing now.</summary>
    Nothing = 0,

    /// <summary>The phone is idle: the brain in waiting is loaded, built small and not warmed, so that its index gets built.</summary>
    LoadForItsIndex = 1,

    /// <summary>The phone is in use again: it is let go with its index as far as it got, and the brain that answers is loaded.</summary>
    LetGoInUse = 2,

    /// <summary>Its index is done and failed its check: it is let go, and the brain that answers is loaded.</summary>
    LetGoFailed = 3,
}

/// <summary>What the service does when a brain lands on the device (BundleModelLoader.ModelLanded).</summary>
public enum TakeUpMove
{
    /// <summary>No brain answers yet: the first load chooses, the brain that landed among the rest.</summary>
    FirstLoadChooses = 0,

    /// <summary>The brain that answers goes on answering; the one that landed is validated, or waits on its index, as the choice said.</summary>
    Stays = 1,

    /// <summary>The brain in waiting is loaded for its index: the clock that sits with it says when it goes, and the next load takes the choice.</summary>
    SittingDecides = 2,

    /// <summary>The choice is another brain than the one that answers: it is loaded once nobody is asking.</summary>
    Switches = 3,
}

/// <summary>What the service knows when its clock asks what to do about the brain in waiting.</summary>
/// <param name="Waiting">The brain in waiting; null when none waits.</param>
/// <param name="Running">The brain loaded and ready now; null when none is.</param>
/// <param name="Loading">Whether a load is in flight.</param>
/// <param name="NobodyAsking">Whether nothing is being asked or waiting to be.</param>
/// <param name="PhoneIdle">Whether the phone is in nobody's hand and well (<see cref="BrainInWaiting.PhoneIdle"/>).</param>
/// <param name="SinceLastTurn">How long since anything was last asked.</param>
/// <param name="Now">The time.</param>
/// <param name="NotBefore">After a load for the index that failed: no other before this.</param>
public readonly record struct SittingNow(string? Waiting, string? Running, bool Loading, bool NobodyAsking, bool PhoneIdle,
    TimeSpan SinceLastTurn, DateTimeOffset Now, DateTimeOffset NotBefore);

/// <summary>Whether a device's chosen brain is ready to answer, and which brain answers while it is not.</summary>
public static class BrainInWaiting
{
    /// <summary>
    /// Whether <paramref name="brain"/> answers as it should on this device now. A brain kept in RAM does once it is
    /// loaded. One the engine reads from flash does when the device could keep every layer of it warm
    /// (<see cref="ModelFit.CouldKeepInRam"/>) - that needs no index, whatever has become of one - or when its index is
    /// complete and passed its check.
    /// </summary>
    /// <param name="brain">The brain's catalogue row.</param>
    /// <param name="probe">The device.</param>
    /// <param name="index">How far the engine's indexing of it has got, from its record; null when there is none.</param>
    public static bool Ready(ModelEntry brain, DeviceProbe probe, IndexingStatus? index)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(probe);
        if (!ModelFit.ReadFromFlash(brain, probe)) return true;

        // A BRAIN THE DEVICE KEEPS IN RAM ANSWERS WITH OR WITHOUT AN INDEX. Every layer of it is warm; an index of it
        // that is part built, not yet measured or failed is simply not served, and every neuron is computed. The index
        // is its turbo. Asked of the index first, a 2B somebody had begun indexing was "not ready", and the old 2B of
        // another engine stood in for the brain the phone answers with.
        if (ModelFit.CouldKeepInRam(brain, probe)) return true;
        return index is { Total: > 0 } begun && begun.Serves;   // complete, and its check passed
    }

    /// <summary>
    /// Whether <paramref name="brain"/> has taken over on this device, so that what it replaces may go: it is ready
    /// (<see cref="Ready"/>), and - where it is a brain that waited on its index - it has answered here, its speed
    /// measured on this device (<paramref name="measuredSpeed"/>, tokens a second; null when it never has).
    /// </summary>
    /// <remarks>
    /// A REPLACEMENT PROVES ITSELF FIRST (CatalogueHousekeeping's rule 5), AND FOR A BRAIN THAT ANSWERS THROUGH AN INDEX
    /// THE PROOF IS AN ANSWER. A complete index says the work is done, not that the brain it serves loads and answers
    /// within what this phone gives it: on the P30 on 2026-11-13 a load of the 27B that warmed was ended twice by the
    /// three minutes a load's step is allowed. Until it has answered, the brain beside it is the phone's only brain.
    /// </remarks>
    public static bool HasTakenOver(ModelEntry brain, DeviceProbe probe, IndexingStatus? index, double? measuredSpeed)
    {
        if (!Ready(brain, probe, index)) return false;
        if (!ModelFit.ReadFromFlash(brain, probe) || ModelFit.CouldKeepInRam(brain, probe)) return true;
        return measuredSpeed is > 0;
    }

    /// <summary>
    /// The brain that answers while <paramref name="best"/> is made ready: the best of <paramref name="others"/> that
    /// this device can keep in RAM (ModelChoice.Best - the Bonsai 2 fleet first, then rank). Null when
    /// <paramref name="best"/> is ready, and when nothing else here can stand in for it - then it answers itself, as
    /// well as the device lets it.
    /// </summary>
    /// <param name="best">The brain the device chose.</param>
    /// <param name="others">The other brains on the device that it may use now and that have loaded on it.</param>
    /// <param name="probe">The device.</param>
    /// <param name="index">How far the engine's indexing of a brain has got; null where there is no record.</param>
    public static ModelEntry? StandIn(ModelEntry best, IEnumerable<ModelEntry> others, DeviceProbe probe,
                                      Func<ModelEntry, IndexingStatus?> index)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(index);
        if (Ready(best, probe, index(best))) return null;

        // A STAND-IN IS A BRAIN THE DEVICE KEEPS IN RAM, whatever its own records say: it is here to answer at once,
        // which is the one thing the brain in waiting cannot do yet.
        return ModelChoice.Best(others.Where(o =>
            !string.Equals(o.Name, best.Name, StringComparison.Ordinal) && ModelFit.CouldKeepInRam(o, probe)));
    }

    /// <summary>
    /// Where a copy of the record of every index whose check failed, or that was built after one did, is kept outside the
    /// engine's own folder - set by a host whose platform clears that folder (the Android service). Null keeps none. The
    /// engine reads the same folder (IndexingQueue.KeptDirectory), for what to build next.
    /// </summary>
    /// <remarks>
    /// THE ENGINE'S FOLDER IS THE APP'S CACHE FOLDER (Path.GetTempPath()), AND A PHONE SHORT OF ROOM CLEARS IT (the owner,
    /// 2026-11-14, of keeping each failed check's record somewhere Android cannot clear it: "Yes, do it"). That record is
    /// what says which share of a brain's neurons was tried and failed; cleared with the cache, the brain's next index
    /// would be built again at a share already tried (IndexingQueue.KeepFailed, <see cref="IndexOf"/>).
    /// </remarks>
    public static string? KeptChecksDirectory
    {
        get => IndexingQueue.KeptDirectory;
        set => IndexingQueue.KeptDirectory = value;
    }

    /// <summary>
    /// How far the engine's indexing of <paramref name="brain"/> has got, from its record in
    /// <paramref name="storeDirectory"/> (the engine's own store folder when none is given) - or, where the engine has
    /// none, from the copy kept in <paramref name="keptDirectory"/> (<see cref="KeptChecksDirectory"/> when none is given).
    /// Null when neither holds anything of it.
    /// </summary>
    public static IndexingStatus? IndexOf(ModelEntry brain, string? storeDirectory = null, string? keptDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var file = FileOf(brain);
        if (file is null) return null;
        return IndexingQueue.ReadStatus(storeDirectory ?? ManagedChatGenerator.DefaultStoreDirectory, file)
               ?? ((keptDirectory ?? KeptChecksDirectory) is { Length: > 0 } kept ? IndexingQueue.ReadStatus(kept, file) : null);
    }

    /// <summary>
    /// How a brain's step up has gone on this device, for the upgrade's climb (ModelChoice.NextStep): failed when nothing
    /// is left to try of it here - its index failed its check with every neuron listed, by this engine
    /// (<see cref="FailedHere"/>) - whether or not its files are still here; not tried when it is not here; passed when it
    /// is here and has taken over - a brain that answers through its index once that index serves and it has answered
    /// (<see cref="HasTakenOver"/>), any other once it has loaded here; else underway.
    /// </summary>
    /// <remarks>
    /// "NO MODELS SHOULD BE BLOCKED - IF IT FAILS THEN CHECK WHY IT FAILED. CLEAN UP AND TRY AGAIN, EVERYTHING DEPENDS ON
    /// THE REASON WHY" (the owner, 2026-11-15). A step failed for good when the device had refused the brain after two
    /// crashes, or its index had failed its check once: the climb ended at it on that phone. A crash refuses nothing now
    /// (CrashVerdict), and an index that failed is followed by one listing more of the brain's neurons
    /// (IndexingQueue's IndexingRungs): such a brain is underway while it is here, and fetched again when it has gone. Only
    /// an index of every neuron that still fails is the end - the engine's fault, which the phone cannot mend - and that
    /// lasts until the engine changes.
    /// </remarks>
    /// <param name="brain">The brain's catalogue row.</param>
    /// <param name="probe">The device.</param>
    /// <param name="present">Whether it is on the device.</param>
    /// <param name="proven">Whether it has loaded on this device.</param>
    /// <param name="index">How far the engine's indexing of it has got, from its record; null when there is none.</param>
    /// <param name="measuredSpeed">The speed this device measured of it, tokens a second; null when it never has.</param>
    public static StepState StepOf(ModelEntry brain, DeviceProbe probe, bool present, bool proven,
                                   IndexingStatus? index, double? measuredSpeed)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(probe);
        if (FailedHere(brain, probe, index)) return StepState.Failed;   // here or gone: nothing is left to try of it
        if (!present) return StepState.NotTried;
        if (ModelFit.AnswersThroughItsIndex(brain, probe))
            return HasTakenOver(brain, probe, index, measuredSpeed) ? StepState.Passed : StepState.Underway;
        return proven ? StepState.Passed : StepState.Underway;
    }

    /// <summary>
    /// What this device's flash measured, in words, to say beside a result: from the profile the engine keeps in
    /// <paramref name="storeDirectory"/> (its own store folder when none is given). Null when it has measured none.
    /// </summary>
    public static string? FlashOf(string? storeDirectory = null)
    {
        try
        {
            return FlashProfile.KeptBy(storeDirectory ?? ManagedChatGenerator.DefaultStoreDirectory)?.Speed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The file a brain of the C# engine is read from, as its row names it: its GGUF. Null for any other row.</summary>
    public static string? FileOf(ModelEntry brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        if (brain.Engine != ModelEngine.Managed || brain.BundleFiles is not { Count: > 0 } files) return null;
        return files.Select(f => f.Name).FirstOrDefault(n => n.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) is { } name
            ? Path.GetFileName(name)
            : null;
    }

    // ---- WHAT THE SERVICE DOES ABOUT IT. Decided here, where each rule is a test on any machine, and carried out by
    // ---- whoever holds the worker: the supervisor sat in the Android head, which no test project can reference, so
    // ---- three of these branches had never run anywhere when the owner asked for their tests (2026-11-13).

    /// <summary>How long nothing must have been asked before the brain in waiting is loaded for its index.</summary>
    public static readonly TimeSpan SitAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The share of its context a brain loaded for its index is built with: a sixteenth. The indexing reads passages of
    /// a hundred and twenty-eight words, and the working memory is reserved for the whole context when a brain is
    /// built - for the 27B's 32,768 words, 750 MB of live heap on a phone with 3.6 GB (measured on the P30, 2026-11-13).
    /// A brain built this small answers nobody: once its index is complete, a question loads it again as one that answers.
    /// </summary>
    public const double ForItsIndexContextShare = 1.0 / 16;

    /// <summary>
    /// How long after a load for the index that failed before the next is tried: ten minutes after the first, twice as
    /// long after each one that follows, two hours at most. A load that succeeds clears the count.
    /// </summary>
    /// <remarks>
    /// On the P30 on 2026-11-13 the first load for the index was ended by the load's own limit, and the clock loaded the
    /// 27B again fifty-five seconds later, into the same end.
    /// </remarks>
    public static TimeSpan SitAgainAfter(int failures) =>
        TimeSpan.FromMinutes(Math.Min(120, 10 * Math.Pow(2, Math.Clamp(failures - 1, 0, 6))));

    /// <summary>
    /// Whether the phone is in nobody's hand and well: nothing says it is in use (<paramref name="inUse"/>; null where
    /// its screen does not say - IndexingWhen.InUse), and it is neither hot nor low.
    /// </summary>
    /// <remarks>
    /// A phone on its cable with Stay awake on is lit for days untouched, and "the screen is off" never saw it idle (the
    /// owner, 2026-11-13: "p30 is idle right? so it should be running"): a screen that does not say is no sign of use.
    /// </remarks>
    public static bool PhoneIdle(bool? inUse, PowerState power)
    {
        ArgumentNullException.ThrowIfNull(power);
        return inUse != true && !power.Hot && !power.Low;
    }

    /// <summary>
    /// Whether a brain's index is done, measured, and under what an index must reach to serve: nothing is left to build,
    /// and it serves nobody. A record that says every unit is done and keeps no measure has its check still to run.
    /// </summary>
    public static bool Failed(IndexingStatus? index) => index is { Complete: true, Agreement: not null, Serves: false };

    /// <summary>
    /// Whether <paramref name="brain"/> answers nobody on this device and nothing is left to try of it: the engine answers
    /// with it through its index (ModelFit.AnswersThroughItsIndex) and that index failed its check with every neuron
    /// listed, by this engine (IndexingStatus.NothingLeftToTry). An index that failed at a smaller share is followed by one
    /// listing more, so its brain waits on that. A brain the device keeps in RAM never has: it answers with or without an
    /// index.
    /// </summary>
    public static bool FailedHere(ModelEntry brain, DeviceProbe probe, IndexingStatus? index)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(probe);
        return ModelFit.AnswersThroughItsIndex(brain, probe) && index is { NothingLeftToTry: true };
    }

    /// <summary>
    /// Whether <paramref name="brain"/>, new to this device, waits on its index before anything is asked of it: the engine
    /// answers with it through its index (ModelFit.AnswersThroughItsIndex) and that index does not serve yet. It is the
    /// brain in waiting (<see cref="Plan"/>), not a brain to validate by questions; one the device keeps in RAM never waits.
    /// </summary>
    /// <remarks>
    /// THREE QUESTIONS WITH NO INDEX MEASURE THE FLASH, NOT THE BRAIN (2026-11-14). The supervisor validated every brain new
    /// to the phone by loading it to answer and asking it three questions, two minutes each. Read from flash with no index,
    /// a brain reads every layer for every word - 26 to 74 seconds a word for the 27B on the P30 - and a load to answer,
    /// built whole and warmed, is the load of the 27B that the three minutes a step is allowed ended twice there. So the 9B
    /// the climb fetches after the 4B waits on its index like any brain in waiting, and the brain beside it answers.
    /// </remarks>
    public static bool WaitsOnItsIndex(ModelEntry brain, DeviceProbe probe, IndexingStatus? index)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(probe);
        return ModelFit.AnswersThroughItsIndex(brain, probe) && !Ready(brain, probe, index);
    }

    /// <summary>
    /// The brain the device goes on with, of the one it chose (<paramref name="chosen"/>): that one, unless it failed here
    /// (<see cref="FailedHere"/>) - then the best of the other brains on the device that has not (ModelChoice.Best). And
    /// what is said of it, for the log; null when the chosen one stands.
    /// </summary>
    /// <remarks>
    /// THE STEP BELOW A BRAIN THAT FAILED IS THE PHONE'S BRAIN (2026-11-14). The selector names the best brain by rank that
    /// the device's flash holds, and knows nothing of an index. On the P30 it named the 27B, whose index failed at 25.0%:
    /// the 2B answered as its stand-in, and the 4B the climb fetches next (ModelChoice.NextStep) would never have been
    /// loaded - a brain that has not loaded here is nobody's stand-in, and the selector never named it. The climb would
    /// have waited on that 4B for ever.
    /// </remarks>
    /// <param name="chosen">The brain the selector chose, after every other rule.</param>
    /// <param name="here">The brains on the device it may use now, whether or not they have loaded here.</param>
    /// <param name="probe">The device.</param>
    /// <param name="index">How far the engine's indexing of a brain has got; null where there is no record.</param>
    public static (ModelEntry Brain, string? Said) PastAFailedIndex(ModelEntry chosen, IEnumerable<ModelEntry> here, DeviceProbe probe,
                                                                 Func<ModelEntry, IndexingStatus?> index)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(index);
        var its = index(chosen);
        if (!FailedHere(chosen, probe, its)) return (chosen, null);

        var measured = string.Create(CultureInfo.InvariantCulture, $"{(its?.Agreement ?? 0) / 10.0:0.0}%");
        var next = ModelChoice.Best(here.Where(e => !string.Equals(e.Name, chosen.Name, StringComparison.Ordinal) && !FailedHere(e, probe, index(e))));
        return next is null
            ? (chosen, $"{chosen.Name}'s index failed its check here with every neuron listed ({measured}), and no other brain is here: it answers as well as it can")
            : (next, $"{chosen.Name}'s index failed its check here with every neuron listed ({measured}): {next.Name} is this phone's brain until the engine changes");
    }

    /// <summary>Whether a brain is warmed when it is built: never one loaded for its index, which answers nobody.</summary>
    public static bool WarmsOnStart(bool forItsIndex, bool asConfigured) => !forItsIndex && asConfigured;

    /// <summary>
    /// The working-memory shape a brain is built with: the one asked for, or - loaded for its index - the engine's own
    /// bits and <see cref="ForItsIndexContextShare"/> of its context, whatever the balance asks of a brain that answers.
    /// </summary>
    public static (KvCompressionMode? KvMode, double ContextShare) LoadShape(bool forItsIndex, KvCompressionMode? kvMode, double contextShare) =>
        forItsIndex ? (null, ForItsIndexContextShare) : (kvMode, contextShare);

    /// <summary>
    /// What the device does about the brain it chose: <paramref name="best"/> answers when it is ready
    /// (<see cref="Ready"/>) or nothing else here can; else the best of <paramref name="others"/> the device keeps in
    /// RAM answers (<see cref="StandIn"/>), and <paramref name="best"/> waits - loaded while the phone is idle, so its
    /// index gets built, or built again listing more of its neurons after a check that failed - unless its index failed
    /// with every neuron listed, which leaves nothing to build until the engine changes.
    /// </summary>
    /// <param name="best">The brain the device chose, after every other rule.</param>
    /// <param name="others">The other brains on it that it may use now and that have loaded on it.</param>
    /// <param name="probe">The device.</param>
    /// <param name="index">How far the engine's indexing of a brain has got; null where there is no record.</param>
    public static WaitingPlan Plan(ModelEntry best, IEnumerable<ModelEntry> others, DeviceProbe probe,
                                   Func<ModelEntry, IndexingStatus?> index)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(index);
        if (StandIn(best, others, probe, index) is not { } standIn) return new WaitingPlan(best, null, null, null);

        // AN INDEX OF EVERY NEURON THAT FAILED ITS CHECK LEAVES NOTHING TO BUILD (IndexingStatus.NothingLeftToTry): the
        // brain does not answer here and is not loaded for it until the engine changes - the brain beside it answers, and
        // that is said as what it is.
        var indexed = index(best);
        if (indexed is { NothingLeftToTry: true } spent)
        {
            return new WaitingPlan(standIn, null, null, string.Create(CultureInfo.InvariantCulture,
                $"{best.Name} is this phone's brain by rank, and its index failed its check with every neuron listed: the same top word as every neuron {(spent.Agreement ?? 0) / 10.0:0.0}% of the time, of the {IndexingStatus.LeastAgreement / 10.0:0}% it must reach - the engine's fault, not this phone's. It does not answer here until the engine changes; {standIn.Name} answers"));
        }

        // ONE THAT FAILED AT A SMALLER SHARE IS FOLLOWED BY ONE LISTING MORE OF ITS NEURONS (IndexingRungs), built while
        // the phone is idle like the first: the brain waits on that.
        var built = indexed is { Failed: true } failed
                ? string.Create(CultureInfo.InvariantCulture, $"being built again listing more of its neurons, the last having measured {(failed.Agreement ?? 0) / 10.0:0.0}% of the {IndexingStatus.LeastAgreement / 10.0:0}% it must reach")
            : indexed is { Complete: true } ? "built and its check is still to run"
            : indexed is { Total: > 0 } begun ? string.Create(CultureInfo.InvariantCulture, $"{begun.Percent}% built")
            : "not begun";
        return new WaitingPlan(standIn, best, FileOf(best),
            $"{best.Name} is this phone's brain and cannot answer yet: its index is {built}. {standIn.Name} answers until it is complete, "
            + $"and {best.Name} is loaded while the phone is idle so that it gets built");
    }

    /// <summary>
    /// What the clock does about the brain in waiting now. Loaded already: it is let go when its index failed its check
    /// with nothing left to try (<paramref name="failedItsCheck"/>; one that failed at a smaller share goes on to the next,
    /// IndexingRungs), or when the phone is in use again and it still cannot answer
    /// (<paramref name="readyToAnswer"/>). Not loaded: it is loaded once the phone is idle, nothing was asked for
    /// <see cref="SitAfter"/>, it is not held off after setbacks (<paramref name="heldOff"/>), and an earlier load that
    /// failed is not being waited out. Each is asked only when the answer decides something.
    /// </summary>
    public static SittingMove Sitting(SittingNow now, Func<bool> failedItsCheck, Func<bool> readyToAnswer, Func<bool> heldOff)
    {
        ArgumentNullException.ThrowIfNull(failedItsCheck);
        ArgumentNullException.ThrowIfNull(readyToAnswer);
        ArgumentNullException.ThrowIfNull(heldOff);
        if (now.Waiting is null || now.Loading || !now.NobodyAsking) return SittingMove.Nothing;

        if (string.Equals(now.Running, now.Waiting, StringComparison.Ordinal))
        {
            // ITS INDEX IS DONE AND FAILED ITS CHECK: nothing is left to build and it answers nobody, so the brain that
            // answers is brought back now, not at the next question's cost.
            if (failedItsCheck()) return SittingMove.LetGoFailed;

            // PICKED UP AGAIN: the brain that answers is brought back before anything is asked. Once its index is
            // complete and passed, the brain in waiting is the brain, and it stays.
            return now.PhoneIdle || readyToAnswer() ? SittingMove.Nothing : SittingMove.LetGoInUse;
        }

        if (!now.PhoneIdle || now.SinceLastTurn < SitAfter || heldOff()) return SittingMove.Nothing;
        return now.Now < now.NotBefore ? SittingMove.Nothing : SittingMove.LoadForItsIndex;
    }

    /// <summary>
    /// What the service does when a brain lands on the device: the choice is made again now (<paramref name="choose"/>), not
    /// at the next load - unless no brain answers yet, when the first load chooses. And what the choice was: the brain that
    /// answers stays, or another is loaded, or the clock that sits with the brain in waiting decides.
    /// </summary>
    /// <remarks>
    /// THE NEXT LOAD NEVER CAME (2026-11-14). The climb fetched the 4B onto the P30 at 15:42:52, the landing asked the brain
    /// to be ready, and the 2B, answering, was: an hour and a half later nothing had validated the 4B, because the
    /// supervisor chooses only when it loads, and nothing had loaded the brain again.
    /// </remarks>
    /// <param name="running">The brain loaded and ready now; null when none is.</param>
    /// <param name="loadedForItsIndex">Whether it is the brain in waiting, loaded for its index.</param>
    /// <param name="choose">The supervisor's choice: which brain answers, which is validated, which waits on its index.</param>
    public static (TakeUpMove Move, string? Choice) TakingUp(string? running, bool loadedForItsIndex, Func<string?> choose)
    {
        ArgumentNullException.ThrowIfNull(choose);
        if (running is null) return (TakeUpMove.FirstLoadChooses, null);

        var choice = choose();
        if (loadedForItsIndex) return (TakeUpMove.SittingDecides, choice);
        return choice is null || string.Equals(choice, running, StringComparison.Ordinal)
            ? (TakeUpMove.Stays, choice)
            : (TakeUpMove.Switches, choice);
    }
}
