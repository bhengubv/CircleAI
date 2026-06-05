// ModelLifecycleManager.cs
//
// Default IModelLifecycleManager.
//
// Admission gate:
//   1. Already loaded under this id?         -> AlreadyLoaded (no-op success)
//   2. Probe -> VRAM headroom ≥ requested?    -> InsufficientVram if not
//   3. Probe -> RAM headroom ≥ requested?     -> InsufficientRam if not
//   4. Run BridgeFactory(ct)                  -> FactoryFailed on any throw
//   5. Register in IInferenceServerModelRegistry, record state, emit counter
//
// VRAM accounting is conservative: every load deducts from the
// CapabilityProbe.Gpu.VramBytes ceiling. CPU loads only deduct RAM. We do
// NOT re-probe between loads (probing is expensive on Windows / Linux);
// the manager caches the first probe result and uses it for all
// admissions.

using System.Collections.Concurrent;
using CircleAI.Core.Diagnostics;
using CircleAI.Inference.Server.Models;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Inference.Server.Lifecycle;

/// <inheritdoc/>
public sealed class ModelLifecycleManager : IModelLifecycleManager
{
    private readonly IInferenceServerModelRegistry _registry;
    private readonly ICapabilityProbe _probe;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ModelLoadState> _loaded = new(StringComparer.Ordinal);

    // Cached probe result — re-probed lazily on first call.
    private HostProfile? _cachedProfile;

    /// <inheritdoc/>
    public long TotalAllocatedVramBytes
    {
        get
        {
            long sum = 0;
            foreach (var s in _loaded.Values) sum += s.VramBytes;
            return sum;
        }
    }

    /// <inheritdoc/>
    public long TotalAllocatedRamBytes
    {
        get
        {
            long sum = 0;
            foreach (var s in _loaded.Values) sum += s.RamBytes;
            return sum;
        }
    }

    public ModelLifecycleManager(IInferenceServerModelRegistry registry, ICapabilityProbe probe)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _probe    = probe    ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc/>
    public async Task<LoadResult> LoadAsync(ModelLoadDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ModelId);
        ArgumentNullException.ThrowIfNull(descriptor.BridgeFactory);

        // Idempotent fast path — already loaded with same backend/tier is a success.
        if (_loaded.TryGetValue(descriptor.ModelId, out var existing))
        {
            return new LoadResult(LoadOutcome.AlreadyLoaded, existing,
                $"Model '{descriptor.ModelId}' is already loaded ({existing.Backend}, {existing.Tier}).");
        }

        var profile = await GetOrProbeAsync(ct).ConfigureAwait(false);

        // VRAM admission — only enforced on GPU-class backends.
        if (descriptor.Backend is BackendKind.Cuda or BackendKind.Vulkan or BackendKind.Metal or BackendKind.OpenCL)
        {
            var vramCeiling = profile.Gpu?.VramBytes ?? 0;
            var vramFree = vramCeiling - TotalAllocatedVramBytes;
            if (vramFree < descriptor.VramRequiredBytes)
            {
                return new LoadResult(LoadOutcome.InsufficientVram, null,
                    $"Need {descriptor.VramRequiredBytes / (1024 * 1024)} MiB VRAM, " +
                    $"have {Math.Max(0, vramFree) / (1024 * 1024)} MiB free " +
                    $"({TotalAllocatedVramBytes / (1024 * 1024)} MiB of {vramCeiling / (1024 * 1024)} MiB in use).");
            }
        }

        // RAM admission — always enforced.
        var ramFree = profile.TotalPhysicalMemoryBytes - TotalAllocatedRamBytes;
        if (ramFree < descriptor.RamRequiredBytes)
        {
            return new LoadResult(LoadOutcome.InsufficientRam, null,
                $"Need {descriptor.RamRequiredBytes / (1024 * 1024)} MiB RAM, " +
                $"have {Math.Max(0, ramFree) / (1024 * 1024)} MiB free " +
                $"({TotalAllocatedRamBytes / (1024 * 1024)} MiB of {profile.TotalPhysicalMemoryBytes / (1024 * 1024)} MiB in use).");
        }

        // Reserve before invoking the factory so concurrent loads see the new accounting.
        var reserveState = new ModelLoadState(
            descriptor.ModelId, descriptor.Backend, descriptor.RequestedTier,
            descriptor.VramRequiredBytes, descriptor.RamRequiredBytes,
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            if (_loaded.TryGetValue(descriptor.ModelId, out var raceWinner))
            {
                return new LoadResult(LoadOutcome.AlreadyLoaded, raceWinner,
                    $"Model '{descriptor.ModelId}' was loaded by a concurrent request.");
            }
            _loaded[descriptor.ModelId] = reserveState;
        }

        try
        {
            var bridge = await descriptor.BridgeFactory(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"BridgeFactory for '{descriptor.ModelId}' returned null.");

            _registry.Register(descriptor.ModelId, bridge);

            CircleAIDiagnostics.OperationsTotal.Add(1,
                new KeyValuePair<string, object?>("component", "ModelLifecycleManager"),
                new KeyValuePair<string, object?>("operation", "Load"),
                new KeyValuePair<string, object?>("outcome",   CircleAIDiagnostics.Outcomes.Success),
                new KeyValuePair<string, object?>("model_id",  descriptor.ModelId),
                new KeyValuePair<string, object?>("backend",   descriptor.Backend.ToString()));

            return new LoadResult(LoadOutcome.Loaded, reserveState,
                $"Loaded '{descriptor.ModelId}' on {descriptor.Backend} at {descriptor.RequestedTier}.");
        }
        catch (Exception ex)
        {
            // Roll the reservation back.
            _loaded.TryRemove(descriptor.ModelId, out _);
            CircleAIDiagnostics.OperationsTotal.Add(1,
                new KeyValuePair<string, object?>("component", "ModelLifecycleManager"),
                new KeyValuePair<string, object?>("operation", "Load"),
                new KeyValuePair<string, object?>("outcome",   CircleAIDiagnostics.Outcomes.Error),
                new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
            return new LoadResult(LoadOutcome.FactoryFailed, null,
                $"Bridge factory for '{descriptor.ModelId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<UnloadOutcome> UnloadAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (!_loaded.TryRemove(modelId, out _)) return UnloadOutcome.NotLoaded;

        var bridge = _registry.Resolve(modelId);
        if (bridge is IAsyncDisposable iad)
            await iad.DisposeAsync().ConfigureAwait(false);
        else if (bridge is IDisposable id)
            id.Dispose();

        // After disposing the bridge, drop it from the registry so any new
        // request for this model resolves to null. In-flight requests
        // already hold a strong reference to the bridge and complete using
        // it — the dispose contract on bridges promises to tolerate that.
        _registry.Deregister(modelId);

        CircleAIDiagnostics.OperationsTotal.Add(1,
            new KeyValuePair<string, object?>("component", "ModelLifecycleManager"),
            new KeyValuePair<string, object?>("operation", "Unload"),
            new KeyValuePair<string, object?>("outcome",   CircleAIDiagnostics.Outcomes.Success),
            new KeyValuePair<string, object?>("model_id",  modelId));

        return UnloadOutcome.Unloaded;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelLoadState> List() => _loaded.Values.ToList();

    private async Task<HostProfile> GetOrProbeAsync(CancellationToken ct)
    {
        // Re-probe at most once per process — capability surface is stable
        // outside hot-plug scenarios. Hosts wanting hot-plug should call
        // a future ICapabilityRefresher (out of scope for Phase 3 — covered
        // by the brief's "auto-refetch on capability change" callout).
        if (_cachedProfile is not null) return _cachedProfile;
        var p = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _cachedProfile, p, null);
        return _cachedProfile!;
    }
}
