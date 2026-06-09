// IModelSelector.cs
//
// Capability-aware model picker. Consumer says what they need;
// selector returns what runs.

using System.Collections.Generic;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>
/// A model that the registry decided fits the device + the requested
/// capabilities. <see cref="RequiresDownload"/> tells the caller whether
/// fetching is needed before load.
/// </summary>
/// <param name="ModelId">Logical model identifier, resolvable by <c>IModelLoader</c>.</param>
/// <param name="RequiresDownload"><c>true</c> when the bundle is not yet on disk.</param>
/// <param name="EstimatedBytes">Sum of every file in the bundle — the on-disk footprint after fetch.</param>
/// <param name="Tier">The <see cref="DeviceTier"/> this selection was sized for.</param>
public sealed record ModelSelection(
    string     ModelId,
    bool       RequiresDownload,
    long       EstimatedBytes,
    DeviceTier Tier);

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
    /// Enumerate every selection candidate in registry order. Useful for
    /// diagnostics endpoints that list "what could run here."
    /// </summary>
    IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe);
}
