//! markets_test.rs
//!
//! Ports the behaviour of `CircleAI.Markets`: instrument catalog (symbol
//! substring search), market-data feed (latest quote + subscribe/broadcast +
//! unsubscribe on drop), order router (positive-quantity / valid-limit /
//! known-symbol validation), and the fail-closed null backends.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use chrono::Utc;
use circle_ai::markets::{
    IInstrumentCatalog, IMarketDataFeed, IOrderRouter, InMemoryInstrumentCatalog,
    InMemoryMarketDataFeed, InMemoryOrderRouter, Instrument, NullInstrumentCatalog,
    NullMarketDataFeed, NullOrderRouter, OrderRequest, OrderResult, OrderSide, OrderType, Quote,
    EMPTY_GUID,
};

fn q(sym: &str, last: f64) -> Quote {
    Quote::new(sym, last - 0.5, last + 0.5, last, Utc::now())
}

#[test]
fn catalog_add_get_search() {
    let cat = InMemoryInstrumentCatalog::new();
    assert_eq!(cat.backend_id(), "in-memory");
    cat.add(Instrument::new("AAPL", "NASDAQ", "USD", "Equity"));
    cat.add(Instrument::new("AMZN", "NASDAQ", "USD", "Equity"));
    cat.add(Instrument::new("MSFT", "NASDAQ", "USD", "Equity"));

    // case-insensitive get.
    assert_eq!(cat.get("aapl").unwrap().exchange, "NASDAQ");
    // substring search, symbol-ordered ascending.
    let hits = cat.search("A", 20);
    let syms: Vec<&str> = hits.iter().map(|i| i.symbol.as_str()).collect();
    assert_eq!(syms, vec!["AAPL", "AMZN"]);
    assert_eq!(cat.search("a", 1).len(), 1);
}

#[test]
fn feed_latest_quote_and_subscribe_broadcast() {
    let feed = InMemoryMarketDataFeed::new();
    assert!(feed.get_quote("AAPL").is_none());

    let count = Arc::new(AtomicUsize::new(0));
    let c2 = Arc::clone(&count);
    let sub = feed.subscribe_quotes("AAPL", Arc::new(move |_q: &Quote| {
        c2.fetch_add(1, Ordering::SeqCst);
    }));

    feed.publish(q("AAPL", 190.0));
    feed.publish(q("MSFT", 400.0)); // different symbol → no callback
    assert_eq!(count.load(Ordering::SeqCst), 1);
    assert_eq!(feed.get_quote("AAPL").unwrap().last, 190.0);

    // Drop the subscription → no further callbacks.
    drop(sub);
    feed.publish(q("AAPL", 191.0));
    assert_eq!(count.load(Ordering::SeqCst), 1);
    // latest quote still updates.
    assert_eq!(feed.get_quote("AAPL").unwrap().last, 191.0);
}

#[test]
fn order_router_validation() {
    let cat: Arc<dyn IInstrumentCatalog + Send + Sync> = Arc::new({
        let c = InMemoryInstrumentCatalog::new();
        c.add(Instrument::new("AAPL", "NASDAQ", "USD", "Equity"));
        c
    });
    let router = InMemoryOrderRouter::new(Arc::clone(&cat));

    // non-positive quantity.
    let r = router.submit(OrderRequest::new("AAPL", OrderSide::Buy, OrderType::Market, 0.0, None));
    assert!(!r.accepted);
    assert_eq!(r.failure_reason.as_deref(), Some("Quantity must be positive"));

    // limit order without a positive limit price.
    let r = router.submit(OrderRequest::new("AAPL", OrderSide::Buy, OrderType::Limit, 1.0, None));
    assert_eq!(r.failure_reason.as_deref(), Some("Limit order requires positive LimitPrice"));

    // unknown symbol.
    let r = router.submit(OrderRequest::new("TSLA", OrderSide::Sell, OrderType::Market, 1.0, None));
    assert_eq!(r.failure_reason.as_deref(), Some("Unknown symbol"));

    // happy path — accepted, sequential ids.
    let ok = router.submit(OrderRequest::new("AAPL", OrderSide::Buy, OrderType::Limit, 10.0, Some(150.0)));
    assert!(ok.accepted);
    assert!(ok.order_id.starts_with("ord-"));
    assert!(ok.failure_reason.is_none());
}

#[test]
fn null_backends_are_fail_closed() {
    assert_eq!(NullMarketDataFeed::INSTANCE.backend_id(), "null");
    assert!(NullMarketDataFeed::INSTANCE.get_quote("AAPL").is_none());
    let _noop = NullMarketDataFeed::INSTANCE
        .subscribe_quotes("AAPL", Arc::new(|_q: &Quote| {}));

    assert_eq!(NullInstrumentCatalog::INSTANCE.backend_id(), "null");
    assert!(NullInstrumentCatalog::INSTANCE.get("AAPL").is_none());
    assert!(NullInstrumentCatalog::INSTANCE.search("a", 20).is_empty());

    let r: OrderResult =
        NullOrderRouter::INSTANCE.submit(OrderRequest::new("AAPL", OrderSide::Buy, OrderType::Market, 1.0, None));
    assert_eq!(r.order_id, EMPTY_GUID);
    assert!(!r.accepted);
    assert_eq!(r.failure_reason.as_deref(), Some("NullOrderRouter — fail-closed."));
}
