#nullable enable

// SpeechModelSelector.cs
//
// Device-aware selection for SPEECH models (ASR / TTS / VAD / wake word),
// parallel to DeviceAwareModelSelector's chat selection.
//
// Speech models are a different kind of thing from chat LLMs — a different
// runtime (VoicePipeline, not IChatGenerator) and a different selection axis
// (modality, not ChatCapability). Bolting them onto BestFit would have meant a
// TTS model competing to be the reasoning core (see ModelModality). So the two
// selectors share the device-fit MATHS but not the query.
//
// The fit-vs-function verdict from Gap 1 matters even more here: an ASR model
// below the intelligibility floor is worse than none, because it acts on the
// wrong words. So this returns SelectionQuality too.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Picks the best speech model of a given modality the device can hold.</summary>
public interface ISpeechModelSelector
{
    /// <summary>
    /// Best model of <paramref name="modality"/> for this device, or <c>null</c>
    /// when none of that modality is catalogued. <see cref="ModelSelection.Quality"/>
    /// reports fit-vs-function exactly as the chat selector does.
    /// </summary>
    ModelSelection? BestFor(DeviceProbe probe, ModelModality modality, int minQualityRank = 0);

    /// <summary>Every catalogued model of a modality, quality-ranked, with fit marked.</summary>
    IReadOnlyList<ModelSelection> CandidatesFor(DeviceProbe probe, ModelModality modality);
}

/// <summary>
/// <see cref="ISpeechModelSelector"/> over the embedded registry. Filters to the
/// requested speech modality — it will never return a chat model, and the chat
/// selector will never return one of these.
/// </summary>
public sealed class SpeechModelSelector : ISpeechModelSelector
{
    private readonly ModelRegistryService _registry;

    public SpeechModelSelector(ModelRegistryService registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc/>
    public ModelSelection? BestFor(DeviceProbe probe, ModelModality modality, int minQualityRank = 0)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (modality == ModelModality.Chat)
            throw new ArgumentException(
                "Chat selection goes through IModelSelector.BestFit, not the speech selector.",
                nameof(modality));

        var tier      = probe.Classify();
        var ramGb     = probe.RamAvailableBytes / (1024.0 * 1024 * 1024);
        var storageGb = probe.StorageFreeBytes  / (1024.0 * 1024 * 1024);

        var ofModality = _registry.AllModels.Where(e => e.Modality == modality).ToList();
        if (ofModality.Count == 0) return null;   // this modality is not catalogued — honest null

        var deviceOk = ofModality
            .Where(e => e.MinRamGb <= ramGb + 0.0001 &&
                        (storageGb <= 0 || e.MinStorageGb <= storageGb + 0.0001))
            .ToList();

        var somethingFits = deviceOk.Count > 0;

        // Same rule as chat: best quality that fits; else the smallest, marked.
        var winner = somethingFits
            ? deviceOk.OrderByDescending(e => e.QualityRank).ThenBy(e => e.MinRamGb).First()
            : ofModality.OrderBy(e => e.MinRamGb).ThenBy(e => e.TotalBytes).First();

        var quality =
            !somethingFits                        ? SelectionQuality.NothingFits
            : winner.QualityRank < minQualityRank ? SelectionQuality.BelowFloor
                                                  : SelectionQuality.Good;

        return new ModelSelection(
            ModelId:          winner.Name,
            RequiresDownload: true,
            EstimatedBytes:   winner.TotalBytes,
            Tier:             tier,
            Quality:          quality);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelSelection> CandidatesFor(DeviceProbe probe, ModelModality modality)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier      = probe.Classify();
        var ramGb     = probe.RamAvailableBytes / (1024.0 * 1024 * 1024);
        var storageGb = probe.StorageFreeBytes  / (1024.0 * 1024 * 1024);

        return _registry.AllModels
            .Where(e => e.Modality == modality)
            .OrderByDescending(e => e.QualityRank)
            .Select(e => new ModelSelection(
                ModelId:          e.Name,
                RequiresDownload: true,
                EstimatedBytes:   e.TotalBytes,
                Tier:             tier,
                Quality:          (e.MinRamGb <= ramGb + 0.0001 &&
                                  (storageGb <= 0 || e.MinStorageGb <= storageGb + 0.0001))
                                     ? SelectionQuality.Good
                                     : SelectionQuality.NothingFits))
            .ToList();
    }
}
