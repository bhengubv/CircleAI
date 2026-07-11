#ifndef CIRCLE_AI_COMMERCE_FINANCE_H
#define CIRCLE_AI_COMMERCE_FINANCE_H

/*
 * commerce_finance.h — CircleAI.Commerce.Finance (C11 port of
 * FinancePrimitives.cs). Invoicing board.
 *
 *   Records : InvoiceLine(Description, Amount, TaxPct);
 *             Invoice(InvoiceId, CustomerId, IssueDate, DueDate, Lines[],
 *                     Currency, Status);
 *             FinancePayment(PaymentId, InvoiceId, Amount, AtUtc).
 *   Board   : IInvoiceBoard -> InMemoryInvoiceBoard.
 *             Issue(i) (InvoiceId keyed set), Get(invoiceId) -> invoice?,
 *             RecordPayment(p) (appended list), MarkOverdue(asOf) (invoices with
 *             DueDate < asOf and Status != "Paid" (OrdinalIgnoreCase) become
 *             "Overdue"), RemainingOn(invoiceId) (billed - paid, 0 for unknown),
 *             TotalOutstanding() (sum of RemainingOn over all invoices),
 *             Overdue() (Status == "Overdue" OrdinalIgnoreCase).
 *
 *   billed = Sum over lines of Amount * (1 + TaxPct/100). The C# multiplies the
 *   decimal Amount by a decimal cast of the double tax factor; here each line's
 *   product is computed in the micro-unit fixed point and rounded to the nearest
 *   micro-unit (banker-free round-half-away-from-zero) before summing, so results
 *   are deterministic. paid = sum of the invoice's payment amounts.
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money
 * (Amount / totals) as ca_invoice_decimal_t (int64 scaled 1e6). IssueDate /
 * DueDate / AtUtc as int64 Unix ms UTC. TaxPct as double. Lines is an owned
 * array. Linear arrays, no pthreads.
 *
 * Pure C11 + libc + libm.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Money surrogate: int64 count of 1e-6 units. */
typedef int64_t ca_invoice_decimal_t;
#define CA_INVOICE_DECIMAL_SCALE 1000000LL

/* InvoiceLine(Description, decimal Amount, double TaxPct). */
typedef struct {
    char                *description; /* owned, non-null */
    ca_invoice_decimal_t amount;
    double               tax_pct;
} ca_invoice_line_t;

/* Invoice(InvoiceId, CustomerId, DateTime IssueDate, DateTime DueDate,
 * IReadOnlyList<InvoiceLine> Lines, Currency, Status). */
typedef struct {
    char              *invoice_id;  /* owned, non-null */
    char              *customer_id; /* owned, non-null */
    int64_t            issue_date_ms;/* DateTime as Unix ms UTC */
    int64_t            due_date_ms;  /* DateTime as Unix ms UTC */
    ca_invoice_line_t *lines;        /* owned (NULL when count 0) */
    size_t             line_count;
    char              *currency;    /* owned, non-null */
    char              *status;      /* owned, non-null */
} ca_invoice_t;

void ca_invoice_free(ca_invoice_t *i);
void ca_invoice_free_array(ca_invoice_t *arr, size_t count);

/* FinancePayment(PaymentId, InvoiceId, decimal Amount, DateTimeOffset AtUtc). */
typedef struct {
    char                *payment_id; /* owned, non-null */
    char                *invoice_id; /* owned, non-null */
    ca_invoice_decimal_t amount;
    int64_t              at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
} ca_invoice_payment_t;

void ca_invoice_payment_free(ca_invoice_payment_t *p);

typedef struct ca_invoice_board ca_invoice_board_t;

/* InMemoryInvoiceBoard(). NULL on OOM. */
ca_invoice_board_t *ca_invoice_board_create(void);
void ca_invoice_board_destroy(ca_invoice_board_t *b);

/* Issue(i) — deep-copies; InvoiceId keyed set. 0 / -1 on bad args/OOM. */
int ca_invoice_board_issue(ca_invoice_board_t *b, const ca_invoice_t *i);
/* Get(invoiceId) -> fresh owned copy into *out, true; false on miss. */
bool ca_invoice_board_get(const ca_invoice_board_t *b, const char *invoice_id,
                          ca_invoice_t *out);
/* RecordPayment(p) — deep-copies; appended list. 0 / -1. */
int ca_invoice_board_record_payment(ca_invoice_board_t *b,
                                    const ca_invoice_payment_t *p);
/* MarkOverdue(asOf_ms): every invoice with DueDate < asOf_ms and Status != "Paid"
 * (OrdinalIgnoreCase) gets Status = "Overdue". */
void ca_invoice_board_mark_overdue(ca_invoice_board_t *b, int64_t as_of_ms);
/* RemainingOn(invoiceId) -> billed - paid (micro-units); 0 for unknown invoice. */
ca_invoice_decimal_t ca_invoice_board_remaining_on(const ca_invoice_board_t *b,
                                                   const char *invoice_id);
/* TotalOutstanding() -> sum of RemainingOn over every invoice. */
ca_invoice_decimal_t ca_invoice_board_total_outstanding(
    const ca_invoice_board_t *b);
/* Overdue() -> fresh owned array (*out_count): invoices whose Status == "Overdue"
 * (OrdinalIgnoreCase), in insertion order. NULL + 0 when empty; NULL + SIZE_MAX
 * on error. */
ca_invoice_t *ca_invoice_board_overdue(const ca_invoice_board_t *b,
                                       size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMERCE_FINANCE_H */
