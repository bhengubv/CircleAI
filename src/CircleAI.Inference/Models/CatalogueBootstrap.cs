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
    public static AssessmentResult Run(
        IModelCatalog catalog,
        IModelAssessor assessor,
        DeviceProbe probe,
        IModelCatalogObserver? observer = null,
        ModelRegistryService? seedFrom = null,
        ILogger? logger = null)
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
