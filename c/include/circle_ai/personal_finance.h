#ifndef CIRCLE_AI_PERSONAL_FINANCE_H
#define CIRCLE_AI_PERSONAL_FINANCE_H

/*
 * personal_finance.h — CircleAI.Personal.Finance (C11 port of
 * PersonalFinancePrimitives.cs). Accounts / transactions / budgets / monthly
 * summary board.
 *
 *   Records : Account(AccountId, Name, Balance, Currency);
 *             FinanceTransaction(TxId, AccountId, Amount, Category, Note?, AtUtc);
 *             BudgetLine(Category, MonthlyLimit);
 *             MonthSummary(Year, Month, TotalIn, TotalOut, ByCategory{cat->sum}).
 *   Board   : IPersonalFinanceBoard -> InMemoryPersonalFinanceBoard.
 *             Upsert(a) (AccountId keyed set), GetAccount(id) -> account?,
 *             Record(t) (throws on unknown account; appends txn and does
 *             Balance += Amount), ListForMonth(accountId, year, month) (matching
 *             AtUtc year/month, insertion order), SetBudget(b) (Category keyed set,
 *             OrdinalIgnoreCase), Budgets (ordered by Category ascending Ordinal),
 *             Summarise(accountId, year, month) (TotalIn = sum of positive
 *             amounts, TotalOut = -sum of negative amounts, ByCategory = grouped
 *             sums in first-seen order).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money
 * (Balance / Amount / MonthlyLimit / totals) as ca_pfin_decimal_t (int64 scaled
 * 1e6). AtUtc as int64 Unix ms UTC; year/month are the calendar fields of that
 * instant (caller supplies UTC-decomposed values to match DateTimeOffset.Year/
 * .Month). Note optional (has_note gate). Linear arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Money surrogate: int64 count of 1e-6 units. */
typedef int64_t ca_pfin_decimal_t;
#define CA_PFIN_DECIMAL_SCALE 1000000LL

/* Account(AccountId, Name, decimal Balance, Currency). */
typedef struct {
    char             *account_id;  /* owned, non-null */
    char             *name;        /* owned, non-null */
    ca_pfin_decimal_t balance;
    char             *currency;    /* owned, non-null */
} ca_pfin_account_t;

void ca_pfin_account_free(ca_pfin_account_t *a);

/* FinanceTransaction(TxId, AccountId, decimal Amount, Category, string? Note,
 * DateTimeOffset AtUtc). year/month are the AtUtc calendar fields (UTC). */
typedef struct {
    char             *tx_id;      /* owned, non-null */
    char             *account_id; /* owned, non-null */
    ca_pfin_decimal_t amount;
    char             *category;   /* owned, non-null */
    bool              has_note;   /* false == C# null Note */
    char             *note;       /* owned, valid only when has_note */
    int64_t           at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
    int               year;       /* AtUtc.Year (UTC) */
    int               month;      /* AtUtc.Month (UTC), 1..12 */
} ca_pfin_txn_t;

void ca_pfin_txn_free(ca_pfin_txn_t *t);
void ca_pfin_txn_free_array(ca_pfin_txn_t *arr, size_t count);

/* BudgetLine(Category, decimal MonthlyLimit). */
typedef struct {
    char             *category;    /* owned, non-null */
    ca_pfin_decimal_t monthly_limit;
} ca_pfin_budget_t;

void ca_pfin_budget_free(ca_pfin_budget_t *b);
void ca_pfin_budget_free_array(ca_pfin_budget_t *arr, size_t count);

/* One (category -> summed amount) pair in a MonthSummary. */
typedef struct {
    char             *category;  /* owned, non-null */
    ca_pfin_decimal_t sum;
} ca_pfin_cat_sum_t;

/* MonthSummary(Year, Month, decimal TotalIn, decimal TotalOut,
 * IReadOnlyDictionary<string,decimal> ByCategory). by_category is an owned array
 * in first-seen (GroupBy) order. */
typedef struct {
    int                year;
    int                month;
    ca_pfin_decimal_t  total_in;
    ca_pfin_decimal_t  total_out;
    ca_pfin_cat_sum_t *by_category;  /* owned (NULL when count 0) */
    size_t             by_category_count;
} ca_pfin_month_summary_t;

void ca_pfin_month_summary_free(ca_pfin_month_summary_t *s);

typedef struct ca_pfin_board ca_pfin_board_t;

/* InMemoryPersonalFinanceBoard(). NULL on OOM. */
ca_pfin_board_t *ca_pfin_board_create(void);
void ca_pfin_board_destroy(ca_pfin_board_t *b);

/* Upsert(a) — deep-copies; AccountId keyed set. 0 / -1 on bad args/OOM. */
int ca_pfin_board_upsert(ca_pfin_board_t *b, const ca_pfin_account_t *a);
/* GetAccount(id) -> fresh owned copy into *out, true; false on miss. */
bool ca_pfin_board_get_account(const ca_pfin_board_t *b, const char *id,
                               ca_pfin_account_t *out);

/* Record(t). 0 on success, -1 on bad args/OOM, 1 when the account is unknown
 * (InvalidOperationException). Appends the txn and does Balance += Amount. */
int ca_pfin_board_record(ca_pfin_board_t *b, const ca_pfin_txn_t *t);
/* ListForMonth(accountId, year, month) -> fresh owned array (*out_count): txns
 * for that account whose year/month match, in insertion order. NULL + 0 when
 * empty; NULL + SIZE_MAX on error. */
ca_pfin_txn_t *ca_pfin_board_list_for_month(const ca_pfin_board_t *b,
                                            const char *account_id,
                                            int year, int month,
                                            size_t *out_count);

/* SetBudget(b) — deep-copies; Category keyed set (OrdinalIgnoreCase). 0 / -1. */
int ca_pfin_board_set_budget(ca_pfin_board_t *b, const ca_pfin_budget_t *bl);
/* Budgets -> fresh owned array (*out_count) ordered by Category ascending
 * (Ordinal). NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_pfin_budget_t *ca_pfin_board_budgets(const ca_pfin_board_t *b,
                                        size_t *out_count);

/* Summarise(accountId, year, month) -> writes a fresh owned MonthSummary into
 * *out. Returns 0 on success, -1 on bad args/OOM. TotalIn/Out and ByCategory are
 * computed from ListForMonth. */
int ca_pfin_board_summarise(const ca_pfin_board_t *b, const char *account_id,
                            int year, int month, ca_pfin_month_summary_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PERSONAL_FINANCE_H */
