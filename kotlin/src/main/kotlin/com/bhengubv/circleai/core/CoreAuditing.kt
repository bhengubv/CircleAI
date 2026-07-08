// CoreAuditing.kt
//
// Kotlin port of the CircleAI.Core auditing + multi-tenant surface:
//   • CircleAI.Core.Auditing.ICircleAIAuditLog (+ CircleAIAuditEntry / CircleAIAuditQuery)
//   • CircleAI.Core.Auditing.NoopAuditLog
//   • CircleAI.Core.Auditing.LoggerAuditLog
//   • CircleAI.Core.Auditing.CircleAIAuditing (ambient default)
//   • CircleAI.Core.MultiTenant.ICircleAITenantContext
//   • CircleAI.Core.MultiTenant.NullTenantContext / SingleTenantContext
//
// The C# audit log exposes an IAsyncEnumerable query surface; here it maps to a
// kotlinx.coroutines Flow. RecordAsync is a suspend function. The Logger
// implementation writes to an injectable sink (default: stderr) so the module
// stays free of a logging-framework dependency, matching the C# ILogger seam.

package com.bhengubv.circleai.core

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import java.time.OffsetDateTime

// ---------------------------------------------------------------------------
// CircleAIAuditEntry — CircleAI.Core.Auditing.CircleAIAuditEntry
// ---------------------------------------------------------------------------

/**
 * An immutable audit entry emitted by the CircleAI SDK. Every state-changing
 * operation a CircleAIComponentBase-derived component performs is auto-recorded
 * (component + operation + outcome + duration + tenant + UHID + error info,
 * with hash-only references to any payload).
 */
data class CircleAIAuditEntry(
    /** UTC timestamp of the action. */
    val at: OffsetDateTime,
    /** Canonical CircleAI component name (e.g. "DefaultSecurityWatchdog"). */
    val component: String,
    /** Logical operation name (e.g. "OnAnomalyDetectedAsync", "GetAsync"). */
    val operation: String,
    /** Outcome — one of CircleAIDiagnostics.Outcomes ("success" / "error" / …). */
    val outcome: String,
    /** Tenant id, when running multi-tenant. Null for single-tenant deployments. */
    val tenantId: String? = null,
    /** User id (UHID) when the operation was scoped to a specific user. */
    val uhidIdentityId: String? = null,
    /** Optional correlation id (e.g. session id, request id). */
    val correlationId: String? = null,
    /** Operation duration in milliseconds. */
    val durationMs: Double = 0.0,
    /** When [outcome] is not "success", the exception type that was thrown. */
    val errorType: String? = null,
    /** Implementation-supplied error code, when applicable. */
    val errorCode: String? = null,
    /** Hash of any sensitive payload involved (never the raw payload itself). */
    val payloadSha256Hex: String? = null,
)

// ---------------------------------------------------------------------------
// CircleAIAuditQuery — CircleAI.Core.Auditing.CircleAIAuditQuery
// ---------------------------------------------------------------------------

/** Query filter for [ICircleAIAuditLog.queryAsync]. */
data class CircleAIAuditQuery(
    /** Inclusive lower bound on [CircleAIAuditEntry.at]. */
    val fromUtc: OffsetDateTime? = null,
    /** Inclusive upper bound on [CircleAIAuditEntry.at]. */
    val toUtc: OffsetDateTime? = null,
    /** Restrict to a single component. */
    val component: String? = null,
    /** Restrict to a single tenant. */
    val tenantId: String? = null,
    /** Restrict to a single UHID identity. */
    val uhidIdentityId: String? = null,
    /** Restrict to a single outcome. */
    val outcome: String? = null,
    /** Maximum entries to return. */
    val maxItems: Int = 1000,
)

// ---------------------------------------------------------------------------
// ICircleAIAuditLog — CircleAI.Core.Auditing.ICircleAIAuditLog
// ---------------------------------------------------------------------------

/**
 * Tamper-aware audit surface for the CircleAI SDK.
 *
 * Default registration is [NoopAuditLog] — entries are silently dropped until a
 * consumer wires [LoggerAuditLog] or their own append-only sink.
 */
interface ICircleAIAuditLog {
    /**
     * Record an audit entry. MUST NOT throw — the caller may be mid-operation
     * and audit-log failure must never bring it down. Implementations should
     * catch and log internally, failing open.
     */
    suspend fun recordAsync(entry: CircleAIAuditEntry)

    /**
     * Query historical entries — for compliance reporting, forensic
     * investigation, debugging. Implementations are expected to support
     * tenant-scoped queries when running multi-tenant.
     */
    fun queryAsync(query: CircleAIAuditQuery): Flow<CircleAIAuditEntry>
}

// ---------------------------------------------------------------------------
// NoopAuditLog — CircleAI.Core.Auditing.NoopAuditLog
// ---------------------------------------------------------------------------

/**
 * Default [ICircleAIAuditLog] — silently discards every entry and returns an
 * empty query result. This is the registration consumers get if they never
 * wire an audit sink.
 */
class NoopAuditLog private constructor() : ICircleAIAuditLog {
    override suspend fun recordAsync(entry: CircleAIAuditEntry) {
        // Fail open — discard.
    }

    override fun queryAsync(query: CircleAIAuditQuery): Flow<CircleAIAuditEntry> = emptyFlow()

    companion object {
        /** Shared singleton instance. */
        val Instance: NoopAuditLog = NoopAuditLog()
    }
}

// ---------------------------------------------------------------------------
// LoggerAuditLog — CircleAI.Core.Auditing.LoggerAuditLog
// ---------------------------------------------------------------------------

/**
 * [ICircleAIAuditLog] implementation that writes structured entries to an
 * injectable [sink] (default: standard error).
 *
 * [queryAsync] always returns empty — query support is a sink-specific feature
 * and reading back from a log line isn't possible at the SDK layer.
 */
class LoggerAuditLog(
    private val sink: (String) -> Unit = { line -> System.err.println(line) },
) : ICircleAIAuditLog {

    override suspend fun recordAsync(entry: CircleAIAuditEntry) {
        // Structured line — every field is present so a downstream log pipeline
        // (Seq / Loki / OpenSearch) can index it.
        sink(
            "CircleAI audit ${entry.component}.${entry.operation} ${entry.outcome} " +
                "tenant=${entry.tenantId ?: "-"} uhid=${entry.uhidIdentityId ?: "-"} " +
                "corr=${entry.correlationId ?: "-"} duration_ms=${entry.durationMs} " +
                "error=${entry.errorType ?: "-"}(${entry.errorCode ?: "-"}) " +
                "payload_sha256=${entry.payloadSha256Hex ?: "-"} at=${entry.at}",
        )
    }

    override fun queryAsync(query: CircleAIAuditQuery): Flow<CircleAIAuditEntry> = emptyFlow()
}

// ---------------------------------------------------------------------------
// CircleAIAuditing — CircleAI.Core.Auditing.CircleAIAuditing (ambient default)
// ---------------------------------------------------------------------------

/**
 * Process-wide ambient access point for the audit sink. Components emit through
 * [default] without depending on a DI container.
 *
 * Initial value is [NoopAuditLog.Instance]. Hosts wire the real sink by calling
 * [setDefault] during startup.
 */
object CircleAIAuditing {
    @Volatile
    private var _default: ICircleAIAuditLog = NoopAuditLog.Instance

    /** The current ambient audit sink. Defaults to [NoopAuditLog]. */
    val default: ICircleAIAuditLog
        get() = _default

    /**
     * Replace the ambient audit sink. Idempotent — calling repeatedly with the
     * same instance is safe.
     */
    fun setDefault(audit: ICircleAIAuditLog) {
        _default = audit
    }

    /** Restore the default to [NoopAuditLog]. Test-helper. */
    fun resetToNoop() {
        _default = NoopAuditLog.Instance
    }
}

// ---------------------------------------------------------------------------
// ICircleAITenantContext — CircleAI.Core.MultiTenant.ICircleAITenantContext
// ---------------------------------------------------------------------------

/**
 * Ambient tenant context. Implementations resolve the current tenant from
 * whatever signal the host uses.
 *
 * Default registration is [NullTenantContext] — a stub that throws on access.
 * There is no safe default for "which tenant is this request for", and silently
 * failing open is the kind of bug that causes cross-tenant data leaks.
 */
interface ICircleAITenantContext {
    /**
     * The tenant identifier for the current request / unit of work. Throws if
     * no tenant is in scope — multi-tenant code paths must NEVER silently fall
     * back to a default.
     *
     * @throws IllegalStateException No tenant is in scope.
     */
    val currentTenantId: String

    /** True when a tenant is currently in scope. */
    val hasTenant: Boolean
}

// ---------------------------------------------------------------------------
// NullTenantContext / SingleTenantContext — CircleAI.Core.MultiTenant
// ---------------------------------------------------------------------------

/**
 * Default [ICircleAITenantContext] — throws on any read. Makes "I forgot to
 * wire tenant resolution" a load-time error rather than a silent data-leak at
 * runtime.
 */
class NullTenantContext private constructor() : ICircleAITenantContext {
    override val currentTenantId: String
        get() = throw IllegalStateException(
            "No CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext " +
                "(e.g. SingleTenantContext, or your own ClaimsPrincipal-backed resolver) before " +
                "using multi-tenant-aware components.",
        )

    override val hasTenant: Boolean = false

    companion object {
        /** Shared singleton instance. */
        val Instance: NullTenantContext = NullTenantContext()
    }
}

/**
 * Explicit single-tenant context. Returns a fixed tenant id for every read.
 * Use when the deployment genuinely has one tenant.
 */
class SingleTenantContext(tenantId: String) : ICircleAITenantContext {
    override val currentTenantId: String

    init {
        require(tenantId.isNotBlank()) { "tenantId must be non-blank." }
        currentTenantId = tenantId
    }

    override val hasTenant: Boolean = true
}
