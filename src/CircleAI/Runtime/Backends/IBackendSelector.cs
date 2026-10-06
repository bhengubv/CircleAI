// IBackendSelector.cs
//
// Maps (HostProfile, requested tier) -> (backend kind, actual tier, rationale).
// The selector NEVER refuses to select — it always returns a runnable
// combination, downgrading the tier if requested compute is not present.

using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.Backends;

/// <summary>
/// Result of an <see cref="IBackendSelector.Select"/> call.
/// </summary>
/// <param name="Backend">Chosen MNN execution backend.</param>
/// <param name="ActualTier">
/// Tier the host can actually run. Equal to or lower than the
/// requested tier — the selector downgrades when compute is short.
/// </param>
/// <param name="Rationale">
/// Human-readable explanation of why this combination was chosen.
/// Suitable for logging and surfacing in operator dashboards.
/// </param>
public sealed record BackendSelection(
    BackendKind Backend,
    CapabilityTier ActualTier,
    string Rationale);

/// <summary>
/// Picks the MNN backend and model tier for a given host. Implementations
/// must NEVER throw and must NEVER return <c>null</c> — every host can run
/// the CPU backend at Tier 0 as a last resort.
/// </summary>
public interface IBackendSelector
{
    /// <summary>
    /// Pick the best <see cref="BackendKind"/> + <see cref="CapabilityTier"/> combo
    /// for the given host. <paramref name="requestedTier"/> is the upper
    /// bound — the returned tier may be lower if the host cannot run it.
    /// </summary>
    BackendSelection Select(HostProfile profile, CapabilityTier requestedTier);
}
