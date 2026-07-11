#ifndef CIRCLE_AI_PERSONAL_HEALTH_H
#define CIRCLE_AI_PERSONAL_HEALTH_H

/*
 * personal_health.h — CircleAI.Personal.Health (C11 port of
 * PersonalHealthPrimitives.cs). Per-user vitals / allergies / medications board.
 *
 *   Enum    : VitalKind { BloodPressureSystolic, BloodPressureDiastolic,
 *             GlucoseMgDl, WeightKg, HeartRateBpm, TemperatureC, OxygenPct,
 *             StepsCount }.
 *   Records : VitalReading(Kind, double Value, AtUtc, Note?);
 *             Allergy(AllergyId, Substance, Severity);
 *             Medication(MedId, Name, Dose, Frequency, StartedAtUtc, EndedAtUtc?).
 *   Board   : IPersonalHealthBoard -> InMemoryPersonalHealthBoard.
 *             Record(v) (appended list), ReadSince(kind, since) (Kind==kind &&
 *             AtUtc >= since, ordered by AtUtc ascending), Latest(kind) (newest
 *             of that kind, or none), AddAllergy(a) (AllergyId keyed set),
 *             Allergies (insertion order), AddMedication(m) (MedId keyed set),
 *             EndMedication(medId, endedAtUtc) (throws on unknown; sets
 *             EndedAtUtc), ActiveMedications() (EndedAtUtc null, ordered by Name
 *             ascending Ordinal).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. AtUtc /
 * StartedAtUtc / EndedAtUtc as int64 Unix ms UTC. Note optional (has_note gate);
 * EndedAtUtc optional (has_ended gate). Linear arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_VITAL_BLOOD_PRESSURE_SYSTOLIC  = 0,
    CA_VITAL_BLOOD_PRESSURE_DIASTOLIC = 1,
    CA_VITAL_GLUCOSE_MG_DL            = 2,
    CA_VITAL_WEIGHT_KG                = 3,
    CA_VITAL_HEART_RATE_BPM           = 4,
    CA_VITAL_TEMPERATURE_C            = 5,
    CA_VITAL_OXYGEN_PCT               = 6,
    CA_VITAL_STEPS_COUNT              = 7
} ca_vital_kind_t;

/* VitalReading(VitalKind Kind, double Value, DateTimeOffset AtUtc, string? Note). */
typedef struct {
    ca_vital_kind_t kind;
    double  value;
    int64_t at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
    bool    has_note;   /* false == C# null Note */
    char   *note;       /* owned, valid only when has_note */
} ca_phealth_vital_t;

void ca_phealth_vital_free(ca_phealth_vital_t *v);
void ca_phealth_vital_free_array(ca_phealth_vital_t *arr, size_t count);

/* Allergy(AllergyId, Substance, Severity). */
typedef struct {
    char *allergy_id;  /* owned, non-null */
    char *substance;   /* owned, non-null */
    char *severity;    /* owned, non-null */
} ca_phealth_allergy_t;

void ca_phealth_allergy_free(ca_phealth_allergy_t *a);
void ca_phealth_allergy_free_array(ca_phealth_allergy_t *arr, size_t count);

/* Medication(MedId, Name, Dose, Frequency, DateTimeOffset StartedAtUtc,
 * DateTimeOffset? EndedAtUtc). */
typedef struct {
    char   *med_id;         /* owned, non-null */
    char   *name;           /* owned, non-null */
    char   *dose;           /* owned, non-null */
    char   *frequency;      /* owned, non-null */
    int64_t started_at_utc_ms;
    bool    has_ended;      /* false == C# null EndedAtUtc (i.e. active) */
    int64_t ended_at_utc_ms;/* valid only when has_ended */
} ca_phealth_medication_t;

void ca_phealth_medication_free(ca_phealth_medication_t *m);
void ca_phealth_medication_free_array(ca_phealth_medication_t *arr, size_t count);

typedef struct ca_phealth_board ca_phealth_board_t;

/* InMemoryPersonalHealthBoard(). NULL on OOM. */
ca_phealth_board_t *ca_phealth_board_create(void);
void ca_phealth_board_destroy(ca_phealth_board_t *b);

/* Record(v) — deep-copies; appended list. 0 / -1 on bad args/OOM. */
int ca_phealth_board_record(ca_phealth_board_t *b, const ca_phealth_vital_t *v);
/* ReadSince(kind, since_ms) -> fresh owned array (*out_count): Kind==kind &&
 * AtUtc >= since_ms, ordered by AtUtc ascending. NULL + 0 when empty;
 * NULL + SIZE_MAX on error. */
ca_phealth_vital_t *ca_phealth_board_read_since(const ca_phealth_board_t *b,
                                                ca_vital_kind_t kind,
                                                int64_t since_ms,
                                                size_t *out_count);
/* Latest(kind) -> newest reading of that kind into *out, true; false (C# null)
 * when none. */
bool ca_phealth_board_latest(const ca_phealth_board_t *b, ca_vital_kind_t kind,
                             ca_phealth_vital_t *out);

/* AddAllergy(a) — deep-copies; AllergyId keyed set. 0 / -1. */
int ca_phealth_board_add_allergy(ca_phealth_board_t *b,
                                 const ca_phealth_allergy_t *a);
/* Allergies -> fresh owned array (*out_count) in insertion order. NULL + 0 when
 * empty; NULL + SIZE_MAX on error. */
ca_phealth_allergy_t *ca_phealth_board_allergies(const ca_phealth_board_t *b,
                                                 size_t *out_count);

/* AddMedication(m) — deep-copies; MedId keyed set. 0 / -1. */
int ca_phealth_board_add_medication(ca_phealth_board_t *b,
                                    const ca_phealth_medication_t *m);
/* EndMedication(medId, endedAtUtc_ms). 0 on success, -1 on bad args, 1 when the
 * medication is unknown (InvalidOperationException). Sets EndedAtUtc. */
int ca_phealth_board_end_medication(ca_phealth_board_t *b, const char *med_id,
                                    int64_t ended_at_utc_ms);
/* ActiveMedications() -> fresh owned array (*out_count): EndedAtUtc null, ordered
 * by Name ascending (Ordinal). NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_phealth_medication_t *ca_phealth_board_active_medications(
    const ca_phealth_board_t *b, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PERSONAL_HEALTH_H */
