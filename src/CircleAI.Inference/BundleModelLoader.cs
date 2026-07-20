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
        _storageRoot = string.IsNullOrWhiteSpace(modelDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CircleAI",
                "Models")
            : modelDirectory!;

        Directory.CreateDirectory(_storageRoot);

        _registry     = registry ?? new ModelRegistryService();
        _ownsRegistry = registry is null;
        _downloads    = new ModelDownloadService(_storageRoot);
    }

    /// <summary>
    /// Ensures the model is on disk and returns the path the generator should
    /// load — <c>config.json</c> for bundles, the weight file for legacy entries.
    /// </summary>
    public async Task<string> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
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

        // IModelLoader speaks IProgress<float>; ModelDownloadService speaks double.
        IProgress<double>? relay = progress is null
            ? null
            : new Progress<double>(d => progress.Report((float)d));

        if (entry.IsBundle)
        {
            if (string.IsNullOrWhiteSpace(entry.Repo))
                throw new InvalidOperationException(
                    $"Registry entry '{modelName}' has BundleFiles but no Repo — bundle URLs cannot be built.");

            var spec = ToSpec(entry.BundleFiles!);

            var modelDir = await _downloads
                .EnsureBundleAsync(modelName, entry.Repo!, spec, relay, CancellationToken.None)
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

            var configPath = Path.Combine(modelDir, ConfigFileName);
            if (!File.Exists(configPath))
                throw new InvalidOperationException(
                    $"Bundle '{modelName}' downloaded but '{ConfigFileName}' is missing in '{modelDir}'.");

            return configPath;
        }

        // Legacy single-file entry.
        if (string.IsNullOrWhiteSpace(entry.Url) ||
            !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Registry entry '{modelName}' has neither BundleFiles nor a valid Url.");

        return await _downloads
            .EnsureModelAsync(modelName, uri, entry.Checksum, relay, CancellationToken.None)
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

        // Bundles: ModelDownloadService writes them to {root}/{modelId}/ and MNN
        // loads config.json from there.
        return entry.IsBundle
            ? Path.Combine(_storageRoot, modelName, ConfigFileName)
            : Path.Combine(_storageRoot, modelName + ".gguf"); // single-file layout
    }

    /// <summary>
    /// True when the model is cached AND passes its integrity check — the
    /// weight file's pinned SHA-256 for bundles, the file checksum otherwise.
    /// </summary>
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

                // MNN needs config.json to load at all.
                if (!File.Exists(Path.Combine(modelDir, ConfigFileName))) return false;

                var anchor = entry.BundleFiles!
                    .FirstOrDefault(f => string.Equals(f.Name, AnchorFileName, StringComparison.OrdinalIgnoreCase));
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
