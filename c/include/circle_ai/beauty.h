#ifndef CIRCLE_AI_BEAUTY_H
#define CIRCLE_AI_BEAUTY_H

/*
 * beauty.h — CircleAI.Beauty (C11 port of BeautyPrimitives.cs).
 *
 *   Records : Treatment(TreatmentId, Name, int DurationMinutes, decimal Price,
 *                       Currency);
 *             Appointment(ApptId, ClientName, TreatmentId, DateTimeOffset AtUtc,
 *                       string? Notes);
 *             SkinProfile(ClientName, SkinType, IReadOnlyList<string> Concerns).
 *   Board   : IBeautyBoard -> InMemoryBeautyBoard
 *               AddTreatment (TreatmentId keyed), GetTreatment(id), Book (appends),
 *               AppointmentsBetween(start, end) inclusive, ordered by AtUtc asc,
 *               SaveProfile (ClientName keyed), GetProfile(clientName),
 *               RecommendFor(clientName) — treatments whose Name contains any of
 *               the client's Concerns (OrdinalIgnoreCase); empty when no profile.
 *
 * decimal Price via ca_decimal_t (int64 micro-units). DateTimeOffset as Unix ms
 * UTC. Notes optional via has_notes.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_beauty_decimal_t; /* micro-units (1e-6) */
#define CA_BEAUTY_DECIMAL_SCALE 1000000LL

/* Treatment(TreatmentId, Name, int DurationMinutes, decimal Price, Currency). */
typedef struct {
    char   *treatment_id;      /* owned, non-null */
    char   *name;              /* owned, non-null */
    int     duration_minutes;
    ca_beauty_decimal_t price; /* micro-units */
    char   *currency;          /* owned, non-null */
} ca_beauty_treatment_t;

void ca_beauty_treatment_free(ca_beauty_treatment_t *t);
void ca_beauty_treatment_free_array(ca_beauty_treatment_t *arr, size_t count);

/* Appointment(ApptId, ClientName, TreatmentId, DateTimeOffset AtUtc,
 * string? Notes). */
typedef struct {
    char   *appt_id;           /* owned, non-null */
    char   *client_name;       /* owned, non-null */
    char   *treatment_id;      /* owned, non-null */
    int64_t at_utc_ms;
    bool    has_notes;         /* false == C# null Notes */
    char   *notes;             /* owned, valid only when has_notes */
} ca_beauty_appointment_t;

void ca_beauty_appointment_free(ca_beauty_appointment_t *a);
void ca_beauty_appointment_free_array(ca_beauty_appointment_t *arr, size_t count);

/* SkinProfile(ClientName, SkinType, IReadOnlyList<string> Concerns). */
typedef struct {
    char   *client_name;       /* owned, non-null */
    char   *skin_type;         /* owned, non-null */
    char  **concerns;          /* owned array of owned strings (may be NULL if 0) */
    size_t  concern_count;
} ca_beauty_skin_profile_t;

void ca_beauty_skin_profile_free(ca_beauty_skin_profile_t *p);

typedef struct ca_beauty_board ca_beauty_board_t;

ca_beauty_board_t *ca_beauty_board_create(void); /* NULL on OOM */
void ca_beauty_board_destroy(ca_beauty_board_t *b);

/* AddTreatment(t) — TreatmentId keyed set. 0 / -1. */
int ca_beauty_board_add_treatment(ca_beauty_board_t *b,
                                  const ca_beauty_treatment_t *t);

/* GetTreatment(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_beauty_board_get_treatment(const ca_beauty_board_t *b, const char *id,
                                   ca_beauty_treatment_t *out);

/* Book(a) — appends. 0 / -1. */
int ca_beauty_board_book(ca_beauty_board_t *b, const ca_beauty_appointment_t *a);

/* AppointmentsBetween(start_ms, end_ms) -> fresh owned array (AtUtc in
 * [start, end]) ordered by AtUtc asc. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_beauty_appointment_t *ca_beauty_board_appointments_between(
    const ca_beauty_board_t *b, int64_t start_ms, int64_t end_ms,
    size_t *out_count);

/* SaveProfile(p) — ClientName keyed set. 0 / -1. */
int ca_beauty_board_save_profile(ca_beauty_board_t *b,
                                 const ca_beauty_skin_profile_t *p);

/* GetProfile(clientName) -> fresh owned copy into *out, true; false on miss. */
bool ca_beauty_board_get_profile(const ca_beauty_board_t *b,
                                 const char *client_name,
                                 ca_beauty_skin_profile_t *out);

/* RecommendFor(clientName) -> fresh owned array (insertion order) of treatments
 * whose Name contains any Concern (OrdinalIgnoreCase). Empty when no profile.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_beauty_treatment_t *ca_beauty_board_recommend_for(const ca_beauty_board_t *b,
                                                     const char *client_name,
                                                     size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BEAUTY_H */
