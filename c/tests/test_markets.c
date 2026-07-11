/*
 * test_markets.c — CircleAI.Markets (C11 port) verification against
 * Contracts.cs + InMemoryMarkets.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_mkt_instrument_t mk_inst(const char *sym) {
    ca_mkt_instrument_t i; memset(&i, 0, sizeof(i));
    i.symbol = (char *)sym; i.exchange = (char *)"NASDAQ";
    i.currency = (char *)"USD"; i.asset_class = (char *)"Equity";
    return i;
}
static ca_mkt_quote_t mk_quote(const char *sym, int64_t last, int64_t at) {
    ca_mkt_quote_t q; memset(&q, 0, sizeof(q));
    q.symbol = (char *)sym; q.bid = last - 1; q.ask = last + 1; q.last = last;
    q.at_utc_ms = at;
    return q;
}

static void test_catalog(void) {
    ca_mkt_catalog_t *c = ca_mkt_catalog_create();
    assert(c);
    assert(strcmp(ca_mkt_catalog_backend_id(c), "in-memory") == 0);

    ca_mkt_instrument_t aapl = mk_inst("AAPL");
    ca_mkt_instrument_t msft = mk_inst("MSFT");
    ca_mkt_instrument_t amzn = mk_inst("AMZN");
    assert(ca_mkt_catalog_add(c, &aapl) == 0);
    assert(ca_mkt_catalog_add(c, &msft) == 0);
    assert(ca_mkt_catalog_add(c, &amzn) == 0);

    /* Get is case-insensitive. */
    ca_mkt_instrument_t got;
    assert(ca_mkt_catalog_get(c, "aapl", &got) && strcmp(got.exchange, "NASDAQ") == 0);
    ca_mkt_instrument_free(&got);
    assert(!ca_mkt_catalog_get(c, "GOOG", &got));

    /* Search "A" (CI substring on symbol): AAPL, AMZN; ordered by Symbol
     * (ordinal) asc => AAPL, AMZN. */
    size_t n = 0;
    ca_mkt_instrument_t *hits = ca_mkt_catalog_search(c, "A", 20, &n);
    assert(n == 2);
    assert(strcmp(hits[0].symbol, "AAPL") == 0);
    assert(strcmp(hits[1].symbol, "AMZN") == 0);
    ca_mkt_instrument_free_array(hits, n);

    /* topK caps after sort. */
    hits = ca_mkt_catalog_search(c, "A", 1, &n);
    assert(n == 1 && strcmp(hits[0].symbol, "AAPL") == 0);
    ca_mkt_instrument_free_array(hits, n);

    assert(ca_mkt_catalog_search(c, NULL, 20, &n) == NULL && n == (size_t)-1);
    assert(ca_mkt_catalog_search(c, "x", 0, &n) == NULL && n == (size_t)-1);

    ca_mkt_catalog_destroy(c);
    printf("  catalog: ok\n");
}

static int g_calls;
static int64_t g_last;
static void on_quote(void *ctx, const ca_mkt_quote_t *q) {
    (void)ctx;
    g_calls++;
    g_last = q->last;
}

static void test_feed(void) {
    ca_mkt_feed_t *f = ca_mkt_feed_create();
    assert(f);
    assert(strcmp(ca_mkt_feed_backend_id(f), "in-memory") == 0);

    /* GetQuote before any publish => miss. */
    ca_mkt_quote_t got;
    assert(!ca_mkt_feed_get_quote(f, "AAPL", &got));

    g_calls = 0; g_last = 0;
    ca_mkt_feed_sub_t *sub = ca_mkt_feed_subscribe(f, "AAPL", on_quote, NULL);
    assert(sub);

    /* Publish to AAPL => handler called + stored + buffered on cursor. */
    ca_mkt_quote_t q1 = mk_quote("AAPL", 190, 1000);
    assert(ca_mkt_feed_publish(f, &q1) == 1);
    assert(g_calls == 1 && g_last == 190);

    /* Publish to a different symbol => this subscriber not called. */
    ca_mkt_quote_t q2 = mk_quote("MSFT", 400, 1000);
    assert(ca_mkt_feed_publish(f, &q2) == 0);
    assert(g_calls == 1);

    /* GetQuote returns latest (case-insensitive lookup). */
    assert(ca_mkt_feed_get_quote(f, "aapl", &got) && got.last == 190);
    ca_mkt_quote_free(&got);

    /* Second publish updates latest + fires again. */
    ca_mkt_quote_t q3 = mk_quote("AAPL", 195, 2000);
    assert(ca_mkt_feed_publish(f, &q3) == 1);
    assert(g_calls == 2 && g_last == 195);

    /* Cursor drained FIFO: 190 then 195. */
    assert(ca_mkt_feed_sub_pending(sub) == 2);
    assert(ca_mkt_feed_sub_next(sub, &got) && got.last == 190);
    ca_mkt_quote_free(&got);
    assert(ca_mkt_feed_sub_next(sub, &got) && got.last == 195);
    ca_mkt_quote_free(&got);
    assert(!ca_mkt_feed_sub_next(sub, &got));

    /* Unsubscribe => no more deliveries. */
    ca_mkt_feed_unsubscribe(f, sub);
    ca_mkt_quote_t q4 = mk_quote("AAPL", 200, 3000);
    assert(ca_mkt_feed_publish(f, &q4) == 0);
    assert(g_calls == 2);

    ca_mkt_feed_destroy(f);
    printf("  feed: ok\n");
}

static void test_router(void) {
    ca_mkt_catalog_t *c = ca_mkt_catalog_create();
    ca_mkt_instrument_t aapl = mk_inst("AAPL");
    assert(ca_mkt_catalog_add(c, &aapl) == 0);

    assert(ca_mkt_router_create(NULL) == NULL);
    ca_mkt_router_t *r = ca_mkt_router_create(c);
    assert(r);
    assert(strcmp(ca_mkt_router_backend_id(r), "in-memory") == 0);

    ca_mkt_order_result_t res;

    /* Non-positive quantity => reject; first id "ord-1". */
    ca_mkt_order_request_t bad_qty; memset(&bad_qty, 0, sizeof(bad_qty));
    bad_qty.symbol = (char *)"AAPL"; bad_qty.side = CA_MKT_SIDE_BUY;
    bad_qty.type = CA_MKT_TYPE_MARKET; bad_qty.quantity = 0;
    assert(ca_mkt_router_submit(r, &bad_qty, &res) == 0);
    assert(!res.accepted && res.has_failure_reason &&
           strcmp(res.failure_reason, "Quantity must be positive") == 0);
    assert(strcmp(res.order_id, "ord-1") == 0);
    ca_mkt_order_result_free(&res);

    /* Limit order without limit price => reject; id "ord-2". */
    ca_mkt_order_request_t bad_lim; memset(&bad_lim, 0, sizeof(bad_lim));
    bad_lim.symbol = (char *)"AAPL"; bad_lim.side = CA_MKT_SIDE_BUY;
    bad_lim.type = CA_MKT_TYPE_LIMIT; bad_lim.quantity = 10 * CA_MKT_DECIMAL_SCALE;
    bad_lim.has_limit_price = false;
    assert(ca_mkt_router_submit(r, &bad_lim, &res) == 0);
    assert(!res.accepted &&
           strcmp(res.failure_reason, "Limit order requires positive LimitPrice") == 0);
    assert(strcmp(res.order_id, "ord-2") == 0);
    ca_mkt_order_result_free(&res);

    /* Unknown symbol => reject. */
    ca_mkt_order_request_t unk; memset(&unk, 0, sizeof(unk));
    unk.symbol = (char *)"GOOG"; unk.side = CA_MKT_SIDE_SELL;
    unk.type = CA_MKT_TYPE_MARKET; unk.quantity = 5 * CA_MKT_DECIMAL_SCALE;
    assert(ca_mkt_router_submit(r, &unk, &res) == 0);
    assert(!res.accepted && strcmp(res.failure_reason, "Unknown symbol") == 0);
    ca_mkt_order_result_free(&res);

    /* Valid market order => accepted, FailureReason null. */
    ca_mkt_order_request_t ok; memset(&ok, 0, sizeof(ok));
    ok.symbol = (char *)"AAPL"; ok.side = CA_MKT_SIDE_BUY;
    ok.type = CA_MKT_TYPE_MARKET; ok.quantity = 5 * CA_MKT_DECIMAL_SCALE;
    assert(ca_mkt_router_submit(r, &ok, &res) == 0);
    assert(res.accepted && !res.has_failure_reason);
    assert(strcmp(res.order_id, "ord-4") == 0);
    ca_mkt_order_result_free(&res);

    /* Valid limit order with positive price => accepted. */
    ca_mkt_order_request_t okl; memset(&okl, 0, sizeof(okl));
    okl.symbol = (char *)"AAPL"; okl.side = CA_MKT_SIDE_BUY;
    okl.type = CA_MKT_TYPE_LIMIT; okl.quantity = 5 * CA_MKT_DECIMAL_SCALE;
    okl.has_limit_price = true; okl.limit_price = 100 * CA_MKT_DECIMAL_SCALE;
    assert(ca_mkt_router_submit(r, &okl, &res) == 0);
    assert(res.accepted);
    ca_mkt_order_result_free(&res);

    ca_mkt_router_destroy(r);
    ca_mkt_catalog_destroy(c);
    printf("  router: ok\n");
}

int main(void) {
    test_catalog();
    test_feed();
    test_router();
    printf("test_markets: all assertions passed\n");
    return 0;
}
