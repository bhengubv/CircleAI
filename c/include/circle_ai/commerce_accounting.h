#ifndef CIRCLE_AI_COMMERCE_ACCOUNTING_H
#define CIRCLE_AI_COMMERCE_ACCOUNTING_H

/*
 * commerce_accounting.h — CircleAI.Commerce.Accounting (C11 port of
 * AccountingPrimitives.cs). Double-entry ledger board.
 *
 *   Records : AccountingEntry(EntryId, AtUtc, AccountCode, DebitAmount,
 *             CreditAmount, Memo);
 *             TaxRate(Code, Percentage);
 *             Period(Year, Month).
 *   Board   : IAccountingBoard -> InMemoryAccountingBoard.
 *             Post(e) (appends; rejects negative Debit/Credit), DefineTax(r)
 *             (Code keyed set), GetTax(code) -> rate?, AccountBalance(code) (sum
 *             of Debit-Credit over the account), Sum(code, period) (same, scoped
 *             to AtUtc year/month), ForAccount(code, period) (matching entries,
 *             ordered by AtUtc ascending), NetProfit(period, rev, exp) =
 *             Sum(rev,period) - Sum(exp,period).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money
 * (Debit/Credit/balances) as ca_acct_decimal_t (int64 scaled 1e6). AtUtc as int64
 * Unix ms UTC; year/month are the AtUtc calendar fields (UTC, supplied by the
 * caller to match DateTime.Year/.Month). Percentage as double. Linear arrays,
 * no pthreads.
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
typedef int64_t ca_acct_decimal_t;
#define CA_ACCT_DECIMAL_SCALE 1000000LL

/* AccountingEntry(EntryId, DateTime AtUtc, AccountCode, decimal DebitAmount,
 * decimal CreditAmount, Memo). year/month are the AtUtc calendar fields (UTC). */
typedef struct {
    char             *entry_id;     /* owned, non-null */
    int64_t           at_utc_ms;    /* DateTime as Unix ms UTC */
    int               year;         /* AtUtc.Year (UTC) */
    int               month;        /* AtUtc.Month (UTC), 1..12 */
    char             *account_code; /* owned, non-null */
    ca_acct_decimal_t debit_amount;
    ca_acct_decimal_t credit_amount;
    char             *memo;         /* owned, non-null */
} ca_acct_entry_t;

void ca_acct_entry_free(ca_acct_entry_t *e);
void ca_acct_entry_free_array(ca_acct_entry_t *arr, size_t count);

/* TaxRate(Code, double Percentage). */
typedef struct {
    char  *code;        /* owned, non-null */
    double percentage;
} ca_acct_tax_rate_t;

void ca_acct_tax_rate_free(ca_acct_tax_rate_t *r);

typedef struct ca_acct_board ca_acct_board_t;

/* InMemoryAccountingBoard(). NULL on OOM. */
ca_acct_board_t *ca_acct_board_create(void);
void ca_acct_board_destroy(ca_acct_board_t *b);

/* Post(e). 0 on success, -1 on bad args/OOM, 2 when DebitAmount < 0 ||
 * CreditAmount < 0 (ArgumentException). Appends the entry. */
int ca_acct_board_post(ca_acct_board_t *b, const ca_acct_entry_t *e);

/* DefineTax(r) — deep-copies; Code keyed set. 0 / -1 on bad args/OOM. */
int ca_acct_board_define_tax(ca_acct_board_t *b, const ca_acct_tax_rate_t *r);
/* GetTax(code) -> fresh owned copy into *out, true; false on miss. */
bool ca_acct_board_get_tax(const ca_acct_board_t *b, const char *code,
                           ca_acct_tax_rate_t *out);

/* AccountBalance(code) -> sum of (Debit - Credit) over the account (all periods). */
ca_acct_decimal_t ca_acct_board_account_balance(const ca_acct_board_t *b,
                                                const char *account_code);
/* Sum(code, year, month) -> sum of (Debit - Credit) over the account, scoped to
 * the period. */
ca_acct_decimal_t ca_acct_board_sum(const ca_acct_board_t *b,
                                    const char *account_code,
                                    int year, int month);
/* ForAccount(code, year, month) -> fresh owned array (*out_count): matching
 * entries ordered by AtUtc ascending. NULL + 0 when empty; NULL + SIZE_MAX on
 * error. */
ca_acct_entry_t *ca_acct_board_for_account(const ca_acct_board_t *b,
                                           const char *account_code,
                                           int year, int month,
                                           size_t *out_count);
/* NetProfit(year, month, revenueAccount, expenseAccount) = Sum(rev) - Sum(exp)
 * over the period. */
ca_acct_decimal_t ca_acct_board_net_profit(const ca_acct_board_t *b,
                                           int year, int month,
                                           const char *revenue_account,
                                           const char *expense_account);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMERCE_ACCOUNTING_H */
