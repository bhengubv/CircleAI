#ifndef CIRCLE_AI_CONSTRUCTION_H
#define CIRCLE_AI_CONSTRUCTION_H

/*
 * construction.h — CircleAI.Construction (C11 port of ConstructionPrimitives.cs).
 *
 *   Records : Project(ProjectId, Name, DateTime StartOn, DateTime? EndOn,
 *                     decimal Budget, Currency);
 *             ConstructionTask(ConstructionTaskId, ProjectId, Description,
 *                     DateTime DueOn, bool Completed);
 *             CostEntry(EntryId, ProjectId, Category, decimal Amount,
 *                     DateTimeOffset AtUtc).
 *   Board   : IConstructionBoard -> InMemoryConstructionBoard
 *               Create (ProjectId keyed), GetProject(id), Add (Task keyed),
 *               Complete(taskId) (unknown throws), OpenConstructionTasksFor(
 *               projectId) — incomplete tasks ordered by DueOn asc, RecordCost
 *               (appends), SpendFor(projectId) — sum of Amount, RemainingBudget(
 *               projectId) = Budget - SpendFor (unknown project throws).
 *
 * decimal Budget/Amount via ca_decimal_t. DateTime/DateTimeOffset as Unix ms UTC.
 * EndOn optional via has_end_on.
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

typedef int64_t ca_construction_decimal_t; /* micro-units (1e-6) */
#define CA_CONSTRUCTION_DECIMAL_SCALE 1000000LL

/* Project(ProjectId, Name, DateTime StartOn, DateTime? EndOn, decimal Budget,
 * Currency). */
typedef struct {
    char   *project_id; /* owned, non-null */
    char   *name;       /* owned, non-null */
    int64_t start_on_ms;
    bool    has_end_on; /* false == C# null EndOn */
    int64_t end_on_ms;  /* valid only when has_end_on */
    ca_construction_decimal_t budget; /* micro-units */
    char   *currency;   /* owned, non-null */
} ca_construction_project_t;

void ca_construction_project_free(ca_construction_project_t *p);

/* ConstructionTask(ConstructionTaskId, ProjectId, Description, DateTime DueOn,
 * bool Completed). */
typedef struct {
    char   *task_id;     /* owned, non-null */
    char   *project_id;  /* owned, non-null */
    char   *description; /* owned, non-null */
    int64_t due_on_ms;
    bool    completed;
} ca_construction_task_t;

void ca_construction_task_free(ca_construction_task_t *t);
void ca_construction_task_free_array(ca_construction_task_t *arr, size_t count);

/* CostEntry(EntryId, ProjectId, Category, decimal Amount, DateTimeOffset AtUtc). */
typedef struct {
    char   *entry_id;    /* owned, non-null */
    char   *project_id;  /* owned, non-null */
    char   *category;    /* owned, non-null */
    ca_construction_decimal_t amount; /* micro-units */
    int64_t at_utc_ms;
} ca_construction_cost_t;

void ca_construction_cost_free(ca_construction_cost_t *c);

typedef struct ca_construction_board ca_construction_board_t;

ca_construction_board_t *ca_construction_board_create(void); /* NULL on OOM */
void ca_construction_board_destroy(ca_construction_board_t *b);

/* Create(p) — ProjectId keyed set. 0 / -1. */
int ca_construction_board_create_project(ca_construction_board_t *b,
                                         const ca_construction_project_t *p);

/* GetProject(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_construction_board_get_project(const ca_construction_board_t *b,
                                       const char *id,
                                       ca_construction_project_t *out);

/* Add(t) — ConstructionTaskId keyed set. 0 / -1. */
int ca_construction_board_add_task(ca_construction_board_t *b,
                                   const ca_construction_task_t *t);

/* Complete(taskId) — sets Completed=true. 0 on success, -1 on bad args, -2 when
 * the task is unknown (C# InvalidOperationException). */
int ca_construction_board_complete(ca_construction_board_t *b,
                                   const char *task_id);

/* OpenConstructionTasksFor(projectId) -> fresh owned array of incomplete tasks
 * ordered by DueOn asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_construction_task_t *ca_construction_board_open_tasks_for(
    const ca_construction_board_t *b, const char *project_id, size_t *out_count);

/* RecordCost(c) — appends. 0 / -1. */
int ca_construction_board_record_cost(ca_construction_board_t *b,
                                      const ca_construction_cost_t *c);

/* SpendFor(projectId) — summed Amount (micro-units) of that project's costs. */
ca_construction_decimal_t ca_construction_board_spend_for(
    const ca_construction_board_t *b, const char *project_id);

/* RemainingBudget(projectId) -> Budget - SpendFor into *out; 0 on success, -1 on
 * bad args, -2 when the project is unknown (C# InvalidOperationException). */
int ca_construction_board_remaining_budget(const ca_construction_board_t *b,
                                           const char *project_id,
                                           ca_construction_decimal_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CONSTRUCTION_H */
