#ifndef CIRCLE_AI_BUSINESS_H
#define CIRCLE_AI_BUSINESS_H

/*
 * business.h — CircleAI.Business (C11 port of BusinessPrimitives.cs).
 *
 *   Records : BusinessUnit(UnitId, Name, ParentUnitId,
 *                          IReadOnlyList<string> KpiTags);
 *             KpiSample(UnitId, Metric, double Value, DateTimeOffset AtUtc);
 *             QuarterTarget(UnitId, Metric, int Year, int Quarter,
 *                           double Target).
 *   Board   : IBusinessBoard -> InMemoryBusinessBoard
 *               Add (UnitId keyed set), GetUnit(id) -> unit?,
 *               ChildrenOf(parentUnitId) where ParentUnitId == parent (ordinal)
 *               in insertion order, Record (appends KpiSample),
 *               LatestKpi(unitId, metric) — newest by AtUtc, NaN if none,
 *               SetTarget (keyed "unit/metric/{year}Q{quarter}"),
 *               TargetAchievement(unitId, metric, year, quarter) =
 *               LatestKpi / Target, NaN when target missing or Target == 0.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. AtUtc as
 * int64 Unix ms UTC. NaN via <math.h>. Linear arrays, no pthreads. Pure C11.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* BusinessUnit(UnitId, Name, ParentUnitId, IReadOnlyList<string> KpiTags). */
typedef struct {
    char   *unit_id;        /* owned, non-null */
    char   *name;           /* owned, non-null */
    char   *parent_unit_id; /* owned, non-null */
    char  **kpi_tags;       /* owned array (may be NULL when count 0) */
    size_t  kpi_tag_count;
} ca_biz_unit_t;

void ca_biz_unit_free(ca_biz_unit_t *u);
void ca_biz_unit_free_array(ca_biz_unit_t *arr, size_t count);

/* KpiSample(UnitId, Metric, double Value, DateTimeOffset AtUtc). */
typedef struct {
    char   *unit_id;   /* owned, non-null */
    char   *metric;    /* owned, non-null */
    double  value;
    int64_t at_utc_ms; /* DateTimeOffset as Unix ms UTC */
} ca_biz_kpi_t;

void ca_biz_kpi_free(ca_biz_kpi_t *k);

/* QuarterTarget(UnitId, Metric, int Year, int Quarter, double Target). */
typedef struct {
    char  *unit_id; /* owned, non-null */
    char  *metric;  /* owned, non-null */
    int    year;
    int    quarter;
    double target;
} ca_biz_target_t;

void ca_biz_target_free(ca_biz_target_t *t);

typedef struct ca_biz_board ca_biz_board_t;

ca_biz_board_t *ca_biz_board_create(void); /* NULL on OOM */
void ca_biz_board_destroy(ca_biz_board_t *b);

/* Add(u) — UnitId keys the store (replace). 0 / -1 on bad args/OOM. */
int ca_biz_board_add(ca_biz_board_t *b, const ca_biz_unit_t *u);

/* GetUnit(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_biz_board_get_unit(const ca_biz_board_t *b, const char *id,
                           ca_biz_unit_t *out);

/* ChildrenOf(parentUnitId) -> fresh owned array (*out_count): ParentUnitId ==
 * parent (ordinal) in insertion order. NULL + 0 when empty; NULL + SIZE_MAX on
 * error. */
ca_biz_unit_t *ca_biz_board_children_of(const ca_biz_board_t *b,
                                        const char *parent_unit_id,
                                        size_t *out_count);

/* Record(s) — appends the KpiSample. 0 / -1. */
int ca_biz_board_record(ca_biz_board_t *b, const ca_biz_kpi_t *s);

/* LatestKpi(unitId, metric) -> newest (by AtUtc) Value for (unit,metric);
 * NaN when none. */
double ca_biz_board_latest_kpi(const ca_biz_board_t *b, const char *unit_id,
                               const char *metric);

/* SetTarget(t) — keyed "{UnitId}/{Metric}/{Year}Q{Quarter}" (replace). 0 / -1. */
int ca_biz_board_set_target(ca_biz_board_t *b, const ca_biz_target_t *t);

/* TargetAchievement(unitId, metric, year, quarter) = LatestKpi / Target;
 * NaN when the target is missing or Target == 0. */
double ca_biz_board_target_achievement(const ca_biz_board_t *b,
                                       const char *unit_id, const char *metric,
                                       int year, int quarter);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BUSINESS_H */
