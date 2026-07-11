/*
 * test_crm.c — CircleAI.CRM (C11 port) verification against the C# reference
 * (Contracts.cs + InMemoryCrm.cs).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_crm_contact_t mk_contact(const char *id, const char *name,
                                   const char *email) {
    ca_crm_contact_t c; memset(&c, 0, sizeof(c));
    c.contact_id = (char *)id;
    c.full_name  = (char *)name;
    if (email) { c.has_email = true; c.email = (char *)email; }
    return c;
}

static void test_contact_store(void) {
    ca_crm_contact_store_t *s = ca_crm_contact_store_create();
    assert(s);
    assert(strcmp(ca_crm_contact_store_backend_id(s), "in-memory") == 0);

    assert(ca_crm_contact_store_upsert(s, NULL) == -1);
    ca_crm_contact_t bad = mk_contact("  ", "X", NULL);
    assert(ca_crm_contact_store_upsert(s, &bad) == 2); /* whitespace id */

    ca_crm_contact_t c1 = mk_contact("c1", "Ada Lovelace", "ada@x.io");
    ca_crm_contact_t c2 = mk_contact("c2", "Grace Hopper", NULL);
    ca_crm_contact_t c3 = mk_contact("c3", "Bob", "BOB@ADA.io");
    assert(ca_crm_contact_store_upsert(s, &c1) == 0);
    assert(ca_crm_contact_store_upsert(s, &c2) == 0);
    assert(ca_crm_contact_store_upsert(s, &c3) == 0);

    ca_crm_contact_t got;
    assert(ca_crm_contact_store_get(s, "c1", &got));
    assert(strcmp(got.full_name, "Ada Lovelace") == 0 && got.has_email &&
           strcmp(got.email, "ada@x.io") == 0);
    ca_crm_contact_free(&got);
    assert(ca_crm_contact_store_get(s, "c2", &got) && !got.has_email);
    ca_crm_contact_free(&got);
    assert(!ca_crm_contact_store_get(s, "nope", &got));

    /* upsert replaces. */
    ca_crm_contact_t c1b = mk_contact("c1", "Ada L.", NULL);
    assert(ca_crm_contact_store_upsert(s, &c1b) == 0);
    assert(ca_crm_contact_store_get(s, "c1", &got) && !got.has_email);
    ca_crm_contact_free(&got);

    /* Search "ada" (CI): matches "Ada L." (name) and "Bob" (email BOB@ADA.io);
     * ordered by FullName CI ascending: "Ada L." then "Bob". */
    size_t n = 0;
    ca_crm_contact_t *hits = ca_crm_contact_store_search(s, "ada", 20, &n);
    assert(n == 2);
    assert(strcmp(hits[0].full_name, "Ada L.") == 0);
    assert(strcmp(hits[1].full_name, "Bob") == 0);
    ca_crm_contact_free_array(hits, n);

    /* empty query matches all (Contains("") == true), CI-sorted. */
    hits = ca_crm_contact_store_search(s, "", 20, &n);
    assert(n == 3);
    assert(strcmp(hits[0].full_name, "Ada L.") == 0);
    assert(strcmp(hits[1].full_name, "Bob") == 0);
    assert(strcmp(hits[2].full_name, "Grace Hopper") == 0);
    ca_crm_contact_free_array(hits, n);

    /* topK caps after sort. */
    hits = ca_crm_contact_store_search(s, "", 2, &n);
    assert(n == 2 && strcmp(hits[1].full_name, "Bob") == 0);
    ca_crm_contact_free_array(hits, n);

    /* error paths. */
    assert(ca_crm_contact_store_search(s, NULL, 20, &n) == NULL && n == (size_t)-1);
    assert(ca_crm_contact_store_search(s, "x", 0, &n) == NULL && n == (size_t)-1);

    ca_crm_contact_store_destroy(s);
    printf("  contact_store: ok\n");
}

static ca_crm_deal_t mk_deal(const char *id, const char *stage, int64_t value) {
    ca_crm_deal_t d; memset(&d, 0, sizeof(d));
    d.deal_id = (char *)id; d.company_id = (char *)"co"; d.name = (char *)"D";
    d.value = value; d.currency = (char *)"USD"; d.stage = (char *)stage;
    return d;
}

static void test_deal_pipeline(void) {
    ca_crm_deal_pipeline_t *p = ca_crm_deal_pipeline_create();
    assert(p);

    ca_crm_deal_t d1 = mk_deal("d1", "Open", 100);
    ca_crm_deal_t d2 = mk_deal("d2", "open", 300);   /* CI-equal stage */
    ca_crm_deal_t d3 = mk_deal("d3", "Won", 500);
    assert(ca_crm_deal_pipeline_upsert(p, &d1) == 0);
    assert(ca_crm_deal_pipeline_upsert(p, &d2) == 0);
    assert(ca_crm_deal_pipeline_upsert(p, &d3) == 0);

    ca_crm_deal_t got;
    assert(ca_crm_deal_pipeline_get(p, "d2", &got) && got.value == 300);
    ca_crm_deal_free(&got);

    /* ListByStage "OPEN" (CI): d1(100), d2(300) ordered by Value desc => d2,d1. */
    size_t n = 0;
    ca_crm_deal_t *hits = ca_crm_deal_pipeline_list_by_stage(p, "OPEN", &n);
    assert(n == 2);
    assert(strcmp(hits[0].deal_id, "d2") == 0 && hits[0].value == 300);
    assert(strcmp(hits[1].deal_id, "d1") == 0);
    ca_crm_deal_free_array(hits, n);

    hits = ca_crm_deal_pipeline_list_by_stage(p, "Lost", &n);
    assert(n == 0 && hits == NULL);

    ca_crm_deal_pipeline_destroy(p);
    printf("  deal_pipeline: ok\n");
}

static ca_crm_activity_t mk_act(const char *id, const char *cid, int64_t at) {
    ca_crm_activity_t a; memset(&a, 0, sizeof(a));
    a.activity_id = (char *)id; a.contact_id = (char *)cid;
    a.kind = (char *)"call"; a.body = (char *)"hi"; a.at_utc_ms = at;
    return a;
}

static void test_activity_log(void) {
    ca_crm_activity_log_t *l = ca_crm_activity_log_create();
    assert(l);

    assert(ca_crm_activity_log_append(l, NULL) == -1);
    ca_crm_activity_t a1 = mk_act("a1", "c1", 100);
    ca_crm_activity_t a2 = mk_act("a2", "c1", 300);
    ca_crm_activity_t a3 = mk_act("a3", "c2", 200);
    assert(ca_crm_activity_log_append(l, &a1) == 0);
    assert(ca_crm_activity_log_append(l, &a2) == 0);
    assert(ca_crm_activity_log_append(l, &a3) == 0);

    /* ReadForContact(c1) newest-first: a2(300), a1(100). */
    size_t n = 0;
    ca_crm_activity_t *arr = ca_crm_activity_log_read_for_contact(l, "c1", 100, &n);
    assert(n == 2);
    assert(strcmp(arr[0].activity_id, "a2") == 0);
    assert(strcmp(arr[1].activity_id, "a1") == 0);
    ca_crm_activity_free_array(arr, n);

    /* limit caps after sort. */
    arr = ca_crm_activity_log_read_for_contact(l, "c1", 1, &n);
    assert(n == 1 && strcmp(arr[0].activity_id, "a2") == 0);
    ca_crm_activity_free_array(arr, n);

    arr = ca_crm_activity_log_read_for_contact(l, "zzz", 100, &n);
    assert(n == 0 && arr == NULL);

    ca_crm_activity_log_destroy(l);
    printf("  activity_log: ok\n");
}

int main(void) {
    test_contact_store();
    test_deal_pipeline();
    test_activity_log();
    printf("test_crm: all assertions passed\n");
    return 0;
}
