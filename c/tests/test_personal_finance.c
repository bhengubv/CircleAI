/*
 * test_personal_finance.c — CircleAI.Personal.Finance (C11 port) verification
 * against PersonalFinancePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_pfin_decimal_t)((x) * CA_PFIN_DECIMAL_SCALE))

static ca_pfin_txn_t mk_txn(const char *id, const char *acc, ca_pfin_decimal_t amt,
                            const char *cat, int y, int m) {
    ca_pfin_txn_t t; memset(&t, 0, sizeof(t));
    t.tx_id = (char *)id; t.account_id = (char *)acc; t.amount = amt;
    t.category = (char *)cat; t.year = y; t.month = m; t.at_utc_ms = 0;
    return t;
}

static void test_accounts_record(void) {
    ca_pfin_board_t *b = ca_pfin_board_create();
    assert(b);

    ca_pfin_account_t a; memset(&a, 0, sizeof(a));
    a.account_id = (char *)"acc1"; a.name = (char *)"Cheque"; a.balance = D(100);
    a.currency = (char *)"ZAR";
    assert(ca_pfin_board_upsert(b, &a) == 0);

    ca_pfin_account_t got;
    assert(ca_pfin_board_get_account(b, "acc1", &got));
    assert(got.balance == D(100) && strcmp(got.currency, "ZAR") == 0);
    ca_pfin_account_free(&got);
    assert(!ca_pfin_board_get_account(b, "none", &got));

    /* Record on unknown account -> 1. */
    ca_pfin_txn_t bad = mk_txn("tx0", "ghost", D(10), "Food", 2026, 7);
    assert(ca_pfin_board_record(b, &bad) == 1);

    /* Record adjusts balance: +50 then -30 -> 120. */
    ca_pfin_txn_t t1 = mk_txn("t1", "acc1", D(50),  "Salary", 2026, 7);
    ca_pfin_txn_t t2 = mk_txn("t2", "acc1", D(-30), "Food",   2026, 7);
    ca_pfin_txn_t t3 = mk_txn("t3", "acc1", D(-20), "Food",   2026, 6); /* other month */
    assert(ca_pfin_board_record(b, &t1) == 0);
    assert(ca_pfin_board_record(b, &t2) == 0);
    assert(ca_pfin_board_record(b, &t3) == 0);
    assert(ca_pfin_board_get_account(b, "acc1", &got));
    assert(got.balance == D(100) + D(50) - D(30) - D(20));
    ca_pfin_account_free(&got);

    /* ListForMonth(acc1, 2026, 7) -> t1, t2 (insertion order). */
    size_t n = 0;
    ca_pfin_txn_t *arr = ca_pfin_board_list_for_month(b, "acc1", 2026, 7, &n);
    assert(n == 2 && strcmp(arr[0].tx_id, "t1") == 0 && strcmp(arr[1].tx_id, "t2") == 0);
    ca_pfin_txn_free_array(arr, n);

    ca_pfin_board_destroy(b);
    printf("  accounts_record: ok\n");
}

static void test_budgets(void) {
    ca_pfin_board_t *b = ca_pfin_board_create();

    ca_pfin_budget_t bl1; memset(&bl1, 0, sizeof(bl1));
    bl1.category = (char *)"Food"; bl1.monthly_limit = D(500);
    ca_pfin_budget_t bl2; memset(&bl2, 0, sizeof(bl2));
    bl2.category = (char *)"Auto"; bl2.monthly_limit = D(300);
    assert(ca_pfin_board_set_budget(b, &bl1) == 0);
    assert(ca_pfin_board_set_budget(b, &bl2) == 0);

    /* SetBudget keyed OrdinalIgnoreCase: "food" replaces "Food". */
    ca_pfin_budget_t bl1b; memset(&bl1b, 0, sizeof(bl1b));
    bl1b.category = (char *)"food"; bl1b.monthly_limit = D(999);
    assert(ca_pfin_board_set_budget(b, &bl1b) == 0);

    /* Budgets ordered by Category ascending (Ordinal): "Auto", "food". */
    size_t n = 0;
    ca_pfin_budget_t *arr = ca_pfin_board_budgets(b, &n);
    assert(n == 2);
    assert(strcmp(arr[0].category, "Auto") == 0 && arr[0].monthly_limit == D(300));
    assert(strcmp(arr[1].category, "food") == 0 && arr[1].monthly_limit == D(999));
    ca_pfin_budget_free_array(arr, n);

    ca_pfin_board_destroy(b);
    printf("  budgets: ok\n");
}

static void test_summarise(void) {
    ca_pfin_board_t *b = ca_pfin_board_create();
    ca_pfin_account_t a; memset(&a, 0, sizeof(a));
    a.account_id = (char *)"acc1"; a.name = (char *)"X"; a.balance = 0; a.currency = (char *)"ZAR";
    assert(ca_pfin_board_upsert(b, &a) == 0);

    ca_pfin_txn_t t1 = mk_txn("t1", "acc1", D(1000), "Salary", 2026, 7);
    ca_pfin_txn_t t2 = mk_txn("t2", "acc1", D(-200), "Food",   2026, 7);
    ca_pfin_txn_t t3 = mk_txn("t3", "acc1", D(-50),  "Food",   2026, 7);
    ca_pfin_txn_t t4 = mk_txn("t4", "acc1", D(-100), "Transport", 2026, 7);
    assert(ca_pfin_board_record(b, &t1) == 0);
    assert(ca_pfin_board_record(b, &t2) == 0);
    assert(ca_pfin_board_record(b, &t3) == 0);
    assert(ca_pfin_board_record(b, &t4) == 0);

    ca_pfin_month_summary_t s;
    assert(ca_pfin_board_summarise(b, "acc1", 2026, 7, &s) == 0);
    assert(s.year == 2026 && s.month == 7);
    assert(s.total_in == D(1000));
    assert(s.total_out == D(200) + D(50) + D(100));   /* 350 */

    /* ByCategory in first-seen order: Salary, Food, Transport. */
    assert(s.by_category_count == 3);
    assert(strcmp(s.by_category[0].category, "Salary") == 0 && s.by_category[0].sum == D(1000));
    assert(strcmp(s.by_category[1].category, "Food") == 0 && s.by_category[1].sum == D(-250));
    assert(strcmp(s.by_category[2].category, "Transport") == 0 && s.by_category[2].sum == D(-100));
    ca_pfin_month_summary_free(&s);

    /* Empty month -> zero totals + no categories. */
    assert(ca_pfin_board_summarise(b, "acc1", 2020, 1, &s) == 0);
    assert(s.total_in == 0 && s.total_out == 0 && s.by_category_count == 0);
    ca_pfin_month_summary_free(&s);

    ca_pfin_board_destroy(b);
    printf("  summarise: ok\n");
}

int main(void) {
    test_accounts_record();
    test_budgets();
    test_summarise();
    printf("test_personal_finance: all assertions passed\n");
    return 0;
}
