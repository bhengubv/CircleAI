/*
 * commerce_accounting.c — CircleAI.Commerce.Accounting (C11 port of
 * AccountingPrimitives.cs).
 *
 * InMemoryAccountingBoard: entries in an appended list, tax rates keyed by Code.
 * Balances are sums of (Debit - Credit). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/commerce_accounting.h"
#include "board_common.h"

/* ── AccountingEntry ────────────────────────────────────────────────────── */

void ca_acct_entry_free(ca_acct_entry_t *e) {
    if (!e) return;
    free(e->entry_id);
    free(e->account_code);
    free(e->memo);
    e->entry_id = e->account_code = e->memo = NULL;
}
void ca_acct_entry_free_array(ca_acct_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_acct_entry_free(&arr[i]);
    free(arr);
}

static bool entry_copy(ca_acct_entry_t *dst, const ca_acct_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->entry_id     = cab_strdup_empty(src->entry_id);
    dst->account_code = cab_strdup_empty(src->account_code);
    dst->memo         = cab_strdup_empty(src->memo);
    dst->at_utc_ms    = src->at_utc_ms;
    dst->year         = src->year;
    dst->month        = src->month;
    dst->debit_amount  = src->debit_amount;
    dst->credit_amount = src->credit_amount;
    if (!dst->entry_id || !dst->account_code || !dst->memo) {
        ca_acct_entry_free(dst);
        return false;
    }
    return true;
}

/* ── TaxRate ────────────────────────────────────────────────────────────── */

void ca_acct_tax_rate_free(ca_acct_tax_rate_t *r) {
    if (!r) return;
    free(r->code);
    r->code = NULL;
}

static bool tax_rate_copy(ca_acct_tax_rate_t *dst,
                          const ca_acct_tax_rate_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->code       = cab_strdup_empty(src->code);
    dst->percentage = src->percentage;
    if (!dst->code) return false;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_acct_board {
    ca_acct_entry_t    *entries;
    size_t              entry_count, entry_cap;
    ca_acct_tax_rate_t *tax;
    size_t              tax_count, tax_cap;
};

ca_acct_board_t *ca_acct_board_create(void) {
    return (ca_acct_board_t *)calloc(1, sizeof(ca_acct_board_t));
}
void ca_acct_board_destroy(ca_acct_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->entry_count; ++i) ca_acct_entry_free(&b->entries[i]);
    for (size_t i = 0; i < b->tax_count; ++i)   ca_acct_tax_rate_free(&b->tax[i]);
    free(b->entries);
    free(b->tax);
    free(b);
}

int ca_acct_board_post(ca_acct_board_t *b, const ca_acct_entry_t *e) {
    if (!b || !e) return -1;
    /* ArgumentException("amounts must be non-negative"). */
    if (e->debit_amount < 0 || e->credit_amount < 0) return 2;
    ca_acct_entry_t copy;
    if (!entry_copy(&copy, e)) return -1;
    if (b->entry_count == b->entry_cap) {
        size_t nc = b->entry_cap ? b->entry_cap * 2 : 4;
        void *n = realloc(b->entries, nc * sizeof(*b->entries));
        if (!n) { ca_acct_entry_free(&copy); return -1; }
        b->entries = (ca_acct_entry_t *)n;
        b->entry_cap = nc;
    }
    b->entries[b->entry_count++] = copy;
    return 0;
}

int ca_acct_board_define_tax(ca_acct_board_t *b, const ca_acct_tax_rate_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->tax_count; ++i) {
        if (cab_ord_eq(b->tax[i].code, r->code)) {
            ca_acct_tax_rate_t copy;
            if (!tax_rate_copy(&copy, r)) return -1;
            ca_acct_tax_rate_free(&b->tax[i]);
            b->tax[i] = copy;
            return 0;
        }
    }
    ca_acct_tax_rate_t copy;
    if (!tax_rate_copy(&copy, r)) return -1;
    if (b->tax_count == b->tax_cap) {
        size_t nc = b->tax_cap ? b->tax_cap * 2 : 4;
        void *n = realloc(b->tax, nc * sizeof(*b->tax));
        if (!n) { ca_acct_tax_rate_free(&copy); return -1; }
        b->tax = (ca_acct_tax_rate_t *)n;
        b->tax_cap = nc;
    }
    b->tax[b->tax_count++] = copy;
    return 0;
}

bool ca_acct_board_get_tax(const ca_acct_board_t *b, const char *code,
                           ca_acct_tax_rate_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !code || !out) return false;
    for (size_t i = 0; i < b->tax_count; ++i)
        if (cab_ord_eq(b->tax[i].code, code))
            return tax_rate_copy(out, &b->tax[i]);
    return false;
}

ca_acct_decimal_t ca_acct_board_account_balance(const ca_acct_board_t *b,
                                                const char *account_code) {
    if (!b || !account_code) return 0;
    ca_acct_decimal_t sum = 0;
    for (size_t i = 0; i < b->entry_count; ++i)
        if (cab_ord_eq(b->entries[i].account_code, account_code))
            sum += b->entries[i].debit_amount - b->entries[i].credit_amount;
    return sum;
}

ca_acct_decimal_t ca_acct_board_sum(const ca_acct_board_t *b,
                                    const char *account_code,
                                    int year, int month) {
    if (!b || !account_code) return 0;
    ca_acct_decimal_t sum = 0;
    for (size_t i = 0; i < b->entry_count; ++i)
        if (cab_ord_eq(b->entries[i].account_code, account_code) &&
            b->entries[i].year == year && b->entries[i].month == month)
            sum += b->entries[i].debit_amount - b->entries[i].credit_amount;
    return sum;
}

/* Stable ascending sort of collected entry indices by at_utc_ms. */
static void entry_sort_asc(const ca_acct_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->entries[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->entries[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_acct_entry_t *ca_acct_board_for_account(const ca_acct_board_t *b,
                                           const char *account_code,
                                           int year, int month,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !account_code) { *out_count = (size_t)-1; return NULL; }
    if (b->entry_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->entry_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->entry_count; ++i)
        if (cab_ord_eq(b->entries[i].account_code, account_code) &&
            b->entries[i].year == year && b->entries[i].month == month)
            idx[n++] = i;
    entry_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_acct_entry_t *out = (ca_acct_entry_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!entry_copy(&out[i], &b->entries[idx[i]])) {
            ca_acct_entry_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_acct_decimal_t ca_acct_board_net_profit(const ca_acct_board_t *b,
                                           int year, int month,
                                           const char *revenue_account,
                                           const char *expense_account) {
    return ca_acct_board_sum(b, revenue_account, year, month) -
           ca_acct_board_sum(b, expense_account, year, month);
}
