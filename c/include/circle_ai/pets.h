#ifndef CIRCLE_AI_PETS_H
#define CIRCLE_AI_PETS_H

/*
 * pets.h — CircleAI.Pets (C11 port of PetsPrimitives.cs).
 *
 *   Records : Pet(PetId, Name, Species, string? Breed, DateTime DateOfBirth);
 *             Vaccination(PetId, Vaccine, DateTimeOffset AdministeredUtc,
 *                         DateTimeOffset? BoosterDueUtc);
 *             WeightSample(PetId, double WeightKg, DateTimeOffset AtUtc);
 *             VetAppointment(ApptId, PetId, Reason, DateTimeOffset AtUtc, Vet).
 *   Board   : IPetsBoard -> InMemoryPetsBoard
 *               Add (PetId keyed), GetPet(id) -> pet?, Pets ordered by Name asc,
 *               RecordVaccination (appends), VaccinationsFor(petId) newest-first
 *               by AdministeredUtc, RecordWeight (appends), WeightHistory(petId)
 *               ascending by AtUtc, Schedule (ApptId keyed),
 *               UpcomingAppointments() where AtUtc >= now, ordered by AtUtc asc.
 *
 * The C# UpcomingAppointments reads DateTimeOffset.UtcNow; to stay deterministic
 * the port takes an explicit now_ms reference (as Retail's RevenueToday takes now).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * Breed / BoosterDueUtc via has_*. *Utc / DateOfBirth as int64 Unix ms UTC. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Pet(PetId, Name, Species, string? Breed, DateTime DateOfBirth). */
typedef struct {
    char   *pet_id;    /* owned, non-null */
    char   *name;      /* owned, non-null */
    char   *species;   /* owned, non-null */
    bool    has_breed; /* false == C# null Breed */
    char   *breed;     /* owned, valid only when has_breed */
    int64_t date_of_birth_ms;
} ca_pet_t;

void ca_pet_free(ca_pet_t *p);
void ca_pet_free_array(ca_pet_t *arr, size_t count);

/* Vaccination(PetId, Vaccine, DateTimeOffset AdministeredUtc,
 * DateTimeOffset? BoosterDueUtc). */
typedef struct {
    char   *pet_id;          /* owned, non-null */
    char   *vaccine;         /* owned, non-null */
    int64_t administered_utc_ms;
    bool    has_booster_due; /* false == C# null BoosterDueUtc */
    int64_t booster_due_utc_ms; /* valid only when has_booster_due */
} ca_pet_vaccination_t;

void ca_pet_vaccination_free(ca_pet_vaccination_t *v);
void ca_pet_vaccination_free_array(ca_pet_vaccination_t *arr, size_t count);

/* WeightSample(PetId, double WeightKg, DateTimeOffset AtUtc). */
typedef struct {
    char   *pet_id;    /* owned, non-null */
    double  weight_kg;
    int64_t at_utc_ms;
} ca_pet_weight_t;

void ca_pet_weight_free(ca_pet_weight_t *w);
void ca_pet_weight_free_array(ca_pet_weight_t *arr, size_t count);

/* VetAppointment(ApptId, PetId, Reason, DateTimeOffset AtUtc, Vet). */
typedef struct {
    char   *appt_id;   /* owned, non-null */
    char   *pet_id;    /* owned, non-null */
    char   *reason;    /* owned, non-null */
    int64_t at_utc_ms;
    char   *vet;       /* owned, non-null */
} ca_pet_appointment_t;

void ca_pet_appointment_free(ca_pet_appointment_t *a);
void ca_pet_appointment_free_array(ca_pet_appointment_t *arr, size_t count);

typedef struct ca_pet_board ca_pet_board_t;

ca_pet_board_t *ca_pet_board_create(void); /* NULL on OOM */
void ca_pet_board_destroy(ca_pet_board_t *b);

/* Add(p) — PetId keyed set. 0 / -1 on bad args/OOM. */
int ca_pet_board_add(ca_pet_board_t *b, const ca_pet_t *p);

/* GetPet(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_pet_board_get_pet(const ca_pet_board_t *b, const char *id,
                          ca_pet_t *out);

/* Pets -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_pet_t *ca_pet_board_pets(const ca_pet_board_t *b, size_t *out_count);

/* RecordVaccination(v) — appends. 0 / -1. */
int ca_pet_board_record_vaccination(ca_pet_board_t *b,
                                    const ca_pet_vaccination_t *v);

/* VaccinationsFor(petId) -> fresh owned array (*out_count) newest-first by
 * AdministeredUtc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_pet_vaccination_t *ca_pet_board_vaccinations_for(const ca_pet_board_t *b,
                                                    const char *pet_id,
                                                    size_t *out_count);

/* RecordWeight(s) — appends. 0 / -1. */
int ca_pet_board_record_weight(ca_pet_board_t *b, const ca_pet_weight_t *s);

/* WeightHistory(petId) -> fresh owned array (*out_count) ascending by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_pet_weight_t *ca_pet_board_weight_history(const ca_pet_board_t *b,
                                             const char *pet_id,
                                             size_t *out_count);

/* Schedule(a) — ApptId keyed set. 0 / -1. */
int ca_pet_board_schedule(ca_pet_board_t *b, const ca_pet_appointment_t *a);

/* UpcomingAppointments(now_ms) -> fresh owned array (*out_count): AtUtc >= now,
 * ordered by AtUtc asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_pet_appointment_t *ca_pet_board_upcoming_appointments(const ca_pet_board_t *b,
                                                         int64_t now_ms,
                                                         size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PETS_H */
