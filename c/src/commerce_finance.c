/*
 * commerce_finance.c — CircleAI.Commerce.Finance (C11 port of
 * FinancePrimitives.cs).
 *
 * InMemoryInvoiceBoard: invoices keyed by InvoiceId, payments in an appended
 * list. RemainingOn = billed - paid, where billed sums each line's
 * Amount * (1 + TaxPct/100) rounded to the micro-unit. Pure C11 + libc + libm.
 * No pthreads.
 */

#include "circle_ai/commerce_finance.h"
#include "board_common.h"

#include <math.h>

/* ── InvoiceLine (value type, no dynamic ownership beyond Description) ───── */

static void invoice_line_free(ca_invoice_line_t *l) {
    if (!l) return;
    free(l->description);
    l->description = NULL;
}

static bool invoice_line_copy(ca_invoice_line_t *dst,
                              const ca_invoice_line_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->description = cab_strdup_empty(src->description);
    dst->amount      = src->amount;
    dst->tax_pct     = src->tax_pct;
    if (!dst->description) return false;
    return true;
}

/* ── Invoice ────────────────────────────────────────────────────────────── */

void ca_invoice_free(ca_invoice_t *i) {
    if (!i) return;
    free(i->invoice_id);
    free(i->customer_id);
    free(i->currency);
    free(i->status);
    if (i->lines) {
        for (size_t k = 0; k < i->line_count; ++k) invoice_line_free(&i->lines[k]);
        free(i->lines);
    }
    i->invoice_id = i->customer_id = i->currency = i->status = NULL;
    i->lines = NULL;
    i->line_count = 0;
}
void ca_invoice_free_array(ca_invoice_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_invoice_free(&arr[i]);
    free(arr);
}

static bool invoice_copy(ca_invoice_t *dst, const ca_invoice_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->invoice_id  = cab_strdup_empty(src->invoice_id);
    dst->customer_id = cab_strdup_empty(src->customer_id);
    dst->currency    = cab_strdup_empty(src->currency);
    dst->status      = cab_strdup_empty(src->status);
    dst->issue_date_ms = src->issue_date_ms;
    dst->due_date_ms   = src->due_date_ms;
    if (!dst->invoice_id || !dst->customer_id || !dst->currency || !dst->status) {
        ca_invoice_free(dst);
        return false;
    }
    if (src->line_count > 0) {
        dst->lines = (ca_invoice_line_t *)calloc(src->line_count,
                                                 sizeof(*dst->lines));
        if (!dst->lines) { ca_invoice_free(dst); return false; }
        for (size_t k = 0; k < src->line_count; ++k) {
            if (!invoice_line_copy(&dst->lines[k], &src->lines[k])) {
                /* free the already-copied lines then the rest of the invoice. */
                for (size_t j = 0; j < k; ++j) invoice_line_free(&dst->lines[j]);
                free(dst->lines);
                dst->lines = NULL;
                dst->line_count = 0;
                ca_invoice_free(dst);
                return false;
            }
        }
        dst->line_count = src->line_count;
    }
    return true;
}

/* ── FinancePayment ─────────────────────────────────────────────────────── */

void ca_invoice_payment_free(ca_invoice_payment_t *p) {
    if (!p) return;
    free(p->payment_id);
    free(p->invoice_id);
    p->payment_id = p->invoice_id = NULL;
}

static bool payment_copy(ca_invoice_payment_t *dst,
                         const ca_invoice_payment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->payment_id = cab_strdup_empty(src->payment_id);
    dst->invoice_id = cab_strdup_empty(src->invoice_id);
    dst->amount     = src->amount;
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->payment_id || !dst->invoice_id) {
        ca_invoice_payment_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_invoice_board {
    ca_invoice_t         *invoices;
    size_t                inv_count, inv_cap;
    ca_invoice_payment_t *payments;
    size_t                pay_count, pay_cap;
};

ca_invoice_board_t *ca_invoice_board_create(void) {
    return (ca_invoice_board_t *)calloc(1, sizeof(ca_invoice_board_t));
}
void ca_invoice_board_destroy(ca_invoice_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->inv_count; ++i) ca_invoice_free(&b->invoices[i]);
    for (size_t i = 0; i < b->pay_count; ++i) ca_invoice_payment_free(&b->payments[i]);
    free(b->invoices);
    free(b->payments);
    free(b);
}

static size_t invoice_index_of(const ca_invoice_board_t *b, const char *id) {
    for (size_t i = 0; i < b->inv_count; ++i)
        if (cab_ord_eq(b->invoices[i].invoice_id, id)) return i;
    return (size_t)-1;
}

int ca_invoice_board_issue(ca_invoice_board_t *b, const ca_invoice_t *i) {
    if (!b || !i) return -1;
    size_t idx = invoice_index_of(b, i->invoice_id);
    ca_invoice_t copy;
    if (!invoice_copy(&copy, i)) return -1;
    if (idx != (size_t)-1) {
        ca_invoice_free(&b->invoices[idx]);
        b->invoices[idx] = copy;
        return 0;
    }
    if (b->inv_count == b->inv_cap) {
        size_t nc = b->inv_cap ? b->inv_cap * 2 : 4;
        void *n = realloc(b->invoices, nc * sizeof(*b->invoices));
        if (!n) { ca_invoice_free(&copy); return -1; }
        b->invoices = (ca_invoice_t *)n;
        b->inv_cap = nc;
    }
    b->invoices[b->inv_count++] = copy;
    return 0;
}

bool ca_invoice_board_get(const ca_invoice_board_t *b, const char *invoice_id,
                          ca_invoice_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !invoice_id || !out) return false;
    size_t idx = invoice_index_of(b, invoice_id);
    if (idx == (size_t)-1) return false;
    return invoice_copy(out, &b->invoices[idx]);
}

int ca_invoice_board_record_payment(ca_invoice_board_t *b,
                                    const ca_invoice_payment_t *p) {
    if (!b || !p) return -1;
    ca_invoice_payment_t copy;
    if (!payment_copy(&copy, p)) return -1;
    if (b->pay_count == b->pay_cap) {
        size_t nc = b->pay_cap ? b->pay_cap * 2 : 4;
        void *n = realloc(b->payments, nc * sizeof(*b->payments));
        if (!n) { ca_invoice_payment_free(&copy); return -1; }
        b->payments = (ca_invoice_payment_t *)n;
        b->pay_cap = nc;
    }
    b->payments[b->pay_count++] = copy;
    return 0;
}

void ca_invoice_board_mark_overdue(ca_invoice_board_t *b, int64_t as_of_ms) {
    if (!b) return;
    for (size_t i = 0; i < b->inv_count; ++i) {
        ca_invoice_t *inv = &b->invoices[i];
        if (inv->due_date_ms < as_of_ms && !cab_ci_eq(inv->status, "Paid")) {
            char *ns = cab_strdup("Overdue");
            if (!ns) return;   /* OOM: leave state unchanged */
            free(inv->status);
            inv->status = ns;
        }
    }
}

/* billed = Sum(Amount * (1 + TaxPct/100)) over lines, each line's product rounded
 * to the nearest micro-unit (round-half-away-from-zero). */
static ca_invoice_decimal_t invoice_billed(const ca_invoice_t *inv) {
    ca_invoice_decimal_t billed = 0;
    for (size_t k = 0; k < inv->line_count; ++k) {
        double factor = 1.0 + inv->lines[k].tax_pct / 100.0;
        double prod = (double)inv->lines[k].amount * factor;
        double rounded = prod < 0 ? ceil(prod - 0.5) : floor(prod + 0.5);
        billed += (ca_invoice_decimal_t)rounded;
    }
    return billed;
}

static ca_invoice_decimal_t invoice_paid(const ca_invoice_board_t *b,
                                         const char *invoice_id) {
    ca_invoice_decimal_t paid = 0;
    for (size_t i = 0; i < b->pay_count; ++i)
        if (cab_ord_eq(b->payments[i].invoice_id, invoice_id))
            paid += b->payments[i].amount;
    return paid;
}

ca_invoice_decimal_t ca_invoice_board_remaining_on(const ca_invoice_board_t *b,
                                                   const char *invoice_id) {
    if (!b || !invoice_id) return 0;
    size_t idx = invoice_index_of(b, invoice_id);
    if (idx == (size_t)-1) return 0;   /* unknown invoice -> 0 */
    return invoice_billed(&b->invoices[idx]) - invoice_paid(b, invoice_id);
}

ca_invoice_decimal_t ca_invoice_board_total_outstanding(
    const ca_invoice_board_t *b) {
    if (!b) return 0;
    ca_invoice_decimal_t total = 0;
    for (size_t i = 0; i < b->inv_count; ++i)
        total += ca_invoice_board_remaining_on(b, b->invoices[i].invoice_id);
    return total;
}

ca_invoice_t *ca_invoice_board_overdue(const ca_invoice_board_t *b,
                                       size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->inv_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->inv_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->inv_count; ++i)
        if (cab_ci_eq(b->invoices[i].status, "Overdue")) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_invoice_t *out = (ca_invoice_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!invoice_copy(&out[i], &b->invoices[idx[i]])) {
            ca_invoice_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
