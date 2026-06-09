// DeviceAwareModelSelector.cs
//
// Default IModelSelector implementation. Reads the embedded model
// registry, filters by capability + device fit, ranks by quality.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// Picks a model by walking the embedded <see cref="ModelRegistryService"/>,
/// filtering on capability + device fit, and ranking by <c>QualityRank</c>.
/// </summary>
public sealed class DeviceAwareModelSelector : IModelSelector
{
    private readonly ModelRegistryService _registry;
    private readonly bool _ownsRegistry;

    /// <summary>
    /// Construct using a caller-supplied registry (e.g. the inference
    /// server's singleton). The selector does not dispose it.
    /// </summary>
    public DeviceAwareModelSelector(ModelRegistryService registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ownsRegistry = false;
    }

    /// <summary>
    /// Construct using a freshly built registry (loaded from the embedded
    /// JSON). The selector owns and disposes it.
    /// </summary>
    public DeviceAwareModelSelector()
    {
        _registry = new ModelRegistryService();
        _ownsRegistry = true;
    }

    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var tier = probe.Classify();
        var entries = EnumerateEntries().ToList();
        if (entries.Count == 0)
            throw new InvalidOperationException(
                "Model registry is empty. Cannot select a model.");

        var ramGb     = probe.RamAvailableBytes / (1024.0 * 1024 * 1024);
        var storageGb = probe.StorageFreeBytes  / (1024.0 * 1024 * 1024);

        // 1. Filter by capability flags. An entry must declare every required flag.
        var capabilityOk = entries
            .Where(e => SatisfiesCapability(e, required))
            .ToList();

        if (capabilityOk.Count == 0)
        {
            // No entry declares the requested capabilities. Treat this as
            // "no model available" rather than silently dropping the
            // capability requirement.
            throw new InvalidOperationException(
                $"No model in the registry satisfies required capabilities '{required}'. " +
                "Refresh the registry or relax the capability requirement.");
        }

        // 2. Filter by device fit. RAM and storage gates are advisory —
        //    when no entry fits, we fall back to the smallest one rather
        //    than throwing. A wearable that can only run the smallest
        //    model should still get the smallest model, not an exception.
        var deviceOk = capabilityOk
            .Where(e => e.MinRamGb <= ramGb + 0.0001 &&
                        (storageGb <= 0 || e.MinStorageGb <= storageGb + 0.0001))
            .ToList();

        var winner = (deviceOk.Count > 0 ? deviceOk : capabilityOk)
            .OrderByDescending(e => e.QualityRank)
            .ThenBy(e => e.MinRamGb)
            .First();

        return new ModelSelection(
            ModelId:          winner.Name,
            RequiresDownload: true, // selector cannot tell — caller checks the cache
            EstimatedBytes:   winner.TotalBytes,
            Tier:             tier);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier = probe.Classify();
        return EnumerateEntries()
            .OrderByDescending(e => e.QualityRank)
            .Select(e => new ModelSelection(
                ModelId:          e.Name,
                RequiresDownload: true,
                EstimatedBytes:   e.TotalBytes,
                Tier:             tier))
            .ToList();
    }

    // Registry is the source of truth. Adding a new bundle to
    // registry.json — or refreshing via the recalibrate-registry-sha tool,
    // or via remote update once signature verification ships — surfaces it
    // here automatically. No SDK release required, no hardcoded model
    // names in source code.
    private IEnumerable<ModelEntry> EnumerateEntries() => _registry.AllModels;

    private static bool SatisfiesCapability(ModelEntry entry, ChatCapability required)
    {
        if (required == ChatCapability.None) return true;

        var declared = ParseCapabilities(entry.Capabilities);
        return (declared & required) == required;
    }

    internal static ChatCapability ParseCapabilities(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0) return ChatCapability.Default;
        var result = ChatCapability.None;
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            if (Enum.TryParse<ChatCapability>(label.Trim(), ignoreCase: true, out var flag))
                result |= flag;
        }
        return result == ChatCapability.None ? ChatCapability.Default : result;
    }
}
