// ──────────────────────────────────────────────────────────────────────────
// MeshGatedCompanionSession
//
// Decorator over ICompanionSession that consults MeshSecurityGate before
// EVERY message-producing call (SendAsync, StreamAsync, AgentAsync). When
// the gate says the session's IdentityId is blocked by an active mesh
// directive, the decorator throws MeshSecurityBlockedException instead of
// reaching the underlying generator.
//
// This is the "chat path consults mesh" wire-up — Item 2 of the audit
// follow-up. The decorator never modifies or impersonates the inner
// session; it strictly adds the gate check.
// ──────────────────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using CircleAI.Companion;

namespace CircleAI.Security.AetherNet;

/// <summary>
/// Wraps an inner <see cref="ICompanionSession"/> and enforces the mesh's
/// "block this user" directives via <see cref="MeshSecurityGate"/> on every
/// message-producing call.
/// </summary>
public sealed class MeshGatedCompanionSession : ICompanionSession
{
    private readonly ICompanionSession _inner;
    private readonly MeshSecurityGate _gate;

    public MeshGatedCompanionSession(ICompanionSession inner, MeshSecurityGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        _inner = inner;
        _gate = gate;
    }

    // ── Pass-through identity / properties ────────────────────────────────

    public string SessionId => _inner.SessionId;
    public string IdentityId => _inner.IdentityId;
    public InterfaceKind Interface => _inner.Interface;
    public IReadOnlyList<CompanionTurn> History => _inner.History;

    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady
    {
        add { _inner.ProactiveMessageReady += value; }
        remove { _inner.ProactiveMessageReady -= value; }
    }

    // ── Guarded entry points ──────────────────────────────────────────────

    public Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        _gate.Enforce(IdentityId);
        return _inner.SendAsync(message, ct);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _gate.Enforce(IdentityId);
        await foreach (var chunk in _inner.StreamAsync(message, ct).ConfigureAwait(false))
            yield return chunk;
    }

    public Task<string> AgentAsync(string instruction, CancellationToken ct = default)
    {
        _gate.Enforce(IdentityId);
        return _inner.AgentAsync(instruction, ct);
    }

    // ── Unguarded pass-through ────────────────────────────────────────────
    // Context / history / feedback are diagnostic / metadata calls. Gating
    // them would prevent a blocked user from even seeing their own state,
    // which goes beyond the "stop the chat" intent and into "punish".

    public CompanionContext GetContext() => _inner.GetContext();

    public Task RefreshContextAsync(CancellationToken ct = default)
        => _inner.RefreshContextAsync(ct);

    public Task SignalFeedbackAsync(bool positive, string? note = null, CancellationToken ct = default)
        => _inner.SignalFeedbackAsync(positive, note, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
