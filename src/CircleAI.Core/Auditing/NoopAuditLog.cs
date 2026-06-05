// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.Auditing;

/// <summary>
/// Default <see cref="ICircleAIAuditLog"/> — silently discards every entry
/// and returns an empty query result.
///
/// <para>This is the registration consumers get if they call <c>AddCircleAI</c>
/// without explicitly wiring an audit sink. It exists so the
/// <see cref="CircleAI.Core.Components.CircleAIComponentBase"/> wrappers can
/// emit unconditionally without forcing every consumer to set up an audit
/// pipeline before they can boot.</para>
///
/// <para>Production deployments should replace this with
/// <see cref="LoggerAuditLog"/> or a custom append-only sink.</para>
/// </summary>
public sealed class NoopAuditLog : ICircleAIAuditLog
{
    /// <summary>Shared singleton instance.</summary>
    public static NoopAuditLog Instance { get; } = new();

    /// <inheritdoc/>
    public Task RecordAsync(CircleAIAuditEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public async IAsyncEnumerable<CircleAIAuditEntry> QueryAsync(
        CircleAIAuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
