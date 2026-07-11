/*
 * personal_health.c — CircleAI.Personal.Health (C11 port of
 * PersonalHealthPrimitives.cs).
 *
 * InMemoryPersonalHealthBoard: vitals in an appended list, allergies + meds in
 * id-keyed linear stores. Per-user instance only. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/personal_health.h"
#include "board_common.h"

/* ── VitalReading ───────────────────────────────────────────────────────── */

void ca_phealth_vital_free(ca_phealth_vital_t *v) {
    if (!v) return;
    free(v->note);
    v->note = NULL;
}
void ca_phealth_vital_free_array(ca_phealth_vital_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_phealth_vital_free(&arr[i]);
    free(arr);
}

static bool vital_copy(ca_phealth_vital_t *dst, const ca_phealth_vital_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->kind      = src->kind;
    dst->value     = src->value;
    dst->at_utc_ms = src->at_utc_ms;
    dst->has_note  = src->has_note;
    if (src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        if (!dst->note) return false;
    }
    return true;
}

/* ── Allergy ────────────────────────────────────────────────────────────── */

void ca_phealth_allergy_free(ca_phealth_allergy_t *a) {
    if (!a) return;
    free(a->allergy_id);
    free(a->substance);
    free(a->severity);
    a->allergy_id = a->substance = a->severity = NULL;
}
void ca_phealth_allergy_free_array(ca_phealth_allergy_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_phealth_allergy_free(&arr[i]);
    free(arr);
}

static bool allergy_copy(ca_phealth_allergy_t *dst,
                         const ca_phealth_allergy_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->allergy_id = cab_strdup_empty(src->allergy_id);
    dst->substance  = cab_strdup_empty(src->substance);
    dst->severity   = cab_strdup_empty(src->severity);
    if (!dst->allergy_id || !dst->substance || !dst->severity) {
        ca_phealth_allergy_free(dst);
        return false;
    }
    return true;
}

/* ── Medication ─────────────────────────────────────────────────────────── */

void ca_phealth_medication_free(ca_phealth_medication_t *m) {
    if (!m) return;
    free(m->med_id);
    free(m->name);
    free(m->dose);
    free(m->frequency);
    m->med_id = m->name = m->dose = m->frequency = NULL;
}
void ca_phealth_medication_free_array(ca_phealth_medication_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_phealth_medication_free(&arr[i]);
    free(arr);
}

static bool medication_copy(ca_phealth_medication_t *dst,
                            const ca_phealth_medication_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->med_id    = cab_strdup_empty(src->med_id);
    dst->name      = cab_strdup_empty(src->name);
    dst->dose      = cab_strdup_empty(src->dose);
    dst->frequency = cab_strdup_empty(src->frequency);
    dst->started_at_utc_ms = src->started_at_utc_ms;
    dst->has_ended       = src->has_ended;
    dst->ended_at_utc_ms = src->ended_at_utc_ms;
    if (!dst->med_id || !dst->name || !dst->dose || !dst->frequency) {
        ca_phealth_medication_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_phealth_board {
    ca_phealth_vital_t      *vitals;
    size_t                   vital_count, vital_cap;
    ca_phealth_allergy_t    *allergies;
    size_t                   allergy_count, allergy_cap;
    ca_phealth_medication_t *meds;
    size_t                   med_count, med_cap;
};

ca_phealth_board_t *ca_phealth_board_create(void) {
    return (ca_phealth_board_t *)calloc(1, sizeof(ca_phealth_board_t));
}
void ca_phealth_board_destroy(ca_phealth_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->vital_count; ++i)   ca_phealth_vital_free(&b->vitals[i]);
    for (size_t i = 0; i < b->allergy_count; ++i) ca_phealth_allergy_free(&b->allergies[i]);
    for (size_t i = 0; i < b->med_count; ++i)     ca_phealth_medication_free(&b->meds[i]);
    free(b->vitals);
    free(b->allergies);
    free(b->meds);
    free(b);
}

int ca_phealth_board_record(ca_phealth_board_t *b, const ca_phealth_vital_t *v) {
    if (!b || !v) return -1;
    ca_phealth_vital_t copy;
    if (!vital_copy(&copy, v)) return -1;
    if (b->vital_count == b->vital_cap) {
        size_t nc = b->vital_cap ? b->vital_cap * 2 : 4;
        void *n = realloc(b->vitals, nc * sizeof(*b->vitals));
        if (!n) { ca_phealth_vital_free(&copy); return -1; }
        b->vitals = (ca_phealth_vital_t *)n;
        b->vital_cap = nc;
    }
    b->vitals[b->vital_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected vital indices by at_utc_ms. */
static void vital_sort_asc(const ca_phealth_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->vitals[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->vitals[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_phealth_vital_t *ca_phealth_board_read_since(const ca_phealth_board_t *b,
                                                ca_vital_kind_t kind,
                                                int64_t since_ms,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->vital_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->vital_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->vital_count; ++i)
        if (b->vitals[i].kind == kind && b->vitals[i].at_utc_ms >= since_ms)
            idx[n++] = i;
    vital_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_phealth_vital_t *out = (ca_phealth_vital_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!vital_copy(&out[i], &b->vitals[idx[i]])) {
            ca_phealth_vital_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_phealth_board_latest(const ca_phealth_board_t *b, ca_vital_kind_t kind,
                             ca_phealth_vital_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !out) return false;
    /* OrderByDescending(AtUtc).FirstOrDefault(): a stable descending sort keeps
     * insertion order among equal AtUtc, so First is the earliest-inserted of the
     * maximum-AtUtc group. Reproduce that: pick the first index whose AtUtc is the
     * running maximum (strictly greater replaces; equal does not). */
    size_t best = (size_t)-1;
    for (size_t i = 0; i < b->vital_count; ++i) {
        if (b->vitals[i].kind != kind) continue;
        if (best == (size_t)-1 || b->vitals[i].at_utc_ms > b->vitals[best].at_utc_ms)
            best = i;
    }
    if (best == (size_t)-1) return false;
    return vital_copy(out, &b->vitals[best]);
}

int ca_phealth_board_add_allergy(ca_phealth_board_t *b,
                                 const ca_phealth_allergy_t *a) {
    if (!b || !a) return -1;
    for (size_t i = 0; i < b->allergy_count; ++i) {
        if (cab_ord_eq(b->allergies[i].allergy_id, a->allergy_id)) {
            ca_phealth_allergy_t copy;
            if (!allergy_copy(&copy, a)) return -1;
            ca_phealth_allergy_free(&b->allergies[i]);
            b->allergies[i] = copy;
            return 0;
        }
    }
    ca_phealth_allergy_t copy;
    if (!allergy_copy(&copy, a)) return -1;
    if (b->allergy_count == b->allergy_cap) {
        size_t nc = b->allergy_cap ? b->allergy_cap * 2 : 4;
        void *n = realloc(b->allergies, nc * sizeof(*b->allergies));
        if (!n) { ca_phealth_allergy_free(&copy); return -1; }
        b->allergies = (ca_phealth_allergy_t *)n;
        b->allergy_cap = nc;
    }
    b->allergies[b->allergy_count++] = copy;
    return 0;
}

ca_phealth_allergy_t *ca_phealth_board_allergies(const ca_phealth_board_t *b,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->allergy_count == 0) { *out_count = 0; return NULL; }
    ca_phealth_allergy_t *out =
        (ca_phealth_allergy_t *)calloc(b->allergy_count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->allergy_count; ++i) {
        if (!allergy_copy(&out[i], &b->allergies[i])) {
            ca_phealth_allergy_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = b->allergy_count;
    return out;
}

int ca_phealth_board_add_medication(ca_phealth_board_t *b,
                                    const ca_phealth_medication_t *m) {
    if (!b || !m) return -1;
    for (size_t i = 0; i < b->med_count; ++i) {
        if (cab_ord_eq(b->meds[i].med_id, m->med_id)) {
            ca_phealth_medication_t copy;
            if (!medication_copy(&copy, m)) return -1;
            ca_phealth_medication_free(&b->meds[i]);
            b->meds[i] = copy;
            return 0;
        }
    }
    ca_phealth_medication_t copy;
    if (!medication_copy(&copy, m)) return -1;
    if (b->med_count == b->med_cap) {
        size_t nc = b->med_cap ? b->med_cap * 2 : 4;
        void *n = realloc(b->meds, nc * sizeof(*b->meds));
        if (!n) { ca_phealth_medication_free(&copy); return -1; }
        b->meds = (ca_phealth_medication_t *)n;
        b->med_cap = nc;
    }
    b->meds[b->med_count++] = copy;
    return 0;
}

int ca_phealth_board_end_medication(ca_phealth_board_t *b, const char *med_id,
                                    int64_t ended_at_utc_ms) {
    if (!b || !med_id) return -1;
    for (size_t i = 0; i < b->med_count; ++i) {
        if (cab_ord_eq(b->meds[i].med_id, med_id)) {
            b->meds[i].has_ended       = true;
            b->meds[i].ended_at_utc_ms = ended_at_utc_ms;
            return 0;
        }
    }
    return 1;   /* InvalidOperationException: unknown medication */
}

/* Stable ascending sort of collected med indices by Name (Ordinal). */
static void med_sort_by_name(const ca_phealth_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        const char *kn = b->meds[key].name;
        size_t j = i;
        while (j > 0 && strcmp(b->meds[idx[j - 1]].name, kn) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_phealth_medication_t *ca_phealth_board_active_medications(
    const ca_phealth_board_t *b, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->med_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->med_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->med_count; ++i)
        if (!b->meds[i].has_ended) idx[n++] = i;
    med_sort_by_name(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_phealth_medication_t *out =
        (ca_phealth_medication_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!medication_copy(&out[i], &b->meds[idx[i]])) {
            ca_phealth_medication_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
