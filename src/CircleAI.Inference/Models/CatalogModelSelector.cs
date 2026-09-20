// CatalogModelSelector.cs
//
// IModelSelector backed by the runtime catalogue (IModelCatalog) instead of a
// live-sorted registry walk. Selection is a DB read of pre-computed columns:
// the compatible rows for the modality, best rank first. This is the "swap the
// source, keep the interface" half of the plan — the catalogue's `compatible`
// bit and `rank` (written by the assessor) do the work the old selector did on
// every call, and a feed can change what's selectable with no app release.
//
// It reuses DeviceAwareModelSelector's capability parsing so a Tools / Vision /
// LongContext request is filtered identically; it mirrors the same
// QualityRank-desc, then smallest-RAM ordering, and the same "vision may be
// catalogued as Chat or Vision" rule.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// An <see cref="IModelSelector"/> that reads the runtime
/// <see cref="IModelCatalog"/>. Selection = the top compatible row for the
/// modality that also satisfies the requested capabilities.
/// </summary>
public sealed class CatalogModelSelector : IModelSelector
{
    private readonly IModelCatalog _catalog;

    public CatalogModelSelector(IModelCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required)
        => BestFit(probe, required, minQualityRank: 0);

    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required, int minQualityRank)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier = probe.Classify();

        // The compatible set for the requested capability — the catalogue already
        // filtered by the device (`compatible = 1`) and ordered by rank; we apply
        // the per-request capability gate and a consistent cross-modality sort.
        var compatible = ModalitiesFor(required)
            .SelectMany(_catalog.Compatible)
            .Where(e => Satisfies(e, required))
            .OrderByDescending(e => e.QualityRank)
            .ThenBy(e => e.MinRamGb)
            .ToList();

        if (compatible.Count > 0)
        {
            var pick = compatible[0];
            var quality = pick.QualityRank < minQualityRank
                ? SelectionQuality.BelowFloor
                : SelectionQuality.Good;
            return ToSelection(pick, tier, quality);
        }

        // Nothing compatible. Mirror DeviceAwareModelSelector: if the catalogue
        // holds NO entry that even satisfies the capability, that is genuinely
        // unavailable — throw, as the old selector does (a ModelSelection cannot
        // exist without a ModelId). Otherwise hand back the smallest capability-
        // matching entry flagged NothingFits, so a constrained device still gets
        // the least-bad option and the caller can escalate to cloud fallback.
        var anyCapabilityMatch = ModalitiesFor(required)
            .SelectMany(m => _catalog.All().Where(e => e.Modality == m))
            .Where(e => Satisfies(e, required))
            .OrderBy(e => e.MinRamGb)
            .ThenBy(e => e.TotalBytes)
            .ToList();

        if (anyCapabilityMatch.Count == 0)
            throw new InvalidOperationException(
                $"No model in the catalogue satisfies required capabilities '{required}'. " +
                "Refresh the catalogue (pull the signed feed) or relax the requirement.");

        return ToSelection(anyCapabilityMatch[0], tier, SelectionQuality.NothingFits);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier = probe.Classify();

        // "What could run here" — the compatible chat rows, best first.
        return _catalog.Compatible(ModelModality.Chat)
            .Select(e => ToSelection(e, tier, SelectionQuality.Good))
            .ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ChainFor(string headModelId)
    {
        if (string.IsNullOrWhiteSpace(headModelId)) return Array.Empty<string>();
        if (_catalog.Get(headModelId) is null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        var cursor = headModelId;
        while (!string.IsNullOrWhiteSpace(cursor) && seen.Add(cursor) && _catalog.Get(cursor) is { } entry)
        {
            chain.Add(entry.Name);
            cursor = entry.FallbackModelId ?? string.Empty;
        }
        return chain;
    }

    // Vision may be catalogued either as its own Vision modality (the embedded
    // VLMs) or as a Chat entry with a Vision capability (a live VLM with
    // chat-shaped metadata) — so a vision request considers both, exactly as
    // DeviceAwareModelSelector does. Every other request is Chat only.
    private static ModelModality[] ModalitiesFor(ChatCapability required)
        => required.HasFlag(ChatCapability.Vision)
            ? new[] { ModelModality.Chat, ModelModality.Vision }
            : new[] { ModelModality.Chat };

    private ModelSelection ToSelection(ModelEntry e, DeviceTier tier, SelectionQuality quality)
        => new(
            ModelId:          e.Name,
            RequiresDownload: !_catalog.IsInstalled(e.Name),
            EstimatedBytes:   e.TotalBytes,
            Tier:             tier,
            Quality:          quality);

    private static bool Satisfies(ModelEntry entry, ChatCapability required)
    {
        if (required == ChatCapability.None) return true;
        var declared = DeviceAwareModelSelector.ParseCapabilities(entry.Capabilities);
        return (declared & required) == required;
    }
}
