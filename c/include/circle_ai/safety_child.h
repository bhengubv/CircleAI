#ifndef CIRCLE_AI_SAFETY_CHILD_H
#define CIRCLE_AI_SAFETY_CHILD_H

/*
 * safety_child.h — CircleAI.Safety.Child domain primitives (C11 port).
 *
 * Ports CircleAI.Safety.Child ChildSafetyPrimitives.cs:
 *   - TrustedAdult / Geofence / CheckIn records
 *   - IChildSafetyBoard + InMemoryChildSafetyBoard
 *
 * The board holds a trusted-adult ring (ordered by RingPriority ascending),
 * geofences (keyed by FenceId, last-write-wins) with a Haversine
 * inside-any-fence test, and check-in events (RecentCheckIns most-recent-first,
 * limited).
 *
 * Conventions: ca_ prefix, _t types, opaque board handle, strdup'd owning
 * fields with matching *_free, deep-copy getters, arrays are fresh copies the
 * caller frees. Errors surface via NULL + count=SIZE_MAX.
 *
 * Pure C11 + libc (needs libm for Haversine).
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── Records ────────────────────────────────────────────────────────────── */

typedef struct {
    char *adult_id;      /* owned */
    char *name;          /* owned */
    char *phone;         /* owned */
    char *relationship;  /* owned */
    int   ring_priority;
} ca_trusted_adult_t;

typedef struct {
    char  *fence_id;      /* owned */
    char  *name;          /* owned */
    double centre_lat;
    double centre_lon;
    double radius_meters;
} ca_geofence_t;

typedef struct {
    char   *child_id;   /* owned */
    char   *status;     /* owned */
    bool    has_lat;
    double  lat;        /* valid iff has_lat */
    bool    has_lon;
    double  lon;        /* valid iff has_lon */
    int64_t at_utc_ms;  /* Unix ms UTC */
} ca_check_in_t;

void ca_trusted_adult_free(ca_trusted_adult_t *a);
void ca_trusted_adult_free_array(ca_trusted_adult_t *arr, size_t count);
ca_trusted_adult_t *ca_trusted_adult_copy(ca_trusted_adult_t *dst,
                                          const ca_trusted_adult_t *src);

void ca_geofence_free(ca_geofence_t *g);
ca_geofence_t *ca_geofence_copy(ca_geofence_t *dst, const ca_geofence_t *src);

void ca_check_in_free(ca_check_in_t *c);
void ca_check_in_free_array(ca_check_in_t *arr, size_t count);
ca_check_in_t *ca_check_in_copy(ca_check_in_t *dst, const ca_check_in_t *src);

/* ── IChildSafetyBoard + InMemoryChildSafetyBoard ───────────────────────── */

typedef struct ca_child_safety_board ca_child_safety_board_t;

ca_child_safety_board_t *ca_child_safety_board_create(void);
void                     ca_child_safety_board_destroy(ca_child_safety_board_t *board);

/* AddAdult — inserts/replaces by AdultId (last-write-wins). Returns false on
 * NULL board/adult. */
bool ca_child_safety_board_add_adult(ca_child_safety_board_t *board,
                                     const ca_trusted_adult_t *a);

/* RingOrdered — adults ordered by RingPriority ascending (stable for ties).
 * Fresh array (caller frees with ca_trusted_adult_free_array). NULL board →
 * *out_count SIZE_MAX + NULL. */
ca_trusted_adult_t *ca_child_safety_board_ring_ordered(ca_child_safety_board_t *board,
                                                       size_t *out_count);

/* DefineGeofence — inserts/replaces by FenceId (last-write-wins). Returns false
 * on NULL board/fence. */
bool ca_child_safety_board_define_geofence(ca_child_safety_board_t *board,
                                           const ca_geofence_t *g);

/* GetGeofence — deep copy of the fence with id into *out (true), or false when
 * absent / board is NULL / id is NULL. Caller frees *out with ca_geofence_free. */
bool ca_child_safety_board_get_geofence(ca_child_safety_board_t *board,
                                        const char *id, ca_geofence_t *out);

/* IsInsideAnyFence — true iff (lat,lon) is within RadiusMeters of any fence
 * centre (Haversine). NULL board → false. */
bool ca_child_safety_board_is_inside_any_fence(ca_child_safety_board_t *board,
                                               double lat, double lon);

/* RecordCheckIn — appends a deep copy. Returns false on NULL board/check-in. */
bool ca_child_safety_board_record_check_in(ca_child_safety_board_t *board,
                                           const ca_check_in_t *c);

/* RecentCheckIns — up to `limit` check-ins for child_id, AtUtc descending.
 * Fresh array (caller frees with ca_check_in_free_array). *out_count receives
 * the count (0 → NULL). limit<=0 → *out_count SIZE_MAX + NULL (C# throws
 * ArgumentOutOfRangeException). NULL board / NULL child_id → SIZE_MAX + NULL. */
ca_check_in_t *ca_child_safety_board_recent_check_ins(ca_child_safety_board_t *board,
                                                      const char *child_id, int limit,
                                                      size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SAFETY_CHILD_H */
