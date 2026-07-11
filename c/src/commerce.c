/*
 * commerce.c — CircleAI.Commerce (C11 port of CommercePrimitives.cs).
 *
 * InMemoryCommerceBoard: customers + orders keyed by id, line items in an
 * appended list. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/commerce.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_commerce_customer_free(ca_commerce_customer_t *c) {
    if (!c) return;
    free(c->customer_id);
    free(c->name);
    free(c->email);
    c->customer_id = c->name = c->email = NULL;
}

static bool customer_copy(ca_commerce_customer_t *dst,
                          const ca_commerce_customer_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->customer_id = cab_strdup_empty(src->customer_id);
    dst->name        = cab_strdup_empty(src->name);
    dst->has_email   = src->has_email;
    dst->created_utc_ms = src->created_utc_ms;
    if (!dst->customer_id || !dst->name) { ca_commerce_customer_free(dst); return false; }
    if (src->has_email) {
        dst->email = cab_strdup_empty(src->email);
        if (!dst->email) { ca_commerce_customer_free(dst); return false; }
    }
    return true;
}

void ca_commerce_order_free(ca_commerce_order_t *o) {
    if (!o) return;
    free(o->order_id);
    free(o->customer_id);
    free(o->currency);
    free(o->status);
    o->order_id = o->customer_id = o->currency = o->status = NULL;
}
void ca_commerce_order_free_array(ca_commerce_order_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_commerce_order_free(&arr[i]);
    free(arr);
}

static bool order_copy(ca_commerce_order_t *dst, const ca_commerce_order_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->order_id    = cab_strdup_empty(src->order_id);
    dst->customer_id = cab_strdup_empty(src->customer_id);
    dst->currency    = cab_strdup_empty(src->currency);
    dst->status      = cab_strdup_empty(src->status);
    dst->total       = src->total;
    dst->at_utc_ms   = src->at_utc_ms;
    if (!dst->order_id || !dst->customer_id || !dst->currency || !dst->status) {
        ca_commerce_order_free(dst);
        return false;
    }
    return true;
}

void ca_commerce_line_free(ca_commerce_line_t *l) {
    if (!l) return;
    free(l->line_id);
    free(l->order_id);
    free(l->sku);
    l->line_id = l->order_id = l->sku = NULL;
}
void ca_commerce_line_free_array(ca_commerce_line_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_commerce_line_free(&arr[i]);
    free(arr);
}

static bool line_copy(ca_commerce_line_t *dst, const ca_commerce_line_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->line_id  = cab_strdup_empty(src->line_id);
    dst->order_id = cab_strdup_empty(src->order_id);
    dst->sku      = cab_strdup_empty(src->sku);
    dst->quantity   = src->quantity;
    dst->unit_price = src->unit_price;
    if (!dst->line_id || !dst->order_id || !dst->sku) {
        ca_commerce_line_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_commerce_board {
    ca_commerce_customer_t *customers;
    size_t                  cust_count, cust_cap;
    ca_commerce_order_t    *orders;
    size_t                  order_count, order_cap;
    ca_commerce_line_t     *lines;
    size_t                  line_count, line_cap;
};

ca_commerce_board_t *ca_commerce_board_create(void) {
    return (ca_commerce_board_t *)calloc(1, sizeof(ca_commerce_board_t));
}
void ca_commerce_board_destroy(ca_commerce_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->cust_count; ++i)  ca_commerce_customer_free(&b->customers[i]);
    for (size_t i = 0; i < b->order_count; ++i) ca_commerce_order_free(&b->orders[i]);
    for (size_t i = 0; i < b->line_count; ++i)  ca_commerce_line_free(&b->lines[i]);
    free(b->customers);
    free(b->orders);
    free(b->lines);
    free(b);
}

int ca_commerce_board_add_customer(ca_commerce_board_t *b,
                                   const ca_commerce_customer_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->cust_count; ++i) {
        if (cab_ord_eq(b->customers[i].customer_id, c->customer_id)) {
            ca_commerce_customer_t copy;
            if (!customer_copy(&copy, c)) return -1;
            ca_commerce_customer_free(&b->customers[i]);
            b->customers[i] = copy;
            return 0;
        }
    }
    ca_commerce_customer_t copy;
    if (!customer_copy(&copy, c)) return -1;
    if (b->cust_count == b->cust_cap) {
        size_t nc = b->cust_cap ? b->cust_cap * 2 : 4;
        void *n = realloc(b->customers, nc * sizeof(*b->customers));
        if (!n) { ca_commerce_customer_free(&copy); return -1; }
        b->customers = (ca_commerce_customer_t *)n;
        b->cust_cap = nc;
    }
    b->customers[b->cust_count++] = copy;
    return 0;
}

bool ca_commerce_board_get_customer(const ca_commerce_board_t *b, const char *id,
                                    ca_commerce_customer_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->cust_count; ++i)
        if (cab_ord_eq(b->customers[i].customer_id, id))
            return customer_copy(out, &b->customers[i]);
    return false;
}

int ca_commerce_board_place(ca_commerce_board_t *b, const ca_commerce_order_t *o) {
    if (!b || !o) return -1;
    for (size_t i = 0; i < b->order_count; ++i) {
        if (cab_ord_eq(b->orders[i].order_id, o->order_id)) {
            ca_commerce_order_t copy;
            if (!order_copy(&copy, o)) return -1;
            ca_commerce_order_free(&b->orders[i]);
            b->orders[i] = copy;
            return 0;
        }
    }
    ca_commerce_order_t copy;
    if (!order_copy(&copy, o)) return -1;
    if (b->order_count == b->order_cap) {
        size_t nc = b->order_cap ? b->order_cap * 2 : 4;
        void *n = realloc(b->orders, nc * sizeof(*b->orders));
        if (!n) { ca_commerce_order_free(&copy); return -1; }
        b->orders = (ca_commerce_order_t *)n;
        b->order_cap = nc;
    }
    b->orders[b->order_count++] = copy;
    return 0;
}

int ca_commerce_board_add_line(ca_commerce_board_t *b,
                               const ca_commerce_line_t *l) {
    if (!b || !l) return -1;
    ca_commerce_line_t copy;
    if (!line_copy(&copy, l)) return -1;
    if (b->line_count == b->line_cap) {
        size_t nc = b->line_cap ? b->line_cap * 2 : 4;
        void *n = realloc(b->lines, nc * sizeof(*b->lines));
        if (!n) { ca_commerce_line_free(&copy); return -1; }
        b->lines = (ca_commerce_line_t *)n;
        b->line_cap = nc;
    }
    b->lines[b->line_count++] = copy;
    return 0;
}

int ca_commerce_board_update_status(ca_commerce_board_t *b, const char *order_id,
                                    const char *status) {
    if (!b || !order_id) return -1;
    for (size_t i = 0; i < b->order_count; ++i) {
        if (cab_ord_eq(b->orders[i].order_id, order_id)) {
            char *ns = cab_strdup_empty(status);
            if (!ns) return -1;
            free(b->orders[i].status);
            b->orders[i].status = ns;
            return 0;
        }
    }
    return 1;   /* InvalidOperationException: unknown order */
}

/* Stable descending sort of collected indices by at_utc_ms. */
static void order_sort_desc(const ca_commerce_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->orders[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->orders[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_commerce_order_t *ca_commerce_board_orders_for(const ca_commerce_board_t *b,
                                                  const char *customer_id,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !customer_id) { *out_count = (size_t)-1; return NULL; }
    if (b->order_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->order_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->order_count; ++i)
        if (cab_ord_eq(b->orders[i].customer_id, customer_id)) idx[n++] = i;
    order_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_commerce_order_t *out = (ca_commerce_order_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!order_copy(&out[i], &b->orders[idx[i]])) {
            ca_commerce_order_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_commerce_line_t *ca_commerce_board_lines_for(const ca_commerce_board_t *b,
                                                const char *order_id,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !order_id) { *out_count = (size_t)-1; return NULL; }
    if (b->line_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->line_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->line_count; ++i)
        if (cab_ord_eq(b->lines[i].order_id, order_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_commerce_line_t *out = (ca_commerce_line_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!line_copy(&out[i], &b->lines[idx[i]])) {
            ca_commerce_line_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_commerce_decimal_t ca_commerce_board_lifetime_value(
    const ca_commerce_board_t *b, const char *customer_id) {
    if (!b || !customer_id) return 0;
    ca_commerce_decimal_t sum = 0;
    for (size_t i = 0; i < b->order_count; ++i)
        if (cab_ord_eq(b->orders[i].customer_id, customer_id))
            sum += b->orders[i].total;
    return sum;
}
