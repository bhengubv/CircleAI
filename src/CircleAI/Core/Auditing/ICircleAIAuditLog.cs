// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.Auditing;

/// <summary>
/// Tamper-aware audit surface for the CircleAI SDK. Every state-changing
/// operation a <see cref="CircleAI.Core.Components.CircleAIComponentBase"/>-derived
/// component performs is auto-recorded here (component + operation + outcome
/// + duration + tenant + UHID + error info, with hash-only references to any
/// payload).
///
/// <para>Default registration is <see cref="NoopAuditLog"/> — entries are
/// silently dropped until a consumer wires <see cref="LoggerAuditLog"/> or
/// their own append-only sink. The interface is here so consumers can record
/// bespoke business-event audit entries alongside SDK ones (e.g. "user X
/// rotated UHID key ring at T", "federation round Y committed N deltas").</para>
///
/// <para>This mirrors <c>Bhengu.Finance.Payments.Core.Auditing.IBhenguPaymentAuditLog</c>.
/// Compliance use cases — POPIA Article 34, PCI-DSS requirement 10
/// equivalent, SARB record-keeping for any AI action that touches financial
/// state via a Personal.Finance adapter.</para>
/// </summary>
public interface ICircleAIAuditLog
{
    /// <summary>
    /// Record an audit entry. MUST NOT throw — the caller may be mid-operation
    /// and audit-log failure must never bring it down. Implementations should
    /// catch and log internally, failing open.
    /// </summary>
    Task RecordAsync(CircleAIAuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Query historical entries — for compliance reporting, forensic
    /// investigation, debugging. Implementations are expected to support
    /// tenant-scoped queries when running multi-tenant.
    /// </summary>
    IAsyncEnumerable<CircleAIAuditEntry> QueryAsync(
        CircleAIAuditQuery query,
        CancellationToken ct = default);
}

/// <summary>An immutable audit entry emitted by the CircleAI SDK.</summary>
public sealed record CircleAIAuditEntry
{
    /// <summary>UTC timestamp of the action.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Canonical CircleAI component name (e.g. "DefaultSecurityWatchdog",
    /// "JsonPersonaProvider", "InMemoryFederationAggregator").
    /// </summary>
    public required string Component { get; init; }

    /// <summary>
    /// Logical operation name (e.g. "OnAnomalyDetectedAsync", "GetAsync",
    /// "TryCommitAsync").
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Outcome — one of <see cref="Diagnostics.CircleAIDiagnostics.Outcomes"/>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Tenant id, when running multi-tenant. Null for single-tenant deployments.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// User id (UHID) when the operation was scoped to a specific user.
    /// Null for tenant-wide or device-wide operations.
    /// </summary>
    public string? UhidIdentityId { get; init; }

    /// <summary>
    /// Optional correlation id (e.g. session id, request id) for joining
    /// audit entries with traces.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Operation duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>
    /// When <see cref="Outcome"/> is not "success", the CLR exception type
    /// that was thrown (e.g. "OperationCanceledException", "InvalidOperationException").
    /// </summary>
    public string? ErrorType { get; init; }

    /// <summary>Implementation-supplied error code, when applicable.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Hash of any sensitive payload involved in the operation (e.g. SHA-256
    /// of an <see cref="CircleAI.Security.SecurityCheckpoint.Payload"/> being
    /// rolled back). Never carries the raw payload itself — this exists so
    /// auditors can correlate without leaking content. Null when no payload
    /// was involved.
    /// </summary>
    public string? PayloadSha256Hex { get; init; }
}

/// <summary>Query filter for <see cref="ICircleAIAuditLog.QueryAsync"/>.</summary>
public sealed record CircleAIAuditQuery
{
    /// <summary>Inclusive lower bound on <see cref="CircleAIAuditEntry.At"/>.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Inclusive upper bound on <see cref="CircleAIAuditEntry.At"/>.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Restrict to a single component.</summary>
    public string? Component { get; init; }

    /// <summary>Restrict to a single tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>Restrict to a single UHID identity.</summary>
    public string? UhidIdentityId { get; init; }

    /// <summary>Restrict to a single outcome.</summary>
    public string? Outcome { get; init; }

    /// <summary>Maximum entries to return.</summary>
    public int MaxItems { get; init; } = 1000;
}
