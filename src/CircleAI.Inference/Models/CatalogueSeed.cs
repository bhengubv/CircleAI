// CatalogueSeed.cs
//
// The embedded registry (embedded_registry.json) demoted from immutable spine to
// FIRST-RUN SEED. On a cold device with no feed yet reachable, this bootstraps
// the catalogue so the app has a model ladder offline; once a signed feed has
// written rows, this never runs again (it only seeds an EMPTY catalogue) and the
// feed — not the APK — is the source of truth. This is what retires
// CatalogueMerge's append-only merge: the seed is a starting point, not a floor.
//
// It stamps each row's Engine (the registry JSON carries none) so the assessor's
// engine gate is honest from the first launch.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Bootstraps an empty <see cref="IModelCatalog"/> from the embedded registry.</summary>
public static class CatalogueSeed
{
    /// <summary>
    /// Seed <paramref name="catalog"/> from the embedded registry when — and only
    /// when — it is empty. Returns the number of rows written (0 if the catalogue
    /// was already populated, e.g. by a feed). Rows land unassessed; run an
    /// <see cref="IModelAssessor"/> afterwards to fill <c>compatible</c>/<c>rank</c>.
    /// </summary>
    /// <param name="catalog">The catalogue to seed.</param>
    /// <param name="registry">
    /// Registry to seed from; defaults to a fresh <see cref="ModelRegistryService"/>
    /// that loads the embedded JSON. Passable for tests.
    /// </param>
    public static int SeedFromEmbedded(IModelCatalog catalog, ModelRegistryService? registry = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Count() > 0) return 0;   // already seeded or fed — never clobber

        var ownsRegistry = registry is null;
        registry ??= new ModelRegistryService();
        try
        {
            var n = 0;
            foreach (var entry in registry.AllModels)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                catalog.Upsert(entry with { Engine = DeriveEngine(entry) });
                n++;
            }
            return n;
        }
        finally
        {
            if (ownsRegistry) registry.Dispose();
        }
    }

    /// <summary>
    /// Best-effort engine for a registry entry that predates the
    /// <see cref="ModelEntry.Engine"/> field. The curated chat ladder is MNN; a
    /// future GGUF row would derive <see cref="ModelEngine.LlamaCpp"/> and be
    /// correctly gated off an MNN-only device.
    /// </summary>
    public static ModelEngine DeriveEngine(ModelEntry e)
    {
        var q = e.Quantization?.ToLowerInvariant() ?? string.Empty;
        var arch = e.Architecture?.ToLowerInvariant() ?? string.Empty;

        if (q.Contains("mnn")) return ModelEngine.Mnn;
        if (q.Contains("gguf") || arch.Contains("gguf")) return ModelEngine.LlamaCpp;
        if (arch.Contains("whisper") || q.Contains("ggml")) return ModelEngine.Ggml;
        if (q.Contains("onnx") || arch.Contains("onnx") || arch.Contains("vits")
            || arch.Contains("piper") || arch.Contains("mms") || arch.Contains("silero")
            || arch.Contains("kokoro"))
            return ModelEngine.Onnx;

        // Speech modalities with no engine named are ONNX in this catalogue.
        return e.Modality switch
        {
            ModelModality.Tts or ModelModality.Asr or ModelModality.Vad
                or ModelModality.WakeWord or ModelModality.SpeakerEmbedding => ModelEngine.Onnx,
            _ => ModelEngine.Mnn,
        };
    }
}
