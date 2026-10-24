// EngineChoice.cs
//
// WHICH ENGINE SHOULD RUN THIS MODEL ON THIS DEVICE, AND WHY.
//
// THE QUESTION WAS NEVER ASKED, IT WAS ASSUMED. DeviceModelAssessor.ShippedEngines
// answers "which engines exist in this APK" and the fit rules use it as a filter -
// a GGUF row is dropped when llama.cpp is absent, and that is all. Nothing ever asks
// the other direction: this device HAS two engines, so which one should open this
// model, and is the one we are using the better of them?
//
// AND THE ANSWER IS NOT A BUILD-TIME FACT, which is the whole reason it needs a type.
// Three things decide it and all three are read at RUNTIME, on the device:
//
//   the ABI        libllama.so ships for android-arm64 and not for armeabi-v7a, so
//                  two phones on the SAME BUILD answer differently. The P30 (32-bit)
//                  has no GGUF engine at all; a Pixel 7a has one.
//   the fork       LlamaQuantSupport reads the loaded library's version string. Stock
//                  llama.cpp "rejects PQ2_0 and PTQ1_0 as unknown types"; the
//                  PrismML-Eng fork reads them. The shipped bridge reports
//                  "prism-b10743-adfffbe" and exports GGML_TYPE_PTQ1_0 - so this
//                  build CAN open Bonsai's ternary packs, which is not something any
//                  catalogue, manifest or compile-time flag knows.
//   what happened  a model MNN has already failed on here is evidence, and evidence
//                  outranks a declaration. That lives in SelfModel.
//
// WHAT THIS IS FOR. Two callers, and they are different questions:
//   - the loader asks "open this one with what" - Run;
//   - Wolverine asks "is there a better way to run what we have" - Better, which is
//     an OPPORTUNITY rather than a failure, and the half of self-healing that has
//     never existed. "This phone could be running the 9B at 2 GB instead of the 2B
//     at 1.4" is not a bug report, and it is the thing that makes the next version
//     better rather than merely not broken.
//
// IT RECOMMENDS; IT DOES NOT ACT. The same rule IFailureAnalyst states for itself.

using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Which engine should open a model here, and whether a better one exists.</summary>
public static class EngineChoice
{
    /// <summary>
    /// The engine that should open <paramref name="entry"/> on this device, or null
    /// when nothing here can.
    /// </summary>
    /// <remarks>
    /// THE MODEL'S OWN ENGINE OR NOTHING. A GGUF is not openable by MNN and an MNN
    /// bundle is not openable by llama.cpp; this is not a preference, it is a file
    /// format. What makes it a question at all is whether this DEVICE has the engine
    /// the model needs - which for GGUF means both a native library for this ABI and,
    /// for the exotic packs, the right fork of it.
    /// </remarks>
    public static ModelEngine? Run(ModelEntry entry, IReadOnlyCollection<ModelEngine>? shipped = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var have = shipped ?? DeviceModelAssessor.ShippedEngines();
        if (!have.Contains(entry.Engine)) return null;

        // AND FOR GGUF, THE ENGINE IS NOT ENOUGH - THE PACK HAS TO BE READABLE.
        // "Do we ship llama.cpp" and "can THIS llama.cpp read THIS pack" are different
        // questions, and a catalogue that knows the first and not the second offers
        // Bonsai on a stock build and fails at load.
        if (entry.Engine == ModelEngine.LlamaCpp && !LlamaQuantSupport.CanRead(entry.Quantization))
            return null;

        return entry.Engine;
    }

    /// <summary>Whether this device can open this model at all.</summary>
    public static bool CanRun(ModelEntry entry, IReadOnlyCollection<ModelEngine>? shipped = null)
        => Run(entry, shipped) is not null;

    /// <summary>
    /// A model this device could run that would be better than the one it is running,
    /// or null when there is nothing worth saying.
    /// </summary>
    /// <param name="running">What it is using now. Null when it has no brain at all.</param>
    /// <param name="catalogue">Everything known, whatever engine it needs.</param>
    /// <param name="probe">This device.</param>
    /// <param name="present">Whether a model is already downloaded here.</param>
    /// <param name="refused">Whether this device has already refused one on evidence.</param>
    /// <param name="shipped">The engines on this device; null asks the build.</param>
    /// <remarks>
    /// THE UPGRADE PATH, WHICH IS THE HALF OF SELF-HEALING THAT NEVER EXISTED.
    /// Wolverine takes a failure and recommends a fix; nothing has ever taken a
    /// WORKING device and recommended a better arrangement of it. The device that
    /// matters most is the one where nothing is broken and something better is
    /// possible - a phone running a 1.4 GB dense model while a ternary 9B it can
    /// also open sits in the catalogue at 2 GB, or a model MNN keeps failing on that
    /// exists as a GGUF the loaded fork reads.
    /// <para>
    /// HIGHER RANK AND ACTUALLY RUNNABLE. Rank is the shipped prior; CanRun is this
    /// device's reality; refused is its experience. All three, because recommending
    /// an upgrade the phone cannot open is worse than recommending nothing.
    /// </para>
    /// </remarks>
    public static ModelEntry? Better(
        ModelEntry? running,
        IEnumerable<ModelEntry> catalogue,
        DeviceProbe probe,
        Func<string, bool> present,
        Func<string, bool>? refused = null,
        IReadOnlyCollection<ModelEngine>? shipped = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(present);

        var floor = running?.QualityRank ?? int.MinValue;

        return catalogue
            .Where(m => !m.Retired)
            .Where(m => m.Modality == (running?.Modality ?? ModelModality.Chat))
            .Where(m => m.QualityRank > floor)
            .Where(m => refused is null || !refused(m.Name))
            .Where(m => CanRun(m, shipped))
            .Where(m => ModelFit.Fits(m, probe))
            // ALREADY HERE FIRST: an upgrade that costs no data is strictly better
            // than one that costs a gigabyte of somebody's bundle, even at equal rank.
            .OrderByDescending(m => present(m.Name))
            .ThenByDescending(m => m.QualityRank)
            .ThenBy(m => m.MinRamGb)
            .FirstOrDefault();
    }

    /// <summary>
    /// One honest sentence about why a model can or cannot be opened here, for a log,
    /// a screen, or a healing verdict.
    /// </summary>
    /// <remarks>
    /// THE REASON IS THE USEFUL PART. "Not compatible" sends somebody looking at the
    /// model; "this phone's ABI has no llama.cpp" sends them looking at the build, and
    /// "the loaded llama.cpp is stock and this pack is PTQ1_0" sends them to the fork.
    /// Three different actions behind one word.
    /// </remarks>
    public static string Explain(ModelEntry entry, IReadOnlyCollection<ModelEngine>? shipped = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var have = shipped ?? DeviceModelAssessor.ShippedEngines();
        if (!have.Contains(entry.Engine))
            return entry.Engine == ModelEngine.LlamaCpp
                ? $"{entry.Name} needs llama.cpp, which this device's ABI has no build of"
                : $"{entry.Name} needs {entry.Engine}, which this build does not ship";

        if (entry.Engine == ModelEngine.LlamaCpp && !LlamaQuantSupport.CanRead(entry.Quantization))
            return $"{entry.Name} is packed as {entry.Quantization}, which the loaded "
                 + $"{LlamaQuantSupport.Loaded?.Id ?? "llama.cpp"} cannot read";

        return $"{entry.Name} opens here with {entry.Engine}";
    }
}
