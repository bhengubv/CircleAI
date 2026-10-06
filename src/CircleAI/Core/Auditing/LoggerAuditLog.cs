// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

using Microsoft.Extensions.Logging;

namespace CircleAI.Core.Auditing;

/// <summary>
/// <see cref="ICircleAIAuditLog"/> implementation that writes structured
/// entries to an <see cref="ILogger"/> at <c>Information</c> level.
///
/// <para>Suitable for development and for production deployments whose log
/// pipeline already routes structured messages into a queryable sink
/// (Seq, Loki, OpenSearch, etc.). For SARB-style record-keeping where
/// the audit trail must be append-only and tamper-evident, replace with
/// a DB-backed implementation (Postgres row-level immutability, AWS
/// QLDB, etc.).</para>
///
/// <para>The <see cref="QueryAsync"/> implementation always returns
/// empty — query support is a sink-specific feature and reading back
/// from <see cref="ILogger"/> isn't possible at the SDK layer.</para>
/// </summary>
public sealed class LoggerAuditLog : ICircleAIAuditLog
{
    private readonly ILogger<LoggerAuditLog> _logger;

    /// <summary>Construct with a logger.</summary>
    public LoggerAuditLog(ILogger<LoggerAuditLog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task RecordAsync(CircleAIAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Structured logging — every field is a queryable property in
        // Seq / Loki / OpenSearch via the named-property template.
        _logger.LogInformation(
            "CircleAI audit {Component}.{Operation} {Outcome} " +
            "tenant={TenantId} uhid={UhidIdentityId} corr={CorrelationId} " +
            "duration_ms={DurationMs} error={ErrorType}({ErrorCode}) " +
            "payload_sha256={PayloadSha256Hex} at={At:O}",
            entry.Component, entry.Operation, entry.Outcome,
            entry.TenantId ?? "-", entry.UhidIdentityId ?? "-", entry.CorrelationId ?? "-",
            entry.DurationMs, entry.ErrorType ?? "-", entry.ErrorCode ?? "-",
            entry.PayloadSha256Hex ?? "-", entry.At);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<CircleAIAuditEntry> QueryAsync(
        CircleAIAuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
