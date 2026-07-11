#ifndef CIRCLE_AI_MARKETS_H
#define CIRCLE_AI_MARKETS_H

/*
 * markets.h — CircleAI.Markets (C11 port of Contracts.cs + InMemoryMarkets.cs).
 *
 *   Enums   : OrderSide { Buy=0, Sell=1 }; OrderType { Market=0, Limit=1 }.
 *   Records : Instrument(Symbol, Exchange, Currency, AssetClass);
 *             Quote(Symbol, decimal Bid, decimal Ask, decimal Last,
 *                   DateTimeOffset AtUtc);
 *             OrderRequest(Symbol, OrderSide Side, OrderType Type,
 *                          decimal Quantity, decimal? LimitPrice);
 *             OrderResult(OrderId, bool Accepted, string? FailureReason).
 *   Backends:
 *     IInstrumentCatalog -> InMemoryInstrumentCatalog (Symbol keyed, case-
 *       INSENSITIVE dictionary): Add(item), Get(symbol) -> instrument?,
 *       Search(query, topK=20) — Symbol OrdinalIgnoreCase substring, ordered by
 *       Symbol (ordinal) asc, Take(topK). BackendId "in-memory".
 *     IMarketDataFeed -> InMemoryMarketDataFeed (case-insensitive symbol keys):
 *       Publish(q) stores + fans out to subscribers, GetQuote(symbol) -> quote?,
 *       SubscribeQuotes(symbol, handler) -> disposable token. BackendId "in-memory".
 *     IOrderRouter -> InMemoryOrderRouter(catalog): Submit(req) validates
 *       positive Quantity, positive LimitPrice for Limit orders, and known
 *       symbol, returning OrderResult("ord-{n}", accepted, reason). BackendId
 *       "in-memory".
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * fields as ca_mkt_decimal_t (int64 scaled 1e6); nullable LimitPrice via has_*.
 * AtUtc as int64 Unix ms UTC. Subscriber fan-out snapshots the list first so a
 * handler may unsubscribe safely. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_mkt_decimal_t;
#define CA_MKT_DECIMAL_SCALE 1000000LL

typedef enum { CA_MKT_SIDE_BUY = 0, CA_MKT_SIDE_SELL = 1 } ca_mkt_order_side_t;
typedef enum { CA_MKT_TYPE_MARKET = 0, CA_MKT_TYPE_LIMIT = 1 } ca_mkt_order_type_t;

/* Instrument(Symbol, Exchange, Currency, AssetClass). */
typedef struct {
    char *symbol;      /* owned, non-null */
    char *exchange;    /* owned, non-null */
    char *currency;    /* owned, non-null */
    char *asset_class; /* owned, non-null */
} ca_mkt_instrument_t;

void ca_mkt_instrument_free(ca_mkt_instrument_t *i);
void ca_mkt_instrument_free_array(ca_mkt_instrument_t *arr, size_t count);

/* Quote(Symbol, decimal Bid, decimal Ask, decimal Last, DateTimeOffset AtUtc). */
typedef struct {
    char            *symbol;    /* owned, non-null */
    ca_mkt_decimal_t bid;
    ca_mkt_decimal_t ask;
    ca_mkt_decimal_t last;
    int64_t          at_utc_ms; /* DateTimeOffset as Unix ms UTC */
} ca_mkt_quote_t;

void ca_mkt_quote_free(ca_mkt_quote_t *q);

/* OrderRequest(Symbol, OrderSide, OrderType, decimal Quantity,
 * decimal? LimitPrice). */
typedef struct {
    char               *symbol;          /* owned, non-null */
    ca_mkt_order_side_t side;
    ca_mkt_order_type_t type;
    ca_mkt_decimal_t    quantity;
    bool                has_limit_price; /* false == C# null LimitPrice */
    ca_mkt_decimal_t    limit_price;     /* valid only when has_limit_price */
} ca_mkt_order_request_t;

/* OrderResult(OrderId, bool Accepted, string? FailureReason). */
typedef struct {
    char *order_id;           /* owned, non-null */
    bool  accepted;
    bool  has_failure_reason; /* false == C# null FailureReason */
    char *failure_reason;     /* owned, valid only when has_failure_reason */
} ca_mkt_order_result_t;

void ca_mkt_order_result_free(ca_mkt_order_result_t *r);

/* ── IInstrumentCatalog -> InMemoryInstrumentCatalog ────────────────────── */

typedef struct ca_mkt_catalog ca_mkt_catalog_t;

ca_mkt_catalog_t *ca_mkt_catalog_create(void); /* NULL on OOM */
void ca_mkt_catalog_destroy(ca_mkt_catalog_t *c);
const char *ca_mkt_catalog_backend_id(const ca_mkt_catalog_t *c);

/* Add(item) — Symbol keys the store (case-insensitive, replace). 0 / -1. */
int ca_mkt_catalog_add(ca_mkt_catalog_t *c, const ca_mkt_instrument_t *item);

/* Get(symbol) -> fresh owned copy into *out, true; false on miss. symbol
 * required (non-null/whitespace); false on bad args. */
bool ca_mkt_catalog_get(const ca_mkt_catalog_t *c, const char *symbol,
                        ca_mkt_instrument_t *out);

/* Search(query, topK) -> fresh owned array (*out_count): Symbol OrdinalIgnoreCase
 * substring, ordered by Symbol (ordinal) asc, Take(topK). NULL + 0 when empty;
 * NULL + SIZE_MAX on error (query NULL or topK <= 0). */
ca_mkt_instrument_t *ca_mkt_catalog_search(const ca_mkt_catalog_t *c,
                                           const char *query, int top_k,
                                           size_t *out_count);

/* ── IMarketDataFeed -> InMemoryMarketDataFeed ──────────────────────────── */

typedef struct ca_mkt_feed ca_mkt_feed_t;
typedef struct ca_mkt_feed_sub ca_mkt_feed_sub_t;

/* Quote subscriber. Receives a borrowed Quote (valid for the call only). */
typedef void (*ca_mkt_quote_handler_fn)(void *ctx, const ca_mkt_quote_t *q);

ca_mkt_feed_t *ca_mkt_feed_create(void); /* NULL on OOM */
void ca_mkt_feed_destroy(ca_mkt_feed_t *f);
const char *ca_mkt_feed_backend_id(const ca_mkt_feed_t *f);

/* Publish(q) — stores latest by Symbol (case-insensitive) and fans out to every
 * live subscriber for that symbol (snapshotting the list first). Returns the
 * subscriber count notified, or -1 on bad args/OOM. */
int ca_mkt_feed_publish(ca_mkt_feed_t *f, const ca_mkt_quote_t *q);

/* GetQuote(symbol) -> fresh owned copy into *out, true; false on miss. symbol
 * required; false on bad args. */
bool ca_mkt_feed_get_quote(const ca_mkt_feed_t *f, const char *symbol,
                           ca_mkt_quote_t *out);

/* SubscribeQuotes(symbol, handler) -> owned token (dispose to unsubscribe).
 * symbol required; handler required. NULL on bad args/OOM. */
ca_mkt_feed_sub_t *ca_mkt_feed_subscribe(ca_mkt_feed_t *f, const char *symbol,
                                         ca_mkt_quote_handler_fn handler,
                                         void *ctx);

/* Dispose the subscription (removes the handler from the symbol). */
void ca_mkt_feed_unsubscribe(ca_mkt_feed_t *f, ca_mkt_feed_sub_t *sub);

/* Drain the next buffered quote from a subscription's cursor into *out (freshly
 * owned; caller frees with ca_mkt_quote_free). Returns true if produced, false
 * when empty. Lets a test read what a publish delivered without a callback. */
bool ca_mkt_feed_sub_next(ca_mkt_feed_sub_t *sub, ca_mkt_quote_t *out);
/* Buffered (undrained) quotes on the cursor. */
size_t ca_mkt_feed_sub_pending(const ca_mkt_feed_sub_t *sub);

/* ── IOrderRouter -> InMemoryOrderRouter ────────────────────────────────── */

typedef struct ca_mkt_router ca_mkt_router_t;

/* InMemoryOrderRouter(catalog). Borrows (does not own) the catalog, which must
 * outlive the router. NULL on bad args/OOM. */
ca_mkt_router_t *ca_mkt_router_create(const ca_mkt_catalog_t *catalog);
void ca_mkt_router_destroy(ca_mkt_router_t *r);
const char *ca_mkt_router_backend_id(const ca_mkt_router_t *r);

/* Submit(req) -> OrderResult into *out (freshly owned; caller frees with
 * ca_mkt_order_result_free). Rejects non-positive Quantity, Limit orders with a
 * missing/non-positive LimitPrice, and unknown symbols; otherwise accepts. Each
 * call mints "ord-{n}" (monotonic). 0 on success, -1 on bad args/OOM. */
int ca_mkt_router_submit(ca_mkt_router_t *r, const ca_mkt_order_request_t *req,
                         ca_mkt_order_result_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MARKETS_H */
