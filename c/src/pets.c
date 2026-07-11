/*
 * pets.c — CircleAI.Pets (C11 port of PetsPrimitives.cs).
 *
 * InMemoryPetsBoard: pets (PetId keyed), vaccinations + weights (flat append
 * lists), appointments (ApptId keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/pets.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_pet_free(ca_pet_t *p) {
    if (!p) return;
    free(p->pet_id);
    free(p->name);
    free(p->species);
    free(p->breed);
    p->pet_id = p->name = p->species = p->breed = NULL;
    p->has_breed = false;
}
void ca_pet_free_array(ca_pet_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pet_free(&arr[i]);
    free(arr);
}

static bool pet_copy(ca_pet_t *dst, const ca_pet_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->pet_id           = cab_strdup_empty(src->pet_id);
    dst->name             = cab_strdup_empty(src->name);
    dst->species          = cab_strdup_empty(src->species);
    dst->date_of_birth_ms = src->date_of_birth_ms;
    bool ok = dst->pet_id && dst->name && dst->species;
    if (ok && src->has_breed) {
        dst->breed = cab_strdup_empty(src->breed);
        ok = dst->breed != NULL;
        dst->has_breed = ok;
    }
    if (!ok) { ca_pet_free(dst); return false; }
    return true;
}

void ca_pet_vaccination_free(ca_pet_vaccination_t *v) {
    if (!v) return;
    free(v->pet_id);
    free(v->vaccine);
    v->pet_id = v->vaccine = NULL;
}
void ca_pet_vaccination_free_array(ca_pet_vaccination_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pet_vaccination_free(&arr[i]);
    free(arr);
}

static bool vaccination_copy(ca_pet_vaccination_t *dst,
                             const ca_pet_vaccination_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->pet_id             = cab_strdup_empty(src->pet_id);
    dst->vaccine            = cab_strdup_empty(src->vaccine);
    dst->administered_utc_ms = src->administered_utc_ms;
    dst->has_booster_due    = src->has_booster_due;
    dst->booster_due_utc_ms = src->has_booster_due ? src->booster_due_utc_ms : 0;
    if (!dst->pet_id || !dst->vaccine) {
        ca_pet_vaccination_free(dst);
        return false;
    }
    return true;
}

void ca_pet_weight_free(ca_pet_weight_t *w) {
    if (!w) return;
    free(w->pet_id);
    w->pet_id = NULL;
}
void ca_pet_weight_free_array(ca_pet_weight_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pet_weight_free(&arr[i]);
    free(arr);
}

static bool weight_copy(ca_pet_weight_t *dst, const ca_pet_weight_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->pet_id    = cab_strdup_empty(src->pet_id);
    dst->weight_kg = src->weight_kg;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->pet_id) return false;
    return true;
}

void ca_pet_appointment_free(ca_pet_appointment_t *a) {
    if (!a) return;
    free(a->appt_id);
    free(a->pet_id);
    free(a->reason);
    free(a->vet);
    a->appt_id = a->pet_id = a->reason = a->vet = NULL;
}
void ca_pet_appointment_free_array(ca_pet_appointment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pet_appointment_free(&arr[i]);
    free(arr);
}

static bool appointment_copy(ca_pet_appointment_t *dst,
                             const ca_pet_appointment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->appt_id   = cab_strdup_empty(src->appt_id);
    dst->pet_id    = cab_strdup_empty(src->pet_id);
    dst->reason    = cab_strdup_empty(src->reason);
    dst->vet       = cab_strdup_empty(src->vet);
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->appt_id || !dst->pet_id || !dst->reason || !dst->vet) {
        ca_pet_appointment_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_pet_board {
    ca_pet_t             *pets;
    size_t                p_count, p_cap;
    ca_pet_vaccination_t *vax;
    size_t                vax_count, vax_cap;
    ca_pet_weight_t      *weights;
    size_t                w_count, w_cap;
    ca_pet_appointment_t *appts;
    size_t                a_count, a_cap;
};

ca_pet_board_t *ca_pet_board_create(void) {
    return (ca_pet_board_t *)calloc(1, sizeof(ca_pet_board_t));
}
void ca_pet_board_destroy(ca_pet_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i)   ca_pet_free(&b->pets[i]);
    for (size_t i = 0; i < b->vax_count; ++i) ca_pet_vaccination_free(&b->vax[i]);
    for (size_t i = 0; i < b->w_count; ++i)   ca_pet_weight_free(&b->weights[i]);
    for (size_t i = 0; i < b->a_count; ++i)   ca_pet_appointment_free(&b->appts[i]);
    free(b->pets);
    free(b->vax);
    free(b->weights);
    free(b->appts);
    free(b);
}

int ca_pet_board_add(ca_pet_board_t *b, const ca_pet_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->pets[i].pet_id, p->pet_id)) {
            ca_pet_t copy;
            if (!pet_copy(&copy, p)) return -1;
            ca_pet_free(&b->pets[i]);
            b->pets[i] = copy;
            return 0;
        }
    }
    ca_pet_t copy;
    if (!pet_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->pets, nc * sizeof(*b->pets));
        if (!n) { ca_pet_free(&copy); return -1; }
        b->pets = (ca_pet_t *)n;
        b->p_cap = nc;
    }
    b->pets[b->p_count++] = copy;
    return 0;
}

bool ca_pet_board_get_pet(const ca_pet_board_t *b, const char *id,
                          ca_pet_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->pets[i].pet_id, id))
            return pet_copy(out, &b->pets[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void pet_sort_name(const ca_pet_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(b->pets[idx[j - 1]].name, b->pets[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_pet_t *ca_pet_board_pets(const ca_pet_board_t *b, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->p_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->p_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    pet_sort_name(b, idx, n);

    ca_pet_t *out = (ca_pet_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!pet_copy(&out[i], &b->pets[idx[i]])) {
            ca_pet_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_pet_board_record_vaccination(ca_pet_board_t *b,
                                    const ca_pet_vaccination_t *v) {
    if (!b || !v) return -1;
    ca_pet_vaccination_t copy;
    if (!vaccination_copy(&copy, v)) return -1;
    if (b->vax_count == b->vax_cap) {
        size_t nc = b->vax_cap ? b->vax_cap * 2 : 4;
        void *n = realloc(b->vax, nc * sizeof(*b->vax));
        if (!n) { ca_pet_vaccination_free(&copy); return -1; }
        b->vax = (ca_pet_vaccination_t *)n;
        b->vax_cap = nc;
    }
    b->vax[b->vax_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AdministeredUtc. */
static void vax_sort_desc(const ca_pet_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->vax[key].administered_utc_ms;
        size_t j = i;
        while (j > 0 && b->vax[idx[j - 1]].administered_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_pet_vaccination_t *ca_pet_board_vaccinations_for(const ca_pet_board_t *b,
                                                    const char *pet_id,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !pet_id) { *out_count = (size_t)-1; return NULL; }
    if (b->vax_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->vax_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->vax_count; ++i)
        if (cab_ord_eq(b->vax[i].pet_id, pet_id)) idx[n++] = i;
    vax_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_pet_vaccination_t *out = (ca_pet_vaccination_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!vaccination_copy(&out[i], &b->vax[idx[i]])) {
            ca_pet_vaccination_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_pet_board_record_weight(ca_pet_board_t *b, const ca_pet_weight_t *s) {
    if (!b || !s) return -1;
    ca_pet_weight_t copy;
    if (!weight_copy(&copy, s)) return -1;
    if (b->w_count == b->w_cap) {
        size_t nc = b->w_cap ? b->w_cap * 2 : 4;
        void *n = realloc(b->weights, nc * sizeof(*b->weights));
        if (!n) { ca_pet_weight_free(&copy); return -1; }
        b->weights = (ca_pet_weight_t *)n;
        b->w_cap = nc;
    }
    b->weights[b->w_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void weight_sort_asc(const ca_pet_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->weights[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->weights[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_pet_weight_t *ca_pet_board_weight_history(const ca_pet_board_t *b,
                                             const char *pet_id,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !pet_id) { *out_count = (size_t)-1; return NULL; }
    if (b->w_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->w_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->w_count; ++i)
        if (cab_ord_eq(b->weights[i].pet_id, pet_id)) idx[n++] = i;
    weight_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_pet_weight_t *out = (ca_pet_weight_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!weight_copy(&out[i], &b->weights[idx[i]])) {
            ca_pet_weight_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_pet_board_schedule(ca_pet_board_t *b, const ca_pet_appointment_t *a) {
    if (!b || !a) return -1;
    for (size_t i = 0; i < b->a_count; ++i) {
        if (cab_ord_eq(b->appts[i].appt_id, a->appt_id)) {
            ca_pet_appointment_t copy;
            if (!appointment_copy(&copy, a)) return -1;
            ca_pet_appointment_free(&b->appts[i]);
            b->appts[i] = copy;
            return 0;
        }
    }
    ca_pet_appointment_t copy;
    if (!appointment_copy(&copy, a)) return -1;
    if (b->a_count == b->a_cap) {
        size_t nc = b->a_cap ? b->a_cap * 2 : 4;
        void *n = realloc(b->appts, nc * sizeof(*b->appts));
        if (!n) { ca_pet_appointment_free(&copy); return -1; }
        b->appts = (ca_pet_appointment_t *)n;
        b->a_cap = nc;
    }
    b->appts[b->a_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void appt_sort_asc(const ca_pet_board_t *b, size_t *idx, size_t n) {
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

ca_pet_appointment_t *ca_pet_board_upcoming_appointments(const ca_pet_board_t *b,
                                                         int64_t now_ms,
                                                         size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->a_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i)
        if (b->appts[i].at_utc_ms >= now_ms) idx[n++] = i;
    appt_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_pet_appointment_t *out = (ca_pet_appointment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!appointment_copy(&out[i], &b->appts[idx[i]])) {
            ca_pet_appointment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
