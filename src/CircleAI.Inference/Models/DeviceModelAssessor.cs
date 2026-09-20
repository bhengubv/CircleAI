// DeviceModelAssessor.cs
//
// The default IModelAssessor. `compatible` is device-fit: the engine is one this
// device ships, RAM fits, storage fits, and (when stated) VRAM fits — reusing the
// same fit units DeviceAwareModelSelector / DeviceProbe use (BytesPerGb,
// RamFitHeadroom). `rank` is quality-dominant (QualityRank), nudged by fit
// headroom and whether the bundle is already on disk, so among models of equal
// measured quality the one that fits most comfortably (or is already downloaded)
// wins.
//
// Licence is NOT a term here. "May we ship this model?" is a curation concern,
// not a device-fit one, and it is enforced once at the trust boundary
// (CatalogueFeedWriter, via the existing ModelScopeCatalogClient.LicenceAllowed) —
// a non-free model never enters the catalogue from a feed, so it never reaches
// this assessment. Seed rows are the shipped, already-vetted ladder.

using System;
using System.Collections.Generic;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// Scores catalogue rows for a specific device. The set of engines the device
/// ships is a BUILD fact (which native runtimes the APK bundles), so it is fixed
/// at construction; the device snapshot varies per <see cref="Assess"/>.
/// </summary>
public sealed class DeviceModelAssessor : IModelAssessor
{
    private readonly IModelCatalog _catalog;
    private readonly HashSet<ModelEngine> _shippedEngines;

    // Floating-point slack so a model whose MinRamGb equals usable RAM to the
    // last bit still counts as fitting — mirrors DeviceAwareModelSelector.
    private const double Eps = 0.0001;

    /// <param name="catalog">The catalogue to score.</param>
    /// <param name="shippedEngines">
    /// The inference engines this build ships natively. A model whose
    /// <see cref="ModelEntry.Engine"/> is not in this set is never compatible —
    /// this is what keeps a GGUF model off an MNN-only device honestly.
    /// </param>
    public DeviceModelAssessor(IModelCatalog catalog, IEnumerable<ModelEngine> shippedEngines)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(shippedEngines);
        _shippedEngines = new HashSet<ModelEngine>(shippedEngines);
    }

    /// <summary>Convenience for the app as it ships today: MNN is the only engine.</summary>
    public static DeviceModelAssessor MnnOnly(IModelCatalog catalog) =>
        new(catalog, new[] { ModelEngine.Mnn });

    /// <inheritdoc />
    public AssessmentResult Assess(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var usableRamGb   = probe.UsableRamGb;     // free RAM minus KV-growth headroom
        var storageFreeGb = probe.StorageFreeGb;   // catalogue units (10^9)

        var assessed = 0;
        var compatibleCount = 0;
        foreach (var e in _catalog.All())
        {
            var compatible = IsCompatible(e, probe, usableRamGb, storageFreeGb);
            var rank = compatible ? RankFor(e, usableRamGb, _catalog.IsInstalled(e.Name)) : 0.0;
            _catalog.SetAssessment(e.Name, compatible, rank);
            assessed++;
            if (compatible) compatibleCount++;
        }
        return new AssessmentResult(assessed, compatibleCount);
    }

    private bool IsCompatible(ModelEntry e, DeviceProbe probe, double usableRamGb, double storageFreeGb)
    {
        // 1. Engine we ship — the term that tells the truth about GGUF vs MNN.
        if (!_shippedEngines.Contains(e.Engine)) return false;

        // 2. RAM fits. usableRamGb already folds in RamFitHeadroom.
        if (e.MinRamGb > usableRamGb + Eps) return false;

        // 3. Storage fits (0 = unknown, skip — same as the selector).
        if (storageFreeGb > 0 && e.MinStorageGb > storageFreeGb + Eps) return false;

        // 4. VRAM, only when the entry states a requirement. Unknown / no GPU
        //    memory when a model demands it → not compatible (video models).
        if (e.MinVramGb is double needVram)
        {
            if (probe.VramGb is not double haveVram || needVram > haveVram + Eps) return false;
        }

        return true;
    }

    // Quality-dominant. QualityRank differences (whole numbers: 6, 8, 10, 14) far
    // outweigh the sub-1 nudges, so Best() tracks measured quality; the nudges
    // only order models of equal quality.
    private static double RankFor(ModelEntry e, double usableRamGb, bool installed)
    {
        var headroomGb = Math.Max(0.0, usableRamGb - e.MinRamGb);
        var headroomBonus = Math.Min(headroomGb, 4.0) * 0.1;   // 0 .. 0.4
        var installedBonus = installed ? 0.25 : 0.0;           // prefer what's on disk
        return e.QualityRank + headroomBonus + installedBonus;
    }
}
