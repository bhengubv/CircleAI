#ifndef CIRCLE_AI_HEALTHCARE_H
#define CIRCLE_AI_HEALTHCARE_H

/*
 * healthcare.h — CircleAI.Healthcare (C11 port of HealthcarePrimitives.cs).
 *
 * Ports the healthcare domain board 1:1:
 *
 *   Records : Patient(PatientId, Name, DateOfBirth);
 *             HealthAppointment(ApptId, PatientId, Provider, AtUtc, Status);
 *             Prescription(RxId, PatientId, MedicationName, Dose, Frequency,
 *                          PrescribedUtc).
 *   Board   : IHealthcareBoard -> InMemoryHealthcareBoard.
 *             Register(patient) (PatientId keyed set), GetPatient(id) -> patient?,
 *             Schedule(appt) (ApptId keyed set), UpdateStatus(apptId, status)
 *             (throws on unknown appointment), AppointmentsFor(patientId) ordered
 *             by AtUtc ascending, Prescribe(rx) (RxId keyed set),
 *             PrescriptionsFor(patientId) ordered by PrescribedUtc descending.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. DateOfBirth (DateTime) and AtUtc /
 * PrescribedUtc (DateTimeOffset) are carried as int64 Unix ms UTC for ordering.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Patient(PatientId, Name, DateTime DateOfBirth). */
typedef struct {
    char   *patient_id;      /* owned, non-null */
    char   *name;            /* owned, non-null */
    int64_t date_of_birth_ms;/* DateTime as Unix ms UTC */
} ca_hc_patient_t;

void ca_hc_patient_free(ca_hc_patient_t *p);

/* HealthAppointment(ApptId, PatientId, Provider, DateTimeOffset AtUtc, Status). */
typedef struct {
    char   *appt_id;         /* owned, non-null */
    char   *patient_id;      /* owned, non-null */
    char   *provider;        /* owned, non-null */
    int64_t at_utc_ms;       /* DateTimeOffset as Unix ms UTC */
    char   *status;          /* owned, non-null */
} ca_hc_appointment_t;

void ca_hc_appointment_free(ca_hc_appointment_t *a);
void ca_hc_appointment_free_array(ca_hc_appointment_t *arr, size_t count);

/* Prescription(RxId, PatientId, MedicationName, Dose, Frequency,
 * DateTimeOffset PrescribedUtc). */
typedef struct {
    char   *rx_id;           /* owned, non-null */
    char   *patient_id;      /* owned, non-null */
    char   *medication_name; /* owned, non-null */
    char   *dose;            /* owned, non-null */
    char   *frequency;       /* owned, non-null */
    int64_t prescribed_utc_ms;/* DateTimeOffset as Unix ms UTC */
} ca_hc_prescription_t;

void ca_hc_prescription_free(ca_hc_prescription_t *r);
void ca_hc_prescription_free_array(ca_hc_prescription_t *arr, size_t count);

typedef struct ca_hc_board ca_hc_board_t;

/* InMemoryHealthcareBoard(). NULL on OOM. */
ca_hc_board_t *ca_hc_board_create(void);
void ca_hc_board_destroy(ca_hc_board_t *b);

/* Register(patient) — deep-copies; PatientId keys the store (replace on repeat).
 * 0 on success, -1 on bad args / OOM (ArgumentNullException -> -1). */
int ca_hc_board_register(ca_hc_board_t *b, const ca_hc_patient_t *p);

/* GetPatient(id) -> fresh owned copy into *out, true; false (C# null) on miss. */
bool ca_hc_board_get_patient(const ca_hc_board_t *b, const char *id,
                             ca_hc_patient_t *out);

/* Schedule(appt) — deep-copies; ApptId keys the store. 0 / -1. */
int ca_hc_board_schedule(ca_hc_board_t *b, const ca_hc_appointment_t *a);

/* UpdateStatus(apptId, status). Returns 0 on success, -1 on bad args/OOM, and 1
 * when the appointment is unknown (InvalidOperationException in C#). */
int ca_hc_board_update_status(ca_hc_board_t *b, const char *appt_id,
                              const char *status);

/* AppointmentsFor(patientId) -> fresh owned array (*out_count) ordered by AtUtc
 * ascending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_hc_appointment_t *ca_hc_board_appointments_for(const ca_hc_board_t *b,
                                                  const char *patient_id,
                                                  size_t *out_count);

/* Prescribe(rx) — deep-copies; RxId keys the store. 0 / -1. */
int ca_hc_board_prescribe(ca_hc_board_t *b, const ca_hc_prescription_t *r);

/* PrescriptionsFor(patientId) -> fresh owned array (*out_count) ordered by
 * PrescribedUtc descending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_hc_prescription_t *ca_hc_board_prescriptions_for(const ca_hc_board_t *b,
                                                    const char *patient_id,
                                                    size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HEALTHCARE_H */
