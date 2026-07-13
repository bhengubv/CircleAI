/*
 * beauty.c — CircleAI.Beauty (C11 port of BeautyPrimitives.cs).
 *
 * InMemoryBeautyBoard: treatments (TreatmentId keyed), appointments (append
 * list), skin profiles (ClientName keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/beauty.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_beauty_treatment_free(ca_beauty_treatment_t *t) {
    if (!t) return;
    free(t->treatment_id);
    free(t->name);
    free(t->currency);
    t->treatment_id = t->name = t->currency = NULL;
}
void ca_beauty_treatment_free_array(ca_beauty_treatment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_beauty_treatment_free(&arr[i]);
    free(arr);
}

static bool treatment_copy(ca_beauty_treatment_t *dst,
                           const ca_beauty_treatment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->treatment_id     = cab_strdup_empty(src->treatment_id);
    dst->name             = cab_strdup_empty(src->name);
    dst->duration_minutes = src->duration_minutes;
    dst->price            = src->price;
    dst->currency         = cab_strdup_empty(src->currency);
    if (!dst->treatment_id || !dst->name || !dst->currency) {
        ca_beauty_treatment_free(dst);
        return false;
    }
    return true;
}

void ca_beauty_appointment_free(ca_beauty_appointment_t *a) {
    if (!a) return;
    free(a->appt_id);
    free(a->client_name);
    free(a->treatment_id);
    free(a->notes);
    a->appt_id = a->client_name = a->treatment_id = a->notes = NULL;
    a->has_notes = false;
}
void ca_beauty_appointment_free_array(ca_beauty_appointment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_beauty_appointment_free(&arr[i]);
    free(arr);
}

static bool appointment_copy(ca_beauty_appointment_t *dst,
                             const ca_beauty_appointment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->appt_id      = cab_strdup_empty(src->appt_id);
    dst->client_name  = cab_strdup_empty(src->client_name);
    dst->treatment_id = cab_strdup_empty(src->treatment_id);
    dst->at_utc_ms    = src->at_utc_ms;
    bool ok = dst->appt_id && dst->client_name && dst->treatment_id;
    if (ok && src->has_notes) {
        dst->notes = cab_strdup_empty(src->notes);
        ok = dst->notes != NULL;
        dst->has_notes = ok;
    }
    if (!ok) { ca_beauty_appointment_free(dst); return false; }
    return true;
}

void ca_beauty_skin_profile_free(ca_beauty_skin_profile_t *p) {
    if (!p) return;
    free(p->client_name);
    free(p->skin_type);
    cab_strv_free(p->concerns, p->concern_count);
    p->client_name = p->skin_type = NULL;
    p->concerns = NULL;
    p->concern_count = 0;
}

static bool profile_copy(ca_beauty_skin_profile_t *dst,
                         const ca_beauty_skin_profile_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->client_name = cab_strdup_empty(src->client_name);
    dst->skin_type   = cab_strdup_empty(src->skin_type);
    bool ok = dst->client_name && dst->skin_type;
    if (ok) ok = cab_strv_copy(&dst->concerns, src->concerns, src->concern_count);
    if (ok) dst->concern_count = src->concern_count;
    if (!ok) { ca_beauty_skin_profile_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_beauty_board {
    ca_beauty_treatment_t    *treatments;
    size_t                    t_count, t_cap;
    ca_beauty_appointment_t  *appts;
    size_t                    a_count, a_cap;
    ca_beauty_skin_profile_t *profiles;
    size_t                    p_count, p_cap;
};

ca_beauty_board_t *ca_beauty_board_create(void) {
    return (ca_beauty_board_t *)calloc(1, sizeof(ca_beauty_board_t));
}
void ca_beauty_board_destroy(ca_beauty_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->t_count; ++i) ca_beauty_treatment_free(&b->treatments[i]);
    for (size_t i = 0; i < b->a_count; ++i) ca_beauty_appointment_free(&b->appts[i]);
    for (size_t i = 0; i < b->p_count; ++i) ca_beauty_skin_profile_free(&b->profiles[i]);
    free(b->treatments);
    free(b->appts);
    free(b->profiles);
    free(b);
}

int ca_beauty_board_add_treatment(ca_beauty_board_t *b,
                                  const ca_beauty_treatment_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->treatments[i].treatment_id, t->treatment_id)) {
            ca_beauty_treatment_t copy;
            if (!treatment_copy(&copy, t)) return -1;
            ca_beauty_treatment_free(&b->treatments[i]);
            b->treatments[i] = copy;
            return 0;
        }
    }
    ca_beauty_treatment_t copy;
    if (!treatment_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->treatments, nc * sizeof(*b->treatments));
        if (!n) { ca_beauty_treatment_free(&copy); return -1; }
        b->treatments = (ca_beauty_treatment_t *)n;
        b->t_cap = nc;
    }
    b->treatments[b->t_count++] = copy;
    return 0;
}

bool ca_beauty_board_get_treatment(const ca_beauty_board_t *b, const char *id,
                                   ca_beauty_treatment_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->treatments[i].treatment_id, id))
            return treatment_copy(out, &b->treatments[i]);
    return false;
}

int ca_beauty_board_book(ca_beauty_board_t *b, const ca_beauty_appointment_t *a) {
    if (!b || !a) return -1;
    ca_beauty_appointment_t copy;
    if (!appointment_copy(&copy, a)) return -1;
    if (b->a_count == b->a_cap) {
        size_t nc = b->a_cap ? b->a_cap * 2 : 4;
        void *n = realloc(b->appts, nc * sizeof(*b->appts));
        if (!n) { ca_beauty_appointment_free(&copy); return -1; }
        b->appts = (ca_beauty_appointment_t *)n;
        b->a_cap = nc;
    }
    b->appts[b->a_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void appt_sort_asc(const ca_beauty_board_t *b, size_t *idx, size_t n) {
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

ca_beauty_appointment_t *ca_beauty_board_appointments_between(
    const ca_beauty_board_t *b, int64_t start_ms, int64_t end_ms,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->a_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i) {
        int64_t at = b->appts[i].at_utc_ms;
        if (at >= start_ms && at <= end_ms) idx[n++] = i;
    }
    appt_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_beauty_appointment_t *out = (ca_beauty_appointment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!appointment_copy(&out[i], &b->appts[idx[i]])) {
            ca_beauty_appointment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_beauty_board_save_profile(ca_beauty_board_t *b,
                                 const ca_beauty_skin_profile_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->profiles[i].client_name, p->client_name)) {
            ca_beauty_skin_profile_t copy;
            if (!profile_copy(&copy, p)) return -1;
            ca_beauty_skin_profile_free(&b->profiles[i]);
            b->profiles[i] = copy;
            return 0;
        }
    }
    ca_beauty_skin_profile_t copy;
    if (!profile_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->profiles, nc * sizeof(*b->profiles));
        if (!n) { ca_beauty_skin_profile_free(&copy); return -1; }
        b->profiles = (ca_beauty_skin_profile_t *)n;
        b->p_cap = nc;
    }
    b->profiles[b->p_count++] = copy;
    return 0;
}

bool ca_beauty_board_get_profile(const ca_beauty_board_t *b,
                                 const char *client_name,
                                 ca_beauty_skin_profile_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !client_name || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->profiles[i].client_name, client_name))
            return profile_copy(out, &b->profiles[i]);
    return false;
}

/* Does Name contain any of the profile's Concerns (OrdinalIgnoreCase)? */
static bool name_matches_concerns(const char *name,
                                  const ca_beauty_skin_profile_t *p) {
    for (size_t i = 0; i < p->concern_count; ++i)
        if (cab_ci_contains(name, p->concerns[i])) return true;
    return false;
}

ca_beauty_treatment_t *ca_beauty_board_recommend_for(const ca_beauty_board_t *b,
                                                     const char *client_name,
                                                     size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !client_name) { *out_count = (size_t)-1; return NULL; }

    /* Locate the profile; absent -> empty (C# Array.Empty). */
    const ca_beauty_skin_profile_t *prof = NULL;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->profiles[i].client_name, client_name)) {
            prof = &b->profiles[i];
            break;
        }
    if (!prof || b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i)
        if (name_matches_concerns(b->treatments[i].name, prof)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_beauty_treatment_t *out = (ca_beauty_treatment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!treatment_copy(&out[i], &b->treatments[idx[i]])) {
            ca_beauty_treatment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

size_t ca_beauty_board_treatment_count(const ca_beauty_board_t *b) {
    return b ? b->t_count : 0;
}

bool ca_beauty_board_cancel_appointment(ca_beauty_board_t *b,
                                        const char *appt_id) {
    /* _appts.RemoveAll(a => ApptId == apptId Ordinal) > 0. */
    if (!b || !appt_id) return false;
    size_t w = 0;
    bool removed = false;
    for (size_t i = 0; i < b->a_count; ++i) {
        if (cab_ord_eq(b->appts[i].appt_id, appt_id)) {
            ca_beauty_appointment_free(&b->appts[i]);
            removed = true;
        } else {
            if (w != i) b->appts[w] = b->appts[i];
            w++;
        }
    }
    b->a_count = w;
    return removed;
}

/* Collect the client's appointments (ClientName OrdinalIgnoreCase) into a fresh
 * owned array ordered by AtUtc ascending (stable). */
ca_beauty_appointment_t *ca_beauty_board_appointments_for_client(
    const ca_beauty_board_t *b, const char *client_name, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !client_name) { *out_count = (size_t)-1; return NULL; }
    if (b->a_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i)
        if (cab_ci_eq(b->appts[i].client_name, client_name)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    appt_sort_asc(b, idx, n);

    ca_beauty_appointment_t *out =
        (ca_beauty_appointment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!appointment_copy(&out[i], &b->appts[idx[i]])) {
            ca_beauty_appointment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_beauty_treatment_t *ca_beauty_board_treatments_under(
    const ca_beauty_board_t *b, ca_beauty_decimal_t max_price,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i)
        if (b->treatments[i].price <= max_price) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderBy(Price) ascending, stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        ca_beauty_decimal_t key = b->treatments[cur].price;
        size_t j = i;
        while (j > 0 && b->treatments[idx[j - 1]].price > key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_beauty_treatment_t *out =
        (ca_beauty_treatment_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!treatment_copy(&out[i], &b->treatments[idx[i]])) {
            ca_beauty_treatment_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_beauty_board_next_appointment_for(const ca_beauty_board_t *b,
                                          const char *client_name,
                                          int64_t now_ms,
                                          ca_beauty_appointment_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !client_name || !out) return false;
    /* Where(ClientName CI && AtUtc >= now).OrderBy(AtUtc).FirstOrDefault():
     * the earliest AtUtc, ties broken by source order (strictly-less update). */
    const ca_beauty_appointment_t *best = NULL;
    for (size_t i = 0; i < b->a_count; ++i) {
        const ca_beauty_appointment_t *a = &b->appts[i];
        if (a->at_utc_ms < now_ms) continue;
        if (!cab_ci_eq(a->client_name, client_name)) continue;
        if (!best || a->at_utc_ms < best->at_utc_ms) best = a;
    }
    if (!best) return false;
    return appointment_copy(out, best);
}

/* Look up a treatment's Price by TreatmentId (Ordinal). *found gates presence. */
static ca_beauty_decimal_t treatment_price(const ca_beauty_board_t *b,
                                           const char *treatment_id,
                                           bool *found) {
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->treatments[i].treatment_id, treatment_id)) {
            *found = true;
            return b->treatments[i].price;
        }
    *found = false;
    return 0;
}

ca_beauty_decimal_t ca_beauty_board_scheduled_revenue_between(
    const ca_beauty_board_t *b, int64_t start_ms, int64_t end_ms) {
    if (!b) return 0;
    ca_beauty_decimal_t sum = 0;
    for (size_t i = 0; i < b->a_count; ++i) {
        int64_t at = b->appts[i].at_utc_ms;
        if (at < start_ms || at > end_ms) continue;
        bool found = false;
        ca_beauty_decimal_t price =
            treatment_price(b, b->appts[i].treatment_id, &found);
        if (found) sum += price;   /* _treatments.ContainsKey(a.TreatmentId) */
    }
    return sum;
}
