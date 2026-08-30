#nullable enable

// BundleModelLoader.cs
//
// IModelLoader that understands the *bundle* registry shape — the shape every
// entry in the embedded registry actually uses today.
//
// Why this exists (two concrete defects in LocalModelLoader):
//
//   1. LocalModelLoader.DownloadModelAsync THROWS on any entry with
//      BundleFiles[], telling the caller to "use ModelDownloadService.
//      EnsureBundleAsync via MnnInferenceBridgeFactory instead". Since every
//      registry entry is bundle-shaped, that loader cannot fetch any current
//      model — so AIService.StartAsync could never download one.
//
//   2. LocalModelLoader.GetModelPath returns "<modelDir>/llm.mnn.weight" (the
//      SHA anchor). AIService passes that straight into QwenTextGenerator ->
//      mnn_llm_create, but MNN-LLM's Llm::create() expects the *config.json*
//      path (see MnnInferenceBridgeFactory, which passes
//      Path.Combine(modelDir, "config.json")). So even a fully-downloaded
//      bundle would hand MNN the weight blob instead of its config.
//
// This loader mirrors what MnnInferenceBridgeFactory already does server-side,
// but behind the IModelLoader seam that AIService actually calls — so an
// on-device host gets device-aware selection -> download -> load with no wiring.
//
// The weight file remains the integrity anchor (largest file, so a hash
// mismatch is the most diagnostic); it is just no longer the *load* path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// <see cref="IModelLoader"/> backed by <see cref="ModelDownloadService"/> and the
/// embedded model registry. Handles both the bundle shape (MNN multi-file models)
/// and the legacy single-file shape.
/// </summary>
public sealed class BundleModelLoader : IModelLoader
{
    /// <summary>What MNN-LLM's <c>Llm::create()</c> actually loads.</summary>
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Canonical weight file — present in every MNN-LLM bundle and the largest
    /// member, so a SHA-256 mismatch here is the most diagnostic integrity check.
    /// </summary>
    private const string AnchorFileName = "llm.mnn.weight";

    private readonly ModelRegistryService _registry;
    private readonly ModelDownloadService _downloads;
    private readonly string _storageRoot;
    private readonly bool _ownsRegistry;
    private readonly IModelDownloadGate? _gate;
    private bool _disposed;

    /// <param name="modelDirectory">
    /// Root directory for cached models. Defaults to
    /// <c>{ApplicationData}/CircleAI/Models</c>. Mobile hosts should pass the
    /// app's own data directory.
    /// </param>
    /// <param name="registry">
    /// Shared registry instance. When <c>null</c> the loader constructs (and
    /// disposes) its own from the embedded catalog.
    /// </param>
    /// <param name="gate">
    /// Optional policy deciding whether a large download may proceed — e.g. not
    /// on mobile data. <c>null</c> means no gate, matching the pre-2026-07-20
    /// behaviour where AIOptions.WifiOnlyModelDownload was inert.
    /// </param>
    public BundleModelLoader(
        string? modelDirectory = null,
        ModelRegistryService? registry = null,
        IModelDownloadGate? gate = null)
    {
        _gate = gate;
        // See ModelPaths: this used SpecialFolder.ApplicationData, which on
        // Android is a subdirectory of the folder the app actually uses, so a
        // caller that passed nothing downloaded a second copy of every model.
        _storageRoot = CircleAI.Core.ModelPaths.Resolve(modelDirectory);

        Directory.CreateDirectory(_storageRoot);

        _registry     = registry ?? new ModelRegistryService();
        _ownsRegistry = registry is null;
        _downloads    = new ModelDownloadService(_storageRoot);
    }

    /// <summary>
    /// Ensures the model is on disk and returns the path the generator should
    /// load — <c>config.json</c> for bundles, the weight file for legacy entries.
    /// </summary>
    /// <summary>
    /// Same as <see cref="DownloadModelAsync(string, IProgress{float})"/>, but
    /// reporting bytes, rate, ETA, which file and what phase.
    /// </summary>
    /// <remarks>
    /// A BARE RATIO IS NOT ENOUGH TO WAIT ON, and how much it is not enough by
    /// depends entirely on the phone. The same 22.8 GB bundle is minutes on a
    /// premium handset and the better part of an hour on a P30 Lite over 48 Mbps
    /// — measured, on both a single stream and eight. A percentage that creeps
    /// is indistinguishable from a hang at the slow end, and the slow end is
    /// exactly where the people this is built for are.
    /// <para>
    /// The download service has computed all of this all along and the ratio-only
    /// overload threw it away one call before the screen. This is that call, not
    /// throwing it away.
    /// </para>
    /// </remarks>
    public Task<string> DownloadModelAsync(
        string modelName, IProgress<CircleAI.Core.DownloadProgress>? progress,
        CancellationToken ct = default)
        => DownloadCoreAsync(modelName, progress, ct);

    public async Task<string> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
    {
        // Adapt onto the rich path so there is one implementation, not two that
        // drift.
        IProgress<CircleAI.Core.DownloadProgress>? rich = progress is null
            ? null
            : new Progress<CircleAI.Core.DownloadProgress>(p => progress.Report((float)p.Ratio));

        return await DownloadCoreAsync(modelName, rich, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string> DownloadCoreAsync(
        string modelName, IProgress<CircleAI.Core.DownloadProgress>? progress, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = _registry.GetLatestModel(modelName)
            ?? throw new ArgumentException(
                $"Model '{modelName}' is not in the registry.", nameof(modelName));

        // Metered-connection gate. Checked BEFORE any bytes move, and skipped
        // when the bundle is already cached — re-verifying an on-disk model
        // must never be refused for being "on mobile data".
        if (_gate is not null && !ModelExists(modelName))
        {
            var blocked = _gate.BlockReason(entry.TotalBytes);
            if (blocked is not null)
                throw new ModelDownloadBlockedException(blocked);
        }

        var relay = progress;

        if (entry.IsBundle)
        {
            if (string.IsNullOrWhiteSpace(entry.Repo))
                throw new InvalidOperationException(
                    $"Registry entry '{modelName}' has BundleFiles but no Repo — bundle URLs cannot be built.");

            var spec = ToSpec(entry.BundleFiles!);

            // Pass entry.Source explicitly. The 5-arg overload hard-codes
            // ModelScope, so every HuggingFace-hosted entry (all the speech
            // models — whisper.cpp, Piper) would be fetched from ModelScope URLs
            // that do not exist. It fails as a 404 at download time, far from
            // the registry entry that actually caused it.
            var modelDir = await _downloads
                .EnsureBundleAsync(modelName, entry.Repo!, entry.Source, spec, relay, ct)
                .ConfigureAwait(false);

            // Stamp installed.json so ModelRegistryService.CheckForUpgradesAsync
            // can detect drift later. Best-effort — never fail the load for it.
            try
            {
                await _downloads.WriteInstalledManifestAsync(
                        modelDir, modelName, entry.Version, entry.Repo, spec, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* manifest is advisory */ }

            return ResolveLoadPath(entry, modelDir, modelName);
        }

        // Legacy single-file entry.
        if (string.IsNullOrWhiteSpace(entry.Url) ||
            !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Registry entry '{modelName}' has neither BundleFiles nor a valid Url.");

        // The single-file path still speaks ratio-only; it is used by the legacy
        // entries, none of which are large enough for the difference to matter.
        IProgress<double>? ratio = relay is null
            ? null
            : new Progress<double>(d => relay.Report(new CircleAI.Core.DownloadProgress
            {
                FileName      = modelName,
                BytesReceived = (long)(d * Math.Max(0, entry.TotalBytes)),
                TotalBytes    = Math.Max(0, entry.TotalBytes),
            }));

        return await _downloads
            .EnsureModelAsync(modelName, uri, entry.Checksum, ratio, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Path the generator should load. Returns the expected location even when
    /// the file is not present yet — callers (AIService) test
    /// <see cref="File.Exists(string)"/> and download when it is missing.
    /// </summary>
    public string GetModelPath(string modelName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = _registry.GetLatestModel(modelName)
            ?? throw new FileNotFoundException($"Model '{modelName}' is not in the registry.");

        if (!entry.IsBundle)
            return Path.Combine(_storageRoot, modelName + ".gguf"); // single-file layout

        // Bundles land in {root}/{modelId}/. WHICH file the runtime loads is
        // modality-specific — resolve it when the bundle is on disk; otherwise
        // return the conventional chat anchor so callers can File.Exists-test
        // and trigger a download.
        var modelDir = Path.Combine(_storageRoot, modelName);
        if (Directory.Exists(modelDir))
        {
            try { return ResolveLoadPath(entry, modelDir, modelName); }
            catch (InvalidOperationException) { /* not fully downloaded yet */ }
        }
        return Path.Combine(modelDir, ConfigFileName);
    }

    /// <summary>
    /// True when the model is cached AND passes its integrity check — the
    /// weight file's pinned SHA-256 for bundles, the file checksum otherwise.
    /// </summary>
    /// <summary>
    /// Is this model present, without hashing it?
    /// </summary>
    /// <remarks>
    /// THE HASH IS FOR LOADING, NOT FOR ASKING. ModelExists verifies the anchor
    /// file's SHA-256, which for the chat model means hashing 470 MB - fine
    /// before you load a model into an inference engine, ruinous on a loading
    /// screen that asks about every model on every launch. That was the four
    /// seconds a census took on a P30.
    ///
    /// "Is it here" is a different question from "is it intact", and a census
    /// asks the first: the anchor file exists at its full catalogued size. A
    /// truncated download fails the size check; a bit-flip it will not catch,
    /// and does not need to - the load path still hashes.
    /// </remarks>
    public bool ModelPresent(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry is null) return false;

            if (entry.IsBundle)
            {
                var modelDir = Path.Combine(_storageRoot, modelName);
                if (!Directory.Exists(modelDir)) return false;

                if (entry.Modality == ModelModality.Chat &&
                    !File.Exists(Path.Combine(modelDir, ConfigFileName)))
                    return false;

                var anchor = entry.BundleFiles!
                    .FirstOrDefault(f => string.Equals(f.Name, AnchorFileName, StringComparison.OrdinalIgnoreCase))
                    ?? entry.BundleFiles!.OrderByDescending(f => f.SizeBytes).FirstOrDefault();
                if (anchor is null) return false;

                var info = new FileInfo(Path.Combine(modelDir, anchor.Name));
                return info.Exists && info.Length >= anchor.SizeBytes;
            }

            var filePath = Path.Combine(_storageRoot, modelName + ".gguf");
            return File.Exists(filePath);
        }
        catch
        {
            return false;
        }
    }

    public bool ModelExists(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry is null) return false;

            if (entry.IsBundle)
            {
                var modelDir = Path.Combine(_storageRoot, modelName);
                if (!Directory.Exists(modelDir)) return false;

                // Chat (MNN) additionally requires config.json to load at all.
                if (entry.Modality == ModelModality.Chat &&
                    !File.Exists(Path.Combine(modelDir, ConfigFileName)))
                    return false;

                // Integrity anchor: the weight file for MNN, else the LARGEST
                // catalogued file (Piper's .onnx, Whisper's ggml .bin) — biggest
                // file means a hash mismatch is the most diagnostic.
                var anchor = entry.BundleFiles!
                    .FirstOrDefault(f => string.Equals(f.Name, AnchorFileName, StringComparison.OrdinalIgnoreCase))
                    ?? entry.BundleFiles!.OrderByDescending(f => f.SizeBytes).FirstOrDefault();
                if (anchor is null) return false;

                var anchorPath = Path.Combine(modelDir, anchor.Name);
                return File.Exists(anchorPath) && VerifySha256(anchorPath, anchor.Sha256);
            }

            var filePath = Path.Combine(_storageRoot, modelName + ".gguf");
            if (!File.Exists(filePath)) return false;
            return entry.Checksum is null || VerifySha256(filePath, entry.Checksum);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Upgrade detection lives in <c>ModelRegistryService.CheckForUpgradesAsync</c>
    /// (surfaced by <c>AIService.CheckForUpgradesAsync</c> and the
    /// <c>installed.json</c> manifests this loader writes), so this loader does
    /// not perform its own out-of-band version check.
    /// </summary>
    public Task<bool> CheckForCriticalUpdateAsync() => Task.FromResult(false);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<BundleFileSpec> ToSpec(IReadOnlyList<BundleFile> files)
        => files.Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes)).ToList();

    /// <summary>
    /// The file a bundle's RUNTIME actually loads — which differs by modality.
    /// MNN chat wants config.json; Piper TTS wants the .onnx; Whisper ASR wants
    /// the ggml .bin. Hard-coding config.json (as this did) made the loader
    /// MNN-only and it REJECTED every speech bundle outright.
    /// </summary>
    private static string ResolveLoadPath(ModelEntry entry, string modelDir, string modelName)
    {
        string? Find(params string[] patterns)
        {
            foreach (var p in patterns)
            {
                var hit = Directory.EnumerateFiles(modelDir, p, SearchOption.AllDirectories)
                    .OrderBy(f => f.Length)   // prefer the shallowest/simplest match
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
            return null;
        }

        var path = entry.Modality switch
        {
            ModelModality.Tts      => Find("*.onnx"),
            ModelModality.Asr      => Find("*.bin", "*.onnx"),
            ModelModality.Vad      => Find("*.onnx"),
            ModelModality.WakeWord => ResolveWakeWord(modelDir) ?? Find("*.onnx", "*.tflite"),
            _                      => Find(ConfigFileName),   // Chat: MNN config.json
        };

        if (path is null)
            throw new InvalidOperationException(
                $"Bundle '{modelName}' ({entry.Modality}) downloaded to '{modelDir}' but no loadable " +
                "model file was found for that modality.");

        return path;
    }

    /// <summary>
    /// A wake bundle's load path: the DIRECTORY when it holds a three-graph
    /// transducer, otherwise null so the single-file rule applies.
    /// </summary>
    /// <remarks>
    /// A streaming transducer is encoder + decoder + joiner + tokens, and no one
    /// of those files is loadable on its own — the runtime needs the folder around
    /// them. Returning "the shortest .onnx name" for such a bundle hands back an
    /// arbitrary third of a model, which cannot be made to work and does not look
    /// broken: it is a real path to a real file. The first consumer worked around
    /// it by scanning for an encoder and taking its directory, and every later one
    /// would have had to rediscover the same trick.
    /// </remarks>
    private static string? ResolveWakeWord(string modelDir)
    {
        var byDirectory = Directory
            .EnumerateFiles(modelDir, "*.onnx", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetDirectoryName(f)!);

        foreach (var group in byDirectory)
        {
            var names = group.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToList();
            if (names.Any(n => n.Contains("encoder")) &&
                names.Any(n => n.Contains("decoder")) &&
                names.Any(n => n.Contains("joiner")))
                return group.Key;
        }
        return null;
    }

    /// <summary>
    /// Compares a file's SHA-256 against an expected value in either
    /// <c>sha256:&lt;hex&gt;</c> or bare-hex form.
    /// </summary>
    private static bool VerifySha256(string filePath, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var want = expected.Trim();
        if (want.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            want = want["sha256:".Length..].Trim();

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));

        return string.Equals(want, actual, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloads.Dispose();
        if (_ownsRegistry) _registry.Dispose();
    }
}
