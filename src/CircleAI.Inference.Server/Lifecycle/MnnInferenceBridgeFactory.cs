// MnnInferenceBridgeFactory.cs
//
// Production IBridgeFactory — composes the four SDK seams (model registry,
// model download service, native runtime fetcher, MNN chat generator) into
// a working IInferenceBridge for whatever (modelId, backend, tier) the
// admin endpoint requests.
//
// Pipeline:
//   1. CapabilityProbe + caller backend pick
//   2. NativeRuntimeFetcher.EnsureRuntimeAsync → mnnbridge + MNN libs on disk
//   3. NativeLibraryResolver.OverrideDirectory → P/Invoke resolves there
//   4. ModelRegistryService.GetLatestModel(modelId) → ModelEntry { Url, Sha }
//   5. ModelDownloadService.EnsureModelAsync(modelId, uri, sha) → modelPath
//   6. new QwenTextGenerator(modelPath) → IChatGenerator
//   7. new LocalProcessInferenceBridge(generator, descriptor, probe)
//
// Replaces UnconfiguredBridgeFactory. Wired by default in
// AddCircleAIInferenceServer.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircleAI.Core.Models;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Inference;
using CircleAI.Inference.Server.Endpoints;
using CircleAI.Inference.Server.Hosting;
using CircleAI.Inference.Server.Options;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;

namespace CircleAI.Inference.Server.Lifecycle;

/// <summary>
/// Production <see cref="IBridgeFactory"/> backed by the Alibaba MNN engine
/// and Qwen-family models from ModelScope.
/// </summary>
public sealed class MnnInferenceBridgeFactory : IBridgeFactory, IDisposable
{
    private readonly ICapabilityProbe _probe;
    private readonly INativeRuntimeFetcher _runtimeFetcher;
    private readonly ModelDownloadService _modelDownload;
    private readonly ModelRegistryService _modelRegistry;
    private readonly INativeRuntimeStatus? _nativeStatus;
    private readonly ILogger<MnnInferenceBridgeFactory> _log;

    /// <summary>
    /// Construct the factory. Hosts wire this via DI; the
    /// <see cref="InferenceServerOptions.ModelStorageRoot"/> path is resolved
    /// once at construction.
    /// </summary>
    public MnnInferenceBridgeFactory(
        ICapabilityProbe probe,
        INativeRuntimeFetcher runtimeFetcher,
        IOptions<InferenceServerOptions> options,
        ILogger<MnnInferenceBridgeFactory> log,
        ModelRegistryService? modelRegistry = null,
        INativeRuntimeStatus? nativeStatus = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(runtimeFetcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _probe          = probe;
        _runtimeFetcher = runtimeFetcher;
        _log            = log;
        _modelRegistry  = modelRegistry ?? new ModelRegistryService();
        _nativeStatus   = nativeStatus;

        var storageRoot = PathExpansion.ExpandUserPath(options.Value.ModelStorageRoot);
        Directory.CreateDirectory(storageRoot);
        _modelDownload = new ModelDownloadService(storageRoot);
    }

    /// <inheritdoc/>
    public async Task<IInferenceBridge> CreateAsync(
        string modelId, BackendKind backend, CapabilityTier tier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // ── 1. Resolve the model entry from the registry FIRST ───────────
        //
        // Cheap, deterministic, no network. Fails fast for unknown ids
        // before we spend the user's bandwidth fetching a 240 MB runtime
        // for a model that doesn't exist.
        var entry = _modelRegistry.GetLatestModel(modelId)
            ?? throw new InvalidOperationException(
                $"Model '{modelId}' is not in the embedded registry. " +
                "Either add it to CircleAI.Core/Models/embedded_registry.json or pre-register the model file path via an alternative IBridgeFactory.");

        Uri? downloadUri = null;
        if (!entry.IsBundle)
        {
            if (string.IsNullOrWhiteSpace(entry.Url) ||
                !Uri.TryCreate(entry.Url, UriKind.Absolute, out downloadUri))
                throw new InvalidOperationException(
                    $"Registry entry for '{modelId}' has neither a BundleFiles array nor a valid Url. " +
                    "Repopulate the registry with tools/recalibrate-registry-sha or pre-register a file path " +
                    "via a custom IBridgeFactory.");
        }
        else if (string.IsNullOrWhiteSpace(entry.Repo))
        {
            throw new InvalidOperationException(
                $"Registry entry for '{modelId}' has BundleFiles but no Repo path — bundle URLs cannot be built.");
        }

        // ── 2 & 3. Probe host, fetch the right MNN runtime bundle ────────
        var profile = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        _log.LogInformation(
            "Materialising bridge for model {ModelId} on {Backend} (host: {Os}/{Arch}, GPU: {Gpu}).",
            modelId, backend, profile.Os, profile.Arch, profile.Gpu?.Model ?? "none");

        NativeRuntimeInstall runtimeInstall;
        try
        {
            runtimeInstall = await _runtimeFetcher
                .EnsureRuntimeAsync(profile.Os, profile.Arch, backend, progress: null, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Could not materialise MNN runtime for ({profile.Os}, {profile.Arch}, {backend}) " +
                $"required by model '{modelId}'. {ex.Message}", ex);
        }

        // ── 4. Flatten + preload + self-check the native runtime ─────────
        //
        // The fetched MNN bundle lands at a deeply nested path (Windows:
        // lib/x64/Release/Dynamic/MD/MNN.dll; macOS: MNN.framework/...).
        // mnnbridge.dll (CircleAI's shim) ships in the NuGet under
        // runtimes/{RID}/native/ and is the FIRST library Windows loads
        // when a P/Invoke for "mnnbridge" fires. Windows then resolves
        // mnnbridge's transitive dep on MNN.dll using normal search order —
        // which won't find the fetched (nested) MNN.dll unless we either
        // copy MNN.dll next to mnnbridge.dll or preload it absolutely.
        //
        // NativeRuntimePrep does both, registers the resolver, runs a
        // self-check, and returns the resolved paths so /v1/diagnostics
        // can surface them.
        NativeRuntimePrep.NativeRuntimePaths paths;
        try
        {
            paths = NativeRuntimePrep.PrepareForLoad(
                runtimeInstall.MnnCorePath,
                runtimeInstall.ExtractedRoot,
                _log);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CircleAI native runtime self-check failed for model '{modelId}' " +
                $"on ({profile.Os}, {profile.Arch}, {backend}). {ex.Message}", ex);
        }
        _nativeStatus?.Update(paths);

        // ── 5. Ensure the model is on disk ────────────────────────────────
        // Bundle path (BundleFiles[] present): every file in the bundle is
        // fetched into a per-model directory. MNN-LLM's Llm::create() expects
        // a config.json path; we pass <modelDir>/config.json.
        // Legacy path: single weight file, pre-pointed by entry.Url.
        string modelPath;
        if (entry.IsBundle)
        {
            var bundleSpec = entry.BundleFiles!
                .Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes))
                .ToList();
            var modelDir = await _modelDownload
                .EnsureBundleAsync(modelId, entry.Repo!, bundleSpec, progress: null, ct)
                .ConfigureAwait(false);
            modelPath = Path.Combine(modelDir, "config.json");
            if (!File.Exists(modelPath))
                throw new InvalidOperationException(
                    $"Model '{modelId}' bundle is missing config.json after download. Bundle dir: '{modelDir}'.");
        }
        else
        {
            modelPath = await _modelDownload
                .EnsureModelAsync(modelId, downloadUri!, entry.Checksum, progress: null, ct)
                .ConfigureAwait(false);
        }

        // ── 6. Construct the chat generator ───────────────────────────────
        // 4096 token context is the Qwen 3 family default. Hosts that want a
        // different size can subclass and override; the SDK doesn't expose
        // a per-call context override through IInferenceBridge.
        var generator = new QwenTextGenerator(modelPath, contextSize: 4096);

        // ── 7. Build a descriptor + wrap as IInferenceBridge ──────────────
        var descriptor = new ModelDescriptor(
            ModelId:                modelId,
            Version:                entry.Version,
            Format:                 ModelFormat.Gguf, // MNN reads the same file family — GGUF is the canonical extension shipped
            ContextWindowTokens:    4096,
            VocabSize:              151_936, // Qwen 3 family default
            ParameterCount:         0L,       // unknown from registry — caller can override via custom descriptor path
            QuantisationLabel:      entry.Quantization,
            ApproximateMemoryBytes: ApproxMemoryFromTier(tier));

        var bridge = new LocalProcessInferenceBridge(generator, descriptor, _probe);
        _log.LogInformation(
            "Bridge ready: model={ModelId} path={ModelPath} runtime={RuntimeRoot}.",
            modelId, modelPath, runtimeInstall.ExtractedRoot);
        return bridge;
    }

    private static long ApproxMemoryFromTier(CapabilityTier tier) => tier switch
    {
        CapabilityTier.Tier0_Tiny       =>  1L * 1024 * 1024 * 1024,
        CapabilityTier.Tier1_Small      =>  2L * 1024 * 1024 * 1024,
        CapabilityTier.Tier2_Medium     =>  6L * 1024 * 1024 * 1024,
        CapabilityTier.Tier3_Large      => 12L * 1024 * 1024 * 1024,
        CapabilityTier.Tier4_Frontier   => 24L * 1024 * 1024 * 1024,
        _                                =>  1L * 1024 * 1024 * 1024,
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        _modelDownload.Dispose();
        _modelRegistry.Dispose();
    }
}
