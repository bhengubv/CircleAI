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
public sealed class DeviceAwareModelSelector : IModelSelector, IDisposable
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
        => BestFit(probe, required, minQualityRank: 0);

    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required, int minQualityRank)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var tier = probe.Classify();
        var entries = EnumerateEntries(required).ToList();
        if (entries.Count == 0)
            throw new InvalidOperationException(
                "Model registry is empty. Cannot select a model.");

        var ramGb     = probe.UsableRamGb;   // free RAM minus KV-growth headroom
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

        // 1. Filter by capability flags. An entry must declare every required flag.
        var capabilityOk = entries
            .Where(e => SatisfiesCapability(e, required))
            .ToList();

        if (capabilityOk.Count == 0)
        {
            // No entry declares the requested capabilities. Treat this as
            // "no model available" rather than silently dropping the
            // capability requirement.
            // SAY HOW MANY WERE LOOKED AT. The old wording sent a reader to the
            // registry to add a capability that was already there - the entries
            // had been filtered out by modality before this gate ever saw them.
            throw new InvalidOperationException(
                $"No model in the registry satisfies required capabilities '{required}' " +
                $"({entries.Count} candidate(s) considered). " +
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

        // Something fits → best quality that fits.
        // Nothing fits → honour the intent stated above and return the SMALLEST
        // candidate. Ordering the fallback by QualityRank (as this did) handed a
        // constrained device the LARGEST model in the catalogue — the exact
        // opposite of "a wearable should still get the smallest model".
        var somethingFits = deviceOk.Count > 0;

        var winner = somethingFits
            ? deviceOk.OrderByDescending(e => e.QualityRank).ThenBy(e => e.MinRamGb).First()
            : capabilityOk.OrderBy(e => e.MinRamGb).ThenBy(e => e.TotalBytes).First();

        // FIT IS NOT FUNCTION. Report which of the three situations this is, so
        // a caller can tell "good choice" from "least-bad option on a device
        // that can run nothing here" and escalate to cloud fallback instead of
        // shipping something unusable.
        var quality =
            !somethingFits                        ? SelectionQuality.NothingFits
            : winner.QualityRank < minQualityRank ? SelectionQuality.BelowFloor
                                                  : SelectionQuality.Good;

        return new ModelSelection(
            ModelId:          winner.Name,
            RequiresDownload: true, // selector cannot tell — caller checks the cache
            EstimatedBytes:   winner.TotalBytes,
            Tier:             tier,
            Quality:          quality);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier = probe.Classify();

        var ramGb     = probe.UsableRamGb;   // free RAM minus KV-growth headroom
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

        return EnumerateEntries()
            .OrderByDescending(e => e.QualityRank)
            .Select(e => new ModelSelection(
                ModelId:          e.Name,
                RequiresDownload: true,
                EstimatedBytes:   e.TotalBytes,
                Tier:             tier,
                // A "what could run here" listing that marks unrunnable entries
                // as Good would be actively misleading — that is the whole point
                // of this endpoint.
                Quality:          (e.MinRamGb <= ramGb + 0.0001 &&
                                   (storageGb <= 0 || e.MinStorageGb <= storageGb + 0.0001))
                                      ? SelectionQuality.Good
                                      : SelectionQuality.NothingFits))
            .ToList();
    }

    // Registry is the source of truth. Adding a new bundle to
    // registry.json — or refreshing via the recalibrate-registry-sha tool,
    // or via remote update once signature verification ships — surfaces it
    // here automatically. No SDK release required, no hardcoded model
    // names in source code.
    // CHAT ONLY. This is the single chokepoint every selection path funnels
    // through (BestFit, AllCandidates, ChainFor), so filtering here guarantees a
    // speech model (Asr/Tts/Vad/WakeWord) can never be handed to a caller asking
    // for a chat brain. Without this, a Tts entry would parse to Default (see
    // ParseCapabilities — unknown labels are dropped) and pollute chat
    // selection. A future speech selector queries the registry by modality
    // separately.
    private IEnumerable<ModelEntry> EnumerateEntries()
        => _registry.AllModels.Where(e => e.Modality == ModelModality.Chat);

    /// <summary>
    /// Candidates for a specific capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE VISION SELECTOR EXCLUDED VISION MODELS.</b> Candidates were
    /// filtered to <c>Modality == Chat</c> before the capability gate ran, so
    /// the two entries that declare <c>["Default","Vision"]</c> - Qwen2.5-VL-3B
    /// and SmolVLM-256M - were dropped one line before anything asked which
    /// capabilities they had. <c>BestFit(probe, Vision)</c> then threw "No model
    /// in the registry satisfies required capabilities 'Vision'" over a registry
    /// containing exactly two models that do.
    /// </para>
    /// <para>
    /// FOUND ON A P30, AFTER THREE BUILDS SPENT FIXING THINGS UPSTREAM. The
    /// image was decoded, scaled to 478x1024, attached and carried intact - and
    /// the throw happened before any of that mattered, was swallowed by a
    /// <c>catch { return generalist; }</c>, and the TEXT model answered about a
    /// picture it had never been given. It said so, politely, every time.
    /// </para>
    /// <para>
    /// Chat stays in the set for a vision request because a VLM may be
    /// catalogued either way: the embedded entries carry
    /// <c>Modality = Vision</c>, while a live ModelScope entry that
    /// <c>InferModality</c> recognises as a VLM also carries Chat-shaped
    /// metadata. Filtering to Vision alone would trade this bug for its mirror.
    /// </para>
    /// <para>
    /// Every non-vision request keeps the old candidate set exactly, so chat and
    /// the speech selectors are untouched.
    /// </para>
    /// </remarks>
    private IEnumerable<ModelEntry> EnumerateEntries(ChatCapability required)
        => required.HasFlag(ChatCapability.Vision)
            ? _registry.AllModels.Where(e => e.Modality is ModelModality.Chat
                                                        or ModelModality.Vision)
            : EnumerateEntries();

    /// <inheritdoc/>
    public IReadOnlyList<string> ChainFor(string headModelId)
    {
        if (string.IsNullOrWhiteSpace(headModelId)) return Array.Empty<string>();

        var lookup = EnumerateEntries()
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        if (!lookup.ContainsKey(headModelId)) return Array.Empty<string>();

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        var cursor = headModelId;
        while (!string.IsNullOrWhiteSpace(cursor) && seen.Add(cursor) && lookup.TryGetValue(cursor, out var entry))
        {
            chain.Add(entry.Name);
            cursor = entry.FallbackModelId ?? string.Empty;
        }
        return chain;
    }

    /// <summary>
    /// Disposes the registry ONLY when this selector created it. _ownsRegistry
    /// was tracked but never acted on (CS0414: assigned, never used) — the
    /// parameterless ctor built a ModelRegistryService and leaked it.
    /// A caller-supplied registry is never disposed here; it belongs to them.
    /// </summary>
    public void Dispose()
    {
        if (_ownsRegistry) _registry.Dispose();
    }

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
