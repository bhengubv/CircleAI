// IModelLifecycleManager.cs

namespace CircleAI.Inference.Server.Lifecycle;

/// <summary>
/// Admits or rejects model loads based on the host's current capacity, and
/// keeps an authoritative ledger of which models are loaded plus what
/// they consume. The lifecycle manager wraps and is the SOLE authorised
/// writer to <see cref="Models.IInferenceServerModelRegistry"/> for the
/// duration of the process.
/// </summary>
public interface IModelLifecycleManager
{
    /// <summary>
    /// Try to load <paramref name="descriptor"/>. Runs the admission gate
    /// (already-loaded? VRAM headroom? RAM headroom?) BEFORE invoking
    /// <see cref="ModelLoadDescriptor.BridgeFactory"/>.
    /// </summary>
    Task<LoadResult> LoadAsync(ModelLoadDescriptor descriptor, CancellationToken ct = default);

    /// <summary>
    /// Unload the model with the given id. Disposes the bridge if it
    /// implements <see cref="IDisposable"/> / <see cref="IAsyncDisposable"/>.
    /// </summary>
    Task<UnloadOutcome> UnloadAsync(string modelId, CancellationToken ct = default);

    /// <summary>Snapshot of every model currently held by the manager.</summary>
    IReadOnlyList<ModelLoadState> List();

    /// <summary>
    /// Total VRAM currently allocated across all loaded models.
    /// Used by the diagnostics endpoint to show "X of Y GiB used".
    /// </summary>
    long TotalAllocatedVramBytes { get; }

    /// <summary>Total system RAM currently allocated across all loaded models.</summary>
    long TotalAllocatedRamBytes { get; }
}
