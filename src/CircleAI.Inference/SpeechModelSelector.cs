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

/// <summary>
/// How a modality can be served on this device — the selector's decision, so
/// each caller does not re-derive it from a bare null.
/// </summary>
/// <param name="Quality">
/// <see cref="SelectionQuality.Good"/>/<see cref="SelectionQuality.BelowFloor"/>/
/// <see cref="SelectionQuality.NothingFits"/> mean "use <paramref name="Model"/>".
/// <see cref="SelectionQuality.HeuristicFallback"/> means "use the built-in, no
/// model". <see cref="SelectionQuality.Unavailable"/> means "decline".
/// </param>
/// <param name="Model">The model to load, or <c>null</c> for fallback/unavailable.</param>
/// <param name="Reason">Human-readable justification, safe to show a user or log.</param>
public sealed record ModalityPlan(
    SelectionQuality Quality,
    ModelSelection?  Model,
    string           Reason)
{
    /// <summary>The capability can be served at all — by a model OR the built-in.</summary>
    public bool IsAvailable => Quality != SelectionQuality.Unavailable;

    /// <summary>Serve this modality without loading anything.</summary>
    public bool UsesBuiltIn => Quality == SelectionQuality.HeuristicFallback;
}

/// <summary>Picks the best speech model of a given modality the device can hold.</summary>
public interface ISpeechModelSelector
{
    /// <summary>
    /// Best model of <paramref name="modality"/> for this device, or <c>null</c>
    /// when none of that modality is catalogued. <see cref="ModelSelection.Quality"/>
    /// reports fit-vs-function exactly as the chat selector does.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> here is ambiguous by construction — it cannot distinguish
    /// "nothing catalogued but the built-in covers it" from "cannot be done at
    /// all". Prefer <see cref="PlanFor"/>, which answers the question callers
    /// actually have. This overload is kept because existing callers bind to it.
    /// </remarks>
    ModelSelection? BestFor(DeviceProbe probe, ModelModality modality, int minQualityRank = 0);

    /// <summary>Every catalogued model of a modality, quality-ranked, with fit marked.</summary>
    IReadOnlyList<ModelSelection> CandidatesFor(DeviceProbe probe, ModelModality modality);

    /// <summary>
    /// How to serve <paramref name="modality"/> on this device: load a model,
    /// use the built-in heuristic, or decline. This is THE decision — callers
    /// should branch on it rather than null-checking <see cref="BestFor"/>.
    /// </summary>
    /// <remarks>
    /// Default implementation so existing <see cref="ISpeechModelSelector"/>
    /// implementers (test fakes included) keep compiling. It is deliberately
    /// conservative: knowing nothing about built-ins, it can only report
    /// model-or-<see cref="SelectionQuality.Unavailable"/>. The real selector
    /// overrides it with the fallback table.
    /// </remarks>
    ModalityPlan PlanFor(DeviceProbe probe, ModelModality modality, int minQualityRank = 0)
    {
        var pick = BestFor(probe, modality, minQualityRank);
        return pick is null
            ? new ModalityPlan(SelectionQuality.Unavailable, null,
                $"no {modality} model is catalogued")
            : new ModalityPlan(pick.Quality, pick, $"{pick.ModelId} ({pick.Quality})");
    }

    /// <summary>
    /// Best model of <paramref name="modality"/> for this device that actually
    /// serves <paramref name="language"/> (BCP-47 / ISO-639), or <c>null</c> when
    /// none of that language is catalogued. This is what makes the node
    /// multilingual: a request for isiZulu TTS must never return the English voice.
    /// </summary>
    /// <remarks>
    /// Default is conservative — knowing nothing about the catalogue's language
    /// tags, it declines. The real <see cref="SpeechModelSelector"/> overrides it
    /// with a language filter over the registry, mirroring how
    /// <see cref="PlanFor(DeviceProbe, ModelModality, int)"/> is defaulted here and
    /// overridden there.
    /// </remarks>
    ModelSelection? BestFor(DeviceProbe probe, ModelModality modality, string language, int minQualityRank = 0)
        => null;

    /// <summary>
    /// How to serve <paramref name="modality"/> in <paramref name="language"/> on
    /// this device: load that language's model, or decline. THE language-aware
    /// decision — callers branch on it instead of null-checking.
    /// </summary>
    ModalityPlan PlanFor(DeviceProbe probe, ModelModality modality, string language, int minQualityRank = 0)
    {
        var pick = BestFor(probe, modality, language, minQualityRank);
        return pick is null
            ? new ModalityPlan(SelectionQuality.Unavailable, null,
                $"no {modality} model is catalogued for language '{language}'")
            : new ModalityPlan(pick.Quality, pick,
                $"{pick.ModelId} for {modality} [{language}] ({pick.Quality})");
    }
}

/// <summary>
/// <see cref="ISpeechModelSelector"/> over the embedded registry. Filters to the
/// requested modality — it will never return a chat model, and the chat
/// selector will never return one of these.
/// </summary>
/// <remarks>
/// Named for speech because that was its first job, but it is really the
/// selector for every NON-chat modality: <see cref="ModelModality.Vision"/> is
/// selected here too, on the same device-fit maths. Kept under the existing name
/// rather than renamed, because the name is load-bearing at several call sites
/// and a rename buys nothing but churn.
/// </remarks>
public sealed class SpeechModelSelector : ISpeechModelSelector
{
    private readonly ModelRegistryService _registry;

    /// <summary>
    /// Minimum <see cref="DeviceTier"/> at which on-device coding is even
    /// attempted. Below it, <see cref="ModelModality.Coding"/> is
    /// <see cref="SelectionQuality.Unavailable"/> on hardware grounds alone.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CircleAI.CodeAgent.CodingModelRequirements.Default.MinDeviceTier</c>
    /// (Tablet — the RAM&#160;&#8805;&#160;6&#160;GB rung under
    /// <see cref="DeviceProbe.Classify"/>). Duplicated as a local constant ON
    /// PURPOSE: <c>CircleAI.CodeAgent</c> references <c>CircleAI.Inference</c>, so
    /// this assembly must NOT reference back — sharing the constant would be a
    /// dependency cycle. The coarse tier gate lives here; the precise RAM /
    /// storage / hash-verified-bundle gate stays in <c>CodingCapabilityPlanner</c>,
    /// the authority the agent loop actually consults. Kept in sync by comment
    /// and by <c>ModalityPlanTests</c>.
    /// </remarks>
    private const DeviceTier CodingFloorTier = DeviceTier.Tablet;

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
        var ramGb     = probe.UsableRamGb;   // free RAM minus KV-growth headroom
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

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
    /// <remarks>
    /// The fallback table below is the whole point of this method, and every row
    /// is a claim about code that actually ships — do not add a row without the
    /// implementation to back it, or the selector starts lying about what the
    /// device can do.
    /// </remarks>
    public ModalityPlan PlanFor(DeviceProbe probe, ModelModality modality, int minQualityRank = 0)
    {
        ArgumentNullException.ThrowIfNull(probe);

        // CODING HARDWARE FLOOR. Coding carries a tier floor the other rungs do
        // not: a real 3-7B code model cannot run in a low-end phone's RAM budget,
        // so below CodingFloorTier the answer is not "a smaller model," it is "not
        // on this device" — Unavailable BY DESIGN, independent of the catalogue.
        // Checked BEFORE BestFor so a catalogued toy entry that happens to fit a
        // weak device's RAM cannot sneak it past the floor. This mirrors
        // CircleAI.CodeAgent.CodingCapabilityPlanner's gate.
        if (modality == ModelModality.Coding)
        {
            var tier = probe.Classify();
            if (tier < CodingFloorTier)
                return new ModalityPlan(SelectionQuality.Unavailable, null,
                    $"on-device coding needs a device of tier >= {CodingFloorTier}; this device is {tier}. " +
                    "Unavailable by design (mirrors CodingCapabilityPlanner's hardware floor).");
        }

        var pick = BestFor(probe, modality, minQualityRank);
        if (pick is not null)
            return new ModalityPlan(pick.Quality, pick,
                $"{pick.ModelId} selected for {modality} ({pick.Quality})");

        // Nothing catalogued. Is there a built-in that needs no model?
        switch (modality)
        {
            // EnergyVadDetector is pure RMS arithmetic over the PCM frames — no
            // model, no download, runs on anything. VAD is never unavailable.
            case ModelModality.Vad:
                return new ModalityPlan(SelectionQuality.HeuristicFallback, null,
                    "no VAD model catalogued; using built-in energy VAD (works, lower accuracy in noise)");

            // EnergyWakeWordDetector has no wake model either — it TRANSCRIBES
            // short segments and string-matches. So its fallback is only real if
            // ASR itself can be served; without ASR there is nothing to match on.
            case ModelModality.WakeWord:
                var asr = BestFor(probe, ModelModality.Asr);
                return asr is null
                    ? new ModalityPlan(SelectionQuality.Unavailable, null,
                        "no wake-word model catalogued, and the energy detector's ASR fallback has no ASR model either")
                    : new ModalityPlan(SelectionQuality.HeuristicFallback, null,
                        $"no wake-word model catalogued; using energy VAD + '{asr.ModelId}' transcribe-and-match " +
                        "(works, costs more battery than a keyword spotter)");

            // ProceduralMusicBedGenerator synthesises a royalty-free chord/arpeggio
            // bed straight to PCM with pure managed maths — no model, no download,
            // runs on the lowest-end device. So music is NEVER unavailable; a neural
            // music model (MusicBedBackend.Neural) supersedes the bed when catalogued.
            case ModelModality.Music:
                return new ModalityPlan(SelectionQuality.HeuristicFallback, null,
                    "no music model catalogued; using the procedural music bed " +
                    "(works offline, rule-based synthesis rather than a neural model)");

            // ManagedMediaRenderer composites layers + text and walks the motion
            // timeline entirely in managed code — the declarative render is real and
            // offline. The neural encoder / HTML-capture path is a seam a catalogued
            // model (or an IHtmlFrameProvider) fills; without one the declarative
            // built-in still produces a clip, so video is never unavailable.
            case ModelModality.Video:
                return new ModalityPlan(SelectionQuality.HeuristicFallback, null,
                    "no video model catalogued; using the programmatic media renderer " +
                    "(works offline, deterministic composition rather than a neural encoder)");

            // Coding cleared the hardware floor at the top of PlanFor (so this is a
            // capable device), but there is no coding model catalogued AND no
            // built-in can stand in — you cannot fake a 3-7B code model with
            // arithmetic. Capable, still Unavailable, for the right reason. Mirrors
            // CodingCapabilityPlanner's "device is capable, but no model installed".
            case ModelModality.Coding:
                return new ModalityPlan(SelectionQuality.Unavailable, null,
                    "device clears the coding hardware floor, but no on-device coding model is " +
                    "catalogued; a real 3-7B code model must be registered (see CircleAI.CodeAgent) to enable");

            // ASR, TTS and Vision have no non-model implementation. Saying
            // otherwise would mean claiming a capability that cannot run.
            default:
                return new ModalityPlan(SelectionQuality.Unavailable, null,
                    $"no {modality} model is catalogued and there is no built-in fallback for it");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelSelection> CandidatesFor(DeviceProbe probe, ModelModality modality)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier      = probe.Classify();
        var ramGb     = probe.UsableRamGb;   // free RAM minus KV-growth headroom
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

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

    /// <inheritdoc/>
    /// <remarks>
    /// The language filter is the whole point: only entries tagged for
    /// <paramref name="language"/> are considered, so a specific-language request
    /// can never be served a different language's voice — speaking the wrong tongue
    /// is worse than declining (the fit-vs-function rule, applied to language). The
    /// device-fit maths is identical to the language-agnostic
    /// <see cref="BestFor(DeviceProbe, ModelModality, int)"/>.
    /// </remarks>
    public ModelSelection? BestFor(DeviceProbe probe, ModelModality modality, string language, int minQualityRank = 0)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (modality == ModelModality.Chat)
            throw new ArgumentException(
                "Chat selection goes through IModelSelector.BestFit, not the speech selector.",
                nameof(modality));

        var tier      = probe.Classify();
        var ramGb     = probe.UsableRamGb;
        var storageGb = probe.StorageFreeGb;   // catalogue units — see DeviceProbe.BytesPerGb

        var ofLanguage = _registry.AllModels
            .Where(e => e.Modality == modality && LanguageMatches(e.Language, language))
            .ToList();
        if (ofLanguage.Count == 0) return null;   // nothing catalogued for this language — honest null

        var deviceOk = ofLanguage
            .Where(e => e.MinRamGb <= ramGb + 0.0001 &&
                        (storageGb <= 0 || e.MinStorageGb <= storageGb + 0.0001))
            .ToList();

        var somethingFits = deviceOk.Count > 0;
        var winner = somethingFits
            ? deviceOk.OrderByDescending(e => e.QualityRank).ThenBy(e => e.MinRamGb).First()
            : ofLanguage.OrderBy(e => e.MinRamGb).ThenBy(e => e.TotalBytes).First();

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

    /// <summary>
    /// Language-tag match: a request matches an entry when their PRIMARY subtags
    /// are equal, case-insensitively — so <c>zu</c> matches <c>zu-ZA</c>/<c>zu_ZA</c>
    /// and <c>en</c> matches <c>en_US</c>. An entry with NO language tag never
    /// matches a specific request: an untagged voice is not assumed to be any
    /// particular language.
    /// </summary>
    /// <remarks>
    /// An entry may list SEVERAL languages, comma-separated, because one model can
    /// serve many: the South African VITS voice is tagged
    /// <c>af,en,nr,nso,st,ss,tn,ts,ve,xh,zu</c> and speaks all eleven.
    ///
    /// Splitting only on '-' and '_' meant such an entry matched no request at all
    /// — the whole eleven-language tag was compared as one opaque string, so every
    /// one of those languages reported "nothing catalogued" while the voice sat in
    /// the registry. That included the only isiNdebele voice we have ever found.
    /// The failure was silent: declining to serve a language looks exactly like
    /// not having it.
    /// </remarks>
    internal static bool LanguageMatches(string? entryLanguage, string requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(entryLanguage)) return false;
        static string Primary(string s) => s.Trim().ToLowerInvariant().Split('-', '_')[0];

        var wanted = Primary(requestedLanguage);
        foreach (var tag in entryLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Primary(tag) == wanted) return true;

        return false;
    }
}
