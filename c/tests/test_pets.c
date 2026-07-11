/*
 * test_pets.c — CircleAI.Pets (C11 port) verification against PetsPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_pet_t mk_pet(const char *id, const char *name, const char *breed) {
    ca_pet_t p; memset(&p, 0, sizeof(p));
    p.pet_id = (char *)id; p.name = (char *)name; p.species = (char *)"dog";
    if (breed) { p.has_breed = true; p.breed = (char *)breed; }
    p.date_of_birth_ms = 0;
    return p;
}

static void test_pets(void) {
    ca_pet_board_t *b = ca_pet_board_create();
    assert(b);
    assert(ca_pet_board_add(b, NULL) == -1);

    ca_pet_t p1 = mk_pet("p1", "Zeus", "Lab");
    ca_pet_t p2 = mk_pet("p2", "Ace", NULL);
    assert(ca_pet_board_add(b, &p1) == 0);
    assert(ca_pet_board_add(b, &p2) == 0);

    ca_pet_t got;
    assert(ca_pet_board_get_pet(b, "p1", &got) && got.has_breed &&
           strcmp(got.breed, "Lab") == 0);
    ca_pet_free(&got);
    assert(ca_pet_board_get_pet(b, "p2", &got) && !got.has_breed);
    ca_pet_free(&got);

    /* Pets ordered by Name: Ace, Zeus. */
    size_t n = 0;
    ca_pet_t *arr = ca_pet_board_pets(b, &n);
    assert(n == 2 && strcmp(arr[0].name, "Ace") == 0);
    ca_pet_free_array(arr, n);

    ca_pet_board_destroy(b);
    printf("  pets: ok\n");
}

static void test_vax_weight(void) {
    ca_pet_board_t *b = ca_pet_board_create();

    ca_pet_vaccination_t v1; memset(&v1, 0, sizeof(v1));
    v1.pet_id = (char *)"p1"; v1.vaccine = (char *)"Rabies";
    v1.administered_utc_ms = 100; v1.has_booster_due = true; v1.booster_due_utc_ms = 999;
    ca_pet_vaccination_t v2; memset(&v2, 0, sizeof(v2));
    v2.pet_id = (char *)"p1"; v2.vaccine = (char *)"Parvo";
    v2.administered_utc_ms = 300; /* newest */ v2.has_booster_due = false;
    assert(ca_pet_board_record_vaccination(b, &v1) == 0);
    assert(ca_pet_board_record_vaccination(b, &v2) == 0);

    /* VaccinationsFor(p1) newest-first: Parvo(300), Rabies(100). */
    size_t n = 0;
    ca_pet_vaccination_t *va = ca_pet_board_vaccinations_for(b, "p1", &n);
    assert(n == 2);
    assert(strcmp(va[0].vaccine, "Parvo") == 0 && !va[0].has_booster_due);
    assert(strcmp(va[1].vaccine, "Rabies") == 0 && va[1].has_booster_due &&
           va[1].booster_due_utc_ms == 999);
    ca_pet_vaccination_free_array(va, n);

    /* Weights ascending by AtUtc. */
    ca_pet_weight_t w1; memset(&w1, 0, sizeof(w1));
    w1.pet_id = (char *)"p1"; w1.weight_kg = 10.0; w1.at_utc_ms = 300;
    ca_pet_weight_t w2; memset(&w2, 0, sizeof(w2));
    w2.pet_id = (char *)"p1"; w2.weight_kg = 8.0; w2.at_utc_ms = 100;
    assert(ca_pet_board_record_weight(b, &w1) == 0);
    assert(ca_pet_board_record_weight(b, &w2) == 0);
    ca_pet_weight_t *wh = ca_pet_board_weight_history(b, "p1", &n);
    assert(n == 2 && wh[0].weight_kg == 8.0 && wh[1].weight_kg == 10.0);
    ca_pet_weight_free_array(wh, n);

    ca_pet_board_destroy(b);
    printf("  vax_weight: ok\n");
}

static void test_appointments(void) {
    ca_pet_board_t *b = ca_pet_board_create();

    ca_pet_appointment_t a1; memset(&a1, 0, sizeof(a1));
    a1.appt_id = (char *)"a1"; a1.pet_id = (char *)"p1"; a1.reason = (char *)"checkup";
    a1.at_utc_ms = 500; a1.vet = (char *)"Dr V";
    ca_pet_appointment_t a2; memset(&a2, 0, sizeof(a2));
    a2.appt_id = (char *)"a2"; a2.pet_id = (char *)"p1"; a2.reason = (char *)"shot";
    a2.at_utc_ms = 300; a2.vet = (char *)"Dr W";
    ca_pet_appointment_t a3; memset(&a3, 0, sizeof(a3));
    a3.appt_id = (char *)"a3"; a3.pet_id = (char *)"p1"; a3.reason = (char *)"past";
    a3.at_utc_ms = 50; a3.vet = (char *)"Dr X";
    assert(ca_pet_board_schedule(b, &a1) == 0);
    assert(ca_pet_board_schedule(b, &a2) == 0);
    assert(ca_pet_board_schedule(b, &a3) == 0);

    /* UpcomingAppointments(now=100): a2(300),a1(500) [a3 at 50 excluded];
     * ordered by AtUtc asc => a2, a1. */
    size_t n = 0;
    ca_pet_appointment_t *arr = ca_pet_board_upcoming_appointments(b, 100, &n);
    assert(n == 2);
    assert(strcmp(arr[0].appt_id, "a2") == 0);
    assert(strcmp(arr[1].appt_id, "a1") == 0);
    ca_pet_appointment_free_array(arr, n);

    ca_pet_board_destroy(b);
    printf("  appointments: ok\n");
}

int main(void) {
    test_pets();
    test_vax_weight();
    test_appointments();
    printf("test_pets: all assertions passed\n");
    return 0;
}
