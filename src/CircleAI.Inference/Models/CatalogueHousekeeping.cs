// CatalogueHousekeeping.cs
//
// "If a better model is found, housekeeping kicks in and a new model checks in."
// This is the reclaim half: an INFERIOR model that has been SUPERSEDED by a better
// one already on the device is clutter, and on a phone it is clutter that costs
// real storage. The user's example: a P30 holding both Qwen3-0.6B (quality 6) and
// Qwen3.5-0.8B (quality 7) — the 0.6B "should not be there, it is inferior."
//
// THE POLICY IS DELIBERATELY CONSERVATIVE, because a delete is irreversible and a
// re-download costs data on exactly the users this product is for. A model M is
// reclaimed ONLY when every one of these holds:
//   1. M is installed (a complete download — installed.json present).
//   2. Another INSTALLED model K of the SAME modality is strictly higher quality.
//   3. K is not dramatically larger than M (K.TotalBytes <= M.TotalBytes * FACTOR),
//      so keeping M offers no real FIT advantage — a genuinely smaller "it least
//      fits" fallback is NOT reclaimed. (TotalBytes, not MinRamGb: an MoE's MinRamGb
//      is its active-expert footprint, which would wrongly make a 22 GB model look
//      "similar size" to a 1 GB one.)
//   4. Either K is compatible on THIS device, OR M is not — so we NEVER remove a
//      model that runs here in favour of a better one that does not. On a device
//      where nothing is compatible, the better installed model is still the keeper.
// Rule 3 is why the Redmi keeps its 2 B alongside the 35 B (15x larger, a real
// fallback for the un-soaked MoE) but the P30 sheds its 0.6 B for the 0.8 B.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Inference;

/// <summary>One model the housekeeping policy would reclaim, and why.</summary>
public readonly record struct HousekeepingReclaim(string Id, string Reason);

/// <summary>
/// Decides which installed models are superseded clutter, and (optionally) reclaims
/// them. The decision (<see cref="Plan"/>) is pure over the catalogue so it is a
/// unit test, not a phone run; <see cref="Reclaim"/> does the deleting.
/// </summary>
public static class CatalogueHousekeeping
{
    /// <summary>
    /// How much larger a better model may be and still make a smaller one
    /// redundant. At 2.0 a strictly-better model up to twice the size supersedes
    /// this one; a fallback that is less than half the size of the next-better
    /// model is kept, because on a tight device its smallness is the whole point.
    /// </summary>
    public const double SizeDominanceFactor = 2.0;

    /// <summary>
    /// The installed models that are superseded clutter on this device, given the
    /// catalogue's current <c>installed</c> and <c>compatible</c> state. Pure — no
    /// I/O. Order is stable (catalogue order).
    /// </summary>
    public static IReadOnlyList<HousekeepingReclaim> Plan(IModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var installed = catalog.All().Where(e => catalog.IsInstalled(e.Name)).ToList();
        if (installed.Count < 2) return Array.Empty<HousekeepingReclaim>();

        // Compatible ids across the modalities actually present on disk.
        var compatible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modality in installed.Select(e => e.Modality).Distinct())
            foreach (var c in catalog.Compatible(modality))
                compatible.Add(c.Name);

        var reclaim = new List<HousekeepingReclaim>();
        foreach (var m in installed)
        {
            var mCompatible = compatible.Contains(m.Name);
            var dominator = installed.FirstOrDefault(k =>
                k.Name != m.Name &&
                k.Modality == m.Modality &&
                k.QualityRank > m.QualityRank &&
                k.TotalBytes > 0 && m.TotalBytes > 0 &&
                k.TotalBytes <= (long)(m.TotalBytes * SizeDominanceFactor) &&
                (compatible.Contains(k.Name) || !mCompatible));

            if (dominator is not null)
                reclaim.Add(new HousekeepingReclaim(
                    m.Name,
                    $"superseded by {dominator.Name} (quality {dominator.QualityRank} > {m.QualityRank}, similar size)"));
        }
        return reclaim;
    }

    /// <summary>
    /// Reclaim the superseded models under <paramref name="modelsDirectory"/>: delete
    /// each planned model's folder (only when its <c>installed.json</c> marks it a
    /// COMPLETE download — a partial download is left for the downloader to resume)
    /// and clear its <c>installed</c> flag. Best-effort per model; never throws.
    /// Returns the number reclaimed.
    /// </summary>
    public static int Reclaim(IModelCatalog catalog, string modelsDirectory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var log = logger ?? NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(modelsDirectory)) return 0;

        var plan = Plan(catalog);
        var reclaimed = 0;
        foreach (var item in plan)
        {
            try
            {
                var dir = Path.Combine(modelsDirectory, item.Id);
                if (!File.Exists(Path.Combine(dir, "installed.json")))
                {
                    log.LogDebug("Model housekeeping: {Id} has no installed.json; leaving it (not a complete download).", item.Id);
                    continue;
                }
                Directory.Delete(dir, recursive: true);
                catalog.SetInstalled(item.Id, false);
                reclaimed++;
                log.LogInformation("Model housekeeping: reclaimed {Id} — {Reason}.", item.Id, item.Reason);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Model housekeeping: failed to reclaim {Id}; it stays on disk.", item.Id);
            }
        }
        if (reclaimed > 0)
            log.LogInformation("Model housekeeping: reclaimed {Count} superseded model(s).", reclaimed);
        return reclaimed;
    }
}
