/*
 * test_integration_calendar.c — CircleAI.Integration.Calendar (C11 port)
 * verification of the in-memory CalDav / Google / MsGraph connectors.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_int_calendar_event_t mk_ev(const char *id, const char *cal,
                                     int64_t start, int64_t end) {
    ca_int_calendar_event_t e; memset(&e, 0, sizeof(e));
    e.event_id = (char *)id; e.calendar_id = (char *)cal;
    e.title = (char *)"E"; e.start_utc_ms = start; e.end_utc_ms = end;
    return e;
}

static void test_provider_ids_and_config(void) {
    ca_int_calendar_connector_t *cd = ca_int_caldav_calendar_create("u", "p");
    ca_int_calendar_connector_t *cd0 = ca_int_caldav_calendar_create("u", "  ");
    ca_int_calendar_connector_t *g = ca_int_google_calendar_create(true, NULL);
    ca_int_calendar_connector_t *g0 = ca_int_google_calendar_create(false, NULL);
    ca_int_calendar_connector_t *ms = ca_int_msgraph_calendar_create(true, "primary");
    assert(cd && cd0 && g && g0 && ms);

    assert(strcmp(cd->provider_id(cd->impl), "caldav") == 0);
    assert(strcmp(g->provider_id(g->impl), "google-calendar") == 0);
    assert(strcmp(ms->provider_id(ms->impl), "ms-graph-calendar") == 0);

    assert(cd->is_configured(cd->impl));          /* both non-blank */
    assert(!cd0->is_configured(cd0->impl));        /* blank password */
    assert(g->is_configured(g->impl));             /* token provider present */
    assert(!g0->is_configured(g0->impl));          /* no token provider */
    assert(ms->is_configured(ms->impl));

    ca_int_calendar_connector_destroy(cd);
    ca_int_calendar_connector_destroy(cd0);
    ca_int_calendar_connector_destroy(g);
    ca_int_calendar_connector_destroy(g0);
    ca_int_calendar_connector_destroy(ms);
    printf("  provider_ids_and_config: ok\n");
}

static void test_create_list_delete(void) {
    ca_int_calendar_connector_t *c = ca_int_google_calendar_create(true, "primary");
    assert(c);

    /* create with explicit id */
    ca_int_calendar_event_t e1 = mk_ev("e1", "primary", 3000, 4000);
    ca_int_calendar_event_t out;
    assert(c->create_event(c->impl, &e1, &out) == 0);
    assert(strcmp(out.event_id, "e1") == 0);
    ca_int_calendar_event_free(&out);

    /* create with blank id -> UID assigned (32 hex chars) */
    ca_int_calendar_event_t e2 = mk_ev("", "primary", 1000, 2000);
    assert(c->create_event(c->impl, &e2, &out) == 0);
    assert(strlen(out.event_id) == 32);
    char uid[33]; strcpy(uid, out.event_id);
    ca_int_calendar_event_free(&out);

    /* another overlapping-with-window event */
    ca_int_calendar_event_t e3 = mk_ev("e3", "primary", 5000, 9000);
    assert(c->create_event(c->impl, &e3, NULL) == 0);

    /* NULL ev -> ArgumentNullException */
    assert(c->create_event(c->impl, NULL, &out) == -1);

    /* ListEvents [0, 4500): overlaps e2 (1000-2000) and e1 (3000-4000); NOT e3
     * (starts 5000). Ordered by StartUtc asc: e2, e1. */
    size_t n = 0;
    ca_int_calendar_event_t *evs = c->list_events(c->impl, 0, 4500, &n);
    assert(n == 2);
    assert(strcmp(evs[0].event_id, uid) == 0);  /* start 1000 */
    assert(strcmp(evs[1].event_id, "e1") == 0);  /* start 3000 */
    ca_int_calendar_event_free_array(evs, n);

    /* Window that touches only the edge: [2000, 3000) -> nothing (half-open:
     * e2 end==2000 not > 2000; e1 start==3000 not < 3000). */
    evs = c->list_events(c->impl, 2000, 3000, &n);
    assert(n == 0 && evs == NULL);

    /* delete e1 (idempotent: second delete still rc 0). */
    assert(c->delete_event(c->impl, "primary", "e1") == 0);
    assert(c->delete_event(c->impl, "primary", "e1") == 0);
    assert(c->delete_event(c->impl, "primary", "unknown") == 0);
    /* whitespace eventId -> ArgumentException (rc -1). */
    assert(c->delete_event(c->impl, "primary", "  ") == -1);

    evs = c->list_events(c->impl, 0, 10000, &n);
    assert(n == 2); /* e2(uid) + e3 remain */
    ca_int_calendar_event_free_array(evs, n);

    ca_int_calendar_connector_destroy(c);
    printf("  create_list_delete: ok\n");
}

static void test_uid_uniqueness(void) {
    ca_int_calendar_connector_t *c = ca_int_caldav_calendar_create("u", "p");
    assert(c);
    ca_int_calendar_event_t a = mk_ev("", "cal", 1, 2);
    ca_int_calendar_event_t b = mk_ev("", "cal", 3, 4);
    ca_int_calendar_event_t oa, ob;
    assert(c->create_event(c->impl, &a, &oa) == 0);
    assert(c->create_event(c->impl, &b, &ob) == 0);
    assert(strcmp(oa.event_id, ob.event_id) != 0); /* distinct UIDs */
    ca_int_calendar_event_free(&oa);
    ca_int_calendar_event_free(&ob);
    ca_int_calendar_connector_destroy(c);
    printf("  uid_uniqueness: ok\n");
}

int main(void) {
    test_provider_ids_and_config();
    test_create_list_delete();
    test_uid_uniqueness();
    printf("test_integration_calendar: all assertions passed\n");
    return 0;
}
