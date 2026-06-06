// ──────────────────────────────────────────────────────────────────────────
// MeshDirectiveStore
//
// In-memory record of every active SecurityDirective the mesh has issued
// against a node. Implements CircleAI.Aether.ISecurityDirectiveConsumer
// so it can be plugged in as the sink for directive notifications.
//
// Two query surfaces are exposed:
//   • IsBlocked(nodeId, out reason)        — fast hot-path check
//   • GetActiveDirectives(nodeId)          — full audit detail
//
// Expiry is handled lazily on read — no background timer to leak. Block
// state observes Avoid + Quarantine; Release lifts both.
// ──────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Aether;

namespace CircleAI.Security.AetherMesh;

/// <summary>
/// Thread-safe in-memory registry of security directives received from the
/// mesh. Acts as both the directive sink and the query surface that other
/// CircleAI components consult before serving a request.
/// </summary>
public sealed class MeshDirectiveStore : ISecurityDirectiveConsumer
{
    private readonly ConcurrentDictionary<string, List<SecurityDirective>> _byNode =
        new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Constructs a store using <see cref="DateTimeOffset.UtcNow"/> as the clock.</summary>
    public MeshDirectiveStore() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Constructs a store with an explicit clock for testing.</summary>
    public MeshDirectiveStore(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc/>
    public void OnDirective(SecurityDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (!directive.HasTarget) return;
        var nodeId = directive.TargetNodeId!;

        if (directive.Kind == SecurityDirectiveKind.ReleaseNode)
        {
            // Release lifts every Avoid/Quarantine for the node.
            _byNode.TryRemove(nodeId, out _);
            return;
        }

        _byNode.AddOrUpdate(
            nodeId,
            _ => new List<SecurityDirective> { directive },
            (_, list) =>
            {
                lock (list) list.Add(directive);
                return list;
            });
    }

    /// <summary>
    /// Returns true when an unexpired Avoid or Quarantine directive is active
    /// for the node. <paramref name="reason"/> carries the most recent block's
    /// reason text.
    /// </summary>
    public bool IsBlocked(string nodeId, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(nodeId)) return false;
        if (!_byNode.TryGetValue(nodeId, out var list)) return false;

        var now = _clock();
        SecurityDirective? latestBlock = null;

        lock (list)
        {
            // Drop expired entries while we walk the list.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var d = list[i];
                if (IsExpired(d, now)) { list.RemoveAt(i); continue; }
                if (IsBlockKind(d.Kind) &&
                    (latestBlock is null || d.IssuedAt > latestBlock.IssuedAt))
                {
                    latestBlock = d;
                }
            }
            if (list.Count == 0) _byNode.TryRemove(nodeId, out _);
        }

        if (latestBlock is null) return false;
        reason = latestBlock.Reason;
        return true;
    }

    /// <summary>Lists every unexpired directive for the node — useful for audit/diagnostics.</summary>
    public IReadOnlyList<SecurityDirective> GetActiveDirectives(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return Array.Empty<SecurityDirective>();
        if (!_byNode.TryGetValue(nodeId, out var list)) return Array.Empty<SecurityDirective>();

        var now = _clock();
        lock (list)
        {
            return list.Where(d => !IsExpired(d, now)).ToList();
        }
    }

    /// <summary>Number of nodes with at least one tracked directive (post-expiry sweep on read).</summary>
    public int TrackedNodeCount => _byNode.Count;

    private static bool IsBlockKind(SecurityDirectiveKind k) =>
        k is SecurityDirectiveKind.AvoidNode or SecurityDirectiveKind.QuarantineNode;

    private static bool IsExpired(SecurityDirective d, DateTimeOffset now) =>
        d.Duration is { } duration && (d.IssuedAt + duration) <= now;
}
