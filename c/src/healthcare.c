/*
 * healthcare.c — CircleAI.Healthcare (C11 port of HealthcarePrimitives.cs).
 *
 * InMemoryHealthcareBoard over three linear stores (patients / appointments /
 * prescriptions), each keyed by its id with dictionary-set replace semantics.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/healthcare.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_hc_patient_free(ca_hc_patient_t *p) {
    if (!p) return;
    free(p->patient_id);
    free(p->name);
    p->patient_id = p->name = NULL;
}

static bool patient_copy(ca_hc_patient_t *dst, const ca_hc_patient_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->patient_id = cab_strdup_empty(src->patient_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->date_of_birth_ms = src->date_of_birth_ms;
    if (!dst->patient_id || !dst->name) { ca_hc_patient_free(dst); return false; }
    return true;
}

void ca_hc_appointment_free(ca_hc_appointment_t *a) {
    if (!a) return;
    free(a->appt_id);
    free(a->patient_id);
    free(a->provider);
    free(a->status);
    a->appt_id = a->patient_id = a->provider = a->status = NULL;
}
void ca_hc_appointment_free_array(ca_hc_appointment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hc_appointment_free(&arr[i]);
    free(arr);
}

static bool appointment_copy(ca_hc_appointment_t *dst,
                             const ca_hc_appointment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->appt_id    = cab_strdup_empty(src->appt_id);
    dst->patient_id = cab_strdup_empty(src->patient_id);
    dst->provider   = cab_strdup_empty(src->provider);
    dst->status     = cab_strdup_empty(src->status);
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->appt_id || !dst->patient_id || !dst->provider || !dst->status) {
        ca_hc_appointment_free(dst);
        return false;
    }
    return true;
}

void ca_hc_prescription_free(ca_hc_prescription_t *r) {
    if (!r) return;
    free(r->rx_id);
    free(r->patient_id);
    free(r->medication_name);
    free(r->dose);
    free(r->frequency);
    r->rx_id = r->patient_id = r->medication_name = r->dose = r->frequency = NULL;
}
void ca_hc_prescription_free_array(ca_hc_prescription_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hc_prescription_free(&arr[i]);
    free(arr);
}

static bool prescription_copy(ca_hc_prescription_t *dst,
                              const ca_hc_prescription_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->rx_id           = cab_strdup_empty(src->rx_id);
    dst->patient_id      = cab_strdup_empty(src->patient_id);
    dst->medication_name = cab_strdup_empty(src->medication_name);
    dst->dose            = cab_strdup_empty(src->dose);
    dst->frequency       = cab_strdup_empty(src->frequency);
    dst->prescribed_utc_ms = src->prescribed_utc_ms;
    if (!dst->rx_id || !dst->patient_id || !dst->medication_name ||
        !dst->dose || !dst->frequency) {
        ca_hc_prescription_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_hc_board {
    ca_hc_patient_t      *patients;
    size_t                pat_count, pat_cap;
    ca_hc_appointment_t  *appts;
    size_t                appt_count, appt_cap;
    ca_hc_prescription_t *rx;
    size_t                rx_count, rx_cap;
};

ca_hc_board_t *ca_hc_board_create(void) {
    return (ca_hc_board_t *)calloc(1, sizeof(ca_hc_board_t));
}

void ca_hc_board_destroy(ca_hc_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->pat_count; ++i)  ca_hc_patient_free(&b->patients[i]);
    for (size_t i = 0; i < b->appt_count; ++i) ca_hc_appointment_free(&b->appts[i]);
    for (size_t i = 0; i < b->rx_count; ++i)   ca_hc_prescription_free(&b->rx[i]);
    free(b->patients);
    free(b->appts);
    free(b->rx);
    free(b);
}

int ca_hc_board_register(ca_hc_board_t *b, const ca_hc_patient_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->pat_count; ++i) {
        if (cab_ord_eq(b->patients[i].patient_id, p->patient_id)) {
            ca_hc_patient_t copy;
            if (!patient_copy(&copy, p)) return -1;
            ca_hc_patient_free(&b->patients[i]);
            b->patients[i] = copy;
            return 0;
        }
    }
    ca_hc_patient_t copy;
    if (!patient_copy(&copy, p)) return -1;
    if (b->pat_count == b->pat_cap) {
        size_t nc = b->pat_cap ? b->pat_cap * 2 : 4;
        void *n = realloc(b->patients, nc * sizeof(*b->patients));
        if (!n) { ca_hc_patient_free(&copy); return -1; }
        b->patients = (ca_hc_patient_t *)n;
        b->pat_cap = nc;
    }
    b->patients[b->pat_count++] = copy;
    return 0;
}

bool ca_hc_board_get_patient(const ca_hc_board_t *b, const char *id,
                             ca_hc_patient_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->pat_count; ++i)
        if (cab_ord_eq(b->patients[i].patient_id, id))
            return patient_copy(out, &b->patients[i]);
    return false;
}

int ca_hc_board_schedule(ca_hc_board_t *b, const ca_hc_appointment_t *a) {
    if (!b || !a) return -1;
    for (size_t i = 0; i < b->appt_count; ++i) {
        if (cab_ord_eq(b->appts[i].appt_id, a->appt_id)) {
            ca_hc_appointment_t copy;
            if (!appointment_copy(&copy, a)) return -1;
            ca_hc_appointment_free(&b->appts[i]);
            b->appts[i] = copy;
            return 0;
        }
    }
    ca_hc_appointment_t copy;
    if (!appointment_copy(&copy, a)) return -1;
    if (b->appt_count == b->appt_cap) {
        size_t nc = b->appt_cap ? b->appt_cap * 2 : 4;
        void *n = realloc(b->appts, nc * sizeof(*b->appts));
        if (!n) { ca_hc_appointment_free(&copy); return -1; }
        b->appts = (ca_hc_appointment_t *)n;
        b->appt_cap = nc;
    }
    b->appts[b->appt_count++] = copy;
    return 0;
}

int ca_hc_board_update_status(ca_hc_board_t *b, const char *appt_id,
                              const char *status) {
    if (!b || !appt_id) return -1;
    for (size_t i = 0; i < b->appt_count; ++i) {
        if (cab_ord_eq(b->appts[i].appt_id, appt_id)) {
            char *ns = cab_strdup_empty(status);
            if (!ns) return -1;
            free(b->appts[i].status);
            b->appts[i].status = ns;
            return 0;
        }
    }
    return 1;   /* InvalidOperationException: unknown appointment */
}

/* Stable ascending sort of collected indices by at_utc_ms. */
static void appt_sort_asc(const ca_hc_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->appts[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->appts[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_hc_appointment_t *ca_hc_board_appointments_for(const ca_hc_board_t *b,
                                                  const char *patient_id,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !patient_id) { *out_count = (size_t)-1; return NULL; }
    if (b->appt_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->appt_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->appt_count; ++i)
        if (cab_ord_eq(b->appts[i].patient_id, patient_id)) idx[n++] = i;
    appt_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_hc_appointment_t *out = (ca_hc_appointment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!appointment_copy(&out[i], &b->appts[idx[i]])) {
            ca_hc_appointment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_hc_board_prescribe(ca_hc_board_t *b, const ca_hc_prescription_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->rx_count; ++i) {
        if (cab_ord_eq(b->rx[i].rx_id, r->rx_id)) {
            ca_hc_prescription_t copy;
            if (!prescription_copy(&copy, r)) return -1;
            ca_hc_prescription_free(&b->rx[i]);
            b->rx[i] = copy;
            return 0;
        }
    }
    ca_hc_prescription_t copy;
    if (!prescription_copy(&copy, r)) return -1;
    if (b->rx_count == b->rx_cap) {
        size_t nc = b->rx_cap ? b->rx_cap * 2 : 4;
        void *n = realloc(b->rx, nc * sizeof(*b->rx));
        if (!n) { ca_hc_prescription_free(&copy); return -1; }
        b->rx = (ca_hc_prescription_t *)n;
        b->rx_cap = nc;
    }
    b->rx[b->rx_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by prescribed_utc_ms. */
static void rx_sort_desc(const ca_hc_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->rx[key].prescribed_utc_ms;
        size_t j = i;
        while (j > 0 && b->rx[idx[j - 1]].prescribed_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_hc_prescription_t *ca_hc_board_prescriptions_for(const ca_hc_board_t *b,
                                                    const char *patient_id,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !patient_id) { *out_count = (size_t)-1; return NULL; }
    if (b->rx_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->rx_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->rx_count; ++i)
        if (cab_ord_eq(b->rx[i].patient_id, patient_id)) idx[n++] = i;
    rx_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_hc_prescription_t *out = (ca_hc_prescription_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!prescription_copy(&out[i], &b->rx[idx[i]])) {
            ca_hc_prescription_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
