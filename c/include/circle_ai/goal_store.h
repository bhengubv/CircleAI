#ifndef CIRCLE_AI_GOAL_STORE_H
#define CIRCLE_AI_GOAL_STORE_H

/*
 * goal_store.h — CircleAI.Memory Goal + IGoalStore (C11 port).
 *
 * Ports the full C# Goal record (Goal.cs) and IGoalStore + InMemoryGoalStore.
 *
 * NB: distinct from models.h's fixture ca_goal_t (id/description/status/progress
 * only). This is the rich record the store persists — Id, UserId, Title,
 * Description, Status, Priority, CreatedUtc, DueUtc?, CompletedUtc?, Notes?,
 * plus the mutable Progress + AdvanceProgress. GoalStatus mirrors the fixture
 * enum values; GoalPriority is new here.
 *
 * Conventions: ca_ prefix, _t types, opaque store handle, strdup'd owning
 * fields with matching *_free, returned arrays are deep copies the caller
 * frees. Nullable DateTimeOffset uses a has_* flag; nullable string uses NULL.
 *
 * Pure C11 + libc.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* GoalStatus (same order + values as models.h ca_goal_status_t / Goal.cs). */
typedef enum {
    CA_GOAL_STATUS_ACTIVE    = 0,
    CA_GOAL_STATUS_COMPLETED = 1,
    CA_GOAL_STATUS_ABANDONED = 2
} ca_goal_status_t2;

/* GoalPriority (Goal.cs). */
typedef enum {
    CA_GOAL_PRIORITY_LOW    = 0,
    CA_GOAL_PRIORITY_NORMAL = 1,
    CA_GOAL_PRIORITY_HIGH   = 2
} ca_goal_priority_t;

/* Full Goal record. */
typedef struct {
    char             *id;             /* owned */
    char             *user_id;        /* owned */
    char             *title;          /* owned */
    char             *description;    /* owned */
    ca_goal_status_t2 status;
    ca_goal_priority_t priority;
    int64_t           created_utc_ms; /* Unix ms UTC */
    bool              has_due_utc;
    int64_t           due_utc_ms;     /* valid iff has_due_utc */
    bool              has_completed_utc;
    int64_t           completed_utc_ms; /* valid iff has_completed_utc */
    char             *notes;          /* owned, or NULL */
    float             progress;       /* [0.0, 1.0], default 0 */
} ca_goal_record_t;

/* Free the owned fields of a goal (does not free the struct). */
void ca_goal_record_free(ca_goal_record_t *g);
/* Free an array of goals + the array (returned deep copies). */
void ca_goal_record_free_array(ca_goal_record_t *arr, size_t count);
/* Deep-copy src into dst. Returns dst. */
ca_goal_record_t *ca_goal_record_copy(ca_goal_record_t *dst, const ca_goal_record_t *src);

/* AdvanceProgress(delta) — returns a copy with Progress = clamp(Progress+delta,
 * 0, 1). *out is a deep copy the caller frees with ca_goal_record_free. */
void ca_goal_record_advance_progress(const ca_goal_record_t *g, float delta,
                                     ca_goal_record_t *out);

/* ── IGoalStore + InMemoryGoalStore ─────────────────────────────────── */

typedef struct ca_goal_store ca_goal_store_t;

ca_goal_store_t *ca_goal_store_create(void);
void             ca_goal_store_destroy(ca_goal_store_t *store);

/* ListAsync — all goals for user_id, insertion order. Fresh array (caller frees
 * with ca_goal_record_free_array). NULL + *out_count 0 when none. Blank user_id
 * → *out_count SIZE_MAX + NULL (ArgumentException). */
ca_goal_record_t *ca_goal_store_list(ca_goal_store_t *store, const char *user_id,
                                     size_t *out_count);

/* GetAsync — deep copy of the goal with id into *out (true), or false when
 * absent. Blank id → false. */
bool ca_goal_store_get(ca_goal_store_t *store, const char *id, ca_goal_record_t *out);

/* UpsertAsync — insert/replace by Id (natural key). Deep-copies the goal in.
 * Returns false on NULL goal / NULL/blank Id. */
bool ca_goal_store_upsert(ca_goal_store_t *store, const ca_goal_record_t *goal);

/* DeleteAsync — remove by id; no-op when absent. Blank id → false. */
bool ca_goal_store_delete(ca_goal_store_t *store, const char *id);

/* GetActiveAsync — all Active goals for user_id. Fresh array (caller frees). */
ca_goal_record_t *ca_goal_store_get_active(ca_goal_store_t *store, const char *user_id,
                                           size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_GOAL_STORE_H */
