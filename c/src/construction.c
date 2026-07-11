/*
 * construction.c — CircleAI.Construction (C11 port of ConstructionPrimitives.cs).
 *
 * InMemoryConstructionBoard: projects (ProjectId keyed), tasks (TaskId keyed),
 * costs (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/construction.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_construction_project_free(ca_construction_project_t *p) {
    if (!p) return;
    free(p->project_id);
    free(p->name);
    free(p->currency);
    p->project_id = p->name = p->currency = NULL;
    p->has_end_on = false;
}

static bool project_copy(ca_construction_project_t *dst,
                         const ca_construction_project_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->project_id = cab_strdup_empty(src->project_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->start_on_ms = src->start_on_ms;
    dst->has_end_on = src->has_end_on;
    dst->end_on_ms  = src->has_end_on ? src->end_on_ms : 0;
    dst->budget     = src->budget;
    dst->currency   = cab_strdup_empty(src->currency);
    if (!dst->project_id || !dst->name || !dst->currency) {
        ca_construction_project_free(dst);
        return false;
    }
    return true;
}

void ca_construction_task_free(ca_construction_task_t *t) {
    if (!t) return;
    free(t->task_id);
    free(t->project_id);
    free(t->description);
    t->task_id = t->project_id = t->description = NULL;
}
void ca_construction_task_free_array(ca_construction_task_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_construction_task_free(&arr[i]);
    free(arr);
}

static bool task_copy(ca_construction_task_t *dst,
                      const ca_construction_task_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->task_id     = cab_strdup_empty(src->task_id);
    dst->project_id  = cab_strdup_empty(src->project_id);
    dst->description = cab_strdup_empty(src->description);
    dst->due_on_ms   = src->due_on_ms;
    dst->completed   = src->completed;
    if (!dst->task_id || !dst->project_id || !dst->description) {
        ca_construction_task_free(dst);
        return false;
    }
    return true;
}

void ca_construction_cost_free(ca_construction_cost_t *c) {
    if (!c) return;
    free(c->entry_id);
    free(c->project_id);
    free(c->category);
    c->entry_id = c->project_id = c->category = NULL;
}

static bool cost_copy(ca_construction_cost_t *dst,
                      const ca_construction_cost_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->entry_id   = cab_strdup_empty(src->entry_id);
    dst->project_id = cab_strdup_empty(src->project_id);
    dst->category   = cab_strdup_empty(src->category);
    dst->amount     = src->amount;
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->entry_id || !dst->project_id || !dst->category) {
        ca_construction_cost_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_construction_board {
    ca_construction_project_t *projects;
    size_t                     p_count, p_cap;
    ca_construction_task_t    *tasks;
    size_t                     t_count, t_cap;
    ca_construction_cost_t    *costs;
    size_t                     c_count, c_cap;
};

ca_construction_board_t *ca_construction_board_create(void) {
    return (ca_construction_board_t *)calloc(1, sizeof(ca_construction_board_t));
}
void ca_construction_board_destroy(ca_construction_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i) ca_construction_project_free(&b->projects[i]);
    for (size_t i = 0; i < b->t_count; ++i) ca_construction_task_free(&b->tasks[i]);
    for (size_t i = 0; i < b->c_count; ++i) ca_construction_cost_free(&b->costs[i]);
    free(b->projects);
    free(b->tasks);
    free(b->costs);
    free(b);
}

int ca_construction_board_create_project(ca_construction_board_t *b,
                                         const ca_construction_project_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->projects[i].project_id, p->project_id)) {
            ca_construction_project_t copy;
            if (!project_copy(&copy, p)) return -1;
            ca_construction_project_free(&b->projects[i]);
            b->projects[i] = copy;
            return 0;
        }
    }
    ca_construction_project_t copy;
    if (!project_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->projects, nc * sizeof(*b->projects));
        if (!n) { ca_construction_project_free(&copy); return -1; }
        b->projects = (ca_construction_project_t *)n;
        b->p_cap = nc;
    }
    b->projects[b->p_count++] = copy;
    return 0;
}

bool ca_construction_board_get_project(const ca_construction_board_t *b,
                                       const char *id,
                                       ca_construction_project_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->projects[i].project_id, id))
            return project_copy(out, &b->projects[i]);
    return false;
}

int ca_construction_board_add_task(ca_construction_board_t *b,
                                   const ca_construction_task_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->tasks[i].task_id, t->task_id)) {
            ca_construction_task_t copy;
            if (!task_copy(&copy, t)) return -1;
            ca_construction_task_free(&b->tasks[i]);
            b->tasks[i] = copy;
            return 0;
        }
    }
    ca_construction_task_t copy;
    if (!task_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->tasks, nc * sizeof(*b->tasks));
        if (!n) { ca_construction_task_free(&copy); return -1; }
        b->tasks = (ca_construction_task_t *)n;
        b->t_cap = nc;
    }
    b->tasks[b->t_count++] = copy;
    return 0;
}

int ca_construction_board_complete(ca_construction_board_t *b,
                                   const char *task_id) {
    if (!b || !task_id) return -1;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->tasks[i].task_id, task_id)) {
            b->tasks[i].completed = true;
            return 0;
        }
    return -2; /* Unknown task -> C# InvalidOperationException */
}

/* Stable ascending sort of collected indices by DueOn. */
static void task_sort_asc(const ca_construction_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->tasks[key].due_on_ms;
        size_t j = i;
        while (j > 0 && b->tasks[idx[j - 1]].due_on_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_construction_task_t *ca_construction_board_open_tasks_for(
    const ca_construction_board_t *b, const char *project_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !project_id) { *out_count = (size_t)-1; return NULL; }
    if (b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i) {
        const ca_construction_task_t *t = &b->tasks[i];
        if (cab_ord_eq(t->project_id, project_id) && !t->completed) idx[n++] = i;
    }
    task_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_construction_task_t *out = (ca_construction_task_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!task_copy(&out[i], &b->tasks[idx[i]])) {
            ca_construction_task_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_construction_board_record_cost(ca_construction_board_t *b,
                                      const ca_construction_cost_t *c) {
    if (!b || !c) return -1;
    ca_construction_cost_t copy;
    if (!cost_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->costs, nc * sizeof(*b->costs));
        if (!n) { ca_construction_cost_free(&copy); return -1; }
        b->costs = (ca_construction_cost_t *)n;
        b->c_cap = nc;
    }
    b->costs[b->c_count++] = copy;
    return 0;
}

ca_construction_decimal_t ca_construction_board_spend_for(
    const ca_construction_board_t *b, const char *project_id) {
    if (!b || !project_id) return 0;
    ca_construction_decimal_t sum = 0;
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->costs[i].project_id, project_id))
            sum += b->costs[i].amount;
    return sum;
}

int ca_construction_board_remaining_budget(const ca_construction_board_t *b,
                                           const char *project_id,
                                           ca_construction_decimal_t *out) {
    if (out) *out = 0;
    if (!b || !project_id || !out) return -1;
    const ca_construction_project_t *p = NULL;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->projects[i].project_id, project_id)) {
            p = &b->projects[i];
            break;
        }
    if (!p) return -2; /* Unknown project -> C# InvalidOperationException */
    *out = p->budget - ca_construction_board_spend_for(b, project_id);
    return 0;
}
