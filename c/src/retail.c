/*
 * retail.c — CircleAI.Retail (C11 port of RetailPrimitives.cs).
 *
 * InMemoryRetailBoard: products (Sku keyed), stock levels (Sku keyed int),
 * sales (flat append list). RevenueToday sums the current UTC day; TopSellers
 * groups by Sku and ranks by summed quantity. Pure C11 + libc.
 */

#include "circle_ai/retail.h"
#include "board_common.h"

#define CA_MS_PER_DAY 86400000LL

/* Floor-divide ms -> UTC day index (correct for negative/pre-epoch too). */
static int64_t utc_day(int64_t ms) {
    int64_t d = ms / CA_MS_PER_DAY;
    if (ms % CA_MS_PER_DAY != 0 && ms < 0) d -= 1;
    return d;
}

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_retail_product_free(ca_retail_product_t *p) {
    if (!p) return;
    free(p->sku);
    free(p->name);
    free(p->currency);
    free(p->category);
    p->sku = p->name = p->currency = p->category = NULL;
    p->has_category = false;
}

static bool product_copy(ca_retail_product_t *dst,
                         const ca_retail_product_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->sku      = cab_strdup_empty(src->sku);
    dst->name     = cab_strdup_empty(src->name);
    dst->currency = cab_strdup_empty(src->currency);
    dst->price    = src->price;
    bool ok = dst->sku && dst->name && dst->currency;
    if (ok && src->has_category) {
        dst->category = cab_strdup_empty(src->category);
        ok = dst->category != NULL;
        dst->has_category = ok;
    }
    if (!ok) { ca_retail_product_free(dst); return false; }
    return true;
}

void ca_retail_sale_free(ca_retail_sale_t *s) {
    if (!s) return;
    free(s->sale_id);
    free(s->sku);
    s->sale_id = s->sku = NULL;
}

static bool sale_copy(ca_retail_sale_t *dst, const ca_retail_sale_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->sale_id    = cab_strdup_empty(src->sale_id);
    dst->sku        = cab_strdup_empty(src->sku);
    dst->quantity   = src->quantity;
    dst->unit_price = src->unit_price;
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->sale_id || !dst->sku) { ca_retail_sale_free(dst); return false; }
    return true;
}

void ca_retail_topseller_free_array(ca_retail_topseller_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i].sku);
    free(arr);
}

/* ── board ──────────────────────────────────────────────────────────────── */

typedef struct {
    char *sku;   /* owned */
    int   qty;
} retail_stock_entry_t;

struct ca_retail_board {
    ca_retail_product_t  *products;
    size_t                p_count, p_cap;
    retail_stock_entry_t *stock;
    size_t                s_count, s_cap;
    ca_retail_sale_t     *sales;
    size_t                sale_count, sale_cap;
};

ca_retail_board_t *ca_retail_board_create(void) {
    return (ca_retail_board_t *)calloc(1, sizeof(ca_retail_board_t));
}
void ca_retail_board_destroy(ca_retail_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i) ca_retail_product_free(&b->products[i]);
    for (size_t i = 0; i < b->s_count; ++i) free(b->stock[i].sku);
    for (size_t i = 0; i < b->sale_count; ++i) ca_retail_sale_free(&b->sales[i]);
    free(b->products);
    free(b->stock);
    free(b->sales);
    free(b);
}

int ca_retail_board_add_product(ca_retail_board_t *b,
                                const ca_retail_product_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->products[i].sku, p->sku)) {
            ca_retail_product_t copy;
            if (!product_copy(&copy, p)) return -1;
            ca_retail_product_free(&b->products[i]);
            b->products[i] = copy;
            return 0;
        }
    }
    ca_retail_product_t copy;
    if (!product_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->products, nc * sizeof(*b->products));
        if (!n) { ca_retail_product_free(&copy); return -1; }
        b->products = (ca_retail_product_t *)n;
        b->p_cap = nc;
    }
    b->products[b->p_count++] = copy;
    return 0;
}

bool ca_retail_board_get_product(const ca_retail_board_t *b, const char *sku,
                                 ca_retail_product_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !sku || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->products[i].sku, sku))
            return product_copy(out, &b->products[i]);
    return false;
}

static bool product_known(const ca_retail_board_t *b, const char *sku) {
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->products[i].sku, sku)) return true;
    return false;
}

/* Find or create the stock slot for sku; returns pointer or NULL on OOM. */
static retail_stock_entry_t *stock_slot(ca_retail_board_t *b, const char *sku) {
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->stock[i].sku, sku)) return &b->stock[i];
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->stock, nc * sizeof(*b->stock));
        if (!n) return NULL;
        b->stock = (retail_stock_entry_t *)n;
        b->s_cap = nc;
    }
    retail_stock_entry_t *e = &b->stock[b->s_count];
    e->sku = cab_strdup_empty(sku);
    if (!e->sku) return NULL;
    e->qty = 0;
    b->s_count++;
    return e;
}

int ca_retail_board_set_stock(ca_retail_board_t *b,
                              const ca_retail_stock_t *l) {
    if (!b || !l) return -1;
    retail_stock_entry_t *e = stock_slot(b, l->sku);
    if (!e) return -1;
    e->qty = l->quantity;
    return 0;
}

int ca_retail_board_stock(const ca_retail_board_t *b, const char *sku) {
    if (!b || !sku) return 0;
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->stock[i].sku, sku)) return b->stock[i].qty;
    return 0;
}

int ca_retail_board_record_sale(ca_retail_board_t *b,
                                const ca_retail_sale_t *s) {
    if (!b || !s) return -1;
    if (!product_known(b, s->sku)) return 1; /* InvalidOperationException */

    ca_retail_sale_t copy;
    if (!sale_copy(&copy, s)) return -1;
    if (b->sale_count == b->sale_cap) {
        size_t nc = b->sale_cap ? b->sale_cap * 2 : 4;
        void *n = realloc(b->sales, nc * sizeof(*b->sales));
        if (!n) { ca_retail_sale_free(&copy); return -1; }
        b->sales = (ca_retail_sale_t *)n;
        b->sale_cap = nc;
    }
    /* Decrement stock: Stock(sku) - Quantity (Stock() is 0 if unknown). */
    int cur = ca_retail_board_stock(b, s->sku);
    retail_stock_entry_t *e = stock_slot(b, s->sku);
    if (!e) { ca_retail_sale_free(&copy); return -1; }
    e->qty = cur - s->quantity;

    b->sales[b->sale_count++] = copy;
    return 0;
}

ca_retail_decimal_t ca_retail_board_revenue_today(const ca_retail_board_t *b,
                                                  int64_t now_ms) {
    if (!b) return 0;
    int64_t today = utc_day(now_ms);
    ca_retail_decimal_t total = 0;
    for (size_t i = 0; i < b->sale_count; ++i) {
        if (utc_day(b->sales[i].at_utc_ms) == today)
            total += b->sales[i].unit_price * (ca_retail_decimal_t)b->sales[i].quantity;
    }
    return total;
}

/* Stable descending sort of (sku,sold) rows by sold. */
static void topseller_sort_desc(ca_retail_topseller_t *rows, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_retail_topseller_t key = rows[i];
        size_t j = i;
        while (j > 0 && rows[j - 1].sold < key.sold) {
            rows[j] = rows[j - 1];
            j--;
        }
        rows[j] = key;
    }
}

ca_retail_topseller_t *ca_retail_board_top_sellers_since(
    const ca_retail_board_t *b, int64_t since_ms, int top_k, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->sale_count == 0) { *out_count = 0; return NULL; }

    /* Group by Sku in first-appearance order over the filtered sales. */
    ca_retail_topseller_t *rows =
        (ca_retail_topseller_t *)calloc(b->sale_count, sizeof(*rows));
    if (!rows) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->sale_count; ++i) {
        const ca_retail_sale_t *s = &b->sales[i];
        if (s->at_utc_ms < since_ms) continue;
        size_t g = n;
        for (size_t j = 0; j < n; ++j) {
            if (cab_ord_eq(rows[j].sku, s->sku)) { g = j; break; }
        }
        if (g == n) {
            rows[n].sku = cab_strdup_empty(s->sku);
            if (!rows[n].sku) {
                ca_retail_topseller_free_array(rows, n);
                *out_count = (size_t)-1;
                return NULL;
            }
            rows[n].sold = 0;
            n++;
            g = n - 1;
        }
        rows[g].sold += s->quantity;
    }

    topseller_sort_desc(rows, n);
    if ((size_t)top_k < n) {
        /* Trim the tail (free dropped skus). */
        for (size_t i = (size_t)top_k; i < n; ++i) free(rows[i].sku);
        n = (size_t)top_k;
    }

    if (n == 0) { free(rows); *out_count = 0; return NULL; }
    *out_count = n;
    return rows;
}
