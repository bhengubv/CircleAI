/*
 * test_tenant_audit.c — tenant context + audit log (C11).
 *
 * Mirrors NullTenantContext / SingleTenantContext and the audit-log contract:
 * Noop drops + empty query; in-memory log records, queries, and filters.
 */

#include "circle_ai/tenant_audit.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

static void test_null_tenant_context(void) {
    ca_tenant_context_t *c = ca_null_tenant_context_create();
    assert(c);
    assert(ca_tenant_context_has_tenant(c) == false);
    assert(ca_tenant_context_current_id(c) == NULL); /* "throws" analogue */
    ca_tenant_context_destroy(c);
}

static void test_single_tenant_context(void) {
    assert(ca_single_tenant_context_create(NULL) == NULL);
    assert(ca_single_tenant_context_create("   ") == NULL); /* blank rejected */
    ca_tenant_context_t *c = ca_single_tenant_context_create("tgn-alpha");
    assert(c);
    assert(ca_tenant_context_has_tenant(c) == true);
    assert(strcmp(ca_tenant_context_current_id(c), "tgn-alpha") == 0);
    ca_tenant_context_destroy(c);
}

static ca_audit_entry_t mkentry(int64_t at, const char *component, const char *op,
                                const char *outcome, const char *tenant) {
    ca_audit_entry_t e;
    memset(&e, 0, sizeof(e));
    e.at_unix_ms = at;
    e.component = component;
    e.operation = op;
    e.outcome = outcome;
    e.tenant_id = tenant;
    e.duration_ms = 1.5;
    return e;
}

static void test_noop_audit_log(void) {
    ca_audit_log_t *l = ca_noop_audit_log_create();
    assert(l);
    ca_audit_entry_t e = mkentry(1000, "Comp", "Op", "success", NULL);
    assert(ca_audit_log_record(l, &e) == false); /* dropped */
    size_t n = 999;
    ca_audit_entry_t *r = ca_audit_log_query(l, NULL, &n);
    assert(r == NULL && n == 0);
    ca_audit_log_destroy(l);
}

static void test_memory_audit_log_record_query(void) {
    ca_audit_log_t *l = ca_memory_audit_log_create();
    assert(l);
    ca_audit_entry_t e1 = mkentry(1000, "SecurityWatchdog", "OnAnomaly", "success", "t1");
    ca_audit_entry_t e2 = mkentry(2000, "JsonPersona", "GetAsync", "failure", "t2");
    e2.error_type = "InvalidOperationException";
    ca_audit_entry_t e3 = mkentry(3000, "SecurityWatchdog", "TryCommit", "success", "t1");
    assert(ca_audit_log_record(l, &e1));
    assert(ca_audit_log_record(l, &e2));
    assert(ca_audit_log_record(l, &e3));

    /* unfiltered — all 3 in insertion order */
    size_t n = 0;
    ca_audit_entry_t *all = ca_audit_log_query(l, NULL, &n);
    assert(all && n == 3);
    assert(strcmp(all[0].component, "SecurityWatchdog") == 0);
    assert(strcmp(all[1].operation, "GetAsync") == 0);
    assert(strcmp(all[1].error_type, "InvalidOperationException") == 0);
    ca_audit_entry_free_array(all, n);

    /* filter by component */
    ca_audit_query_t q; memset(&q, 0, sizeof(q));
    q.component = "SecurityWatchdog";
    ca_audit_entry_t *comp = ca_audit_log_query(l, &q, &n);
    assert(comp && n == 2);
    ca_audit_entry_free_array(comp, n);

    /* filter by tenant + outcome */
    memset(&q, 0, sizeof(q));
    q.tenant_id = "t1"; q.outcome = "success";
    ca_audit_entry_t *t1 = ca_audit_log_query(l, &q, &n);
    assert(t1 && n == 2);
    ca_audit_entry_free_array(t1, n);

    /* time bounds: [2000, 3000] -> e2, e3 */
    memset(&q, 0, sizeof(q));
    q.from_set = true; q.from_unix_ms = 2000;
    q.to_set = true;   q.to_unix_ms = 3000;
    ca_audit_entry_t *win = ca_audit_log_query(l, &q, &n);
    assert(win && n == 2);
    assert(win[0].at_unix_ms == 2000);
    assert(win[1].at_unix_ms == 3000);
    ca_audit_entry_free_array(win, n);

    /* max_items cap */
    memset(&q, 0, sizeof(q));
    q.max_items = 1;
    ca_audit_entry_t *one = ca_audit_log_query(l, &q, &n);
    assert(one && n == 1);
    ca_audit_entry_free_array(one, n);

    /* no match -> NULL/0 */
    memset(&q, 0, sizeof(q));
    q.component = "Nonexistent";
    ca_audit_entry_t *none = ca_audit_log_query(l, &q, &n);
    assert(none == NULL && n == 0);

    ca_audit_log_destroy(l);
}

int main(void) {
    test_null_tenant_context();
    test_single_tenant_context();
    test_noop_audit_log();
    test_memory_audit_log_record_query();
    printf("test_tenant_audit: all assertions passed\n");
    return 0;
}
