/*
 * home.c — CircleAI.Home (C11 port of HomePrimitives.cs).
 *
 * InMemoryHomeBoard: rooms (RoomId keyed), devices (DeviceId keyed), tasks
 * (TaskId keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/home.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_home_room_free(ca_home_room_t *r) {
    if (!r) return;
    free(r->room_id);
    free(r->name);
    r->room_id = r->name = NULL;
}
void ca_home_room_free_array(ca_home_room_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_home_room_free(&arr[i]);
    free(arr);
}

static bool room_copy(ca_home_room_t *dst, const ca_home_room_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->room_id = cab_strdup_empty(src->room_id);
    dst->name    = cab_strdup_empty(src->name);
    dst->area_m2 = src->area_m2;
    if (!dst->room_id || !dst->name) { ca_home_room_free(dst); return false; }
    return true;
}

void ca_home_device_free(ca_home_device_t *d) {
    if (!d) return;
    free(d->device_id);
    free(d->name);
    free(d->kind);
    free(d->room_id);
    d->device_id = d->name = d->kind = d->room_id = NULL;
    d->has_room = false;
}
void ca_home_device_free_array(ca_home_device_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_home_device_free(&arr[i]);
    free(arr);
}

static bool device_copy(ca_home_device_t *dst, const ca_home_device_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id = cab_strdup_empty(src->device_id);
    dst->name      = cab_strdup_empty(src->name);
    dst->kind      = cab_strdup_empty(src->kind);
    dst->is_on     = src->is_on;
    bool ok = dst->device_id && dst->name && dst->kind;
    if (ok && src->has_room) {
        dst->room_id = cab_strdup_empty(src->room_id);
        ok = dst->room_id != NULL;
        dst->has_room = ok;
    }
    if (!ok) { ca_home_device_free(dst); return false; }
    return true;
}

void ca_home_task_free(ca_home_task_t *t) {
    if (!t) return;
    free(t->task_id);
    free(t->description);
    t->task_id = t->description = NULL;
}
void ca_home_task_free_array(ca_home_task_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_home_task_free(&arr[i]);
    free(arr);
}

static bool task_copy(ca_home_task_t *dst, const ca_home_task_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->task_id     = cab_strdup_empty(src->task_id);
    dst->description = cab_strdup_empty(src->description);
    dst->due_on_ms   = src->due_on_ms;
    dst->completed   = src->completed;
    if (!dst->task_id || !dst->description) { ca_home_task_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_home_board {
    ca_home_room_t   *rooms;
    size_t            r_count, r_cap;
    ca_home_device_t *devices;
    size_t            d_count, d_cap;
    ca_home_task_t   *tasks;
    size_t            t_count, t_cap;
};

ca_home_board_t *ca_home_board_create(void) {
    return (ca_home_board_t *)calloc(1, sizeof(ca_home_board_t));
}
void ca_home_board_destroy(ca_home_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->r_count; ++i) ca_home_room_free(&b->rooms[i]);
    for (size_t i = 0; i < b->d_count; ++i) ca_home_device_free(&b->devices[i]);
    for (size_t i = 0; i < b->t_count; ++i) ca_home_task_free(&b->tasks[i]);
    free(b->rooms);
    free(b->devices);
    free(b->tasks);
    free(b);
}

int ca_home_board_add_room(ca_home_board_t *b, const ca_home_room_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->rooms[i].room_id, r->room_id)) {
            ca_home_room_t copy;
            if (!room_copy(&copy, r)) return -1;
            ca_home_room_free(&b->rooms[i]);
            b->rooms[i] = copy;
            return 0;
        }
    }
    ca_home_room_t copy;
    if (!room_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->rooms, nc * sizeof(*b->rooms));
        if (!n) { ca_home_room_free(&copy); return -1; }
        b->rooms = (ca_home_room_t *)n;
        b->r_cap = nc;
    }
    b->rooms[b->r_count++] = copy;
    return 0;
}

bool ca_home_board_get_room(const ca_home_board_t *b, const char *id,
                            ca_home_room_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ord_eq(b->rooms[i].room_id, id))
            return room_copy(out, &b->rooms[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void room_sort_name(const ca_home_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->rooms[idx[j - 1]].name, b->rooms[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_home_room_t *ca_home_board_rooms(const ca_home_board_t *b, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->r_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    room_sort_name(b, idx, n);

    ca_home_room_t *out = (ca_home_room_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!room_copy(&out[i], &b->rooms[idx[i]])) {
            ca_home_room_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_home_board_add_device(ca_home_board_t *b, const ca_home_device_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->d_count; ++i) {
        if (cab_ord_eq(b->devices[i].device_id, d->device_id)) {
            ca_home_device_t copy;
            if (!device_copy(&copy, d)) return -1;
            ca_home_device_free(&b->devices[i]);
            b->devices[i] = copy;
            return 0;
        }
    }
    ca_home_device_t copy;
    if (!device_copy(&copy, d)) return -1;
    if (b->d_count == b->d_cap) {
        size_t nc = b->d_cap ? b->d_cap * 2 : 4;
        void *n = realloc(b->devices, nc * sizeof(*b->devices));
        if (!n) { ca_home_device_free(&copy); return -1; }
        b->devices = (ca_home_device_t *)n;
        b->d_cap = nc;
    }
    b->devices[b->d_count++] = copy;
    return 0;
}

int ca_home_board_toggle(ca_home_board_t *b, const char *device_id, bool on) {
    if (!b || !device_id) return -1;
    for (size_t i = 0; i < b->d_count; ++i) {
        if (cab_ord_eq(b->devices[i].device_id, device_id)) {
            b->devices[i].is_on = on;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown device */
}

/* Copy the indices matching a predicate into a fresh device array. */
static ca_home_device_t *collect_devices(const ca_home_board_t *b,
                                         const size_t *idx, size_t n,
                                         size_t *out_count) {
    if (n == 0) { *out_count = 0; return NULL; }
    ca_home_device_t *out = (ca_home_device_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!device_copy(&out[i], &b->devices[idx[i]])) {
            ca_home_device_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

ca_home_device_t *ca_home_board_devices_in(const ca_home_board_t *b,
                                           const char *room_id,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !room_id) { *out_count = (size_t)-1; return NULL; }
    if (b->d_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->d_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->d_count; ++i)
        if (b->devices[i].has_room && cab_ord_eq(b->devices[i].room_id, room_id))
            idx[n++] = i;
    ca_home_device_t *out = collect_devices(b, idx, n, out_count);
    free(idx);
    return out;
}

ca_home_device_t *ca_home_board_active_devices(const ca_home_board_t *b,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->d_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->d_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->d_count; ++i)
        if (b->devices[i].is_on) idx[n++] = i;
    ca_home_device_t *out = collect_devices(b, idx, n, out_count);
    free(idx);
    return out;
}

int ca_home_board_schedule_task(ca_home_board_t *b, const ca_home_task_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->tasks[i].task_id, t->task_id)) {
            ca_home_task_t copy;
            if (!task_copy(&copy, t)) return -1;
            ca_home_task_free(&b->tasks[i]);
            b->tasks[i] = copy;
            return 0;
        }
    }
    ca_home_task_t copy;
    if (!task_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->tasks, nc * sizeof(*b->tasks));
        if (!n) { ca_home_task_free(&copy); return -1; }
        b->tasks = (ca_home_task_t *)n;
        b->t_cap = nc;
    }
    b->tasks[b->t_count++] = copy;
    return 0;
}

int ca_home_board_complete_task(ca_home_board_t *b, const char *task_id) {
    if (!b || !task_id) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->tasks[i].task_id, task_id)) {
            b->tasks[i].completed = true;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown task */
}

/* Stable ascending sort of collected indices by DueOn. */
static void task_sort_asc(const ca_home_board_t *b, size_t *idx, size_t n) {
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

ca_home_task_t *ca_home_board_upcoming_tasks(const ca_home_board_t *b,
                                             int64_t by_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i)
        if (!b->tasks[i].completed && b->tasks[i].due_on_ms <= by_ms) idx[n++] = i;
    task_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_home_task_t *out = (ca_home_task_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!task_copy(&out[i], &b->tasks[idx[i]])) {
            ca_home_task_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
