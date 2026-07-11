/*
 * test_retail.c — CircleAI.Retail (C11 port) verification against
 * RetailPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL

static ca_retail_product_t mk_product(const char *sku, const char *cat) {
    ca_retail_product_t p; memset(&p, 0, sizeof(p));
    p.sku = (char *)sku; p.name = (char *)"N"; p.price = 5 * CA_RETAIL_DECIMAL_SCALE;
    p.currency = (char *)"USD";
    if (cat) { p.has_category = true; p.category = (char *)cat; }
    return p;
}
static ca_retail_sale_t mk_sale(const char *id, const char *sku, int qty,
                                int64_t up, int64_t at) {
    ca_retail_sale_t s; memset(&s, 0, sizeof(s));
    s.sale_id = (char *)id; s.sku = (char *)sku; s.quantity = qty;
    s.unit_price = up; s.at_utc_ms = at;
    return s;
}

static void test_products_stock(void) {
    ca_retail_board_t *b = ca_retail_board_create();
    assert(b);
    assert(ca_retail_board_add_product(b, NULL) == -1);

    ca_retail_product_t p = mk_product("A", "food");
    assert(ca_retail_board_add_product(b, &p) == 0);
    ca_retail_product_t got;
    assert(ca_retail_board_get_product(b, "A", &got));
    assert(got.has_category && strcmp(got.category, "food") == 0);
    ca_retail_product_free(&got);

    ca_retail_product_t p2 = mk_product("B", NULL);
    assert(ca_retail_board_add_product(b, &p2) == 0);
    assert(ca_retail_board_get_product(b, "B", &got) && !got.has_category);
    ca_retail_product_free(&got);

    /* Stock defaults to 0; SetStock then read. */
    assert(ca_retail_board_stock(b, "A") == 0);
    ca_retail_stock_t l; memset(&l, 0, sizeof(l)); l.sku = (char *)"A"; l.quantity = 10;
    assert(ca_retail_board_set_stock(b, &l) == 0);
    assert(ca_retail_board_stock(b, "A") == 10);

    ca_retail_board_destroy(b);
    printf("  products_stock: ok\n");
}

static void test_sales(void) {
    ca_retail_board_t *b = ca_retail_board_create();
    ca_retail_product_t p = mk_product("A", NULL);
    assert(ca_retail_board_add_product(b, &p) == 0);
    ca_retail_stock_t l; memset(&l, 0, sizeof(l)); l.sku = (char *)"A"; l.quantity = 10;
    assert(ca_retail_board_set_stock(b, &l) == 0);

    /* RecordSale on unknown SKU => 1. */
    ca_retail_sale_t bad = mk_sale("x", "ZZ", 1, CA_RETAIL_DECIMAL_SCALE, 0);
    assert(ca_retail_board_record_sale(b, &bad) == 1);

    /* Two sales today (day 100), price 3.00 x qty. */
    int64_t today = 100 * DAY + 1000;
    ca_retail_sale_t s1 = mk_sale("s1", "A", 2, 3 * CA_RETAIL_DECIMAL_SCALE, today);
    ca_retail_sale_t s2 = mk_sale("s2", "A", 1, 3 * CA_RETAIL_DECIMAL_SCALE, today + 500);
    /* one sale yesterday. */
    ca_retail_sale_t s3 = mk_sale("s3", "A", 5, 3 * CA_RETAIL_DECIMAL_SCALE, 99 * DAY);
    assert(ca_retail_board_record_sale(b, &s1) == 0);
    assert(ca_retail_board_record_sale(b, &s2) == 0);
    assert(ca_retail_board_record_sale(b, &s3) == 0);

    /* stock decremented: 10 - 2 - 1 - 5 = 2. */
    assert(ca_retail_board_stock(b, "A") == 2);

    /* RevenueToday(now on day 100): (2+1)*3.00 = 9.00. */
    ca_retail_decimal_t rev = ca_retail_board_revenue_today(b, today + 9999);
    assert(rev == 9 * CA_RETAIL_DECIMAL_SCALE);

    ca_retail_board_destroy(b);
    printf("  sales: ok\n");
}

static void test_top_sellers(void) {
    ca_retail_board_t *b = ca_retail_board_create();
    ca_retail_product_t pa = mk_product("A", NULL), pb = mk_product("B", NULL),
                        pc = mk_product("C", NULL);
    assert(ca_retail_board_add_product(b, &pa) == 0);
    assert(ca_retail_board_add_product(b, &pb) == 0);
    assert(ca_retail_board_add_product(b, &pc) == 0);

    ca_retail_sale_t s1 = mk_sale("1", "A", 3, CA_RETAIL_DECIMAL_SCALE, 100);
    ca_retail_sale_t s2 = mk_sale("2", "B", 10, CA_RETAIL_DECIMAL_SCALE, 100);
    ca_retail_sale_t s3 = mk_sale("3", "A", 4, CA_RETAIL_DECIMAL_SCALE, 200);
    ca_retail_sale_t s4 = mk_sale("4", "C", 1, CA_RETAIL_DECIMAL_SCALE, 50); /* before since */
    assert(ca_retail_board_record_sale(b, &s1) == 0);
    assert(ca_retail_board_record_sale(b, &s2) == 0);
    assert(ca_retail_board_record_sale(b, &s3) == 0);
    assert(ca_retail_board_record_sale(b, &s4) == 0);

    /* TopSellersSince(since=100): A=3+4=7, B=10; C excluded (at 50 < 100).
     * Ordered by sold desc: B(10), A(7). */
    size_t n = 0;
    ca_retail_topseller_t *rows = ca_retail_board_top_sellers_since(b, 100, 5, &n);
    assert(n == 2);
    assert(strcmp(rows[0].sku, "B") == 0 && rows[0].sold == 10);
    assert(strcmp(rows[1].sku, "A") == 0 && rows[1].sold == 7);
    ca_retail_topseller_free_array(rows, n);

    /* topK caps. */
    rows = ca_retail_board_top_sellers_since(b, 100, 1, &n);
    assert(n == 1 && strcmp(rows[0].sku, "B") == 0);
    ca_retail_topseller_free_array(rows, n);

    /* topK <= 0 => error. */
    assert(ca_retail_board_top_sellers_since(b, 100, 0, &n) == NULL && n == (size_t)-1);

    ca_retail_board_destroy(b);
    printf("  top_sellers: ok\n");
}

int main(void) {
    test_products_stock();
    test_sales();
    test_top_sellers();
    printf("test_retail: all assertions passed\n");
    return 0;
}
