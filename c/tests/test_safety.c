/*
 * test_safety.c — CircleAI.Safety domain primitives (C11 port).
 *
 * Verifies InMemorySafetyBoard: incident logging + Active/AtOrAboveSeverity
 * descending order, hazard last-write-wins + descending order, emergency-contact
 * insertion order + FirstContact, against SafetyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_incident_t mk_inc(const char *id, ca_incident_severity_t sev, int64_t at) {
    ca_incident_t i; memset(&i, 0, sizeof(i));
    i.incident_id = strdup(id);
    i.severity = sev;
    i.description = strdup("d");
    i.at_utc_ms = at;
    return i;
}
static ca_hazard_t mk_haz(const char *id, const char *cat, int64_t noted) {
    ca_hazard_t h; memset(&h, 0, sizeof(h));
    h.hazard_id = strdup(id);
    h.description = strdup("d");
    h.category = strdup(cat);
    h.noted_utc_ms = noted;
    return h;
}
static ca_emergency_contact_t mk_con(const char *id, const char *name) {
    ca_emergency_contact_t c; memset(&c, 0, sizeof(c));
    c.contact_id = strdup(id);
    c.name = strdup(name);
    c.phone = strdup("000");
    c.relationship = strdup("kin");
    return c;
}

static void test_incidents(void) {
    ca_safety_board_t *b = ca_safety_board_create();
    assert(b);

    size_t n = 0;
    assert(ca_safety_board_active(b, &n) == NULL && n == 0);

    ca_incident_t i1 = mk_inc("i1", CA_INCIDENT_SEVERITY_INFO,      100);
    ca_incident_t i2 = mk_inc("i2", CA_INCIDENT_SEVERITY_WARNING,   300);
    ca_incident_t i3 = mk_inc("i3", CA_INCIDENT_SEVERITY_CRITICAL,  200);
    ca_incident_t i4 = mk_inc("i4", CA_INCIDENT_SEVERITY_EMERGENCY, 400);
    /* location on one */
    i3.has_latitude = true; i3.latitude = -26.2; i3.has_longitude = true; i3.longitude = 28.0;
    assert(ca_safety_board_log(b, &i1));
    assert(ca_safety_board_log(b, &i2));
    assert(ca_safety_board_log(b, &i3));
    assert(ca_safety_board_log(b, &i4));
    assert(ca_safety_board_log(b, NULL) == false);

    /* Active: descending by at_utc_ms → i4(400), i2(300), i3(200), i1(100) */
    ca_incident_t *act = ca_safety_board_active(b, &n);
    assert(n == 4);
    assert(strcmp(act[0].incident_id, "i4") == 0);
    assert(strcmp(act[1].incident_id, "i2") == 0);
    assert(strcmp(act[2].incident_id, "i3") == 0);
    assert(strcmp(act[3].incident_id, "i1") == 0);
    /* deep-copied location carried */
    assert(act[2].has_latitude && act[2].latitude == -26.2);
    ca_incident_free_array(act, n);

    /* AtOrAboveSeverity(Critical) → i4(Emergency), i3(Critical) descending */
    ca_incident_t *hi = ca_safety_board_at_or_above_severity(b, CA_INCIDENT_SEVERITY_CRITICAL, &n);
    assert(n == 2);
    assert(strcmp(hi[0].incident_id, "i4") == 0);
    assert(strcmp(hi[1].incident_id, "i3") == 0);
    ca_incident_free_array(hi, n);

    /* AtOrAboveSeverity(Emergency) → only i4 */
    hi = ca_safety_board_at_or_above_severity(b, CA_INCIDENT_SEVERITY_EMERGENCY, &n);
    assert(n == 1 && strcmp(hi[0].incident_id, "i4") == 0);
    ca_incident_free_array(hi, n);

    /* AtOrAboveSeverity(Info) → all 4 */
    hi = ca_safety_board_at_or_above_severity(b, CA_INCIDENT_SEVERITY_INFO, &n);
    assert(n == 4);
    ca_incident_free_array(hi, n);

    /* NULL board sentinel */
    assert(ca_safety_board_active(NULL, &n) == NULL && n == SIZE_MAX);

    ca_incident_free(&i1); ca_incident_free(&i2); ca_incident_free(&i3); ca_incident_free(&i4);
    ca_safety_board_destroy(b);
    printf("  incidents: ok\n");
}

static void test_stable_order_equal_ts(void) {
    /* Equal timestamps preserve insertion order (LINQ OrderByDescending stable). */
    ca_safety_board_t *b = ca_safety_board_create();
    ca_incident_t a = mk_inc("a", CA_INCIDENT_SEVERITY_INFO, 500);
    ca_incident_t c = mk_inc("c", CA_INCIDENT_SEVERITY_INFO, 500);
    ca_incident_t d = mk_inc("d", CA_INCIDENT_SEVERITY_INFO, 500);
    ca_safety_board_log(b, &a); ca_safety_board_log(b, &c); ca_safety_board_log(b, &d);
    size_t n = 0;
    ca_incident_t *act = ca_safety_board_active(b, &n);
    assert(n == 3);
    assert(strcmp(act[0].incident_id, "a") == 0);
    assert(strcmp(act[1].incident_id, "c") == 0);
    assert(strcmp(act[2].incident_id, "d") == 0);
    ca_incident_free_array(act, n);
    ca_incident_free(&a); ca_incident_free(&c); ca_incident_free(&d);
    ca_safety_board_destroy(b);
    printf("  stable_order_equal_ts: ok\n");
}

static void test_hazards(void) {
    ca_safety_board_t *b = ca_safety_board_create();
    size_t n = 0;
    assert(ca_safety_board_hazards(b, &n) == NULL && n == 0);

    ca_hazard_t h1 = mk_haz("h1", "electrical", 100);
    ca_hazard_t h2 = mk_haz("h2", "chemical",   300);
    ca_hazard_t h3 = mk_haz("h3", "fall",       200);
    assert(ca_safety_board_note_hazard(b, &h1));
    assert(ca_safety_board_note_hazard(b, &h2));
    assert(ca_safety_board_note_hazard(b, &h3));
    assert(ca_safety_board_note_hazard(b, NULL) == false);

    /* descending by noted → h2(300), h3(200), h1(100) */
    ca_hazard_t *hz = ca_safety_board_hazards(b, &n);
    assert(n == 3);
    assert(strcmp(hz[0].hazard_id, "h2") == 0);
    assert(strcmp(hz[1].hazard_id, "h3") == 0);
    assert(strcmp(hz[2].hazard_id, "h1") == 0);
    ca_hazard_free_array(hz, n);

    /* last-write-wins by hazard_id: re-note h1 with new category + newer ts */
    ca_hazard_t h1b = mk_haz("h1", "electrical-updated", 500);
    assert(ca_safety_board_note_hazard(b, &h1b));
    ca_hazard_free(&h1b);
    hz = ca_safety_board_hazards(b, &n);
    assert(n == 3);   /* replaced, not added */
    assert(strcmp(hz[0].hazard_id, "h1") == 0);   /* now newest (500) */
    assert(strcmp(hz[0].category, "electrical-updated") == 0);
    ca_hazard_free_array(hz, n);

    ca_hazard_free(&h1); ca_hazard_free(&h2); ca_hazard_free(&h3);
    ca_safety_board_destroy(b);
    printf("  hazards: ok\n");
}

static void test_contacts(void) {
    ca_safety_board_t *b = ca_safety_board_create();
    ca_emergency_contact_t out; memset(&out, 0, sizeof(out));
    /* no contacts → FirstContact false */
    assert(ca_safety_board_first_contact(b, &out) == false);

    size_t n = 0;
    assert(ca_safety_board_contacts(b, &n) == NULL && n == 0);

    ca_emergency_contact_t c1 = mk_con("c1", "Alice");
    ca_emergency_contact_t c2 = mk_con("c2", "Bob");
    assert(ca_safety_board_add_contact(b, &c1));
    assert(ca_safety_board_add_contact(b, &c2));
    assert(ca_safety_board_add_contact(b, NULL) == false);

    /* FirstContact = first added */
    assert(ca_safety_board_first_contact(b, &out));
    assert(strcmp(out.contact_id, "c1") == 0 && strcmp(out.name, "Alice") == 0);
    ca_emergency_contact_free(&out);

    /* Contacts in insertion order */
    ca_emergency_contact_t *cs = ca_safety_board_contacts(b, &n);
    assert(n == 2);
    assert(strcmp(cs[0].contact_id, "c1") == 0 && strcmp(cs[1].contact_id, "c2") == 0);
    ca_emergency_contact_free_array(cs, n);

    ca_emergency_contact_free(&c1); ca_emergency_contact_free(&c2);
    ca_safety_board_destroy(b);
    printf("  contacts: ok\n");
}

int main(void) {
    test_incidents();
    test_stable_order_equal_ts();
    test_hazards();
    test_contacts();
    printf("test_safety: all assertions passed\n");
    return 0;
}
