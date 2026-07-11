/*
 * test_healthcare.c — CircleAI.Healthcare (C11 port) verification against the C#
 * reference (HealthcarePrimitives.cs).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_hc_patient_t mk_patient(const char *id, const char *name, int64_t dob) {
    ca_hc_patient_t p; memset(&p, 0, sizeof(p));
    p.patient_id = (char *)id; p.name = (char *)name; p.date_of_birth_ms = dob;
    return p;
}
static ca_hc_appointment_t mk_appt(const char *id, const char *pid,
                                   const char *prov, int64_t at, const char *st) {
    ca_hc_appointment_t a; memset(&a, 0, sizeof(a));
    a.appt_id = (char *)id; a.patient_id = (char *)pid; a.provider = (char *)prov;
    a.at_utc_ms = at; a.status = (char *)st;
    return a;
}
static ca_hc_prescription_t mk_rx(const char *id, const char *pid,
                                  const char *med, int64_t at) {
    ca_hc_prescription_t r; memset(&r, 0, sizeof(r));
    r.rx_id = (char *)id; r.patient_id = (char *)pid; r.medication_name = (char *)med;
    r.dose = (char *)"10mg"; r.frequency = (char *)"daily"; r.prescribed_utc_ms = at;
    return r;
}

static void test_patients(void) {
    ca_hc_board_t *b = ca_hc_board_create();
    assert(b);

    assert(ca_hc_board_register(b, NULL) == -1);   /* ArgumentNullException */
    ca_hc_patient_t p1 = mk_patient("p1", "Ada", 100);
    assert(ca_hc_board_register(b, &p1) == 0);

    ca_hc_patient_t got;
    assert(ca_hc_board_get_patient(b, "p1", &got));
    assert(strcmp(got.name, "Ada") == 0 && got.date_of_birth_ms == 100);
    ca_hc_patient_free(&got);
    assert(!ca_hc_board_get_patient(b, "none", &got));

    /* register with same id replaces. */
    ca_hc_patient_t p1b = mk_patient("p1", "Ada Lovelace", 200);
    assert(ca_hc_board_register(b, &p1b) == 0);
    assert(ca_hc_board_get_patient(b, "p1", &got));
    assert(strcmp(got.name, "Ada Lovelace") == 0);
    ca_hc_patient_free(&got);

    ca_hc_board_destroy(b);
    printf("  patients: ok\n");
}

static void test_appointments(void) {
    ca_hc_board_t *b = ca_hc_board_create();

    /* UpdateStatus on unknown -> 1 (InvalidOperationException). */
    assert(ca_hc_board_update_status(b, "nope", "done") == 1);

    ca_hc_appointment_t a1 = mk_appt("a1", "p1", "Dr A", 300, "booked");
    ca_hc_appointment_t a2 = mk_appt("a2", "p1", "Dr B", 100, "booked");
    ca_hc_appointment_t a3 = mk_appt("a3", "p2", "Dr C", 200, "booked");
    assert(ca_hc_board_schedule(b, &a1) == 0);
    assert(ca_hc_board_schedule(b, &a2) == 0);
    assert(ca_hc_board_schedule(b, &a3) == 0);

    /* AppointmentsFor(p1) ordered by AtUtc ascending: a2(100), a1(300). */
    size_t n = 0;
    ca_hc_appointment_t *arr = ca_hc_board_appointments_for(b, "p1", &n);
    assert(n == 2);
    assert(strcmp(arr[0].appt_id, "a2") == 0);
    assert(strcmp(arr[1].appt_id, "a1") == 0);
    ca_hc_appointment_free_array(arr, n);

    /* UpdateStatus flips it. */
    assert(ca_hc_board_update_status(b, "a1", "completed") == 0);
    arr = ca_hc_board_appointments_for(b, "p1", &n);
    assert(n == 2 && strcmp(arr[1].appt_id, "a1") == 0 &&
           strcmp(arr[1].status, "completed") == 0);
    ca_hc_appointment_free_array(arr, n);

    /* no appointments for unknown patient. */
    arr = ca_hc_board_appointments_for(b, "zzz", &n);
    assert(n == 0 && arr == NULL);

    ca_hc_board_destroy(b);
    printf("  appointments: ok\n");
}

static void test_prescriptions(void) {
    ca_hc_board_t *b = ca_hc_board_create();

    ca_hc_prescription_t r1 = mk_rx("r1", "p1", "Aspirin", 100);
    ca_hc_prescription_t r2 = mk_rx("r2", "p1", "Ibuprofen", 300);
    ca_hc_prescription_t r3 = mk_rx("r3", "p2", "Other", 200);
    assert(ca_hc_board_prescribe(b, &r1) == 0);
    assert(ca_hc_board_prescribe(b, &r2) == 0);
    assert(ca_hc_board_prescribe(b, &r3) == 0);

    /* PrescriptionsFor(p1) ordered by PrescribedUtc descending: r2(300), r1(100). */
    size_t n = 0;
    ca_hc_prescription_t *arr = ca_hc_board_prescriptions_for(b, "p1", &n);
    assert(n == 2);
    assert(strcmp(arr[0].rx_id, "r2") == 0);
    assert(strcmp(arr[1].rx_id, "r1") == 0);
    assert(strcmp(arr[0].dose, "10mg") == 0 && strcmp(arr[0].frequency, "daily") == 0);
    ca_hc_prescription_free_array(arr, n);

    arr = ca_hc_board_prescriptions_for(b, "zzz", &n);
    assert(n == 0 && arr == NULL);

    ca_hc_board_destroy(b);
    printf("  prescriptions: ok\n");
}

int main(void) {
    test_patients();
    test_appointments();
    test_prescriptions();
    printf("test_healthcare: all assertions passed\n");
    return 0;
}
