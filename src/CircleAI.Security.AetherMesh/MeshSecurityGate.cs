// ──────────────────────────────────────────────────────────────────────────
// MeshSecurityGate
//
// Read-only fast-path query surface over MeshDirectiveStore. The gate is
// the type CircleAI features inject when they want to consult mesh-issued
// directives before serving a request — e.g. chat refusing a blocked user.
//
// Separating the gate from the store lets DI bind the query view as a
// dependency without exposing the directive-write surface (the store) to
// every consumer.
// ──────────────────────────────────────────────────────────────────────────

namespace CircleAI.Security.AetherMesh;

/// <summary>
/// Query surface for asking "is this user/node currently blocked by the mesh?"
/// Backed by a <see cref="MeshDirectiveStore"/>.
/// </summary>
public sealed class MeshSecurityGate
{
    private readonly MeshDirectiveStore _store;

    public MeshSecurityGate(MeshDirectiveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Decision returned from <see cref="Decide(string)"/>.
    /// </summary>
    public readonly record struct GateDecision(bool IsBlocked, string Reason)
    {
        /// <summary>Convenience: allow with no reason text.</summary>
        public static GateDecision Allowed { get; } = new(false, string.Empty);
    }

    /// <summary>
    /// Returns a single-shot decision for the given user/node id. The reason
    /// text comes from the most recent active block directive.
    /// </summary>
    public GateDecision Decide(string userOrNodeId)
    {
        if (string.IsNullOrWhiteSpace(userOrNodeId)) return GateDecision.Allowed;
        return _store.IsBlocked(userOrNodeId, out var reason)
            ? new GateDecision(true, reason)
            : GateDecision.Allowed;
    }

    /// <summary>
    /// Convenience wrapper that throws when a request from a blocked id
    /// would proceed. Use in DI-injected service code that wants a one-line
    /// guard at the top of a method.
    /// </summary>
    public void Enforce(string userOrNodeId)
    {
        var decision = Decide(userOrNodeId);
        if (decision.IsBlocked)
        {
            throw new MeshSecurityBlockedException(userOrNodeId, decision.Reason);
        }
    }
}

/// <summary>
/// Thrown by <see cref="MeshSecurityGate.Enforce(string)"/> when the mesh
/// has issued a block directive against the requesting id.
/// </summary>
public sealed class MeshSecurityBlockedException : Exception
{
    public string BlockedId { get; }

    public MeshSecurityBlockedException(string blockedId, string reason)
        : base($"Mesh has blocked '{blockedId}': {reason}")
    {
        BlockedId = blockedId;
    }
}
