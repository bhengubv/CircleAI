/*
 * personal_finance.c — CircleAI.Personal.Finance (C11 port of
 * PersonalFinancePrimitives.cs).
 *
 * InMemoryPersonalFinanceBoard: accounts keyed by AccountId (Ordinal), budgets
 * keyed by Category (OrdinalIgnoreCase), transactions in an appended list.
 * Record appends a txn and applies Balance += Amount. Pure C11 + libc. No
 * pthreads.
 */

#include "circle_ai/personal_finance.h"
#include "board_common.h"

/* ── Account ────────────────────────────────────────────────────────────── */

void ca_pfin_account_free(ca_pfin_account_t *a) {
    if (!a) return;
    free(a->account_id);
    free(a->name);
    free(a->currency);
    a->account_id = a->name = a->currency = NULL;
}

static bool account_copy(ca_pfin_account_t *dst, const ca_pfin_account_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->account_id = cab_strdup_empty(src->account_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->currency   = cab_strdup_empty(src->currency);
    dst->balance    = src->balance;
    if (!dst->account_id || !dst->name || !dst->currency) {
        ca_pfin_account_free(dst);
        return false;
    }
    return true;
}

/* ── FinanceTransaction ─────────────────────────────────────────────────── */

void ca_pfin_txn_free(ca_pfin_txn_t *t) {
    if (!t) return;
    free(t->tx_id);
    free(t->account_id);
    free(t->category);
    free(t->note);
    t->tx_id = t->account_id = t->category = t->note = NULL;
}
void ca_pfin_txn_free_array(ca_pfin_txn_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pfin_txn_free(&arr[i]);
    free(arr);
}

static bool txn_copy(ca_pfin_txn_t *dst, const ca_pfin_txn_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->tx_id      = cab_strdup_empty(src->tx_id);
    dst->account_id = cab_strdup_empty(src->account_id);
    dst->category   = cab_strdup_empty(src->category);
    dst->amount     = src->amount;
    dst->has_note   = src->has_note;
    dst->at_utc_ms  = src->at_utc_ms;
    dst->year       = src->year;
    dst->month      = src->month;
    if (!dst->tx_id || !dst->account_id || !dst->category) {
        ca_pfin_txn_free(dst);
        return false;
    }
    if (src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        if (!dst->note) { ca_pfin_txn_free(dst); return false; }
    }
    return true;
}

/* ── BudgetLine ─────────────────────────────────────────────────────────── */

void ca_pfin_budget_free(ca_pfin_budget_t *b) {
    if (!b) return;
    free(b->category);
    b->category = NULL;
}
void ca_pfin_budget_free_array(ca_pfin_budget_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pfin_budget_free(&arr[i]);
    free(arr);
}

static bool budget_copy(ca_pfin_budget_t *dst, const ca_pfin_budget_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->category      = cab_strdup_empty(src->category);
    dst->monthly_limit = src->monthly_limit;
    if (!dst->category) return false;
    return true;
}

/* ── MonthSummary ───────────────────────────────────────────────────────── */

void ca_pfin_month_summary_free(ca_pfin_month_summary_t *s) {
    if (!s) return;
    if (s->by_category) {
        for (size_t i = 0; i < s->by_category_count; ++i)
            free(s->by_category[i].category);
        free(s->by_category);
    }
    s->by_category = NULL;
    s->by_category_count = 0;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_pfin_board {
    ca_pfin_account_t *accounts;
    size_t             acct_count, acct_cap;
    ca_pfin_budget_t  *budgets;
    size_t             budget_count, budget_cap;
    ca_pfin_txn_t     *txns;
    size_t             txn_count, txn_cap;
};

ca_pfin_board_t *ca_pfin_board_create(void) {
    return (ca_pfin_board_t *)calloc(1, sizeof(ca_pfin_board_t));
}
void ca_pfin_board_destroy(ca_pfin_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->acct_count; ++i)   ca_pfin_account_free(&b->accounts[i]);
    for (size_t i = 0; i < b->budget_count; ++i) ca_pfin_budget_free(&b->budgets[i]);
    for (size_t i = 0; i < b->txn_count; ++i)    ca_pfin_txn_free(&b->txns[i]);
    free(b->accounts);
    free(b->budgets);
    free(b->txns);
    free(b);
}

static size_t acct_index_of(const ca_pfin_board_t *b, const char *id) {
    for (size_t i = 0; i < b->acct_count; ++i)
        if (cab_ord_eq(b->accounts[i].account_id, id)) return i;
    return (size_t)-1;
}

int ca_pfin_board_upsert(ca_pfin_board_t *b, const ca_pfin_account_t *a) {
    if (!b || !a) return -1;
    size_t idx = acct_index_of(b, a->account_id);
    ca_pfin_account_t copy;
    if (!account_copy(&copy, a)) return -1;
    if (idx != (size_t)-1) {
        ca_pfin_account_free(&b->accounts[idx]);
        b->accounts[idx] = copy;
        return 0;
    }
    if (b->acct_count == b->acct_cap) {
        size_t nc = b->acct_cap ? b->acct_cap * 2 : 4;
        void *n = realloc(b->accounts, nc * sizeof(*b->accounts));
        if (!n) { ca_pfin_account_free(&copy); return -1; }
        b->accounts = (ca_pfin_account_t *)n;
        b->acct_cap = nc;
    }
    b->accounts[b->acct_count++] = copy;
    return 0;
}

bool ca_pfin_board_get_account(const ca_pfin_board_t *b, const char *id,
                               ca_pfin_account_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    size_t idx = acct_index_of(b, id);
    if (idx == (size_t)-1) return false;
    return account_copy(out, &b->accounts[idx]);
}

int ca_pfin_board_record(ca_pfin_board_t *b, const ca_pfin_txn_t *t) {
    if (!b || !t) return -1;
    size_t ai = acct_index_of(b, t->account_id);
    if (ai == (size_t)-1) return 1;   /* InvalidOperationException: unknown account */

    ca_pfin_txn_t copy;
    if (!txn_copy(&copy, t)) return -1;
    if (b->txn_count == b->txn_cap) {
        size_t nc = b->txn_cap ? b->txn_cap * 2 : 4;
        void *n = realloc(b->txns, nc * sizeof(*b->txns));
        if (!n) { ca_pfin_txn_free(&copy); return -1; }
        b->txns = (ca_pfin_txn_t *)n;
        b->txn_cap = nc;
    }
    b->txns[b->txn_count++] = copy;
    /* Balance = a with { Balance = a.Balance + t.Amount }. */
    b->accounts[ai].balance += t->amount;
    return 0;
}

ca_pfin_txn_t *ca_pfin_board_list_for_month(const ca_pfin_board_t *b,
                                            const char *account_id,
                                            int year, int month,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !account_id) { *out_count = (size_t)-1; return NULL; }
    if (b->txn_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->txn_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->txn_count; ++i)
        if (cab_ord_eq(b->txns[i].account_id, account_id) &&
            b->txns[i].year == year && b->txns[i].month == month)
            idx[n++] = i;   /* insertion order preserved */

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_pfin_txn_t *out = (ca_pfin_txn_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!txn_copy(&out[i], &b->txns[idx[i]])) {
            ca_pfin_txn_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_pfin_board_set_budget(ca_pfin_board_t *b, const ca_pfin_budget_t *bl) {
    if (!b || !bl) return -1;
    /* Category keyed OrdinalIgnoreCase. */
    for (size_t i = 0; i < b->budget_count; ++i) {
        if (cab_ci_eq(b->budgets[i].category, bl->category)) {
            ca_pfin_budget_t copy;
            if (!budget_copy(&copy, bl)) return -1;
            ca_pfin_budget_free(&b->budgets[i]);
            b->budgets[i] = copy;
            return 0;
        }
    }
    ca_pfin_budget_t copy;
    if (!budget_copy(&copy, bl)) return -1;
    if (b->budget_count == b->budget_cap) {
        size_t nc = b->budget_cap ? b->budget_cap * 2 : 4;
        void *n = realloc(b->budgets, nc * sizeof(*b->budgets));
        if (!n) { ca_pfin_budget_free(&copy); return -1; }
        b->budgets = (ca_pfin_budget_t *)n;
        b->budget_cap = nc;
    }
    b->budgets[b->budget_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected budget indices by Category (Ordinal). */
static void budget_sort_by_category(const ca_pfin_board_t *b, size_t *idx,
                                    size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        const char *kc = b->budgets[key].category;
        size_t j = i;
        while (j > 0 && strcmp(b->budgets[idx[j - 1]].category, kc) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_pfin_budget_t *ca_pfin_board_budgets(const ca_pfin_board_t *b,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->budget_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->budget_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->budget_count; ++i) idx[i] = i;
    budget_sort_by_category(b, idx, b->budget_count);

    ca_pfin_budget_t *out =
        (ca_pfin_budget_t *)calloc(b->budget_count, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->budget_count; ++i) {
        if (!budget_copy(&out[i], &b->budgets[idx[i]])) {
            ca_pfin_budget_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = b->budget_count;
    return out;
}

int ca_pfin_board_summarise(const ca_pfin_board_t *b, const char *account_id,
                            int year, int month, ca_pfin_month_summary_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !account_id || !out) return -1;

    out->year  = year;
    out->month = month;
    out->total_in = out->total_out = 0;
    out->by_category = NULL;
    out->by_category_count = 0;

    /* Walk the month's transactions once, mirroring ListForMonth's order (so the
     * GroupBy first-seen key order is preserved), accumulating totals + category
     * sums. */
    for (size_t i = 0; i < b->txn_count; ++i) {
        const ca_pfin_txn_t *t = &b->txns[i];
        if (!cab_ord_eq(t->account_id, account_id) ||
            t->year != year || t->month != month)
            continue;

        if (t->amount > 0) out->total_in  += t->amount;
        if (t->amount < 0) out->total_out += -t->amount;

        /* ByCategory: GroupBy(Category) keeps first-appearance key order. */
        size_t k = (size_t)-1;
        for (size_t j = 0; j < out->by_category_count; ++j) {
            if (cab_ord_eq(out->by_category[j].category, t->category)) { k = j; break; }
        }
        if (k == (size_t)-1) {
            ca_pfin_cat_sum_t *grown = (ca_pfin_cat_sum_t *)realloc(
                out->by_category,
                (out->by_category_count + 1) * sizeof(*out->by_category));
            if (!grown) { ca_pfin_month_summary_free(out); return -1; }
            out->by_category = grown;
            ca_pfin_cat_sum_t *slot = &out->by_category[out->by_category_count];
            slot->category = cab_strdup_empty(t->category);
            slot->sum      = 0;
            if (!slot->category) { ca_pfin_month_summary_free(out); return -1; }
            k = out->by_category_count;
            out->by_category_count++;
        }
        out->by_category[k].sum += t->amount;
    }
    return 0;
}
