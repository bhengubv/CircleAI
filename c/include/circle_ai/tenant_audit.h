#ifndef CIRCLE_AI_TENANT_AUDIT_H
#define CIRCLE_AI_TENANT_AUDIT_H

/*
 * tenant_audit.h — Multi-tenant context + audit-log contracts (C11 port).
 *
 * Ports:
 *   - CircleAI.Core.MultiTenant.ICircleAITenantContext + NullTenantContext +
 *     SingleTenantContext
 *   - CircleAI.Core.Auditing.ICircleAIAuditLog + CircleAIAuditEntry +
 *     CircleAIAuditQuery + NoopAuditLog (+ an in-memory queryable log standing
 *     in for LoggerAuditLog, whose C# QueryAsync always returns empty).
 *
 * The tenant context "throws on read when no tenant is in scope" — in C that is
 * modelled as ca_tenant_context_current_id returning NULL for the Null context
 * (and a non-NULL id for a Single context). ca_tenant_context_has_tenant mirrors
 * HasTenant.
 *
 * Audit logs MUST NOT fail the caller: ca_audit_log_record always returns and
 * never aborts. In-memory only. Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * ICircleAITenantContext
 * =========================================================================== */

typedef struct ca_tenant_context ca_tenant_context_t;

/* NullTenantContext — HasTenant == false, CurrentTenantId "throws": here that
 * means ca_tenant_context_current_id returns NULL. Singleton-style: create/free. */
ca_tenant_context_t *ca_null_tenant_context_create(void);

/* SingleTenantContext — a fixed tenant id for every read. Returns NULL when
 * tenant_id is NULL or blank (mirrors ArgumentException.ThrowIfNullOrWhiteSpace). */
ca_tenant_context_t *ca_single_tenant_context_create(const char *tenant_id);

void ca_tenant_context_destroy(ca_tenant_context_t *ctx);

/* The tenant id for the current unit of work, or NULL when none is in scope
 * (the Null context always returns NULL). The returned string is owned by the
 * context — do NOT free it. */
const char *ca_tenant_context_current_id(const ca_tenant_context_t *ctx);

/* True when a tenant is currently in scope. */
bool ca_tenant_context_has_tenant(const ca_tenant_context_t *ctx);

/* ===========================================================================
 * CircleAIAuditEntry
 * =========================================================================== */

/*
 * An immutable audit entry. All string fields are borrowed on input to
 * ca_audit_log_record (the log deep-copies what it retains) and owned on output
 * from ca_audit_log_query (free the returned array with ca_audit_entry_free_array).
 * Optional fields may be NULL.
 */
typedef struct {
    int64_t     at_unix_ms;         /* required — UTC timestamp (ms) */
    const char *component;          /* required */
    const char *operation;          /* required */
    const char *outcome;            /* required */
    const char *tenant_id;          /* optional */
    const char *uhid_identity_id;   /* optional */
    const char *correlation_id;     /* optional */
    double      duration_ms;
    const char *error_type;         /* optional */
    const char *error_code;         /* optional */
    const char *payload_sha256_hex; /* optional */
} ca_audit_entry_t;

/* Free a deep-copied entry array returned by ca_audit_log_query. */
void ca_audit_entry_free_array(ca_audit_entry_t *entries, size_t count);

/* ===========================================================================
 * CircleAIAuditQuery
 * ===========================================================================
 *
 * Filter for ca_audit_log_query. A bound/filter is "unset" when its *_set flag
 * is false (for the timestamp bounds) or the pointer is NULL (for the string
 * filters). max_items caps the result (default 1000 in C#; pass 1000 to match).
 */
typedef struct {
    bool        from_set;
    int64_t     from_unix_ms;       /* inclusive lower bound on at */
    bool        to_set;
    int64_t     to_unix_ms;         /* inclusive upper bound on at */
    const char *component;          /* optional exact match */
    const char *tenant_id;          /* optional exact match */
    const char *uhid_identity_id;   /* optional exact match */
    const char *outcome;            /* optional exact match */
    int         max_items;          /* cap; <= 0 treated as 1000 */
} ca_audit_query_t;

/* ===========================================================================
 * ICircleAIAuditLog
 * =========================================================================== */

typedef struct ca_audit_log ca_audit_log_t;

/* NoopAuditLog — silently drops every entry; queries return empty. */
ca_audit_log_t *ca_noop_audit_log_create(void);

/* In-memory queryable audit log (stands in for LoggerAuditLog with real query
 * support). Records are deep-copied and retained in insertion order. */
ca_audit_log_t *ca_memory_audit_log_create(void);

void ca_audit_log_destroy(ca_audit_log_t *log);

/* Record one entry. MUST NOT fail the caller — returns true if retained, false
 * if dropped (Noop always drops). A NULL log or entry is a no-op returning false. */
bool ca_audit_log_record(ca_audit_log_t *log, const ca_audit_entry_t *entry);

/* Query historical entries most-recent-insertion-order-preserving. Returns a
 * fresh deep-copied array (caller frees with ca_audit_entry_free_array) and sets
 * *out_count. Returns NULL with *out_count == 0 when nothing matches (or for the
 * Noop log, which never retains anything). */
ca_audit_entry_t *ca_audit_log_query(ca_audit_log_t *log,
                                     const ca_audit_query_t *query,
                                     size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TENANT_AUDIT_H */
