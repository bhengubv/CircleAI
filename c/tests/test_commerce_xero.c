/*
 * test_commerce_xero.c — CircleAI.Commerce.Integration.Xero (C11 port)
 * verification against XeroPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_xero_tokens_t mk_tokens(const char *at, int64_t exp) {
    ca_xero_tokens_t t; memset(&t, 0, sizeof(t));
    t.access_token = (char *)at; t.refresh_token = (char *)"rt";
    t.expires_at_utc_ms = exp; t.id_token = (char *)"idt";
    return t;
}
static ca_xero_tenant_t mk_tenant(const char *id, const char *name) {
    ca_xero_tenant_t t; memset(&t, 0, sizeof(t));
    t.tenant_id = (char *)id; t.tenant_name = (char *)name; t.tenant_type = (char *)"ORGANISATION";
    return t;
}
static ca_xero_event_t mk_event(const char *tid, const char *rid, int64_t at) {
    ca_xero_event_t e; memset(&e, 0, sizeof(e));
    e.tenant_id = (char *)tid; e.resource_type = (char *)"Invoice";
    e.resource_id = (char *)rid; e.at_utc_ms = at;
    return e;
}

static void test_tokens(void) {
    ca_xero_board_t *b = ca_xero_board_create();
    assert(b);

    /* No tokens -> TokensExpired true; GetTokens false. */
    assert(ca_xero_board_tokens_expired(b, "u1", 100));
    ca_xero_tokens_t got;
    assert(!ca_xero_board_get_tokens(b, "u1", &got));

    ca_xero_tokens_t t = mk_tokens("access-1", 500);
    assert(ca_xero_board_store_tokens(b, "u1", &t) == 0);
    assert(ca_xero_board_get_tokens(b, "u1", &got));
    assert(strcmp(got.access_token, "access-1") == 0 && got.expires_at_utc_ms == 500);
    ca_xero_tokens_free(&got);

    /* now < expiry -> not expired; now >= expiry -> expired. */
    assert(!ca_xero_board_tokens_expired(b, "u1", 499));
    assert(ca_xero_board_tokens_expired(b, "u1", 500));
    assert(ca_xero_board_tokens_expired(b, "u1", 501));

    /* store replaces. */
    ca_xero_tokens_t t2 = mk_tokens("access-2", 900);
    assert(ca_xero_board_store_tokens(b, "u1", &t2) == 0);
    assert(ca_xero_board_get_tokens(b, "u1", &got));
    assert(strcmp(got.access_token, "access-2") == 0);
    ca_xero_tokens_free(&got);

    ca_xero_board_destroy(b);
    printf("  tokens: ok\n");
}

static void test_tenants(void) {
    ca_xero_board_t *b = ca_xero_board_create();

    /* no tenants -> empty. */
    size_t n = 0;
    ca_xero_tenant_t *arr = ca_xero_board_tenants_for(b, "u1", &n);
    assert(n == 0 && arr == NULL);

    ca_xero_tenant_t t1 = mk_tenant("T1", "Org One");
    ca_xero_tenant_t t2 = mk_tenant("T2", "Org Two");
    assert(ca_xero_board_add_tenant(b, "u1", &t1) == 0);
    assert(ca_xero_board_add_tenant(b, "u1", &t2) == 0);
    /* dedup by TenantId: re-adding T1 is a no-op. */
    assert(ca_xero_board_add_tenant(b, "u1", &t1) == 0);
    /* another user is isolated. */
    ca_xero_tenant_t t3 = mk_tenant("T9", "Other");
    assert(ca_xero_board_add_tenant(b, "u2", &t3) == 0);

    arr = ca_xero_board_tenants_for(b, "u1", &n);
    assert(n == 2 && strcmp(arr[0].tenant_id, "T1") == 0 && strcmp(arr[1].tenant_id, "T2") == 0);
    ca_xero_tenant_free_array(arr, n);

    arr = ca_xero_board_tenants_for(b, "u2", &n);
    assert(n == 1 && strcmp(arr[0].tenant_id, "T9") == 0);
    ca_xero_tenant_free_array(arr, n);

    ca_xero_board_destroy(b);
    printf("  tenants: ok\n");
}

static void test_events(void) {
    ca_xero_board_t *b = ca_xero_board_create();

    ca_xero_event_t e1 = mk_event("T1", "r1", 100);
    ca_xero_event_t e2 = mk_event("T1", "r2", 300);
    ca_xero_event_t e3 = mk_event("T1", "r3", 200);
    assert(ca_xero_board_record_webhook(b, &e1) == 0);
    assert(ca_xero_board_record_webhook(b, &e2) == 0);
    assert(ca_xero_board_record_webhook(b, &e3) == 0);

    /* RecentEvents ordered by AtUtc descending: r2(300), r3(200), r1(100). */
    size_t n = 0;
    ca_xero_event_t *arr = ca_xero_board_recent_events(b, 20, &n);
    assert(n == 3);
    assert(strcmp(arr[0].resource_id, "r2") == 0);
    assert(strcmp(arr[1].resource_id, "r3") == 0);
    assert(strcmp(arr[2].resource_id, "r1") == 0);
    ca_xero_event_free_array(arr, n);

    /* limit truncates after ordering. */
    arr = ca_xero_board_recent_events(b, 1, &n);
    assert(n == 1 && strcmp(arr[0].resource_id, "r2") == 0);
    ca_xero_event_free_array(arr, n);

    /* limit 0 -> empty; negative -> SIZE_MAX. */
    arr = ca_xero_board_recent_events(b, 0, &n);
    assert(n == 0 && arr == NULL);
    arr = ca_xero_board_recent_events(b, -1, &n);
    assert(n == (size_t)-1);

    ca_xero_board_destroy(b);
    printf("  events: ok\n");
}

int main(void) {
    test_tokens();
    test_tenants();
    test_events();
    printf("test_commerce_xero: all assertions passed\n");
    return 0;
}
