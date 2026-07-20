// IModelSelector.cs
//
// Capability-aware model picker. Consumer says what they need;
// selector returns what runs.

using System.Collections.Generic;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>
/// Whether the selected model is actually usable here, as distinct from merely
/// being the best available.
/// <para>
/// FIT IS NOT FUNCTION. Before this existed, a device that could hold nothing
/// useful was silently handed the smallest entry and the caller had no way to
/// know the difference between "this is a good choice" and "nothing here fits,
/// have the least-bad option". Shipping that silently is how a product ends up
/// technically running and practically useless on cheap hardware.
/// </para>
/// </summary>
public enum SelectionQuality
{
    /// <summary>An entry satisfied the capability flags AND the device gates.</summary>
    Good,

    /// <summary>
    /// Fits the device, but below the caller's requested quality floor. The
    /// caller should consider a cloud fallback or disabling the feature.
    /// </summary>
    BelowFloor,

    /// <summary>
    /// NOTHING fits this device. The returned model is the smallest candidate
    /// and may fail to load or be unusably slow. Never silently treat this as
    /// a normal selection.
    /// </summary>
    NothingFits,
}

/// <summary>
/// A model that the registry decided fits the device + the requested
/// capabilities. <see cref="ModelSelection.RequiresDownload"/> tells the caller
/// whether fetching is needed before load.
/// </summary>
/// <param name="ModelId">Logical model identifier, resolvable by <c>IModelLoader</c>.</param>
/// <param name="RequiresDownload"><c>true</c> when the bundle is not yet on disk.</param>
/// <param name="EstimatedBytes">Sum of every file in the bundle — the on-disk footprint after fetch.</param>
/// <param name="Tier">The <see cref="DeviceTier"/> this selection was sized for.</param>
/// <param name="Quality">
/// Whether this selection is genuinely usable — see <see cref="SelectionQuality"/>.
/// Defaults to <see cref="SelectionQuality.Good"/> so existing constructions
/// keep compiling; the selector always sets it explicitly.
/// </param>
public sealed record ModelSelection(
    string     ModelId,
    bool       RequiresDownload,
    long       EstimatedBytes,
    DeviceTier Tier,
    SelectionQuality Quality = SelectionQuality.Good);

/// <summary>
/// Picks the best <see cref="ModelSelection"/> for a given device + capability set.
/// </summary>
/// <remarks>
/// The contract is "best fit," not "exact match" — the selector returns
/// the highest-quality entry it can find that:
/// <list type="bullet">
///   <item>satisfies every flag in <c>required</c>, AND</item>
///   <item>has <c>MinRamGb</c> ≤ device RAM, AND</item>
///   <item>has <c>MinStorageGb</c> ≤ device free storage.</item>
/// </list>
/// If no entry passes every gate, the selector falls back to the lowest-RAM
/// entry that satisfies the capability flags — never <c>null</c>.
/// </remarks>
public interface IModelSelector
{
    /// <summary>
    /// Pick the best model for this device + required capabilities.
    /// </summary>
    /// <param name="probe">Hardware snapshot from <see cref="DeviceProbe.Snapshot"/>.</param>
    /// <param name="required">Capability flags the consumer declared (see <see cref="ChatCapability"/>).</param>
    ModelSelection BestFit(DeviceProbe probe, ChatCapability required);

    /// <summary>
    /// <see cref="BestFit(DeviceProbe, ChatCapability)"/> with a functional floor.
    /// A winner whose <c>QualityRank</c> is below <paramref name="minQualityRank"/>
    /// comes back as <see cref="SelectionQuality.BelowFloor"/>, so the caller can
    /// choose cloud fallback instead of shipping something unusable.
    /// </summary>
    /// <remarks>
    /// A DEFAULT INTERFACE METHOD on purpose. Adding a required member to a
    /// published interface breaks every existing implementer — including ones
    /// outside this repo that we cannot see. The default ignores the floor and
    /// delegates, so existing selectors keep working unchanged; implementers
    /// that care about the floor override it.
    /// <para>
    /// There is deliberately NO default floor value in the selector. Real
    /// thresholds must come from CircleAI.SelfBench measurements on real
    /// devices — inventing a number here would harden a guess into a contract
    /// everything downstream trusts, which is exactly the failure mode that
    /// produced "native MNN libraries DO ship".
    /// </para>
    /// </remarks>
    ModelSelection BestFit(DeviceProbe probe, ChatCapability required, int minQualityRank)
        => BestFit(probe, required);

    /// <summary>
    /// Enumerate every selection candidate in registry order. Useful for
    /// diagnostics endpoints that list "what could run here."
    /// </summary>
    IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe);

    /// <summary>
    /// (RT-08) Walk the <c>FallbackModelId</c> chain rooted at
    /// <paramref name="headModelId"/>. Returns the head first, then every
    /// smaller fallback in descending quality order. Self-references and
    /// cycles short-circuit. Used by the brownout swap (RT-04).
    /// </summary>
    /// <param name="headModelId">Chain-head model identifier.</param>
    /// <returns>
    /// At least one entry (the head). Empty if the head is not in the
    /// registry.
    /// </returns>
    IReadOnlyList<string> ChainFor(string headModelId)
    {
        // Default impl: head only, no fallbacks. Concrete selectors that
        // back onto a registry override this to walk FallbackModelId.
        return string.IsNullOrWhiteSpace(headModelId)
            ? Array.Empty<string>()
            : new[] { headModelId };
    }
}
