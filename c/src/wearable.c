/*
 * wearable.c — CircleAI.Wearable (C11 port of WearablePrimitives.cs).
 *
 * InMemoryWearableBoard: devices (DeviceId keyed), samples (append list; Record
 * requires a known device). Pure C11 + libc + libm. No pthreads.
 */

#include "circle_ai/wearable.h"
#include "board_common.h"
#include <math.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_wearable_device_free(ca_wearable_device_t *d) {
    if (!d) return;
    free(d->device_id);
    free(d->vendor);
    free(d->firmware_version);
    d->device_id = d->vendor = d->firmware_version = NULL;
}
void ca_wearable_device_free_array(ca_wearable_device_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_wearable_device_free(&arr[i]);
    free(arr);
}

static bool device_copy(ca_wearable_device_t *dst,
                        const ca_wearable_device_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id        = cab_strdup_empty(src->device_id);
    dst->kind             = src->kind;
    dst->vendor           = cab_strdup_empty(src->vendor);
    dst->firmware_version = cab_strdup_empty(src->firmware_version);
    dst->battery_pct      = src->battery_pct;
    if (!dst->device_id || !dst->vendor || !dst->firmware_version) {
        ca_wearable_device_free(dst);
        return false;
    }
    return true;
}

void ca_wearable_sample_free(ca_wearable_sample_t *s) {
    if (!s) return;
    free(s->device_id);
    s->device_id = NULL;
}
void ca_wearable_sample_free_array(ca_wearable_sample_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_wearable_sample_free(&arr[i]);
    free(arr);
}

static bool sample_copy(ca_wearable_sample_t *dst,
                        const ca_wearable_sample_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id = cab_strdup_empty(src->device_id);
    dst->kind      = src->kind;
    dst->value     = src->value;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->device_id) return false;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_wearable_board {
    ca_wearable_device_t *devices;
    size_t                d_count, d_cap;
    ca_wearable_sample_t *samples;
    size_t                s_count, s_cap;
};

ca_wearable_board_t *ca_wearable_board_create(void) {
    return (ca_wearable_board_t *)calloc(1, sizeof(ca_wearable_board_t));
}
void ca_wearable_board_destroy(ca_wearable_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->d_count; ++i) ca_wearable_device_free(&b->devices[i]);
    for (size_t i = 0; i < b->s_count; ++i) ca_wearable_sample_free(&b->samples[i]);
    free(b->devices);
    free(b->samples);
    free(b);
}

int ca_wearable_board_add(ca_wearable_board_t *b, const ca_wearable_device_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->d_count; ++i) {
        if (cab_ord_eq(b->devices[i].device_id, d->device_id)) {
            ca_wearable_device_t copy;
            if (!device_copy(&copy, d)) return -1;
            ca_wearable_device_free(&b->devices[i]);
            b->devices[i] = copy;
            return 0;
        }
    }
    ca_wearable_device_t copy;
    if (!device_copy(&copy, d)) return -1;
    if (b->d_count == b->d_cap) {
        size_t nc = b->d_cap ? b->d_cap * 2 : 4;
        void *n = realloc(b->devices, nc * sizeof(*b->devices));
        if (!n) { ca_wearable_device_free(&copy); return -1; }
        b->devices = (ca_wearable_device_t *)n;
        b->d_cap = nc;
    }
    b->devices[b->d_count++] = copy;
    return 0;
}

bool ca_wearable_board_get_device(const ca_wearable_board_t *b, const char *id,
                                  ca_wearable_device_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->d_count; ++i)
        if (cab_ord_eq(b->devices[i].device_id, id))
            return device_copy(out, &b->devices[i]);
    return false;
}

/* Stable ascending sort of collected indices by Vendor (ordinal). */
static void device_sort_vendor(const ca_wearable_board_t *b, size_t *idx,
                               size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(b->devices[idx[j - 1]].vendor,
                              b->devices[key].vendor) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_wearable_device_t *ca_wearable_board_devices(const ca_wearable_board_t *b,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->d_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->d_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    device_sort_vendor(b, idx, n);

    ca_wearable_device_t *out = (ca_wearable_device_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!device_copy(&out[i], &b->devices[idx[i]])) {
            ca_wearable_device_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

static bool device_known(const ca_wearable_board_t *b, const char *device_id) {
    for (size_t i = 0; i < b->d_count; ++i)
        if (cab_ord_eq(b->devices[i].device_id, device_id)) return true;
    return false;
}

int ca_wearable_board_record(ca_wearable_board_t *b,
                             const ca_wearable_sample_t *s) {
    if (!b || !s) return -1;
    if (!device_known(b, s->device_id)) return -2; /* unknown device */
    ca_wearable_sample_t copy;
    if (!sample_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->samples, nc * sizeof(*b->samples));
        if (!n) { ca_wearable_sample_free(&copy); return -1; }
        b->samples = (ca_wearable_sample_t *)n;
        b->s_cap = nc;
    }
    b->samples[b->s_count++] = copy;
    return 0;
}

/* Collect this device+kind's samples (>= since) into idx, ascending by AtUtc. */
static size_t collect_samples_sorted(const ca_wearable_board_t *b,
                                     const char *device_id,
                                     ca_wearable_telemetry_kind_t kind,
                                     int64_t since_ms, size_t *idx) {
    size_t n = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_wearable_sample_t *s = &b->samples[i];
        if (cab_ord_eq(s->device_id, device_id) && s->kind == kind &&
            s->at_utc_ms >= since_ms)
            idx[n++] = i;
    }
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->samples[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->samples[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
    return n;
}

ca_wearable_sample_t *ca_wearable_board_read_since(
    const ca_wearable_board_t *b, const char *device_id,
    ca_wearable_telemetry_kind_t kind, int64_t since_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !device_id) { *out_count = (size_t)-1; return NULL; }
    if (b->s_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->s_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = collect_samples_sorted(b, device_id, kind, since_ms, idx);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_wearable_sample_t *out = (ca_wearable_sample_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!sample_copy(&out[i], &b->samples[idx[i]])) {
            ca_wearable_sample_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_wearable_board_latest_value(const ca_wearable_board_t *b,
                                    const char *device_id,
                                    ca_wearable_telemetry_kind_t kind,
                                    double *out_value) {
    if (!b || !device_id || !out_value) return false;
    bool found = false;
    int64_t best_at = 0;
    double best_val = 0.0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_wearable_sample_t *s = &b->samples[i];
        if (cab_ord_eq(s->device_id, device_id) && s->kind == kind) {
            if (!found || s->at_utc_ms > best_at) {
                best_at = s->at_utc_ms;
                best_val = s->value;
                found = true;
            }
        }
    }
    if (found) *out_value = best_val;
    return found;
}

double ca_wearable_board_average_value(const ca_wearable_board_t *b,
                                       const char *device_id,
                                       ca_wearable_telemetry_kind_t kind,
                                       int64_t since_ms) {
    if (!b || !device_id || b->s_count == 0) return NAN;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_wearable_sample_t *s = &b->samples[i];
        if (cab_ord_eq(s->device_id, device_id) && s->kind == kind &&
            s->at_utc_ms >= since_ms) {
            sum += s->value;
            n++;
        }
    }
    return n == 0 ? NAN : sum / (double)n;
}
