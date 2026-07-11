#ifndef CIRCLE_AI_RETAIL_H
#define CIRCLE_AI_RETAIL_H

/*
 * retail.h — CircleAI.Retail (C11 port of RetailPrimitives.cs).
 *
 *   Records : Product(Sku, Name, decimal Price, Currency, string? Category);
 *             StockLevel(Sku, int Quantity);
 *             Sale(SaleId, Sku, int Quantity, decimal UnitPrice,
 *                  DateTimeOffset AtUtc).
 *   Board   : IRetailBoard -> InMemoryRetailBoard
 *               AddProduct (Sku keyed set), GetProduct(sku) -> product?,
 *               SetStock (Sku keyed level), Stock(sku) -> qty (0 if unknown),
 *               RecordSale(s) — throws on unknown SKU (=> rc 1), appends the
 *               sale, decrements stock by Quantity,
 *               RevenueToday(now) — sum UnitPrice*Quantity over sales whose AtUtc
 *               falls on now's (UTC) calendar day,
 *               TopSellersSince(since, topK=5) — sales with AtUtc >= since grouped
 *               by Sku, summed Quantity, ordered by sum descending (ties keep
 *               first-appearance), Take(topK) -> (Sku, Sold) rows.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * Price / UnitPrice / revenue as ca_retail_decimal_t (int64 scaled 1e6). AtUtc as
 * int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_retail_decimal_t;
#define CA_RETAIL_DECIMAL_SCALE 1000000LL

/* Product(Sku, Name, decimal Price, Currency, string? Category). */
typedef struct {
    char               *sku;          /* owned, non-null */
    char               *name;         /* owned, non-null */
    ca_retail_decimal_t price;
    char               *currency;     /* owned, non-null */
    bool                has_category; /* false == C# null Category */
    char               *category;     /* owned, valid only when has_category */
} ca_retail_product_t;

void ca_retail_product_free(ca_retail_product_t *p);

/* StockLevel(Sku, int Quantity). */
typedef struct {
    char *sku;       /* owned, non-null */
    int   quantity;
} ca_retail_stock_t;

/* Sale(SaleId, Sku, int Quantity, decimal UnitPrice, DateTimeOffset AtUtc). */
typedef struct {
    char               *sale_id;    /* owned, non-null */
    char               *sku;        /* owned, non-null */
    int                 quantity;
    ca_retail_decimal_t unit_price;
    int64_t             at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
} ca_retail_sale_t;

void ca_retail_sale_free(ca_retail_sale_t *s);

/* (Sku, Sold) tuple row from TopSellersSince. */
typedef struct {
    char *sku;  /* owned, non-null */
    int   sold;
} ca_retail_topseller_t;

void ca_retail_topseller_free_array(ca_retail_topseller_t *arr, size_t count);

typedef struct ca_retail_board ca_retail_board_t;

ca_retail_board_t *ca_retail_board_create(void); /* NULL on OOM */
void ca_retail_board_destroy(ca_retail_board_t *b);

/* AddProduct(p) — Sku keys the store (replace). 0 / -1 on bad args/OOM. */
int ca_retail_board_add_product(ca_retail_board_t *b,
                                const ca_retail_product_t *p);

/* GetProduct(sku) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_retail_board_get_product(const ca_retail_board_t *b, const char *sku,
                                 ca_retail_product_t *out);

/* SetStock(l) — Sku keyed level (replace). 0 / -1. */
int ca_retail_board_set_stock(ca_retail_board_t *b,
                              const ca_retail_stock_t *l);

/* Stock(sku) -> quantity (0 when unknown). */
int ca_retail_board_stock(const ca_retail_board_t *b, const char *sku);

/* RecordSale(s) — 0 on success, -1 on bad args/OOM, 1 when SKU unknown
 * (InvalidOperationException). Appends the sale and decrements stock. */
int ca_retail_board_record_sale(ca_retail_board_t *b,
                                const ca_retail_sale_t *s);

/* RevenueToday(now_ms) -> sum of UnitPrice*Quantity (micro-units) over sales on
 * now's UTC calendar day. */
ca_retail_decimal_t ca_retail_board_revenue_today(const ca_retail_board_t *b,
                                                  int64_t now_ms);

/* TopSellersSince(since_ms, topK) -> fresh owned array (*out_count) of (Sku,Sold)
 * rows ordered by Sold descending, Take(topK). NULL + 0 when empty; NULL +
 * SIZE_MAX on error (topK <= 0). */
ca_retail_topseller_t *ca_retail_board_top_sellers_since(
    const ca_retail_board_t *b, int64_t since_ms, int top_k, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_RETAIL_H */
