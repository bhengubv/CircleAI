// CatalogueBootstrap.cs
//
// First-run + every-launch startup for the runtime catalogue: seed an empty
// catalogue from the embedded registry (offline bootstrap), re-assess every row
// against the current device, and SURFACE the result to the observer — so the
// self-heal log (and Wolverine) sees "N of M models can run here", and, when the
// device can run nothing, hears the "outdated model" symptom even before any feed
// is pulled. Idempotent: the seed only fills an empty catalogue.
//
// Every step logs what it did and any failure — a bootstrap that swallowed its
// errors would hide exactly the "why is there no model" question a person asks.

using System;
using System.IO;
using CircleAI.Core;
using CircleAI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Inference;

/// <summary>Seeds, re-assesses, and surfaces the catalogue at startup.</summary>
public static class CatalogueBootstrap
{
    /// <summary>
    /// Seed (if empty), assess against <paramref name="probe"/>, and report to
    /// <paramref name="observer"/> + <paramref name="logger"/>. Returns the
    /// assessment. Never throws for a seed/observer hiccup — a startup step must
    /// not take the app down — but never swallows one silently either.
    /// </summary>
    /// <param name="catalog">The catalogue to bootstrap.</param>
    /// <param name="assessor">The device assessor.</param>
    /// <param name="probe">The current device snapshot.</param>
    /// <param name="observer">Where to surface the result; defaults to no-op.</param>
    /// <param name="seedFrom">Registry to seed from; defaults to the embedded one.</param>
    /// <param name="logger">Verbose diagnostics; defaults to a no-op logger.</param>
    /// <param name="modelsDirectory">
    /// Directory whose <c>&lt;modelId&gt;/installed.json</c> markers set the
    /// <c>installed</c> flag; defaults to <see cref="ModelPaths.Default"/>.
    /// </param>
    /// <param name="reclaimInferior">
    /// When <c>true</c>, reclaim installed models that a better same-modality model
    /// already on disk has superseded (see <see cref="CatalogueHousekeeping"/>).
    /// Runs only off a measured probe. Off by default.
    /// </param>
    /// <param name="keep">
    /// Models housekeeping must not touch - the one the brain is running or loading.
    /// </param>
    /// <param name="indexDirectory">
    /// Where the engine records how far a brain's indexing has got; the engine's own store folder when null.
    /// Housekeeping reads it: a brain whose index is still being built replaces nothing yet.
    /// </param>
    /// <param name="keptDirectory">
    /// Where a copy of the record of every failed check is kept, outside the engine's folder;
    /// BrainInWaiting.KeptChecksDirectory when null, and none is kept when that is null too.
    /// </param>
    public static AssessmentResult Run(
        IModelCatalog catalog,
        IModelAssessor assessor,
        DeviceProbe probe,
        IModelCatalogObserver? observer = null,
        ModelRegistryService? seedFrom = null,
        ILogger? logger = null,
        string? modelsDirectory = null,
        bool reclaimInferior = false,
        Func<string, bool>? keep = null,
        string? indexDirectory = null,
        string? keptDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assessor);
        ArgumentNullException.ThrowIfNull(probe);
        var obs = observer ?? NullModelCatalogObserver.Instance;
        var log = logger ?? CircleAI.Core.Logging.CircleLog.Logger("Models.Catalogue");

        try
        {
            var seeded = CatalogueSeed.SeedFromEmbedded(catalog, seedFrom);
            if (seeded > 0)
                log.LogInformation("Model catalogue: seeded {Count} row(s) from the embedded registry.", seeded);
            else
            {
                // AN EXISTING CATALOGUE IS REFRESHED, NOT LEFT AS IT WAS ON THE DAY IT
                // WAS SEEDED. Before the assessor runs, so a corrected rank or size is
                // what it judges - see CatalogueSeed.ReSyncFromEmbedded.
                var (refreshed, added) = CatalogueSeed.ReSyncFromEmbedded(catalog, seedFrom);
                log.LogInformation(
                    "Model catalogue: refreshed {Refreshed} row(s) and added {Added} new one(s) from the embedded registry; verdicts kept.",
                    refreshed, added);

                // Console as well, because ILogger reaches nothing on Android unless a
                // host wires a provider - the same reason CIRCLEAI-PROMPT uses it. This
                // is the line that proves a rank change reached the phone.
                Console.WriteLine($"CIRCLEAI-CATALOGUE resync refreshed={refreshed} added={added} verdicts=kept");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Model catalogue: seeding from the embedded registry failed; assessing whatever is already stored.");
        }

        // RECONCILE what is actually on disk into `installed`. A model downloaded by
        // a prior build — or before the catalogue existed — leaves a folder with an
        // installed.json marker. Without this the catalogue reads installed=0 for it,
        // so housekeeping cannot see it to reclaim, a loader may re-download what is
        // already here, and rank's installed-bonus never applies. Scans
        // ModelPaths.Default (the one place the model dir is decided) unless a caller
        // names one. Bidirectional: a flag set for a folder that is now gone is
        // cleared, so the bit tracks the disk both ways.
        var modelsDir = string.IsNullOrWhiteSpace(modelsDirectory) ? ModelPaths.Default : modelsDirectory!;
        try
        {
            if (Directory.Exists(modelsDir))
            {
                var reconciled = 0;
                foreach (var e in catalog.All())
                {
                    var onDisk = File.Exists(Path.Combine(modelsDir, e.Name, "installed.json"));
                    if (onDisk != catalog.IsInstalled(e.Name))
                    {
                        catalog.SetInstalled(e.Name, onDisk);
                        reconciled++;
                    }
                }
                if (reconciled > 0)
                    log.LogInformation(
                        "Model catalogue: reconciled {Count} model(s) with what is on disk (dir: {Dir}).",
                        reconciled, modelsDir);
            }
            else
            {
                log.LogDebug("Model catalogue: models directory {Dir} does not exist yet; nothing to reconcile.", modelsDir);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Model catalogue: reconciling on-disk models failed; installed flags may be stale.");
        }

        var result = assessor.Assess(probe);
        log.LogInformation(
            "Model catalogue: assessed {Assessed} model(s), {Compatible} run on this device (RAM source: {RamSource}; brains read from flash: {FromFlash}).",
            result.Assessed, result.Compatible, probe.RamSource, probe.BrainsReadFromFlash ? "yes" : "no");

        // Only surface on a probe we can trust. A HEURISTIC probe reports the GC
        // heap limit, not the hardware, and would make every model look
        // incompatible — crying "can run nothing" off a guess is the trap
        // MeasurementWarning warns about.
        if (probe.RamSource == DeviceProbe.RamMeasurement.Heuristic)
        {
            log.LogDebug("Model catalogue: device RAM is an unmeasured guess; not surfacing compatibility to avoid a false alarm.");
            return result;
        }

        // HOUSEKEEPING: shed superseded clutter now the compatible/installed columns
        // are trustworthy (this only runs off a measured probe — reclaiming against a
        // guessed compatibility could delete the wrong model). Opt-in, and every
        // reclaim is logged. See CatalogueHousekeeping for the conservative policy.
        if (reclaimInferior)
        {
            try
            {
                // Console, like the resync line above: ILogger reaches nothing on
                // Android, and a deletion has to be visible somewhere.
                // A lesser model is redundant beside a brain this device reads from flash, whatever their sizes
                // (ModelFit.ReadFromFlash, asked of this probe): that is how the space is made for the best one.
                // And a brain whose index is still being built replaces nothing yet, nor one that has not answered
                // here since: the one beside it answers until then (BrainInWaiting.HasTakenOver, from the engine's
                // record of how far that index has got and the speed this device measured of it).
                // AND WE CLEAN UP AFTER OURSELVES (the owner, 2026-11-14: "we need to clean up after ourselves or we will
                // kill users phones"). A brain that goes takes what the engine made of it - its stores, its index -
                // out of the engine's folder too, which is not in the model store and which nothing looked at.
                // AND A BRAIN WHOSE INDEX FAILED ITS CHECK HERE GOES (the owner, 2026-11-14: "Yes, remove them"), once
                // another has loaded here and answers. The record of its check stays: it is what tells the climb that
                // step failed (BrainInWaiting.StepOf), so the brain is not fetched again into the same check.
                var store = indexDirectory ?? Managed.ManagedChatGenerator.DefaultStoreDirectory;

                // AND WHAT EACH FAILED CHECK SAID IS KEPT WHERE THE PHONE DOES NOT CLEAR IT (the owner, 2026-11-14: "Yes,
                // do it"), before anything goes - and the record of each index built after one failed. The engine's
                // folder is the app's cache folder, and a phone short of room clears it; the record is what says which
                // share of a brain's neurons was tried (IndexingRungs), so its next index lists more and not the same
                // again. The copies are read wherever the engine's own is gone, by the climb and by the engine.
                var kept = keptDirectory ?? BrainInWaiting.KeptChecksDirectory;
                if (!string.IsNullOrWhiteSpace(kept))
                {
                    var (keptNow, letGo) = Managed.Store.IndexingQueue.KeepFailed(store, kept);
                    foreach (var name in keptNow)
                    {
                        log.LogInformation("Model housekeeping: the record of {Model}'s index check is kept in {Kept}, which the phone does not clear.", name, kept);
                        Console.WriteLine($"CIRCLEAI-HOUSEKEEPING kept the record of {name}'s index check in {kept}, which the phone does not clear");
                    }
                    foreach (var name in letGo)
                    {
                        log.LogInformation("Model housekeeping: the kept record of {Model}'s index check is let go: its index serves, or one has begun again from the first share.", name);
                        Console.WriteLine($"CIRCLEAI-HOUSEKEEPING let go of the kept record of {name}'s index check: its index serves, or one has begun again from the first share");
                    }
                }

                CatalogueHousekeeping.Reclaim(catalog, modelsDir, log, keep,
                    line => Console.WriteLine("CIRCLEAI-HOUSEKEEPING " + line),
                    entry => ModelFit.ReadFromFlash(entry, probe),
                    entry => !ModelFit.ReadFromFlash(entry, probe)
                             || BrainInWaiting.HasTakenOver(entry, probe, BrainInWaiting.IndexOf(entry, indexDirectory, kept),
                                                            BrainSpeed.MeasuredOf(entry.Name)),
                    removeStore: id => catalog.Assessment(id)?.Entry is { } gone && BrainInWaiting.FileOf(gone) is { } file
                        ? Managed.ManagedChatGenerator.RemoveStoreOf(file, store,
                              keepRecord: BrainInWaiting.Failed(BrainInWaiting.IndexOf(gone, indexDirectory, kept)))
                        : 0,
                    failedHere: entry => BrainInWaiting.FailedHere(entry, probe, BrainInWaiting.IndexOf(entry, indexDirectory, kept)));

                // AND WHAT AN INDEX THAT FAILED ITS CHECK LEFT. It serves nobody, and an earlier build left such pieces
                // where its engine never removed them: on the P30 the 27B's sat on 3.6 GB of a phone with 4.3 GB free.
                // Here, at every start, from the record alone. The record stays - it is what says which share failed,
                // so the next index lists more (IndexingRungs) and is not built again into the same failure.
                foreach (var failed in Managed.Store.IndexingQueue.RemoveFailed(store))
                {
                    var megabytes = failed.Bytes / 1048576;
                    var measured = (failed.Agreement / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    log.LogInformation(
                        "Model housekeeping: the index of {Model} failed its check ({Measured}%) and serves nobody; what it left is removed, {Freed} MB given back to the flash.",
                        failed.Model, measured, megabytes);
                    Console.WriteLine($"CIRCLEAI-HOUSEKEEPING index of {failed.Model} failed its check ({measured}%): {megabytes} MB removed");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Model catalogue: housekeeping pass failed; nothing reclaimed.");
            }
        }

        try
        {
            obs.OnCatalogueAssessed(result.Compatible, result.Assessed, probe);
            if (catalog.Best(ModelModality.Chat) is null)
            {
                log.LogWarning(
                    "Model catalogue: NO compatible chat model for this device ({Count} catalogued) — a smaller model or a catalogue refresh is needed.",
                    catalog.Count());
                obs.OnNoCompatibleModel(ModelModality.Chat, catalog.Count(), probe);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Model catalogue: surfacing the assessment to the observer failed.");
        }

        return result;
    }
}
