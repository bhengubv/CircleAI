/*
 * test_commerce.c — CircleAI.Commerce (C11 port) verification against
 * CommercePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_commerce_decimal_t)((x) * CA_COMMERCE_DECIMAL_SCALE))

static ca_commerce_customer_t mk_cust(const char *id, const char *name,
                                      const char *email, int64_t created) {
    ca_commerce_customer_t c; memset(&c, 0, sizeof(c));
    c.customer_id = (char *)id; c.name = (char *)name;
    c.has_email = (email != NULL); c.email = (char *)email;
    c.created_utc_ms = created;
    return c;
}
static ca_commerce_order_t mk_order(const char *id, const char *cid,
                                    ca_commerce_decimal_t total, int64_t at,
                                    const char *status) {
    ca_commerce_order_t o; memset(&o, 0, sizeof(o));
    o.order_id = (char *)id; o.customer_id = (char *)cid; o.total = total;
    o.currency = (char *)"ZAR"; o.status = (char *)status; o.at_utc_ms = at;
    return o;
}
static ca_commerce_line_t mk_line(const char *id, const char *oid,
                                  const char *sku, int qty,
                                  ca_commerce_decimal_t unit) {
    ca_commerce_line_t l; memset(&l, 0, sizeof(l));
    l.line_id = (char *)id; l.order_id = (char *)oid; l.sku = (char *)sku;
    l.quantity = qty; l.unit_price = unit;
    return l;
}

static void test_customers(void) {
    ca_commerce_board_t *b = ca_commerce_board_create();
    assert(b);

    ca_commerce_customer_t c1 = mk_cust("c1", "Ada", "ada@x.io", 100);
    ca_commerce_customer_t c2 = mk_cust("c2", "Bob", NULL, 200);   /* null email */
    assert(ca_commerce_board_add_customer(b, &c1) == 0);
    assert(ca_commerce_board_add_customer(b, &c2) == 0);

    ca_commerce_customer_t got;
    assert(ca_commerce_board_get_customer(b, "c1", &got));
    assert(got.has_email && strcmp(got.email, "ada@x.io") == 0);
    ca_commerce_customer_free(&got);
    assert(ca_commerce_board_get_customer(b, "c2", &got));
    assert(!got.has_email && got.email == NULL);   /* null email preserved */
    ca_commerce_customer_free(&got);
    assert(!ca_commerce_board_get_customer(b, "none", &got));

    ca_commerce_board_destroy(b);
    printf("  customers: ok\n");
}

static void test_orders_lines_ltv(void) {
    ca_commerce_board_t *b = ca_commerce_board_create();

    assert(ca_commerce_board_update_status(b, "nope", "shipped") == 1);

    ca_commerce_order_t o1 = mk_order("o1", "c1", D(100), 100, "new");
    ca_commerce_order_t o2 = mk_order("o2", "c1", D(250), 300, "new");
    ca_commerce_order_t o3 = mk_order("o3", "c2", D(999), 200, "new");
    assert(ca_commerce_board_place(b, &o1) == 0);
    assert(ca_commerce_board_place(b, &o2) == 0);
    assert(ca_commerce_board_place(b, &o3) == 0);

    /* OrdersFor(c1) ordered by AtUtc descending: o2(300), o1(100). */
    size_t n = 0;
    ca_commerce_order_t *arr = ca_commerce_board_orders_for(b, "c1", &n);
    assert(n == 2);
    assert(strcmp(arr[0].order_id, "o2") == 0);
    assert(strcmp(arr[1].order_id, "o1") == 0);
    ca_commerce_order_free_array(arr, n);

    /* UpdateStatus. */
    assert(ca_commerce_board_update_status(b, "o1", "shipped") == 0);

    /* Lines appended, LinesFor filters. */
    ca_commerce_line_t l1 = mk_line("l1", "o1", "sku-a", 2, D(10));
    ca_commerce_line_t l2 = mk_line("l2", "o1", "sku-b", 1, D(5));
    ca_commerce_line_t l3 = mk_line("l3", "o2", "sku-c", 3, D(7));
    assert(ca_commerce_board_add_line(b, &l1) == 0);
    assert(ca_commerce_board_add_line(b, &l2) == 0);
    assert(ca_commerce_board_add_line(b, &l3) == 0);

    ca_commerce_line_t *larr = ca_commerce_board_lines_for(b, "o1", &n);
    assert(n == 2 && strcmp(larr[0].line_id, "l1") == 0 &&
           strcmp(larr[1].line_id, "l2") == 0);
    assert(larr[0].quantity == 2 && larr[0].unit_price == D(10));
    ca_commerce_line_free_array(larr, n);

    larr = ca_commerce_board_lines_for(b, "zzz", &n);
    assert(n == 0 && larr == NULL);

    /* LifetimeValue(c1) = 100 + 250 = 350. */
    assert(ca_commerce_board_lifetime_value(b, "c1") == D(350));
    assert(ca_commerce_board_lifetime_value(b, "c2") == D(999));
    assert(ca_commerce_board_lifetime_value(b, "ghost") == 0);

    ca_commerce_board_destroy(b);
    printf("  orders_lines_ltv: ok\n");
}

int main(void) {
    test_customers();
    test_orders_lines_ltv();
    printf("test_commerce: all assertions passed\n");
    return 0;
}
