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
    private readonly bool _mmapAllowed;
    private readonly double _maxModelBytes;

    // Floating-point slack so a model whose MinRamGb equals usable RAM to the
    // last bit still counts as fitting — mirrors DeviceAwareModelSelector.
    private const double Eps = 0.0001;

    /// <summary>
    /// EXPERIMENT HOOK: a model id forced compatible regardless of the RAM gate, so
    /// a deliberately-bigger model can be SELECTED and its runtime path (mmap /
    /// single-thread / streaming) proven on a real phone — the "value picks the
    /// model, software earns the fit" test. Null (default) disables it; a host sets
    /// it only to run that on-device experiment.
    /// </summary>
    public static string? ExperimentForceCompatibleId { get; set; }

    /// <param name="catalog">The catalogue to score.</param>
    /// <param name="shippedEngines">
    /// The inference engines this build ships natively. A model whose
    /// <see cref="ModelEntry.Engine"/> is not in this set is never compatible —
    /// this is what keeps a GGUF model off an MNN-only device honestly.
    /// </param>
    /// <param name="mmapAllowed">
    /// Whether the MNN runtime will memory-map model weights. <c>null</c> (the
    /// default) reads the one runtime source of truth,
    /// <see cref="QwenTextGenerator.MmapIsAllowed"/>. It changes the fit check:
    /// with mmap the kernel pages weights off disk, so an MoE bundle's low
    /// <see cref="ModelEntry.MinRamGb"/> (only the active experts stay resident)
    /// holds; WITHOUT mmap — the Android default today, after an MNN SIGSEGV took
    /// it out — the whole weight file must live in RAM, so a 30B-A3B advertised at
    /// 2.5 GB would in fact OOM a phone. Kept injectable so a host that has
    /// enabled mmap, and the tests, can state it outright.
    /// </param>
    /// <param name="maxModelBytes">
    /// The form-factor weight ceiling. A model bigger than this is never compatible,
    /// however much RAM and storage the device reports. <c>null</c> (the default)
    /// uses <see cref="FormFactorMaxBytes"/>.
    /// </param>
    public DeviceModelAssessor(IModelCatalog catalog, IEnumerable<ModelEngine> shippedEngines,
                               bool? mmapAllowed = null, double? maxModelBytes = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(shippedEngines);
        _shippedEngines = new HashSet<ModelEngine>(shippedEngines);
        _mmapAllowed = mmapAllowed ?? QwenTextGenerator.MmapIsAllowed;
        _maxModelBytes = maxModelBytes ?? FormFactorMaxBytes;
        if (_maxModelBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxModelBytes));
    }

    /// <summary>
    /// The weight ceiling a model must clear to be offered: <b>20 GB</b>.
    /// </summary>
    /// <remarks>
    /// WHERE 20 COMES FROM: a common handset ships <b>128 GB of storage</b>, and one
    /// model may not take more than about a sixth of everything the person owns —
    /// their photos, their music, every other app. It is a claim on the DEVICE'S
    /// DISK, which is why it is a fixed product rule and not a reading of free space:
    /// free space is whatever happens to be spare this morning, and a model that fits
    /// today because the gallery is empty is a model that should never have been
    /// offered. The RAM and storage gates below still apply on top; this one asks a
    /// different question, and asks it first.
    /// </para>
    /// <para>
    /// WHAT IT ADMITS. Image generation is the capability that set the floor: a
    /// picture needs three parts and the transformer alone makes none. The working
    /// set is UC Q8_0 (7.59) + Qwen3-VL-8B Q4_K_M (5.03) + VAE (0.68) = 13.30 GB, and
    /// the cheaper Q4_K_M transformer brings it to 10.31 GB. Both fit with room. So
    /// does the dense <c>Qwen3-14B-MNN</c> at 9.44 GB, and <c>Qwen3-30B-A3B</c> at
    /// 17.75 GB.
    /// </para>
    /// <para>
    /// What it still refuses: <c>Qwen3.6-35B-A3B</c> at 22.80 GB — nearly a fifth of
    /// a 128 GB handset for one model, which is the case this rule exists to say no
    /// to.
    /// </para>
    /// <para>
    /// Units are the catalogue's (10^9, matching <see cref="ModelEntry.TotalBytes"/>),
    /// not 2^30. Injectable so a desktop or server host can lift it deliberately
    /// rather than by accident.
    /// </para>
    /// </remarks>
    public const double FormFactorMaxBytes = 20_000_000_000d;

    /// <summary>Convenience for the app as it ships today: MNN is the only engine.</summary>
    public static DeviceModelAssessor MnnOnly(IModelCatalog catalog) =>
        new(catalog, new[] { ModelEngine.Mnn });

    /// <summary>
    /// The engines this BUILD can actually load, probed at runtime rather than
    /// declared. MNN is always present; llama.cpp counts only when its native
    /// library really loaded, which is what <see cref="LlamaGenerator.IsAvailable"/>
    /// answers (it is <c>NativeVersion is not null</c>, not a compile-time flag).
    /// </summary>
    /// <remarks>
    /// This is the seam that lets a GGUF model be offered honestly. Until the native
    /// bridge is built, every GGUF row stays <c>compatible = 0</c> and the app can
    /// say the model exists without offering a download that cannot load. Build the
    /// bridge and the same rows flip to compatible with no catalogue edit and no app
    /// release — which is the entire point of the runtime catalogue.
    /// </remarks>
    public static IReadOnlyCollection<ModelEngine> ShippedEngines()
    {
        var set = new HashSet<ModelEngine> { ModelEngine.Mnn };
        if (LlamaGenerator.IsAvailable) set.Add(ModelEngine.LlamaCpp);
        return set;
    }

    /// <summary>Assessor for what this device can really run, engines probed at runtime.</summary>
    public static DeviceModelAssessor ForThisBuild(IModelCatalog catalog, double? maxModelBytes = null) =>
        new(catalog, ShippedEngines(), mmapAllowed: null, maxModelBytes: maxModelBytes);

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
        // 0. EXPERIMENT: force one named model compatible so its runtime path can be
        //    proven on-device against the naive RAM gate. Storage still has to hold
        //    it (a model that is not on disk cannot be run), but RAM is what we are
        //    deliberately testing, so it is bypassed here.
        if (ExperimentForceCompatibleId is { Length: > 0 } forced && e.Name == forced)
            return storageFreeGb <= 0 || e.MinStorageGb <= storageFreeGb + Eps;

        // 1. Engine we ship — the term that tells the truth about GGUF vs MNN.
        if (!_shippedEngines.Contains(e.Engine)) return false;

        // 1b. QUANTISATION the loaded backend can actually read. The engine gate
        //     above says we ship llama.cpp; it does not say THIS llama.cpp opens
        //     THIS pack. Stock rejects Bonsai's PTQ1_0/PQ2_0 as unknown types —
        //     prism's card states it outright — so an engine-only gate would offer
        //     Bonsai on a stock build and fail at load. Only applies to GGUF rows;
        //     MNN quantisation strings are left to the engine gate.
        if (e.Engine == ModelEngine.LlamaCpp && !LlamaQuantSupport.CanRead(e.Quantization))
            return false;

        // 2. FORM FACTOR. A product ceiling, deliberately ahead of the RAM and
        //    storage gates: those ask what THIS device happens to have, and a
        //    roomy tablet would otherwise make a pack compatible that we cannot
        //    ship to a phone. Judged on the weight itself, so the answer does not
        //    drift by device. The experiment hook above returns before this on
        //    purpose — proving an oversized model's runtime path on a real phone
        //    is the one case that is allowed to ignore it.
        if (e.TotalBytes > 0 && e.TotalBytes > _maxModelBytes) return false;

        // 3. RAM fits. usableRamGb already folds in RamFitHeadroom, and
        //    EffectiveMinRamGb folds in whether weights will be memory-mapped.
        if (EffectiveMinRamGb(e) > usableRamGb + Eps) return false;

        // 4. Storage fits (0 = unknown, skip — same as the selector).
        if (storageFreeGb > 0 && e.MinStorageGb > storageFreeGb + Eps) return false;

        // 5. VRAM, only when the entry states a requirement. Unknown / no GPU
        //    memory when a model demands it → not compatible (video models).
        if (e.MinVramGb is double needVram)
        {
            if (probe.VramGb is not double haveVram || needVram > haveVram + Eps) return false;
        }

        return true;
    }

    // The RAM a model actually needs resident on THIS runtime. With mmap the
    // kernel pages weights off disk, so an MoE bundle's low MinRamGb (only the
    // active experts stay resident) is the real floor. Without mmap the whole
    // weight file must be resident, so the floor rises to the full weight
    // footprint — which is what keeps a 30B-A3B (18 GB, MinRamGb 2.5) out of the
    // compatible set on a phone that would OOM loading it. Dense bundles are
    // unaffected: their MinRamGb already exceeds TotalBytes/1e9 (it includes KV +
    // overhead), so the Max is a no-op for them.
    // Whether this model will be memory-mapped on this device: only weights too
    // large to load eagerly, and only when mmap is enabled. Mirrors
    // QwenTextGenerator.WeightsExceedEagerFit so the assessment matches what loads.
    private bool WillMmap(ModelEntry e) =>
        _mmapAllowed &&
        (e.TotalBytes > QwenTextGenerator.MmapWeightThresholdBytes || IsMmapCapableVision(e));

    // VISION BUNDLES MMAP BELOW THE GIANT THRESHOLD. A VLM carries a vision
    // encoder (visual.mnn + visual.mnn.weight) alongside the LLM, so its weight
    // footprint clears a phone's RAM at a far smaller TotalBytes than a dense
    // text model does — Qwen2.5-VL-3B is 2.74 GB with MinRamGb 3.9, which no
    // 3.6 GB phone can hold eagerly, and it sat permanently incompatible as a
    // result. The 8 GB threshold exists to keep mmap away from models that fit
    // eagerly (mmap'd inference has crashed MNN's threadpool on those); a VLM
    // that does NOT fit eagerly is exactly the case mmap is for.
    // Safe because KimiVlGenerator now carries the same weight-mmap +
    // single-thread + kvcache-OFF recipe QwenTextGenerator was cured with.
    private bool IsMmapCapableVision(ModelEntry e)
    {
        if (e.Modality != ModelModality.Vision) return false;
        var weightGb = e.TotalBytes > 0 ? e.TotalBytes / DeviceProbe.BytesPerGb : 0.0;
        return weightGb > 0 && e.MinRamGb > weightGb;   // needs more resident than it weighs
    }

    // What an mmap'd model still needs resident once the kernel is paging its
    // weights: KV cache, activations and runtime overhead. MinRamGb bundles all
    // of that TOGETHER WITH the weights, so subtracting the weights is what is
    // left. Floored, because a bundle whose MinRamGb barely exceeds its weights
    // must not come out at ~0 and look free.
    // Grounded in measurement: Qwen2.5-3B, 2.4 GB of weights, ran on a P30 at
    // ~800 MB resident once weight-mmap was on (2026-09-22).
    private const double MmapResidentFloorGb = 0.6;

    private double EffectiveMinRamGb(ModelEntry e)
    {
        var weightGb = e.TotalBytes > 0 ? e.TotalBytes / DeviceProbe.BytesPerGb : 0.0;

        if (!WillMmap(e))
        {
            // Eager: the whole weight file must be resident.
            return Math.Max(e.MinRamGb, weightGb);
        }

        // Mmap'd: the weights are paged, so only the rest has to fit. For an MoE
        // whose MinRamGb is already just the active experts (below its weight),
        // that figure is the truthful floor and is kept as-is.
        return e.MinRamGb <= weightGb
            ? e.MinRamGb
            : Math.Max(MmapResidentFloorGb, e.MinRamGb - weightGb);
    }

    // How far a slow-to-load model drops below the responsive set. Larger than any
    // QualityRank (6..16), so a model that pages tens of GB before its first token
    // sits beneath EVERY model that answers quickly, whatever their quality.
    private const double SlowLoadRankPenalty = 100.0;

    // Quality-dominant among models that answer quickly, THEN responsiveness. A model
    // too large to load eagerly stays compatible (a person can still choose it), but
    // it must not be the silent default: mapping tens of GB off disk means many
    // seconds to a first token, or an OS kill mid-load on a busy phone. Best() picks
    // the top rank, so the penalty keeps a giant off the default while quality still
    // orders everything that loads fast. If ONLY a giant fits, it is still the top of
    // one and still selected — nothing is excluded, only deprioritised.
    private double RankFor(ModelEntry e, double usableRamGb, bool installed)
    {
        var headroomGb = Math.Max(0.0, usableRamGb - EffectiveMinRamGb(e));
        var headroomBonus = Math.Min(headroomGb, 4.0) * 0.1;   // 0 .. 0.4
        var installedBonus = installed ? 0.25 : 0.0;           // prefer what's on disk
        var slowLoadPenalty = WillMmap(e) ? SlowLoadRankPenalty : 0.0;
        return e.QualityRank + headroomBonus + installedBonus - slowLoadPenalty;
    }
}
