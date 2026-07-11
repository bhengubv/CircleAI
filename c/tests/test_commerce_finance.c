/*
 * test_commerce_finance.c — CircleAI.Commerce.Finance (C11 port) verification
 * against FinancePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_invoice_decimal_t)((x) * CA_INVOICE_DECIMAL_SCALE))

static void test_invoices(void) {
    ca_invoice_board_t *b = ca_invoice_board_create();
    assert(b);

    /* Two-line invoice: 100@15% (=115) + 50@0% (=50) -> billed 165. */
    ca_invoice_line_t lines[2];
    memset(lines, 0, sizeof(lines));
    lines[0].description = (char *)"Consulting"; lines[0].amount = D(100); lines[0].tax_pct = 15.0;
    lines[1].description = (char *)"Postage";    lines[1].amount = D(50);  lines[1].tax_pct = 0.0;

    ca_invoice_t inv; memset(&inv, 0, sizeof(inv));
    inv.invoice_id = (char *)"INV1"; inv.customer_id = (char *)"c1";
    inv.issue_date_ms = 100; inv.due_date_ms = 200;
    inv.lines = lines; inv.line_count = 2;
    inv.currency = (char *)"ZAR"; inv.status = (char *)"Issued";
    assert(ca_invoice_board_issue(b, &inv) == 0);

    /* Get -> deep copy with lines. */
    ca_invoice_t got;
    assert(ca_invoice_board_get(b, "INV1", &got));
    assert(got.line_count == 2 && strcmp(got.lines[0].description, "Consulting") == 0);
    assert(got.lines[0].amount == D(100));
    ca_invoice_free(&got);
    assert(!ca_invoice_board_get(b, "none", &got));

    /* RemainingOn with no payments = billed 165. */
    assert(ca_invoice_board_remaining_on(b, "INV1") == D(165));
    /* unknown invoice -> 0. */
    assert(ca_invoice_board_remaining_on(b, "ghost") == 0);

    /* Record a 65 payment -> remaining 100. */
    ca_invoice_payment_t p; memset(&p, 0, sizeof(p));
    p.payment_id = (char *)"pay1"; p.invoice_id = (char *)"INV1"; p.amount = D(65); p.at_utc_ms = 150;
    assert(ca_invoice_board_record_payment(b, &p) == 0);
    assert(ca_invoice_board_remaining_on(b, "INV1") == D(100));

    /* TotalOutstanding = 100. */
    assert(ca_invoice_board_total_outstanding(b) == D(100));

    ca_invoice_board_destroy(b);
    printf("  invoices: ok\n");
}

static void test_overdue(void) {
    ca_invoice_board_t *b = ca_invoice_board_create();

    ca_invoice_line_t l; memset(&l, 0, sizeof(l));
    l.description = (char *)"x"; l.amount = D(10); l.tax_pct = 0.0;

    /* i1 due before asOf and unpaid -> becomes Overdue. */
    ca_invoice_t i1; memset(&i1, 0, sizeof(i1));
    i1.invoice_id = (char *)"i1"; i1.customer_id = (char *)"c";
    i1.issue_date_ms = 0; i1.due_date_ms = 100;
    i1.lines = &l; i1.line_count = 1; i1.currency = (char *)"ZAR"; i1.status = (char *)"Issued";
    /* i2 due before asOf but already Paid -> untouched. */
    ca_invoice_t i2 = i1; i2.invoice_id = (char *)"i2"; i2.status = (char *)"Paid";
    /* i3 due after asOf -> untouched. */
    ca_invoice_t i3 = i1; i3.invoice_id = (char *)"i3"; i3.due_date_ms = 500; i3.status = (char *)"Issued";
    assert(ca_invoice_board_issue(b, &i1) == 0);
    assert(ca_invoice_board_issue(b, &i2) == 0);
    assert(ca_invoice_board_issue(b, &i3) == 0);

    ca_invoice_board_mark_overdue(b, 300);

    size_t n = 0;
    ca_invoice_t *arr = ca_invoice_board_overdue(b, &n);
    assert(n == 1 && strcmp(arr[0].invoice_id, "i1") == 0 &&
           strcmp(arr[0].status, "Overdue") == 0);
    ca_invoice_free_array(arr, n);

    /* i2 still Paid, i3 still Issued. */
    ca_invoice_t chk;
    assert(ca_invoice_board_get(b, "i2", &chk) && strcmp(chk.status, "Paid") == 0);
    ca_invoice_free(&chk);
    assert(ca_invoice_board_get(b, "i3", &chk) && strcmp(chk.status, "Issued") == 0);
    ca_invoice_free(&chk);

    ca_invoice_board_destroy(b);
    printf("  overdue: ok\n");
}

int main(void) {
    test_invoices();
    test_overdue();
    printf("test_commerce_finance: all assertions passed\n");
    return 0;
}
