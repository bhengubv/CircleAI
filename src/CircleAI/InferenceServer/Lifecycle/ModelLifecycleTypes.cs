// ModelLifecycleTypes.cs
//
// DTOs and contracts for the model-lifecycle layer. The lifecycle manager
// is the policy gate around the in-memory IInferenceServerModelRegistry —
// it decides whether a load is admitted (VRAM/RAM headroom + duplicate
// check) and tracks the on-host footprint so the admin endpoints have a
// truthful picture.

using CircleAI.Hosting.InferenceBridge;
using CircleAI.Runtime.Backends;

namespace CircleAI.Inference.Server.Lifecycle;

/// <summary>
/// What the caller wants to load. The factory function returns the bridge
/// + its claimed VRAM footprint; the lifecycle manager handles the gate.
/// </summary>
/// <param name="ModelId">Logical model id the bridge will respond to.</param>
/// <param name="Backend">Backend the load will run on (CPU / CUDA / Metal / …).</param>
/// <param name="RequestedTier">Capability tier the model targets.</param>
/// <param name="VramRequiredBytes">
/// Best estimate of dedicated VRAM the model needs once loaded. <c>0</c>
/// for CPU backends.
/// </param>
/// <param name="RamRequiredBytes">
/// Best estimate of system RAM the model needs once loaded.
/// </param>
/// <param name="BridgeFactory">
/// Factory that produces the bridge. Called only after the admission gate
/// passes; the manager's count is incremented before the factory runs to
/// prevent overcommit races.
/// </param>
public sealed record ModelLoadDescriptor(
    string ModelId,
    BackendKind Backend,
    CapabilityTier RequestedTier,
    long VramRequiredBytes,
    long RamRequiredBytes,
    Func<CancellationToken, Task<IInferenceBridge>> BridgeFactory);

/// <summary>Runtime view of one loaded model.</summary>
/// <param name="ModelId">Logical model id.</param>
/// <param name="Backend">Backend the bridge is running on.</param>
/// <param name="Tier">Capability tier this load was accounted at.</param>
/// <param name="VramBytes">VRAM the bookkeeper attributes to this load.</param>
/// <param name="RamBytes">RAM the bookkeeper attributes to this load.</param>
/// <param name="LoadedAt">UTC time the load was admitted.</param>
public sealed record ModelLoadState(
    string ModelId,
    BackendKind Backend,
    CapabilityTier Tier,
    long VramBytes,
    long RamBytes,
    DateTimeOffset LoadedAt);

/// <summary>Outcome enum for a load attempt.</summary>
public enum LoadOutcome
{
    /// <summary>Bridge factory ran, registry was updated.</summary>
    Loaded = 0,
    /// <summary>The model was already loaded — no-op success.</summary>
    AlreadyLoaded = 1,
    /// <summary>Insufficient VRAM headroom for the requested footprint.</summary>
    InsufficientVram = 2,
    /// <summary>Insufficient RAM headroom for the requested footprint.</summary>
    InsufficientRam = 3,
    /// <summary>Bridge factory threw — registry untouched.</summary>
    FactoryFailed = 4,
}

/// <summary>Result of a load attempt.</summary>
public sealed record LoadResult(
    LoadOutcome Outcome,
    ModelLoadState? State,
    string Rationale);

/// <summary>Outcome enum for an unload attempt.</summary>
public enum UnloadOutcome
{
    /// <summary>Model was loaded; bridge was disposed and removed.</summary>
    Unloaded = 0,
    /// <summary>Model was not loaded; nothing to do.</summary>
    NotLoaded = 1,
}
