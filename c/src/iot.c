/*
 * iot.c — CircleAI.IoT (C11 port of IoTPrimitives.cs).
 *
 * InMemoryIoTBoard: devices (DeviceId keyed), telemetry + commands (flat append
 * lists). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/iot.h"
#include "board_common.h"
#include <math.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_iot_device_free(ca_iot_device_t *d) {
    if (!d) return;
    free(d->device_id);
    free(d->name);
    free(d->kind);
    free(d->firmware_version);
    d->device_id = d->name = d->kind = d->firmware_version = NULL;
}
void ca_iot_device_free_array(ca_iot_device_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_iot_device_free(&arr[i]);
    free(arr);
}

static bool device_copy(ca_iot_device_t *dst, const ca_iot_device_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id        = cab_strdup_empty(src->device_id);
    dst->name             = cab_strdup_empty(src->name);
    dst->kind             = cab_strdup_empty(src->kind);
    dst->firmware_version = cab_strdup_empty(src->firmware_version);
    dst->last_seen_utc_ms = src->last_seen_utc_ms;
    if (!dst->device_id || !dst->name || !dst->kind || !dst->firmware_version) {
        ca_iot_device_free(dst);
        return false;
    }
    return true;
}

void ca_iot_telemetry_free(ca_iot_telemetry_t *t) {
    if (!t) return;
    free(t->device_id);
    free(t->metric);
    t->device_id = t->metric = NULL;
}
void ca_iot_telemetry_free_array(ca_iot_telemetry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_iot_telemetry_free(&arr[i]);
    free(arr);
}

static bool telemetry_copy(ca_iot_telemetry_t *dst,
                           const ca_iot_telemetry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id = cab_strdup_empty(src->device_id);
    dst->metric    = cab_strdup_empty(src->metric);
    dst->value     = src->value;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->device_id || !dst->metric) {
        ca_iot_telemetry_free(dst);
        return false;
    }
    return true;
}

void ca_iot_command_free(ca_iot_command_t *c) {
    if (!c) return;
    free(c->command_id);
    free(c->device_id);
    free(c->action);
    free(c->arguments_json);
    c->command_id = c->device_id = c->action = c->arguments_json = NULL;
}
void ca_iot_command_free_array(ca_iot_command_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_iot_command_free(&arr[i]);
    free(arr);
}

static bool command_copy(ca_iot_command_t *dst, const ca_iot_command_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->command_id     = cab_strdup_empty(src->command_id);
    dst->device_id      = cab_strdup_empty(src->device_id);
    dst->action         = cab_strdup_empty(src->action);
    dst->arguments_json = cab_strdup_empty(src->arguments_json);
    dst->sent_utc_ms    = src->sent_utc_ms;
    if (!dst->command_id || !dst->device_id || !dst->action ||
        !dst->arguments_json) {
        ca_iot_command_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_iot_board {
    ca_iot_device_t    *devices;
    size_t              d_count, d_cap;
    ca_iot_telemetry_t *telemetry;
    size_t              tel_count, tel_cap;
    ca_iot_command_t   *commands;
    size_t              cmd_count, cmd_cap;
};

ca_iot_board_t *ca_iot_board_create(void) {
    return (ca_iot_board_t *)calloc(1, sizeof(ca_iot_board_t));
}
void ca_iot_board_destroy(ca_iot_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->d_count; ++i)   ca_iot_device_free(&b->devices[i]);
    for (size_t i = 0; i < b->tel_count; ++i) ca_iot_telemetry_free(&b->telemetry[i]);
    for (size_t i = 0; i < b->cmd_count; ++i) ca_iot_command_free(&b->commands[i]);
    free(b->devices);
    free(b->telemetry);
    free(b->commands);
    free(b);
}

int ca_iot_board_register(ca_iot_board_t *b, const ca_iot_device_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->d_count; ++i) {
        if (cab_ord_eq(b->devices[i].device_id, d->device_id)) {
            ca_iot_device_t copy;
            if (!device_copy(&copy, d)) return -1;
            ca_iot_device_free(&b->devices[i]);
            b->devices[i] = copy;
            return 0;
        }
    }
    ca_iot_device_t copy;
    if (!device_copy(&copy, d)) return -1;
    if (b->d_count == b->d_cap) {
        size_t nc = b->d_cap ? b->d_cap * 2 : 4;
        void *n = realloc(b->devices, nc * sizeof(*b->devices));
        if (!n) { ca_iot_device_free(&copy); return -1; }
        b->devices = (ca_iot_device_t *)n;
        b->d_cap = nc;
    }
    b->devices[b->d_count++] = copy;
    return 0;
}

bool ca_iot_board_get_device(const ca_iot_board_t *b, const char *id,
                             ca_iot_device_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->d_count; ++i)
        if (cab_ord_eq(b->devices[i].device_id, id))
            return device_copy(out, &b->devices[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void device_sort_name(const ca_iot_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->devices[idx[j - 1]].name, b->devices[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_iot_device_t *ca_iot_board_devices(const ca_iot_board_t *b,
                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->d_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->d_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    device_sort_name(b, idx, n);

    ca_iot_device_t *out = (ca_iot_device_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!device_copy(&out[i], &b->devices[idx[i]])) {
            ca_iot_device_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_iot_board_record_telemetry(ca_iot_board_t *b,
                                  const ca_iot_telemetry_t *t) {
    if (!b || !t) return -1;
    ca_iot_telemetry_t copy;
    if (!telemetry_copy(&copy, t)) return -1;
    if (b->tel_count == b->tel_cap) {
        size_t nc = b->tel_cap ? b->tel_cap * 2 : 4;
        void *n = realloc(b->telemetry, nc * sizeof(*b->telemetry));
        if (!n) { ca_iot_telemetry_free(&copy); return -1; }
        b->telemetry = (ca_iot_telemetry_t *)n;
        b->tel_cap = nc;
    }
    b->telemetry[b->tel_count++] = copy;
    return 0;
}

double ca_iot_board_latest_value(const ca_iot_board_t *b, const char *device_id,
                                 const char *metric) {
    if (!b || !device_id || !metric) return NAN;
    bool found = false;
    int64_t best_at = 0;
    double best_val = NAN;
    for (size_t i = 0; i < b->tel_count; ++i) {
        const ca_iot_telemetry_t *t = &b->telemetry[i];
        if (cab_ord_eq(t->device_id, device_id) && cab_ord_eq(t->metric, metric)) {
            if (!found || t->at_utc_ms > best_at) {
                found = true;
                best_at = t->at_utc_ms;
                best_val = t->value;
            }
        }
    }
    return found ? best_val : NAN;
}

/* Stable descending sort of collected indices by AtUtc. */
static void telemetry_sort_desc(const ca_iot_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->telemetry[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->telemetry[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_iot_telemetry_t *ca_iot_board_history(const ca_iot_board_t *b,
                                         const char *device_id,
                                         const char *metric, int limit,
                                         size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !device_id || !metric || limit <= 0) {
        *out_count = (size_t)-1;
        return NULL;
    }
    if (b->tel_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->tel_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->tel_count; ++i) {
        const ca_iot_telemetry_t *t = &b->telemetry[i];
        if (cab_ord_eq(t->device_id, device_id) && cab_ord_eq(t->metric, metric))
            idx[n++] = i;
    }
    telemetry_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_iot_telemetry_t *out = (ca_iot_telemetry_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!telemetry_copy(&out[i], &b->telemetry[idx[i]])) {
            ca_iot_telemetry_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_iot_board_send_command(ca_iot_board_t *b, const ca_iot_command_t *c) {
    if (!b || !c) return -1;
    ca_iot_command_t copy;
    if (!command_copy(&copy, c)) return -1;
    if (b->cmd_count == b->cmd_cap) {
        size_t nc = b->cmd_cap ? b->cmd_cap * 2 : 4;
        void *n = realloc(b->commands, nc * sizeof(*b->commands));
        if (!n) { ca_iot_command_free(&copy); return -1; }
        b->commands = (ca_iot_command_t *)n;
        b->cmd_cap = nc;
    }
    b->commands[b->cmd_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by SentUtc. */
static void command_sort_desc(const ca_iot_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->commands[key].sent_utc_ms;
        size_t j = i;
        while (j > 0 && b->commands[idx[j - 1]].sent_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_iot_command_t *ca_iot_board_commands_for(const ca_iot_board_t *b,
                                            const char *device_id,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !device_id) { *out_count = (size_t)-1; return NULL; }
    if (b->cmd_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->cmd_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->cmd_count; ++i)
        if (cab_ord_eq(b->commands[i].device_id, device_id)) idx[n++] = i;
    command_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_iot_command_t *out = (ca_iot_command_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!command_copy(&out[i], &b->commands[idx[i]])) {
            ca_iot_command_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
