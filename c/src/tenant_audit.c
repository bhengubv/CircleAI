/*
 * tenant_audit.c — Multi-tenant context + audit log (C11 port).
 *
 * See tenant_audit.h. Ports CircleAI.Core.MultiTenant + CircleAI.Core.Auditing.
 * In-memory only; pure C11 + libc.
 */

#include "circle_ai/tenant_audit.h"

#include <stdlib.h>
#include <string.h>

/* strdup is POSIX/MSVC but not ISO C — provide a local one for -std=c11. */
static char *ca_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ─────────────────────── Tenant context ─────────────────────── */

struct ca_tenant_context {
    bool  has_tenant;
    char *tenant_id; /* owned; NULL for the Null context */
};

ca_tenant_context_t *ca_null_tenant_context_create(void) {
    ca_tenant_context_t *c = (ca_tenant_context_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->has_tenant = false;
    c->tenant_id = NULL;
    return c;
}

/* Blank check mirrors string.IsNullOrWhiteSpace. */
static bool is_null_or_whitespace(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' &&
            *p != '\v' && *p != '\f') {
            return false;
        }
    }
    return true;
}

ca_tenant_context_t *ca_single_tenant_context_create(const char *tenant_id) {
    if (is_null_or_whitespace(tenant_id)) return NULL;
    ca_tenant_context_t *c = (ca_tenant_context_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->has_tenant = true;
    c->tenant_id = ca_strdup(tenant_id);
    if (!c->tenant_id) { free(c); return NULL; }
    return c;
}

void ca_tenant_context_destroy(ca_tenant_context_t *ctx) {
    if (!ctx) return;
    free(ctx->tenant_id);
    free(ctx);
}

const char *ca_tenant_context_current_id(const ca_tenant_context_t *ctx) {
    if (!ctx) return NULL;
    return ctx->tenant_id; /* NULL for the Null context ("throws" analogue) */
}

bool ca_tenant_context_has_tenant(const ca_tenant_context_t *ctx) {
    return ctx && ctx->has_tenant;
}

/* ─────────────────────── Audit entry helpers ─────────────────────── */

static void audit_entry_deep_copy(ca_audit_entry_t *dst, const ca_audit_entry_t *src) {
    dst->at_unix_ms         = src->at_unix_ms;
    dst->component          = ca_strdup(src->component);
    dst->operation          = ca_strdup(src->operation);
    dst->outcome            = ca_strdup(src->outcome);
    dst->tenant_id          = ca_strdup(src->tenant_id);
    dst->uhid_identity_id   = ca_strdup(src->uhid_identity_id);
    dst->correlation_id     = ca_strdup(src->correlation_id);
    dst->duration_ms        = src->duration_ms;
    dst->error_type         = ca_strdup(src->error_type);
    dst->error_code         = ca_strdup(src->error_code);
    dst->payload_sha256_hex = ca_strdup(src->payload_sha256_hex);
}

static void audit_entry_free_fields(ca_audit_entry_t *e) {
    free((void *)e->component);
    free((void *)e->operation);
    free((void *)e->outcome);
    free((void *)e->tenant_id);
    free((void *)e->uhid_identity_id);
    free((void *)e->correlation_id);
    free((void *)e->error_type);
    free((void *)e->error_code);
    free((void *)e->payload_sha256_hex);
}

void ca_audit_entry_free_array(ca_audit_entry_t *entries, size_t count) {
    if (!entries) return;
    for (size_t i = 0; i < count; i++) audit_entry_free_fields(&entries[i]);
    free(entries);
}

/* ─────────────────────── Audit log ─────────────────────── */

struct ca_audit_log {
    bool              retains;  /* false for Noop */
    ca_audit_entry_t *entries;  /* deep-copied, insertion order */
    size_t            count;
    size_t            cap;
};

ca_audit_log_t *ca_noop_audit_log_create(void) {
    ca_audit_log_t *l = (ca_audit_log_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->retains = false;
    return l;
}

ca_audit_log_t *ca_memory_audit_log_create(void) {
    ca_audit_log_t *l = (ca_audit_log_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->retains = true;
    return l;
}

void ca_audit_log_destroy(ca_audit_log_t *log) {
    if (!log) return;
    for (size_t i = 0; i < log->count; i++) audit_entry_free_fields(&log->entries[i]);
    free(log->entries);
    free(log);
}

bool ca_audit_log_record(ca_audit_log_t *log, const ca_audit_entry_t *entry) {
    if (!log || !entry) return false;
    if (!log->retains) return false; /* Noop drops (never throws). */
    if (log->count >= log->cap) {
        size_t new_cap = log->cap == 0 ? 8 : log->cap * 2;
        ca_audit_entry_t *grown =
            (ca_audit_entry_t *)realloc(log->entries, new_cap * sizeof(ca_audit_entry_t));
        if (!grown) return false; /* fail open — never bring the caller down */
        log->entries = grown;
        log->cap = new_cap;
    }
    audit_entry_deep_copy(&log->entries[log->count], entry);
    log->count++;
    return true;
}

static bool str_eq_opt(const char *filter, const char *value) {
    /* filter NULL → no constraint. value NULL treated as non-matching. */
    if (!filter) return true;
    if (!value) return false;
    return strcmp(filter, value) == 0;
}

ca_audit_entry_t *ca_audit_log_query(ca_audit_log_t *log,
                                     const ca_audit_query_t *query,
                                     size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!log || !out_count) return NULL;
    if (log->count == 0) return NULL;

    int max_items = 1000;
    if (query && query->max_items > 0) max_items = query->max_items;

    ca_audit_entry_t *result =
        (ca_audit_entry_t *)malloc(log->count * sizeof(ca_audit_entry_t));
    if (!result) return NULL;

    size_t n = 0;
    for (size_t i = 0; i < log->count && (int)n < max_items; i++) {
        const ca_audit_entry_t *e = &log->entries[i];
        if (query) {
            if (query->from_set && e->at_unix_ms < query->from_unix_ms) continue;
            if (query->to_set   && e->at_unix_ms > query->to_unix_ms)   continue;
            if (!str_eq_opt(query->component, e->component)) continue;
            if (!str_eq_opt(query->tenant_id, e->tenant_id)) continue;
            if (!str_eq_opt(query->uhid_identity_id, e->uhid_identity_id)) continue;
            if (!str_eq_opt(query->outcome, e->outcome)) continue;
        }
        audit_entry_deep_copy(&result[n], e);
        n++;
    }

    if (n == 0) { free(result); return NULL; }
    *out_count = n;
    return result;
}
