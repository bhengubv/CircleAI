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
    public static AssessmentResult Run(
        IModelCatalog catalog,
        IModelAssessor assessor,
        DeviceProbe probe,
        IModelCatalogObserver? observer = null,
        ModelRegistryService? seedFrom = null,
        ILogger? logger = null,
        string? modelsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assessor);
        ArgumentNullException.ThrowIfNull(probe);
        var obs = observer ?? NullModelCatalogObserver.Instance;
        var log = logger ?? NullLogger.Instance;

        try
        {
            var seeded = CatalogueSeed.SeedFromEmbedded(catalog, seedFrom);
            if (seeded > 0)
                log.LogInformation("Model catalogue: seeded {Count} row(s) from the embedded registry.", seeded);
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
            "Model catalogue: assessed {Assessed} model(s), {Compatible} run on this device (RAM source: {RamSource}).",
            result.Assessed, result.Compatible, probe.RamSource);

        // Only surface on a probe we can trust. A HEURISTIC probe reports the GC
        // heap limit, not the hardware, and would make every model look
        // incompatible — crying "can run nothing" off a guess is the trap
        // MeasurementWarning warns about.
        if (probe.RamSource == DeviceProbe.RamMeasurement.Heuristic)
        {
            log.LogDebug("Model catalogue: device RAM is an unmeasured guess; not surfacing compatibility to avoid a false alarm.");
            return result;
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
