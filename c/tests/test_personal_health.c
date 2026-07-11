/*
 * test_personal_health.c — CircleAI.Personal.Health (C11 port) verification
 * against PersonalHealthPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_phealth_vital_t mk_vital(ca_vital_kind_t k, double v, int64_t at,
                                   const char *note) {
    ca_phealth_vital_t x; memset(&x, 0, sizeof(x));
    x.kind = k; x.value = v; x.at_utc_ms = at;
    x.has_note = (note != NULL); x.note = (char *)note;
    return x;
}

static void test_vitals(void) {
    ca_phealth_board_t *b = ca_phealth_board_create();
    assert(b);

    /* Latest of empty -> false. */
    ca_phealth_vital_t got;
    assert(!ca_phealth_board_latest(b, CA_VITAL_WEIGHT_KG, &got));

    ca_phealth_vital_t w1 = mk_vital(CA_VITAL_WEIGHT_KG, 80.0, 100, NULL);
    ca_phealth_vital_t w2 = mk_vital(CA_VITAL_WEIGHT_KG, 79.5, 300, "morning");
    ca_phealth_vital_t g1 = mk_vital(CA_VITAL_GLUCOSE_MG_DL, 5.4, 200, NULL);
    ca_phealth_vital_t w0 = mk_vital(CA_VITAL_WEIGHT_KG, 81.0, 50, NULL);
    assert(ca_phealth_board_record(b, &w1) == 0);
    assert(ca_phealth_board_record(b, &w2) == 0);
    assert(ca_phealth_board_record(b, &g1) == 0);
    assert(ca_phealth_board_record(b, &w0) == 0);

    /* ReadSince(Weight, 100): w1(100), w2(300) ascending (w0@50 excluded). */
    size_t n = 0;
    ca_phealth_vital_t *arr = ca_phealth_board_read_since(b, CA_VITAL_WEIGHT_KG, 100, &n);
    assert(n == 2);
    assert(arr[0].at_utc_ms == 100 && arr[1].at_utc_ms == 300);
    assert(arr[1].has_note && strcmp(arr[1].note, "morning") == 0);
    assert(!arr[0].has_note && arr[0].note == NULL);
    ca_phealth_vital_free_array(arr, n);

    /* Latest(Weight) -> the max-AtUtc reading (w2@300). */
    assert(ca_phealth_board_latest(b, CA_VITAL_WEIGHT_KG, &got));
    assert(got.at_utc_ms == 300 && got.value == 79.5);
    ca_phealth_vital_free(&got);

    /* Latest(Glucose) -> g1. */
    assert(ca_phealth_board_latest(b, CA_VITAL_GLUCOSE_MG_DL, &got));
    assert(got.at_utc_ms == 200);
    ca_phealth_vital_free(&got);

    /* Latest of a never-recorded kind -> false. */
    assert(!ca_phealth_board_latest(b, CA_VITAL_STEPS_COUNT, &got));

    ca_phealth_board_destroy(b);
    printf("  vitals: ok\n");
}

static void test_allergies(void) {
    ca_phealth_board_t *b = ca_phealth_board_create();

    ca_phealth_allergy_t a1; memset(&a1, 0, sizeof(a1));
    a1.allergy_id = (char *)"a1"; a1.substance = (char *)"Peanut"; a1.severity = (char *)"High";
    ca_phealth_allergy_t a2; memset(&a2, 0, sizeof(a2));
    a2.allergy_id = (char *)"a2"; a2.substance = (char *)"Pollen"; a2.severity = (char *)"Low";
    assert(ca_phealth_board_add_allergy(b, &a1) == 0);
    assert(ca_phealth_board_add_allergy(b, &a2) == 0);

    size_t n = 0;
    ca_phealth_allergy_t *arr = ca_phealth_board_allergies(b, &n);
    assert(n == 2);
    ca_phealth_allergy_free_array(arr, n);

    ca_phealth_board_destroy(b);
    printf("  allergies: ok\n");
}

static void test_medications(void) {
    ca_phealth_board_t *b = ca_phealth_board_create();

    /* EndMedication on unknown -> 1. */
    assert(ca_phealth_board_end_medication(b, "mX", 999) == 1);

    ca_phealth_medication_t m1; memset(&m1, 0, sizeof(m1));
    m1.med_id = (char *)"m1"; m1.name = (char *)"Zeta"; m1.dose = (char *)"5mg";
    m1.frequency = (char *)"bid"; m1.started_at_utc_ms = 10; m1.has_ended = false;
    ca_phealth_medication_t m2; memset(&m2, 0, sizeof(m2));
    m2.med_id = (char *)"m2"; m2.name = (char *)"Alpha"; m2.dose = (char *)"1mg";
    m2.frequency = (char *)"qd"; m2.started_at_utc_ms = 20; m2.has_ended = false;
    ca_phealth_medication_t m3; memset(&m3, 0, sizeof(m3));
    m3.med_id = (char *)"m3"; m3.name = (char *)"Beta"; m3.dose = (char *)"2mg";
    m3.frequency = (char *)"qd"; m3.started_at_utc_ms = 30; m3.has_ended = false;
    assert(ca_phealth_board_add_medication(b, &m1) == 0);
    assert(ca_phealth_board_add_medication(b, &m2) == 0);
    assert(ca_phealth_board_add_medication(b, &m3) == 0);

    /* ActiveMedications ordered by Name ascending: Alpha, Beta, Zeta. */
    size_t n = 0;
    ca_phealth_medication_t *arr = ca_phealth_board_active_medications(b, &n);
    assert(n == 3);
    assert(strcmp(arr[0].name, "Alpha") == 0);
    assert(strcmp(arr[1].name, "Beta") == 0);
    assert(strcmp(arr[2].name, "Zeta") == 0);
    ca_phealth_medication_free_array(arr, n);

    /* End m2 -> active is Beta, Zeta. */
    assert(ca_phealth_board_end_medication(b, "m2", 500) == 0);
    arr = ca_phealth_board_active_medications(b, &n);
    assert(n == 2 && strcmp(arr[0].name, "Beta") == 0 && strcmp(arr[1].name, "Zeta") == 0);
    ca_phealth_medication_free_array(arr, n);

    ca_phealth_board_destroy(b);
    printf("  medications: ok\n");
}

int main(void) {
    test_vitals();
    test_allergies();
    test_medications();
    printf("test_personal_health: all assertions passed\n");
    return 0;
}
