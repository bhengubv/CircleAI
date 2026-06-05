// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.MultiTenant;

/// <summary>
/// Default <see cref="ICircleAITenantContext"/> — throws on any read.
///
/// <para>This is what <c>AddCircleAI</c> registers when the host has not
/// wired a real tenant resolver. The throw is intentional: it makes
/// "I forgot to wire tenant resolution" a load-time error rather than
/// a silent data-leak at runtime.</para>
///
/// <para>If you genuinely want a single-tenant deployment, register
/// <see cref="SingleTenantContext"/> explicitly instead — that says
/// out loud "I have one tenant, named X" rather than "I haven't thought
/// about tenancy yet".</para>
/// </summary>
public sealed class NullTenantContext : ICircleAITenantContext
{
    /// <summary>Shared singleton instance.</summary>
    public static NullTenantContext Instance { get; } = new();

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public string CurrentTenantId => throw new InvalidOperationException(
        "No CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext " +
        "(e.g. SingleTenantContext, or your own ClaimsPrincipal-backed resolver) before " +
        "using multi-tenant-aware components.");

    /// <inheritdoc/>
    public bool HasTenant => false;
}

/// <summary>
/// Explicit single-tenant context. Returns a fixed tenant id for every read.
/// Use this when the deployment genuinely has one tenant (a personal
/// CircleAI install, a single-org appliance) and the throwing default would
/// just be ceremony.
/// </summary>
public sealed class SingleTenantContext : ICircleAITenantContext
{
    /// <summary>Construct with the fixed tenant id.</summary>
    public SingleTenantContext(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        CurrentTenantId = tenantId;
    }

    /// <inheritdoc/>
    public string CurrentTenantId { get; }

    /// <inheritdoc/>
    public bool HasTenant => true;
}
