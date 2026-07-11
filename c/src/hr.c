/*
 * hr.c — CircleAI.HR (C11 port of HRPrimitives.cs).
 *
 * InMemoryHRBoard over three linear stores: employees (EmployeeId keyed),
 * leaves (RequestId keyed), reviews (flat append list). Pure C11 + libc.
 */

#include "circle_ai/hr.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_hr_employee_free(ca_hr_employee_t *e) {
    if (!e) return;
    free(e->employee_id);
    free(e->name);
    free(e->role);
    free(e->currency);
    e->employee_id = e->name = e->role = e->currency = NULL;
}
void ca_hr_employee_free_array(ca_hr_employee_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hr_employee_free(&arr[i]);
    free(arr);
}

static bool employee_copy(ca_hr_employee_t *dst, const ca_hr_employee_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->employee_id = cab_strdup_empty(src->employee_id);
    dst->name        = cab_strdup_empty(src->name);
    dst->role        = cab_strdup_empty(src->role);
    dst->currency    = cab_strdup_empty(src->currency);
    dst->hired_on_ms = src->hired_on_ms;
    dst->salary      = src->salary;
    if (!dst->employee_id || !dst->name || !dst->role || !dst->currency) {
        ca_hr_employee_free(dst);
        return false;
    }
    return true;
}

void ca_hr_leave_free(ca_hr_leave_t *r) {
    if (!r) return;
    free(r->request_id);
    free(r->employee_id);
    free(r->kind);
    free(r->status);
    r->request_id = r->employee_id = r->kind = r->status = NULL;
}
void ca_hr_leave_free_array(ca_hr_leave_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hr_leave_free(&arr[i]);
    free(arr);
}

static bool leave_copy(ca_hr_leave_t *dst, const ca_hr_leave_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->request_id  = cab_strdup_empty(src->request_id);
    dst->employee_id = cab_strdup_empty(src->employee_id);
    dst->kind        = cab_strdup_empty(src->kind);
    dst->status      = cab_strdup_empty(src->status);
    dst->from_ms     = src->from_ms;
    dst->to_ms       = src->to_ms;
    if (!dst->request_id || !dst->employee_id || !dst->kind || !dst->status) {
        ca_hr_leave_free(dst);
        return false;
    }
    return true;
}

void ca_hr_review_free(ca_hr_review_t *r) {
    if (!r) return;
    free(r->review_id);
    free(r->employee_id);
    free(r->notes);
    r->review_id = r->employee_id = r->notes = NULL;
}

static bool review_copy(ca_hr_review_t *dst, const ca_hr_review_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->review_id      = cab_strdup_empty(src->review_id);
    dst->employee_id    = cab_strdup_empty(src->employee_id);
    dst->notes          = cab_strdup_empty(src->notes);
    dst->reviewed_on_ms = src->reviewed_on_ms;
    dst->rating_out_of_5 = src->rating_out_of_5;
    if (!dst->review_id || !dst->employee_id || !dst->notes) {
        ca_hr_review_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_hr_board {
    ca_hr_employee_t *employees;
    size_t            emp_count, emp_cap;
    ca_hr_leave_t    *leaves;
    size_t            lv_count, lv_cap;
    ca_hr_review_t   *reviews;
    size_t            rv_count, rv_cap;
};

ca_hr_board_t *ca_hr_board_create(void) {
    return (ca_hr_board_t *)calloc(1, sizeof(ca_hr_board_t));
}
void ca_hr_board_destroy(ca_hr_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->emp_count; ++i) ca_hr_employee_free(&b->employees[i]);
    for (size_t i = 0; i < b->lv_count; ++i)  ca_hr_leave_free(&b->leaves[i]);
    for (size_t i = 0; i < b->rv_count; ++i)  ca_hr_review_free(&b->reviews[i]);
    free(b->employees);
    free(b->leaves);
    free(b->reviews);
    free(b);
}

int ca_hr_board_hire(ca_hr_board_t *b, const ca_hr_employee_t *e) {
    if (!b || !e) return -1;
    for (size_t i = 0; i < b->emp_count; ++i) {
        if (cab_ord_eq(b->employees[i].employee_id, e->employee_id)) {
            ca_hr_employee_t copy;
            if (!employee_copy(&copy, e)) return -1;
            ca_hr_employee_free(&b->employees[i]);
            b->employees[i] = copy;
            return 0;
        }
    }
    ca_hr_employee_t copy;
    if (!employee_copy(&copy, e)) return -1;
    if (b->emp_count == b->emp_cap) {
        size_t nc = b->emp_cap ? b->emp_cap * 2 : 4;
        void *n = realloc(b->employees, nc * sizeof(*b->employees));
        if (!n) { ca_hr_employee_free(&copy); return -1; }
        b->employees = (ca_hr_employee_t *)n;
        b->emp_cap = nc;
    }
    b->employees[b->emp_count++] = copy;
    return 0;
}

bool ca_hr_board_get_employee(const ca_hr_board_t *b, const char *id,
                              ca_hr_employee_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->emp_count; ++i)
        if (cab_ord_eq(b->employees[i].employee_id, id))
            return employee_copy(out, &b->employees[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void emp_sort_name(const ca_hr_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->employees[idx[j - 1]].name, b->employees[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_hr_employee_t *ca_hr_board_employees(const ca_hr_board_t *b,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->emp_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->emp_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    emp_sort_name(b, idx, n);

    ca_hr_employee_t *out = (ca_hr_employee_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!employee_copy(&out[i], &b->employees[idx[i]])) {
            ca_hr_employee_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_hr_board_request(ca_hr_board_t *b, const ca_hr_leave_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->lv_count; ++i) {
        if (cab_ord_eq(b->leaves[i].request_id, r->request_id)) {
            ca_hr_leave_t copy;
            if (!leave_copy(&copy, r)) return -1;
            ca_hr_leave_free(&b->leaves[i]);
            b->leaves[i] = copy;
            return 0;
        }
    }
    ca_hr_leave_t copy;
    if (!leave_copy(&copy, r)) return -1;
    if (b->lv_count == b->lv_cap) {
        size_t nc = b->lv_cap ? b->lv_cap * 2 : 4;
        void *n = realloc(b->leaves, nc * sizeof(*b->leaves));
        if (!n) { ca_hr_leave_free(&copy); return -1; }
        b->leaves = (ca_hr_leave_t *)n;
        b->lv_cap = nc;
    }
    b->leaves[b->lv_count++] = copy;
    return 0;
}

int ca_hr_board_decide_leave(ca_hr_board_t *b, const char *request_id,
                             const char *decision) {
    if (!b || !request_id) return -1;
    for (size_t i = 0; i < b->lv_count; ++i) {
        if (cab_ord_eq(b->leaves[i].request_id, request_id)) {
            char *ns = cab_strdup_empty(decision);
            if (!ns) return -1;
            free(b->leaves[i].status);
            b->leaves[i].status = ns;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown leave request */
}

ca_hr_leave_t *ca_hr_board_pending_leaves(const ca_hr_board_t *b,
                                          size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->lv_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->lv_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->lv_count; ++i)
        if (cab_ci_eq(b->leaves[i].status, "Pending")) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_hr_leave_t *out = (ca_hr_leave_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!leave_copy(&out[i], &b->leaves[idx[i]])) {
            ca_hr_leave_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_hr_board_review(ca_hr_board_t *b, const ca_hr_review_t *r) {
    if (!b || !r) return -1;
    ca_hr_review_t copy;
    if (!review_copy(&copy, r)) return -1;
    if (b->rv_count == b->rv_cap) {
        size_t nc = b->rv_cap ? b->rv_cap * 2 : 4;
        void *n = realloc(b->reviews, nc * sizeof(*b->reviews));
        if (!n) { ca_hr_review_free(&copy); return -1; }
        b->reviews = (ca_hr_review_t *)n;
        b->rv_cap = nc;
    }
    b->reviews[b->rv_count++] = copy;
    return 0;
}

double ca_hr_board_avg_rating_for(const ca_hr_board_t *b,
                                  const char *employee_id) {
    if (!b || !employee_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < b->rv_count; ++i) {
        if (cab_ord_eq(b->reviews[i].employee_id, employee_id)) {
            sum += (double)b->reviews[i].rating_out_of_5;
            n++;
        }
    }
    /* DefaultIfEmpty(0).Average() => 0.0 when no reviews. */
    return n == 0 ? 0.0 : sum / (double)n;
}
