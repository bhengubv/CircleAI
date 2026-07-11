/*
 * test_commerce_accounting.c — CircleAI.Commerce.Accounting (C11 port)
 * verification against AccountingPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_acct_decimal_t)((x) * CA_ACCT_DECIMAL_SCALE))

static ca_acct_entry_t mk_entry(const char *id, int y, int m, const char *code,
                                ca_acct_decimal_t dr, ca_acct_decimal_t cr) {
    ca_acct_entry_t e; memset(&e, 0, sizeof(e));
    e.entry_id = (char *)id; e.year = y; e.month = m; e.account_code = (char *)code;
    e.debit_amount = dr; e.credit_amount = cr; e.memo = (char *)"memo"; e.at_utc_ms = 0;
    return e;
}

static void test_post_balance(void) {
    ca_acct_board_t *b = ca_acct_board_create();
    assert(b);

    /* Post rejects negatives -> 2. */
    ca_acct_entry_t neg = mk_entry("x", 2026, 7, "4000", D(-1), D(0));
    assert(ca_acct_board_post(b, &neg) == 2);
    ca_acct_entry_t neg2 = mk_entry("x", 2026, 7, "4000", D(0), D(-1));
    assert(ca_acct_board_post(b, &neg2) == 2);

    /* Two debits + a credit on 4000. */
    ca_acct_entry_t e1 = mk_entry("e1", 2026, 7, "4000", D(100), D(0));
    ca_acct_entry_t e2 = mk_entry("e2", 2026, 7, "4000", D(0),   D(30));
    ca_acct_entry_t e3 = mk_entry("e3", 2026, 8, "4000", D(50),  D(0));   /* other month */
    ca_acct_entry_t e4 = mk_entry("e4", 2026, 7, "5000", D(70),  D(0));   /* other account */
    assert(ca_acct_board_post(b, &e1) == 0);
    assert(ca_acct_board_post(b, &e2) == 0);
    assert(ca_acct_board_post(b, &e3) == 0);
    assert(ca_acct_board_post(b, &e4) == 0);

    /* AccountBalance(4000) = (100-0)+(0-30)+(50-0) = 120. */
    assert(ca_acct_board_account_balance(b, "4000") == D(120));
    /* Sum(4000, 2026/7) = (100-0)+(0-30) = 70. */
    assert(ca_acct_board_sum(b, "4000", 2026, 7) == D(70));

    /* ForAccount(4000, 2026/7) -> e1, e2 ordered by AtUtc ascending (both 0 -> stable). */
    size_t n = 0;
    ca_acct_entry_t *arr = ca_acct_board_for_account(b, "4000", 2026, 7, &n);
    assert(n == 2 && strcmp(arr[0].entry_id, "e1") == 0 && strcmp(arr[1].entry_id, "e2") == 0);
    ca_acct_entry_free_array(arr, n);

    /* NetProfit(2026/7, rev=4000, exp=5000) = 70 - 70 = 0. */
    assert(ca_acct_board_net_profit(b, 2026, 7, "4000", "5000") == D(0));
    /* NetProfit with expense on a different code that has nothing = 70 - 0. */
    assert(ca_acct_board_net_profit(b, 2026, 7, "4000", "9999") == D(70));

    ca_acct_board_destroy(b);
    printf("  post_balance: ok\n");
}

static void test_tax(void) {
    ca_acct_board_t *b = ca_acct_board_create();

    ca_acct_tax_rate_t r; memset(&r, 0, sizeof(r));
    r.code = (char *)"VAT"; r.percentage = 15.0;
    assert(ca_acct_board_define_tax(b, &r) == 0);

    ca_acct_tax_rate_t got;
    assert(ca_acct_board_get_tax(b, "VAT", &got));
    assert(fabs(got.percentage - 15.0) < 1e-9);
    ca_acct_tax_rate_free(&got);
    assert(!ca_acct_board_get_tax(b, "NONE", &got));

    /* redefine replaces. */
    ca_acct_tax_rate_t r2; memset(&r2, 0, sizeof(r2));
    r2.code = (char *)"VAT"; r2.percentage = 14.0;
    assert(ca_acct_board_define_tax(b, &r2) == 0);
    assert(ca_acct_board_get_tax(b, "VAT", &got));
    assert(fabs(got.percentage - 14.0) < 1e-9);
    ca_acct_tax_rate_free(&got);

    ca_acct_board_destroy(b);
    printf("  tax: ok\n");
}

int main(void) {
    test_post_balance();
    test_tax();
    printf("test_commerce_accounting: all assertions passed\n");
    return 0;
}
