#ifndef CIRCLE_AI_AGRICULTURE_H
#define CIRCLE_AI_AGRICULTURE_H

/*
 * agriculture.h — CircleAI.Agriculture (C11 port of AgriculturePrimitives.cs).
 *
 *   Records : Field(FieldId, double AreaHa, SoilType, IrrigationKind);
 *             Crop(CropId, FieldId, Variety, DateTime PlantedOn,
 *                  DateTime? ExpectedHarvest);
 *             YieldRecord(CropId, double TonsPerHa, DateTime HarvestedOn).
 *   Board   : IFarmBoard -> InMemoryFarmBoard
 *               AddField (FieldId keyed), Plant (CropId keyed), RecordYield
 *               (appends), GetField(id), CropsForField(fieldId) ordered by
 *               PlantedOn asc, AvgYieldOfVariety(variety) — mean TonsPerHa over
 *               yields whose crop's Variety matches (OrdinalIgnoreCase); 0.0 when
 *               none.
 *
 * DateTime as Unix ms UTC. ExpectedHarvest optional via has_expected_harvest.
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

/* Field(FieldId, double AreaHa, SoilType, IrrigationKind). */
typedef struct {
    char   *field_id;         /* owned, non-null */
    double  area_ha;
    char   *soil_type;        /* owned, non-null */
    char   *irrigation_kind;  /* owned, non-null */
} ca_farm_field_t;

void ca_farm_field_free(ca_farm_field_t *f);

/* Crop(CropId, FieldId, Variety, DateTime PlantedOn, DateTime? ExpectedHarvest). */
typedef struct {
    char   *crop_id;             /* owned, non-null */
    char   *field_id;            /* owned, non-null */
    char   *variety;             /* owned, non-null */
    int64_t planted_on_ms;
    bool    has_expected_harvest; /* false == C# null ExpectedHarvest */
    int64_t expected_harvest_ms;  /* valid only when has_expected_harvest */
} ca_farm_crop_t;

void ca_farm_crop_free(ca_farm_crop_t *c);
void ca_farm_crop_free_array(ca_farm_crop_t *arr, size_t count);

/* YieldRecord(CropId, double TonsPerHa, DateTime HarvestedOn). */
typedef struct {
    char   *crop_id;          /* owned, non-null */
    double  tons_per_ha;
    int64_t harvested_on_ms;
} ca_farm_yield_t;

void ca_farm_yield_free(ca_farm_yield_t *y);

typedef struct ca_farm_board ca_farm_board_t;

ca_farm_board_t *ca_farm_board_create(void); /* NULL on OOM */
void ca_farm_board_destroy(ca_farm_board_t *b);

/* AddField(f) — FieldId keyed set. 0 / -1. */
int ca_farm_board_add_field(ca_farm_board_t *b, const ca_farm_field_t *f);

/* Plant(c) — CropId keyed set. 0 / -1. */
int ca_farm_board_plant(ca_farm_board_t *b, const ca_farm_crop_t *c);

/* RecordYield(y) — appends. 0 / -1. */
int ca_farm_board_record_yield(ca_farm_board_t *b, const ca_farm_yield_t *y);

/* GetField(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_farm_board_get_field(const ca_farm_board_t *b, const char *id,
                             ca_farm_field_t *out);

/* CropsForField(fieldId) -> fresh owned array ordered by PlantedOn asc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_farm_crop_t *ca_farm_board_crops_for_field(const ca_farm_board_t *b,
                                              const char *field_id,
                                              size_t *out_count);

/* AvgYieldOfVariety(variety) — mean TonsPerHa over yields whose crop matches
 * `variety` (OrdinalIgnoreCase); 0.0 when there are none. */
double ca_farm_board_avg_yield_of_variety(const ca_farm_board_t *b,
                                          const char *variety);

/* FieldCount — number of registered fields. NULL board → 0. */
size_t ca_farm_board_field_count(const ca_farm_board_t *b);

/* RemoveField(fieldId) — drop a field by id. Returns true if it was present. */
bool ca_farm_board_remove_field(ca_farm_board_t *b, const char *field_id);

/* TotalAreaHa() — sum of AreaHa across every field. */
double ca_farm_board_total_area_ha(const ca_farm_board_t *b);

/* FieldsBySoil(soilType) -> fresh owned array of fields whose SoilType matches
 * (OrdinalIgnoreCase), ordered by AreaHa descending. NULL + 0 empty; NULL +
 * SIZE_MAX on error. Caller frees each field + the block. */
ca_farm_field_t *ca_farm_board_fields_by_soil(const ca_farm_board_t *b,
                                              const char *soil_type,
                                              size_t *out_count);

/* DueForHarvest(asOf) -> fresh owned array of crops whose ExpectedHarvest is set
 * and <= asOf (Unix ms UTC), ordered by ExpectedHarvest ascending. NULL + 0
 * empty; NULL + SIZE_MAX on error. */
ca_farm_crop_t *ca_farm_board_due_for_harvest(const ca_farm_board_t *b,
                                              int64_t as_of_ms,
                                              size_t *out_count);

/* BestYieldingVariety() -> owned string naming the variety with the highest mean
 * TonsPerHa across yields whose crop still exists (grouped OrdinalIgnoreCase, the
 * first-seen spelling wins; ties keep first-appearance order). NULL when there
 * are no such yields, or on OOM. Caller frees with free(). */
char *ca_farm_board_best_yielding_variety(const ca_farm_board_t *b);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AGRICULTURE_H */
