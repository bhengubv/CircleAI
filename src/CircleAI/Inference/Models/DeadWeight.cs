// DeadWeight.cs
//
// 22.8 GB OF A MODEL THIS PHONE CANNOT RUN, AND NOTHING WOULD EVER SAY SO.
//
// A Circle OS device finished downloading Qwen3.6-35B-A3B-MNN, loaded it, aborted
// the process with signal 6 and set lowmemorykiller on a dozen system apps. After
// that it is refused - correctly, permanently, and silently. The bytes stay.
//
// AND HOUSEKEEPING WILL NOT TOUCH THEM, FOR A GOOD REASON THAT DOES NOT APPLY HERE.
// CatalogueHousekeeping refuses to reclaim a model that stopped fitting, and its
// comment says why: "free space comes back, a fit rule gets corrected, and the bytes
// are already paid for. Re-downloading something already on the phone is a worse
// outcome than the disk it holds." That is right for a model refused by a PREDICTION,
// which can be wrong and has been. It is not right for one refused by an ATTEMPT -
// there the correction already happened and it went the other way.
//
// SO IT SAYS SO AND OFFERS. It does not delete. feedback_never_delete_without_asking
// is a standing rule and 22 GB is a lot of somebody's data to reclaim on a hunch;
// the spoken offer IS the ask, and somebody answering it is the authorisation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>A model taking up space on a device that cannot use it.</summary>
/// <param name="Name">The catalogue id.</param>
/// <param name="Bytes">What it occupies on disk.</param>
/// <param name="Why">The reason, in words safe to say to somebody.</param>
public readonly record struct DeadWeightItem(string Name, long Bytes, string Why);

/// <summary>Finds models that are here and unusable, and clears them when told to.</summary>
public static class DeadWeight
{
    /// <summary>Everything on this device that it cannot use, largest first.</summary>
    /// <param name="catalog">This device's verdicts. Null finds nothing.</param>
    /// <param name="loader">The model store, for what is actually on disk.</param>
    /// <remarks>
    /// REFUSED FROM EVIDENCE ONLY, not merely incompatible. A model the assessor
    /// predicts will not fit may fit tomorrow - a smaller build, a corrected claim, a
    /// phone with its storage cleared - and offering to delete it on a prediction is
    /// how somebody loses a download to an estimate. A model that actually stopped
    /// the phone is a different thing, and that distinction is the whole reason
    /// refused_reason is a separate column from compatible.
    /// </remarks>
    public static IReadOnlyList<DeadWeightItem> On(IModelCatalog? catalog, string storageRoot)
    {
        try
        {
            if (catalog is null || string.IsNullOrWhiteSpace(storageRoot)) return [];

            var found = new List<DeadWeightItem>();
            foreach (var a in catalog.AllAssessed())
            {
                if (!a.Refused) continue;

                // MEASURED OFF THE DISK, NOT OFF THE EMBEDDED REGISTRY. The first
                // version asked BundleModelLoader.Progress, which resolves a name
                // through ModelRegistryService - so a model that reached this device
                // as a ROW from a feed, which is the entire point of the catalogue,
                // measured as zero bytes and was never reported. The bytes are on the
                // disk; asking the disk cannot be wrong about which catalogue a row
                // came from.
                var size = Size(Path.Combine(storageRoot, a.Entry.Name));
                if (size <= 0) continue;              // nothing here to reclaim

                found.Add(new DeadWeightItem(a.Entry.Name, size, a.RefusedReason!));
            }

            return found.OrderByDescending(f => f.Bytes).ToList();
        }
        catch { return []; }
    }

    /// <summary>Delete the bytes of everything <see cref="On"/> found, and report it.</summary>
    /// <param name="catalog">Marked not-installed for each one cleared.</param>
    /// <param name="storageRoot">Where the model directories live.</param>
    /// <param name="say">Where to report, in words safe to show somebody.</param>
    /// <returns>Bytes freed.</returns>
    /// <remarks>
    /// ONLY CALLED WHEN SOMEBODY HAS SAID SO. There is no automatic path to this
    /// method and there must not be one: the offer is spoken, the answer is the
    /// authorisation, and a deletion nobody asked for is the one failure mode worse
    /// than wasted disk.
    ///
    /// THE VERDICT IS KEPT, deliberately. Clearing the bytes does not pardon the
    /// model - it stopped this phone and that is still true - so a later download of
    /// the same version will not be offered. Pardon is a separate word somebody says
    /// on purpose.
    /// </remarks>
    public static long Clear(IModelCatalog? catalog, string storageRoot, Action<string>? say = null)
    {
        if (string.IsNullOrWhiteSpace(storageRoot)) return 0;

        long freed = 0;
        foreach (var item in On(catalog, storageRoot))
        {
            try
            {
                var dir = Path.Combine(storageRoot, item.Name);
                if (!Directory.Exists(dir)) continue;

                var size = item.Bytes;
                Directory.Delete(dir, recursive: true);
                catalog?.SetInstalled(item.Name, false);
                freed += size;
                say?.Invoke($"cleared {item.Name}");
            }
            catch (Exception ex)
            {
                say?.Invoke($"could not clear {item.Name}: {ex.Message}");
            }
        }
        return freed;
    }

    private static long Size(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}
