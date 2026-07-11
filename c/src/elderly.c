/*
 * elderly.c — CircleAI.Elderly (C11 port of ElderlyPrimitives.cs).
 *
 * InMemoryElderlyCareBoard: care plans (ResidentName keyed), reminders
 * (ReminderId keyed), check-ins (flat append list). Pure C11 + libc.
 */

#include "circle_ai/elderly.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_eld_care_plan_free(ca_eld_care_plan_t *p) {
    if (!p) return;
    free(p->plan_id);
    free(p->resident_name);
    cab_strv_free(p->medical_conditions, p->medical_condition_count);
    cab_strv_free(p->allergies, p->allergy_count);
    free(p->carer_notes);
    p->plan_id = p->resident_name = p->carer_notes = NULL;
    p->medical_conditions = p->allergies = NULL;
    p->medical_condition_count = p->allergy_count = 0;
}

static bool care_plan_copy(ca_eld_care_plan_t *dst,
                           const ca_eld_care_plan_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->plan_id       = cab_strdup_empty(src->plan_id);
    dst->resident_name = cab_strdup_empty(src->resident_name);
    dst->carer_notes   = cab_strdup_empty(src->carer_notes);
    if (!dst->plan_id || !dst->resident_name || !dst->carer_notes) {
        ca_eld_care_plan_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->medical_conditions, src->medical_conditions,
                       src->medical_condition_count)) {
        ca_eld_care_plan_free(dst);
        return false;
    }
    dst->medical_condition_count = src->medical_condition_count;
    if (!cab_strv_copy(&dst->allergies, src->allergies, src->allergy_count)) {
        ca_eld_care_plan_free(dst);
        return false;
    }
    dst->allergy_count = src->allergy_count;
    return true;
}

void ca_eld_reminder_free(ca_eld_reminder_t *r) {
    if (!r) return;
    free(r->reminder_id);
    free(r->resident_name);
    free(r->medication);
    r->reminder_id = r->resident_name = r->medication = NULL;
}
void ca_eld_reminder_free_array(ca_eld_reminder_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_eld_reminder_free(&arr[i]);
    free(arr);
}

static bool reminder_copy(ca_eld_reminder_t *dst, const ca_eld_reminder_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->reminder_id   = cab_strdup_empty(src->reminder_id);
    dst->resident_name = cab_strdup_empty(src->resident_name);
    dst->medication    = cab_strdup_empty(src->medication);
    dst->daily_at_ms   = src->daily_at_ms;
    dst->active        = src->active;
    if (!dst->reminder_id || !dst->resident_name || !dst->medication) {
        ca_eld_reminder_free(dst);
        return false;
    }
    return true;
}

void ca_eld_check_in_free(ca_eld_check_in_t *c) {
    if (!c) return;
    free(c->check_in_id);
    free(c->resident_name);
    free(c->status);
    free(c->note);
    c->check_in_id = c->resident_name = c->status = c->note = NULL;
    c->has_note = false;
}

static bool check_in_copy(ca_eld_check_in_t *dst, const ca_eld_check_in_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->check_in_id   = cab_strdup_empty(src->check_in_id);
    dst->resident_name = cab_strdup_empty(src->resident_name);
    dst->status        = cab_strdup_empty(src->status);
    dst->at_utc_ms     = src->at_utc_ms;
    bool ok = dst->check_in_id && dst->resident_name && dst->status;
    if (ok && src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        ok = dst->note != NULL;
        dst->has_note = ok;
    }
    if (!ok) { ca_eld_check_in_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_eld_board {
    ca_eld_care_plan_t *plans;
    size_t              p_count, p_cap;
    ca_eld_reminder_t  *reminders;
    size_t              r_count, r_cap;
    ca_eld_check_in_t  *check_ins;
    size_t              c_count, c_cap;
};

ca_eld_board_t *ca_eld_board_create(void) {
    return (ca_eld_board_t *)calloc(1, sizeof(ca_eld_board_t));
}
void ca_eld_board_destroy(ca_eld_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i) ca_eld_care_plan_free(&b->plans[i]);
    for (size_t i = 0; i < b->r_count; ++i) ca_eld_reminder_free(&b->reminders[i]);
    for (size_t i = 0; i < b->c_count; ++i) ca_eld_check_in_free(&b->check_ins[i]);
    free(b->plans);
    free(b->reminders);
    free(b->check_ins);
    free(b);
}

int ca_eld_board_set_plan(ca_eld_board_t *b, const ca_eld_care_plan_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->plans[i].resident_name, p->resident_name)) {
            ca_eld_care_plan_t copy;
            if (!care_plan_copy(&copy, p)) return -1;
            ca_eld_care_plan_free(&b->plans[i]);
            b->plans[i] = copy;
            return 0;
        }
    }
    ca_eld_care_plan_t copy;
    if (!care_plan_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->plans, nc * sizeof(*b->plans));
        if (!n) { ca_eld_care_plan_free(&copy); return -1; }
        b->plans = (ca_eld_care_plan_t *)n;
        b->p_cap = nc;
    }
    b->plans[b->p_count++] = copy;
    return 0;
}

bool ca_eld_board_get_plan(const ca_eld_board_t *b, const char *resident,
                           ca_eld_care_plan_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !resident || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->plans[i].resident_name, resident))
            return care_plan_copy(out, &b->plans[i]);
    return false;
}

int ca_eld_board_add_reminder(ca_eld_board_t *b, const ca_eld_reminder_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->reminders[i].reminder_id, r->reminder_id)) {
            ca_eld_reminder_t copy;
            if (!reminder_copy(&copy, r)) return -1;
            ca_eld_reminder_free(&b->reminders[i]);
            b->reminders[i] = copy;
            return 0;
        }
    }
    ca_eld_reminder_t copy;
    if (!reminder_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->reminders, nc * sizeof(*b->reminders));
        if (!n) { ca_eld_reminder_free(&copy); return -1; }
        b->reminders = (ca_eld_reminder_t *)n;
        b->r_cap = nc;
    }
    b->reminders[b->r_count++] = copy;
    return 0;
}

int ca_eld_board_deactivate_reminder(ca_eld_board_t *b,
                                     const char *reminder_id) {
    if (!b || !reminder_id) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->reminders[i].reminder_id, reminder_id)) {
            b->reminders[i].active = false;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown reminder */
}

ca_eld_reminder_t *ca_eld_board_active_reminders_for(const ca_eld_board_t *b,
                                                     const char *resident,
                                                     size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !resident) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ord_eq(b->reminders[i].resident_name, resident) &&
            b->reminders[i].active)
            idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_eld_reminder_t *out = (ca_eld_reminder_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!reminder_copy(&out[i], &b->reminders[idx[i]])) {
            ca_eld_reminder_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_eld_board_record_check_in(ca_eld_board_t *b, const ca_eld_check_in_t *c) {
    if (!b || !c) return -1;
    ca_eld_check_in_t copy;
    if (!check_in_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->check_ins, nc * sizeof(*b->check_ins));
        if (!n) { ca_eld_check_in_free(&copy); return -1; }
        b->check_ins = (ca_eld_check_in_t *)n;
        b->c_cap = nc;
    }
    b->check_ins[b->c_count++] = copy;
    return 0;
}

/* Index of the newest check-in for resident, or SIZE_MAX when none. */
static size_t latest_check_in_index(const ca_eld_board_t *b,
                                    const char *resident) {
    size_t best = (size_t)-1;
    int64_t best_at = 0;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->check_ins[i].resident_name, resident)) {
            if (best == (size_t)-1 || b->check_ins[i].at_utc_ms > best_at) {
                best = i;
                best_at = b->check_ins[i].at_utc_ms;
            }
        }
    }
    return best;
}

bool ca_eld_board_latest_check_in(const ca_eld_board_t *b, const char *resident,
                                  ca_eld_check_in_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !resident || !out) return false;
    size_t i = latest_check_in_index(b, resident);
    if (i == (size_t)-1) return false;
    return check_in_copy(out, &b->check_ins[i]);
}

bool ca_eld_board_missed_check_in(const ca_eld_board_t *b, const char *resident,
                                  int64_t since_ms) {
    if (!b || !resident) return true; /* no latest => missed */
    size_t i = latest_check_in_index(b, resident);
    if (i == (size_t)-1) return true;
    return b->check_ins[i].at_utc_ms < since_ms;
}
