#ifndef CIRCLE_AI_BUSINESS_OPS_H
#define CIRCLE_AI_BUSINESS_OPS_H

/*
 * business_ops.h - CircleAI.BusinessOps (C11): clients, invoices, reminders.
 *
 * The smallest set of things somebody running a business from a phone actually
 * needs: who you work for, what they owe, and what you have to do next.
 *
 * MONEY IS AN INTEGER OF MINOR UNITS AND A CURRENCY CODE, ALWAYS TOGETHER.
 * Not a double, because 0.1 + 0.2 is not 0.3 and an invoice total that does not
 * match the sum of its lines is the single most damaging bug this module could
 * have - it is not a rendering artefact, it is somebody being billed the wrong
 * amount. And not a bare number, because an amount without a currency is a
 * number that will eventually be added to a different one.
 *
 * INVOICE NUMBERS ARE SEQUENTIAL AND GAPLESS. Not a preference: in most
 * jurisdictions a gap in the sequence is something you have to explain, and a
 * random or timestamp-derived number cannot be defended to a tax authority.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- money ---------------------------------------------------------------- */

typedef struct {
    /* Minor units. R19.50 is 1950, ZAR. */
    int64_t amount_minor;
    char currency[4];   /* ISO 4217, NUL-terminated */
} ca_money_t;

/* ZAR. Stated as a default rather than assumed everywhere, so the one place it
 * changes is here. */
const char *ca_currencies_default(void);

/* How many minor units are in one major unit. Not always 100: JPY has 1 and
 * some currencies have 1000, and a formatter that assumes two decimal places
 * renders a yen amount a hundred times too small. */
int ca_currencies_minor_units(const char *iso_code);

bool ca_currencies_is_known(const char *iso_code);

ca_money_t ca_money_make(int64_t amount_minor, const char *iso_code);

/* Returns false on a currency mismatch rather than converting. There is no
 * exchange rate here, and silently adding two currencies is a wrong total that
 * looks completely ordinary. */
bool ca_money_add(ca_money_t a, ca_money_t b, ca_money_t *out);
bool ca_money_subtract(ca_money_t a, ca_money_t b, ca_money_t *out);

/* Multiplies by a rate - a tax percentage, a quantity - rounding half away from
 * zero. The rounding mode is stated because "round half to even" and "round
 * half up" disagree by a cent on exactly the amounts an auditor checks. */
ca_money_t ca_money_multiply(ca_money_t amount, double rate);

/* Caller frees. */
char *ca_money_format(ca_money_t amount);

/* -- clients -------------------------------------------------------------- */

typedef struct {
    char *client_id;
    char *name;
    char *email;
    char *phone_e164;
    char *vat_number;
    char *address;
    int64_t created_unix;
} ca_client_t;

void ca_client_free(ca_client_t *client);

typedef struct ca_client_book {
    void *state;
    bool (*put)(void *state, const ca_client_t *client);
    const ca_client_t *(*get)(void *state, const char *client_id);
    ca_client_t *(*list)(void *state, size_t *out_count);
    /* Matches on name, email and phone together. Somebody searching for a
     * client types whichever of the three they can remember. */
    ca_client_t *(*search)(void *state, const char *query, size_t *out_count);
    void (*free_fn)(void *state);
} ca_client_book_t;

void ca_client_book_free(ca_client_book_t *book);

ca_client_book_t *ca_client_book_new(void);

/* Holds nothing and finds nothing. The default. */
ca_client_book_t *ca_null_client_book_new(void);

/* -- invoices ------------------------------------------------------------- */

typedef enum {
    CA_INVOICE_STATUS_DRAFT = 0,
    CA_INVOICE_STATUS_SENT,
    CA_INVOICE_STATUS_PARTIALLY_PAID,
    CA_INVOICE_STATUS_PAID,
    CA_INVOICE_STATUS_OVERDUE,
    /* Cancelled, not deleted. A number that was issued stays issued - see the
     * gapless rule above. */
    CA_INVOICE_STATUS_CANCELLED
} ca_invoice_status_t;

const char *ca_invoice_status_name(ca_invoice_status_t status);

typedef struct {
    char *description;
    double quantity;
    ca_money_t unit_price;
    /* Basis points, so 15% VAT is 1500. Percent as a double would reintroduce
     * exactly the rounding problem the money type exists to avoid. */
    int tax_basis_points;
} ca_invoice_line_t;

void ca_invoice_line_free(ca_invoice_line_t *line);

typedef struct {
    char *name;
    char *address;
    char *vat_number;
    char *email;
} ca_invoice_party_t;

void ca_invoice_party_free(ca_invoice_party_t *party);

typedef struct {
    char *invoice_id;
    char *number;
    ca_invoice_party_t *from_party;
    ca_invoice_party_t *to_party;
    ca_invoice_line_t *lines;
    size_t line_count;
    ca_invoice_status_t status;
    int64_t issued_unix;
    int64_t due_unix;
    char *notes;
} ca_invoice_t;

void ca_invoice_free(ca_invoice_t *invoice);

/* Totals computed from the lines, never stored. A stored total is a second
 * source of truth for the same fact, and the two disagree the first time a line
 * is edited. */
bool ca_invoice_subtotal(const ca_invoice_t *invoice, ca_money_t *out);
bool ca_invoice_tax(const ca_invoice_t *invoice, ca_money_t *out);
bool ca_invoice_total(const ca_invoice_t *invoice, ca_money_t *out);

typedef struct ca_invoice_number_generator {
    void *state;
    /* Caller frees. */
    char *(*next)(void *state, int year);
    void (*free_fn)(void *state);
} ca_invoice_number_generator_t;

void ca_invoice_number_generator_free(ca_invoice_number_generator_t *generator);

/* Sequential per year, zero-padded, gapless. The counter persists through the
 * store, so a restart does not begin again at 1 and produce two invoices with
 * the same number. */
ca_invoice_number_generator_t *ca_sequential_invoice_number_generator_new(
    const char *prefix, int start_at);

typedef struct ca_invoice_pdf_renderer {
    void *state;
    uint8_t *(*render)(void *state, const ca_invoice_t *invoice, size_t *out_len);
    void (*free_fn)(void *state);
} ca_invoice_pdf_renderer_t;

void ca_invoice_pdf_renderer_free(ca_invoice_pdf_renderer_t *renderer);

/* Renders nothing. The default, because a PDF engine is a large dependency and
 * a device that cannot produce one should say so rather than ship a blank
 * document that looks like a delivery failure. */
ca_invoice_pdf_renderer_t *ca_null_invoice_pdf_renderer_new(void);

/* -- reminders ------------------------------------------------------------ */

typedef enum {
    CA_RECURRENCE_NONE = 0,
    CA_RECURRENCE_DAILY,
    CA_RECURRENCE_WEEKLY,
    CA_RECURRENCE_MONTHLY,
    CA_RECURRENCE_YEARLY
} ca_recurrence_t;

const char *ca_recurrence_name(ca_recurrence_t recurrence);

typedef struct {
    ca_recurrence_t kind;
    /* Every `interval` units. 2 with WEEKLY is fortnightly. */
    int interval;
} ca_recurrence_rule_t;

ca_recurrence_rule_t ca_recurrence_rule_once(void);

/*
 * The next occurrence at or after `after_unix`, or a negative value when there
 * is none.
 *
 * MONTHLY IS THE HARD ONE. The 31st of January plus one month has no obvious
 * answer, and the two plausible ones - clamp to the 28th, or roll into March -
 * differ by three days on a reminder somebody set for rent. This clamps, and
 * clamping does not accumulate: a monthly reminder set for the 31st still fires
 * on the 31st in March, rather than drifting to the 28th forever after one
 * February.
 */
int64_t ca_recurrence_rule_next(ca_recurrence_rule_t rule, int64_t start_unix,
                                int64_t after_unix);

typedef enum {
    CA_REMINDER_KIND_GENERAL = 0,
    CA_REMINDER_KIND_INVOICE_DUE,
    CA_REMINDER_KIND_FOLLOW_UP,
    CA_REMINDER_KIND_TAX,
    CA_REMINDER_KIND_RENEWAL
} ca_reminder_kind_t;

typedef struct {
    char *reminder_id;
    char *title;
    char *notes;
    ca_reminder_kind_t kind;
    int64_t due_unix;
    ca_recurrence_rule_t recurrence;
    char *client_id;    /* NULL when not about a client */
    bool completed;
} ca_reminder_t;

void ca_reminder_free(ca_reminder_t *reminder);

typedef struct ca_reminder_scheduler {
    void *state;
    bool (*schedule)(void *state, const ca_reminder_t *reminder);
    bool (*complete)(void *state, const char *reminder_id, int64_t at_unix);
    /* Everything due at or before `at_unix` and not completed. */
    ca_reminder_t *(*due)(void *state, int64_t at_unix, size_t *out_count);
    void (*free_fn)(void *state);
} ca_reminder_scheduler_t;

void ca_reminder_scheduler_free(ca_reminder_scheduler_t *scheduler);

/* Completing a recurring reminder schedules the NEXT one rather than marking
 * the series done. Otherwise a monthly reminder is a reminder exactly once. */
ca_reminder_scheduler_t *ca_reminder_scheduler_new(void);
ca_reminder_scheduler_t *ca_null_reminder_scheduler_new(void);

/* -- storage -------------------------------------------------------------- */

typedef struct ca_client_repository {
    void *state;
    bool (*save)(void *state, const ca_client_t *client);
    ca_client_t *(*load_all)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_client_repository_t;

void ca_client_repository_free(ca_client_repository_t *repository);

typedef struct ca_invoice_repository {
    void *state;
    bool (*save)(void *state, const ca_invoice_t *invoice);
    ca_invoice_t *(*load_all)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_invoice_repository_t;

void ca_invoice_repository_free(ca_invoice_repository_t *repository);

typedef struct ca_reminder_repository {
    void *state;
    bool (*save)(void *state, const ca_reminder_t *reminder);
    ca_reminder_t *(*load_all)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_reminder_repository_t;

void ca_reminder_repository_free(ca_reminder_repository_t *repository);

/* -- bridges and samples -------------------------------------------------- */

typedef struct ca_crm_bridge ca_crm_bridge_t;

/* Pushes clients out to whatever CRM a host wired, one way. One way because a
 * two-way sync needs a conflict policy, and the honest default for somebody's
 * client list is that the device is right. */
ca_crm_bridge_t *ca_crm_bridge_new(ca_client_book_t *book);
void ca_crm_bridge_free(ca_crm_bridge_t *bridge);

bool ca_crm_bridge_push(ca_crm_bridge_t *bridge, const char *client_id);

/* A worked example - two clients, an invoice, a recurring reminder - so an
 * empty install has something to look at. Clearly marked as sample data:
 * somebody must never wonder whether an invoice in their list is real. */
bool ca_business_ops_sample_data_seed(ca_client_book_t *book,
                                      ca_reminder_scheduler_t *scheduler);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BUSINESS_OPS_H */
