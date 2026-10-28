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
    private readonly IReadOnlyCollection<ModelEngine>? _engines;
    private readonly IModelCatalog? _catalogue;

    /// <summary>
    /// Construct using a caller-supplied registry (e.g. the inference
    /// server's singleton). The selector does not dispose it.
    /// </summary>
    /// <param name="registry">The catalogue to rank.</param>
    /// <param name="engines">
    /// The engines this build ships; null asks the build at the moment of the question.
    /// </param>
    /// <param name="catalogue">
    /// This device's assessed catalogue, when it has one. It is a SOURCE of rows and of
    /// fit verdicts, not a second selector: see <see cref="Rows"/> and
    /// <see cref="FitsHere"/>. Null means rank the registry live.
    /// </param>
    public DeviceAwareModelSelector(ModelRegistryService registry,
                                    IReadOnlyCollection<ModelEngine>? engines = null,
                                    IModelCatalog? catalogue = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ownsRegistry = false;
        _engines = engines;
        _catalogue = catalogue;
    }

    /// <summary>
    /// Construct using a freshly built registry (loaded from the embedded
    /// JSON). The selector owns and disposes it.
    /// </summary>
    /// <param name="engines">
    /// The engines this build ships; null asks the build at the moment of the question.
    /// </param>
    public DeviceAwareModelSelector(IReadOnlyCollection<ModelEngine>? engines = null,
                                    IModelCatalog? catalogue = null)
    {
        _registry = new ModelRegistryService();
        _ownsRegistry = true;
        _engines = engines;
        _catalogue = catalogue;
    }

    /// <summary>
    /// The one selector, given whatever this device has: the assessed catalogue when it
    /// has been assessed, the registry until then.
    /// </summary>
    /// <remarks>
    /// THIS WAS A FACTORY THAT CHOSE BETWEEN TWO IMPLEMENTATIONS, and its own comment
    /// said what that cost: "ONE PLACE, BECAUSE FIVE PLACES IS WHAT WENT WRONG...
    /// CatalogModelSelector, SqliteModelCatalog, DeviceModelAssessor and
    /// AddModelCatalogue were all built, tested and committed, and five production sites
    /// went on calling new DeviceAwareModelSelector(registry) - so the assessed catalogue
    /// had zero callers and the crash verdicts written into it could not reach a
    /// decision." And then: "This is still a second rule, but it is ONE, it is named, and
    /// it is tested."
    /// <para>
    /// IT IS NO LONGER A SECOND RULE. There is one selector and the catalogue is a source
    /// inside it, so the floor, the engine gate, the capability gate and the ranking
    /// exist once whichever source answers. The thing this factory was protecting - that
    /// an unassessed catalogue must not be selected from - is now a question Rows asks
    /// per call rather than a type chosen once.
    /// </para>
    /// <para>
    /// AND ASKING PER CALL FIXES A RACE THIS FACTORY HAD. It evaluated HasBeenAssessed
    /// once, at construction, and CatalogueBootstrap assesses on a background task at
    /// startup - so the selector handed to AIService before the bootstrap finished stayed
    /// registry-based for the life of the process, however complete the catalogue became.
    /// </para>
    /// </remarks>
    public static IModelSelector Preferring(
        IModelCatalog? catalogue, ModelRegistryService? registry = null,
        IReadOnlyCollection<ModelEngine>? engines = null)
        => registry is null
            ? new DeviceAwareModelSelector(engines, catalogue)
            : new DeviceAwareModelSelector(registry, engines, catalogue);

    /// <summary>Whether this device has actually assessed its catalogue yet.</summary>
    /// <remarks>
    /// Rows alone are not enough. A freshly seeded catalogue has every model in it with
    /// compatible = 0 and assessed_at NULL, and selecting from that returns the SMALLEST
    /// capability match flagged NothingFits - a wrong answer delivered confidently, which
    /// is worse than ranking the registry. Asked per call, so the answer can change from
    /// no to yes while the process runs, which is exactly what the bootstrap does.
    /// </remarks>
    public static bool HasBeenAssessed(IModelCatalog? catalogue)
    {
        if (catalogue is null) return false;
        try { return catalogue.AllAssessed().Any(a => a.AssessedAt is not null); }
        catch { return false; }
    }

    /// <summary>Whether this model still has to be downloaded, when anything knows.</summary>
    /// <remarks>
    /// Defaults to TRUE when there is no catalogue to ask, which is the old behaviour and
    /// the safe direction: offering to fetch something already present costs a cache
    /// check, while claiming a missing model is present costs a failed load.
    /// </remarks>
    private bool NeedsFetching(string modelName)
    {
        try { return _catalogue is null || !_catalogue.IsInstalled(modelName); }
        catch { return true; }
    }

    /// <summary>Where the rows come from: the catalogue when it holds any, else the registry.</summary>
    /// <remarks>
    /// WHERE THE ROWS COME FROM AND WHETHER THE VERDICTS ARE TRUSTWORTHY ARE TWO
    /// QUESTIONS, and the first version of this answered both with HasBeenAssessed. A
    /// catalogue that has been SEEDED but not yet ASSESSED then vanished entirely: its
    /// rows were ignored and the embedded registry answered instead, so a feed that had
    /// added or removed a model was invisible until the first assessment landed.
    /// ChainFor_walks_the_fallback_chain caught it - a chain seeded as head -> mid -> tail
    /// came back empty, because none of those three exist in the registry.
    /// <para>
    /// SO THE SOURCE IS THE CATALOGUE WHENEVER IT HAS ROWS, and HasBeenAssessed now only
    /// governs <see cref="FitsHere"/> - which is the question it was always about: an
    /// unassessed row reads compatible = 0, and believing that would mark everything
    /// unfit.
    /// </para>
    /// </remarks>
    private IEnumerable<ModelEntry> Rows()
    {
        IReadOnlyList<ModelEntry>? rows = null;
        try { rows = _catalogue?.All(); } catch { rows = null; }

        return rows is { Count: > 0 } ? rows : _registry.AllModels;
    }

    /// <summary>
    /// Whether this device can hold the model - the assessor's verdict when there is one.
    /// </summary>
    /// <remarks>
    /// THE ASSESSOR KNOWS MORE THAN ModelFit DOES: it applies the share-of-disk budget,
    /// the engine, the quantisation the loaded backend can read, and any crash verdict
    /// this device recorded. Where it has spoken, its answer is the fit answer; where it
    /// has not, the live RAM and storage gates are all there is.
    /// </remarks>
    private bool FitsHere(ModelEntry entry, DeviceProbe probe)
    {
        if (HasBeenAssessed(_catalogue))
        {
            try
            {
                // AssessedModel.Usable IS the composite, and it is already written:
                // Compatible && !Refused && !Entry.Retired. Scanning Compatible(modality)
                // instead - which is what the selector this one replaced did - gets the
                // first term and silently drops REFUSED, so a model this device has
                // already been killed by stays selectable. That verdict is the only input
                // here that comes from an attempt rather than from metadata, and it is
                // the one that outranks every prediction.
                return _catalogue!.Assessment(entry.Name)?.Usable == true;
            }
            catch { /* fall through to the live gates */ }
        }

        // THE DURABLE QUESTION, NOT THIS SECOND'S. CouldEverHold reads TOTAL ram against
        // a resident ceiling; Fits reads MemAvailable, which is whatever happens to be
        // spare right now - and selecting on that names a different brain at nine in the
        // morning and at midnight. Measured on an 8 GB phone holding Qwen3.5-0.8B and
        // Qwen3.5-2B: Qwen3.5-4B at 5 GB free, Qwen3.5-0.8B at 300 MB free, same store,
        // same device. "Best LLM is X, X is used" cannot hold if X is a function of what
        // else is running.
        //
        // THE MOMENTARY QUESTION STILL EXISTS AND IS STILL ASKED - by the loader, through
        // LoadableNow below, which is the failover. The 2026-10-10 kill is what that gate
        // is for: a 2B loaded with 982 MB free took iAware's low-memory killer through
        // com.huawei.lbs and totemweather. Selection must not flap; LOADING must not
        // overcommit. Two questions, one candidate set, one owner.
        return ModelFit.CouldEverHold(entry, probe);
    }

    /// <summary>The best candidate that can be LOADED right now, or null when none can.</summary>
    /// <param name="probe">This device, read at this moment.</param>
    /// <param name="required">The capability the caller needs.</param>
    /// <remarks>
    /// THE FAILOVER, AND IT IS NOT A SECOND SELECTOR. It shares
    /// <see cref="Candidates"/> with <see cref="BestFit"/>, so the floor, the engine and
    /// pack gate, the refusal verdict and the capability gate are the same ones - what
    /// differs is only the memory question: CouldEverHold for "what does this device
    /// use", Fits for "what will load in the memory that is free this second".
    /// <para>
    /// A CALLER USES THIS ONLY WHEN BestFit's ANSWER WILL NOT LOAD. It never changes what
    /// the device's model IS: X stays X, stays the fetch target, stays what the screens
    /// name. This answers the narrower question of what can speak while memory is tight,
    /// and AssistantPersona.OnTheSmallerBrain is the sentence for it - that line was
    /// right all along and was wired to the wrong trigger, standing in for a model that
    /// was never downloaded rather than one that cannot be loaded this minute.
    /// </para>
    /// </remarks>
    public ModelSelection? LoadableNow(DeviceProbe probe, ChatCapability required)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var winner = Candidates(required)
            .Where(e => ModelFit.Fits(e, probe))
            .OrderByDescending(e => e.QualityRank)
            .ThenBy(e => e.MinRamGb)
            .FirstOrDefault();

        return winner is null
            ? null
            : new ModelSelection(
                ModelId:          winner.Name,
                RequiresDownload: NeedsFetching(winner.Name),
                EstimatedBytes:   winner.TotalBytes,
                Tier:             probe.Classify(),
                Quality:          SelectionQuality.Good);
    }


    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required)
        => BestFit(probe, required, minQualityRank: 0);

    /// <inheritdoc/>
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required, int minQualityRank)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var tier = probe.Classify();

        var ramGb     = probe.UsableRamGb;   // free RAM minus KV-growth headroom
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

        // ONE CALL TO THE ONE SOURCE. The modality, the floor, the engine and the
        // capability are all applied in Candidates and none of them is applied here, so
        // there is no second filter to forget to update.
        var capabilityOk = Candidates(required).ToList();

        if (capabilityOk.Count == 0)
        {
            // SAY HOW MANY WERE LOOKED AT, AND WHY THE SET IS EMPTY. The old wording sent
            // a reader to the registry to add a capability that was already there - the
            // entries had been filtered out by modality before the gate ever saw them.
            // Now the engine can also be the reason, and that sends somebody to the build
            // rather than to the catalogue, so Explain names which it was.
            var runnableAnywhere = Candidates(required: null).ToList();
            var blocked = Rows()
                .FirstOrDefault(e => e.Modality == ModelModality.Chat && !e.Retired
                                  && !EngineChoice.CanRun(e, _engines));

            throw new InvalidOperationException(
                $"No model in the registry satisfies required capabilities '{required}' " +
                $"({runnableAnywhere.Count} runnable candidate(s) considered). " +
                (blocked is not null
                    ? EngineChoice.Explain(blocked, _engines) + ". "
                    : string.Empty) +
                "Refresh the registry or relax the capability requirement.");
        }

        // 2. TWO TIERS, AND COLLAPSING THEM TO ONE BROKE ELEVEN TESTS AT ONCE.
        //
        // The first version of the durable change made this gate AND the fallback below
        // both CouldEverHold, so they were the same predicate: when nothing could be
        // held there was no second tier left to fall to, SelectionQuality.NothingFits
        // became unreachable, and a 0.25 GB device got an exception where the contract
        // says it gets the smallest entry flagged honestly.
        //
        // THE TIERS ANSWER DIFFERENT QUESTIONS AND BOTH ARE NEEDED:
        //   durable   - CouldEverHold, on TOTAL memory against a resident ceiling. This
        //               is what RANKS, so the chosen model does not change because the
        //               phone is briefly busy.
        //   honest    - Fits, on the memory free right now. This does not change the
        //               choice; it decides whether the answer is Good or NothingFits,
        //               which is how a caller tells "this will load" from "this is what
        //               this device is for, but not this minute".
        var durableOk = capabilityOk
            .Where(e => FitsHere(e, probe))
            .ToList();

        // Nothing the device could EVER hold → the smallest eligible entry, flagged
        // NothingFits. Ordering this by QualityRank (as it once did) handed a
        // constrained device the LARGEST model in the catalogue - the exact opposite of
        // "a wearable should still get the smallest model" - so it is MinRamGb ascending.
        //
        // IT ANSWERS RATHER THAN THROWS, because the quality flag is the contract:
        // DeviceCapability.PlanFor renders it as an unavailable line and ModelChoice.For
        // turns it into null for a screen. A selector that threw instead would make every
        // caller handle an exception to learn what a field already says.
        var winner = durableOk.Count > 0
            ? durableOk.OrderByDescending(e => e.QualityRank).ThenBy(e => e.MinRamGb).First()
            : capabilityOk.OrderBy(e => e.MinRamGb).ThenBy(e => e.TotalBytes).First();

        // THE FLAG IS ABOUT THE DEVICE, NOT ABOUT THIS MINUTE - and making it momentary
        // put the same question in two places and had them disagree.
        //
        // Ranking durably picks a bigger model than a momentary rule would, so flagging
        // that winner with Fits said "does not fit" about a model the device is plainly
        // for: CapablePhone_ReportsGood failed on a phone that is capable. Good means
        // "this device can hold this"; NothingFits means "this device can hold nothing
        // here". Whether there is room RIGHT NOW is LoadableNow's question and only the
        // loader asks it - which is the whole point of having two methods rather than
        // one rule trying to be both.
        var somethingFits = durableOk.Count > 0;

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
            // THE CATALOGUE KNOWS, AND THIS THREW THE ANSWER AWAY. "selector cannot tell -
            // caller checks the cache" was true of a registry-only selector and false the
            // moment a catalogue was handed to it: IModelCatalog.IsInstalled is exactly
            // this fact, written by the loader when a bundle lands. Hardcoding true made
            // every caller re-derive it, and a caller that forgot showed a download
            // prompt for a model already on disk.
            RequiresDownload: NeedsFetching(winner.Name),
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

        return Candidates(required: null)
            .OrderByDescending(e => e.QualityRank)
            .Select(e => new ModelSelection(
                ModelId:          e.Name,
                RequiresDownload: NeedsFetching(e.Name),
                EstimatedBytes:   e.TotalBytes,
                Tier:             tier,
                // A "what could run here" listing that marks unrunnable entries
                // as Good would be actively misleading — that is the whole point
                // of this endpoint.
                Quality:          FitsHere(e, probe)
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
    //
    // AND NEVER A RETIRED ONE. The owner's floor - "nothing less than Qwen3.5 is the
    // LLM" - is a registry fact (ModelEntry.Retired), applied here so BestFit,
    // AllCandidates and ChainFor all honour it, and a fallback chain stops at the
    // floor instead of walking down to a lesser model.
    //
    // AND NEVER ONE THIS BUILD CANNOT OPEN, for the same reason and in the same place.
    // This selector is what CatalogModelSelector.Preferring hands back until the
    // catalogue has been assessed - which on a fresh install is every selection made
    // before the bootstrap finishes - so it is a live production path and not a
    // fallback curiosity. Its nothing-fits branch then orders by MinRamGb and takes the
    // smallest, and the smallest rows are the aggressively quantised GGUFs: on a build
    // whose ABI has no llama.cpp that handed back a model nothing could open. The
    // sibling hole in CatalogModelSelector's own fallback is fixed the same way, and
    // EngineChoice.CanRun is the one owner of the rule - the engine AND the pack.
    /// <summary>Every model this selector may ever answer with. The only source.</summary>
    /// <param name="required">
    /// The capability asked for, which also widens the modality set: a Vision request
    /// admits Vision entries beside Chat, because a VLM may be catalogued either way.
    /// <c>null</c> asks for the whole runnable set without a capability gate, which is
    /// what a listing and a fallback chain want.
    /// </param>
    /// <remarks>
    /// ONE SET, AND IT USED TO BE TWO. This type's own comment calls itself "the single
    /// chokepoint every selection path funnels through (BestFit, AllCandidates,
    /// ChainFor), so filtering here guarantees a speech model can never be handed to a
    /// caller asking for a chat brain" - and it was not one. There were two
    /// EnumerateEntries: a parameterless one carrying the floor and the engine gate, and
    /// a ChatCapability overload that BestFit actually called, which for a Vision request
    /// went straight to _registry.AllModels with nothing but !Retired.
    /// <para>
    /// SO THE CHOKEPOINT WAS SKIPPED ON THE ONE PATH THAT MATTERED. A vision request got
    /// no engine gate, so a build with no llama.cpp could select a GGUF VLM; and the
    /// chat-only guarantee the comment promises did not hold either. The fork was added
    /// to fix the bug recorded below - "THE VISION SELECTOR EXCLUDED VISION MODELS" - and
    /// it fixed it by forking the chokepoint rather than widening it, which is how a
    /// second owner of one rule gets created by a correct fix.
    /// </para>
    /// <para>
    /// EVERY GATE, IN HERE, ONCE: the modality, the floor (ModelEntry.Retired), the
    /// engine and pack (EngineChoice.CanRun) and the capability. Nothing above this
    /// method filters, and nothing below it may.
    /// </para>
    /// </remarks>
    private IEnumerable<ModelEntry> Candidates(ChatCapability? required)
    {
        var wantsVision = required.HasValue && required.Value.HasFlag(ChatCapability.Vision);

        return Rows()
            .Where(e => wantsVision
                ? e.Modality is ModelModality.Chat or ModelModality.Vision
                : e.Modality == ModelModality.Chat)
            .Where(e => !e.Retired)
            .Where(e => EngineChoice.CanRun(e, _engines))
            // AND NEVER ONE THIS DEVICE HAS ALREADY BEEN KILLED BY, whether or not the
            // catalogue has been assessed. Every gate above is a PREDICTION from
            // metadata; a refusal is the result of an ATTEMPT, and evidence outranks a
            // prediction. It was enforced one layer out, in CircleNeuronService, by
            // swapping in whatever was installed instead - so the refusal was honoured
            // and the selector was overruled in the same breath. Asked here, the refused
            // model simply is not a candidate and the selector's own answer is already
            // the right one.
            .Where(e => !CrashVerdict.IsRefused(_catalogue, e.Name))
            .Where(e => required is null || SatisfiesCapability(e, required.Value));
    }

    // THE VISION WIDENING LIVES IN Candidates NOW, not in a second method.
    //
    // It was added here, as its own overload, to fix a real bug: candidates were
    // filtered to Modality == Chat before the capability gate ran, so the two entries
    // declaring ["Default","Vision"] - Qwen2.5-VL-3B and SmolVLM-256M - were dropped one
    // line before anything asked what they could do, and BestFit(probe, Vision) threw
    // "No model in the registry satisfies required capabilities 'Vision'" over a registry
    // holding exactly two that did. Found on a P30 after three builds spent fixing things
    // upstream: the image was decoded, scaled to 478x1024, attached and carried intact,
    // the throw happened before any of that mattered, a catch { return generalist; }
    // swallowed it, and the TEXT model answered about a picture it had never been given.
    // It said so, politely, every time.
    //
    // Chat stays in the set for a vision request because a VLM may be catalogued either
    // way - the embedded entries carry Modality = Vision, while a live ModelScope entry
    // InferModality recognises as a VLM carries Chat-shaped metadata - so filtering to
    // Vision alone would trade this bug for its mirror. All of that is now one Where in
    // Candidates, which is also where the floor and the engine gate are, so the vision
    // path cannot skip them the way the overload did.

    /// <inheritdoc/>
    public IReadOnlyList<string> ChainFor(string headModelId)
    {
        if (string.IsNullOrWhiteSpace(headModelId)) return Array.Empty<string>();

        var lookup = Candidates(required: null)
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
