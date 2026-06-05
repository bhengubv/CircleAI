// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.MultiTenant;

/// <summary>
/// Ambient tenant context. Implementations resolve the current tenant from
/// whatever signal the host uses — claims principal, HTTP header, route
/// segment, gRPC metadata, background-job context. Multi-tenant wrappers
/// around CircleAI's stateful stores (<c>JsonPersonaProvider</c>,
/// <c>FileSystemKnowledgeStore</c>, <c>MarkdownEpisodicMemoryStore</c>) read
/// <see cref="CurrentTenantId"/> to scope the on-disk root directory.
///
/// <para>Default registration in <c>AddCircleAI</c> is
/// <see cref="NullTenantContext"/> — a stub that throws on access. This is
/// intentional: there is no safe default for "which tenant is this request
/// for", and silently failing open is the kind of bug that causes
/// cross-tenant data leaks. Consumers MUST register their own implementation
/// before any multi-tenant code path executes.</para>
///
/// <para>Mirrors <c>Bhengu.Finance.Payments.Core.MultiTenant.IBhenguTenantContext</c>.</para>
/// </summary>
public interface ICircleAITenantContext
{
    /// <summary>
    /// The tenant identifier for the current request / unit of work. Throws
    /// if no tenant is in scope — multi-tenant code paths must NEVER silently
    /// fall back to a default.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant is in scope.</exception>
    string CurrentTenantId { get; }

    /// <summary>True when a tenant is currently in scope. Use to gate optional behaviour.</summary>
    bool HasTenant { get; }
}
