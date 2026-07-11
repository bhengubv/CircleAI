#ifndef CIRCLE_AI_COMMERCE_H
#define CIRCLE_AI_COMMERCE_H

/*
 * commerce.h — CircleAI.Commerce (C11 port of CommercePrimitives.cs).
 *
 *   Records : CommerceCustomer(CustomerId, Name, Email?, CreatedUtc);
 *             CommerceOrder(OrderId, CustomerId, Total, Currency, Status, AtUtc);
 *             CommerceLineItem(LineId, OrderId, Sku, Quantity, UnitPrice).
 *   Board   : ICommerceBoard -> InMemoryCommerceBoard.
 *             AddCustomer(c) (CustomerId keyed set), GetCustomer(id) -> cust?,
 *             Place(o) (OrderId keyed set), AddLine(l) (appended list),
 *             UpdateStatus(orderId, status) (throws on unknown order),
 *             OrdersFor(customerId) ordered by AtUtc descending,
 *             LinesFor(orderId) (insertion order), LifetimeValue(customerId)
 *             (sum of Total over the customer's orders).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money
 * (Total / UnitPrice) as ca_decimal_t (int64 scaled 1e6). AtUtc / CreatedUtc as
 * int64 Unix ms UTC. Email is optional (has_email gate). Linear arrays, no
 * pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Money surrogate: int64 count of 1e-6 units (mirrors board_common ca_decimal_t). */
typedef int64_t ca_commerce_decimal_t;
#define CA_COMMERCE_DECIMAL_SCALE 1000000LL

/* CommerceCustomer(CustomerId, Name, string? Email, DateTimeOffset CreatedUtc). */
typedef struct {
    char   *customer_id;   /* owned, non-null */
    char   *name;          /* owned, non-null */
    bool    has_email;     /* false == C# null Email */
    char   *email;         /* owned, valid only when has_email */
    int64_t created_utc_ms;/* DateTimeOffset as Unix ms UTC */
} ca_commerce_customer_t;

void ca_commerce_customer_free(ca_commerce_customer_t *c);

/* CommerceOrder(OrderId, CustomerId, decimal Total, Currency, Status,
 * DateTimeOffset AtUtc). */
typedef struct {
    char                *order_id;    /* owned, non-null */
    char                *customer_id; /* owned, non-null */
    ca_commerce_decimal_t total;
    char                *currency;    /* owned, non-null */
    char                *status;      /* owned, non-null */
    int64_t              at_utc_ms;   /* DateTimeOffset as Unix ms UTC */
} ca_commerce_order_t;

void ca_commerce_order_free(ca_commerce_order_t *o);
void ca_commerce_order_free_array(ca_commerce_order_t *arr, size_t count);

/* CommerceLineItem(LineId, OrderId, Sku, int Quantity, decimal UnitPrice). */
typedef struct {
    char                *line_id;   /* owned, non-null */
    char                *order_id;  /* owned, non-null */
    char                *sku;       /* owned, non-null */
    int                  quantity;
    ca_commerce_decimal_t unit_price;
} ca_commerce_line_t;

void ca_commerce_line_free(ca_commerce_line_t *l);
void ca_commerce_line_free_array(ca_commerce_line_t *arr, size_t count);

typedef struct ca_commerce_board ca_commerce_board_t;

/* InMemoryCommerceBoard(). NULL on OOM. */
ca_commerce_board_t *ca_commerce_board_create(void);
void ca_commerce_board_destroy(ca_commerce_board_t *b);

/* AddCustomer(c) — deep-copies; CustomerId keyed set. 0 / -1 on bad args/OOM. */
int ca_commerce_board_add_customer(ca_commerce_board_t *b,
                                   const ca_commerce_customer_t *c);
/* GetCustomer(id) -> fresh owned copy into *out, true; false on miss. */
bool ca_commerce_board_get_customer(const ca_commerce_board_t *b, const char *id,
                                    ca_commerce_customer_t *out);

/* Place(o) — deep-copies; OrderId keyed set. 0 / -1. */
int ca_commerce_board_place(ca_commerce_board_t *b, const ca_commerce_order_t *o);
/* AddLine(l) — deep-copies; appended (list, not keyed). 0 / -1. */
int ca_commerce_board_add_line(ca_commerce_board_t *b,
                               const ca_commerce_line_t *l);
/* UpdateStatus(orderId, status). 0 on success, -1 on bad args, 1 when the order
 * is unknown (InvalidOperationException). */
int ca_commerce_board_update_status(ca_commerce_board_t *b, const char *order_id,
                                    const char *status);
/* OrdersFor(customerId) -> fresh owned array (*out_count) ordered by AtUtc
 * descending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_commerce_order_t *ca_commerce_board_orders_for(const ca_commerce_board_t *b,
                                                  const char *customer_id,
                                                  size_t *out_count);
/* LinesFor(orderId) -> fresh owned array (*out_count) in insertion order.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_commerce_line_t *ca_commerce_board_lines_for(const ca_commerce_board_t *b,
                                                const char *order_id,
                                                size_t *out_count);
/* LifetimeValue(customerId) -> sum of Total (decimal micro-units) over the
 * customer's orders. */
ca_commerce_decimal_t ca_commerce_board_lifetime_value(
    const ca_commerce_board_t *b, const char *customer_id);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMERCE_H */
