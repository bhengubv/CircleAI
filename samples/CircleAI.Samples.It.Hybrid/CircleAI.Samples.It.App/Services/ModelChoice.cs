// ModelChoice.cs
//
// Which model this phone should use for a job. Asked in ONE place.
//
// TWO SCREENS ANSWERED THIS DIFFERENTLY AND BOTH WERE ON THE PHONE AT ONCE.
// Settings offered Answering at 547 MB - the best chat model that actually fits
// the device - while the chat screen said "Answering needs Qwen3.6-35B-A3B-MNN
// — 22797 MB", because it took the highest quality model in the catalogue
// without asking whether the phone could run it. Same question, two rules,
// forty times apart, on a handset with 1.4 GB of usable RAM.
//
// A person reading 22.8 GB on a phone with 38 GB free concludes the thing is not
// worth it and stops. The number they should have seen was 547 MB.
//
// So the choice lives here and both callers use it. The rule is not complicated;
// what it needed was to exist once.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It.App.Services;

/// <summary>Picks the model this device should use for a modality.</summary>
internal static class ModelChoice
{
    /// <summary>Whether a phone can run it at all.</summary>
    /// <remarks>
    /// Storage is only checked when the probe reports a figure: a device that
    /// cannot measure free space returns 0, and refusing every model on that
    /// basis would leave the app claiming a working phone can do nothing.
    /// </remarks>
    public static bool Fits(ModelEntry m, DeviceProbe probe)
        => m.MinRamGb <= probe.UsableRamGb + 0.0001
        && (probe.StorageFreeGb <= 0 || m.MinStorageGb <= probe.StorageFreeGb + 0.0001);

    /// <summary>
    /// What is installed for this job, or the best thing that would fit.
    /// </summary>
    /// <remarks>
    /// INSTALLED FIRST, ALWAYS. Something already on the phone is the answer
    /// whatever the catalogue thinks of it - offering to replace a working model
    /// with a better one is a different question, asked at a different time.
    /// <para>
    /// Then the best that FITS, best-quality first and smallest as the
    /// tie-break. Null means nothing catalogued for this job will run here, which
    /// is a real answer on a cheap phone and has to be said rather than papered
    /// over with a model the device cannot load.
    /// </para>
    /// </remarks>
    public static ModelEntry? For(
        ModelModality modality,
        ModelRegistryService registry,
        BundleModelLoader loader,
        DeviceProbe probe)
    {
        var candidates = registry.AllModels.Where(m => m.Modality == modality).ToList();

        var installed = candidates.FirstOrDefault(m => loader.ModelExists(m.Name));
        if (installed is not null) return installed;

        return candidates
            .Where(m => Fits(m, probe))
            .OrderByDescending(m => m.QualityRank)
            .ThenBy(m => m.MinRamGb)
            .FirstOrDefault();
    }

    /// <summary>Whether anything for this job is catalogued at all.</summary>
    /// <remarks>
    /// Separate from <see cref="For"/> because "nothing exists yet" and "nothing
    /// that runs on this phone" are different things to tell somebody, and the
    /// second is about their handset rather than about our catalogue.
    /// </remarks>
    public static bool AnyCatalogued(ModelModality modality, ModelRegistryService registry)
        => registry.AllModels.Any(m => m.Modality == modality);

    /// <summary>A size a person can read.</summary>
    public static string Size(long bytes)
        => bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000.0:0.#} GB"
            : $"{bytes / 1_000_000.0:0} MB";
}
