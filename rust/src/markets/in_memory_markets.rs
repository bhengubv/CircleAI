//! in_memory_markets.rs
//!
//! (3.3.0) Real in-memory market-data feed + instrument catalog + order router
//! — Rust port of `src/CircleAI.Markets/InMemoryMarkets.cs`.
//!
//! The feed supports subscribe/broadcast quote pushes; the order router accepts
//! or rejects on simple rules (positive quantity, known instrument, valid limit
//! price for limit orders).
//!
//! Fan-out safety follows the workspace idiom: the subscriber list is snapshot
//! under the lock and each callback fires **outside** it, so a slow / re-entrant
//! subscriber cannot stall or deadlock a publish. Each callback is wrapped in
//! `catch_unwind` to mirror the C# `try/catch` that swallows subscriber
//! exceptions. Order ids use an atomically-incremented sequence (`ord-{n}`).

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering as AtomicOrdering};
use std::sync::{Arc, Mutex};

use super::contracts::{
    IInstrumentCatalog, IMarketDataFeed, IOrderRouter, Instrument, OrderRequest, OrderResult,
    OrderType, Quote, QuoteHandler, QuoteSubscription,
};

/// (3.3.0) In-memory [`IInstrumentCatalog`].
///
/// Mirrors `sealed class InMemoryInstrumentCatalog`. Keyed case-insensitively,
/// as the C# uses `StringComparer.OrdinalIgnoreCase`.
pub struct InMemoryInstrumentCatalog {
    // Key is the lower-cased symbol (the C# OrdinalIgnoreCase dictionary); the
    // stored `Instrument` keeps the original-cased symbol.
    items: Mutex<HashMap<String, Instrument>>,
}

impl InMemoryInstrumentCatalog {
    /// Creates an empty catalog.
    pub fn new() -> Self {
        Self {
            items: Mutex::new(HashMap::new()),
        }
    }

    /// Adds (or overwrites) an instrument, keyed by symbol.
    pub fn add(&self, item: Instrument) {
        self.items
            .lock()
            .unwrap()
            .insert(item.symbol.to_lowercase(), item);
    }
}

impl Default for InMemoryInstrumentCatalog {
    fn default() -> Self {
        Self::new()
    }
}

impl IInstrumentCatalog for InMemoryInstrumentCatalog {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn get(&self, symbol: &str) -> Option<Instrument> {
        if symbol.trim().is_empty() {
            panic!("symbol required");
        }
        self.items.lock().unwrap().get(&symbol.to_lowercase()).cloned()
    }

    fn search(&self, query: &str, top_k: usize) -> Vec<Instrument> {
        if top_k == 0 {
            panic!("topK out of range");
        }
        let items = self.items.lock().unwrap();
        let ql = query.to_lowercase();
        let mut hits: Vec<Instrument> = items
            .values()
            .filter(|i| i.symbol.to_lowercase().contains(&ql))
            .cloned()
            .collect();
        // OrderBy(Symbol) — ordinal ascending.
        hits.sort_by(|a, b| a.symbol.cmp(&b.symbol));
        hits.truncate(top_k);
        hits
    }
}

/// (3.3.0) In-memory [`IMarketDataFeed`].
///
/// Mirrors `sealed class InMemoryMarketDataFeed`.
pub struct InMemoryMarketDataFeed {
    quotes: Mutex<HashMap<String, Quote>>,
    // Per-symbol id-keyed handler lists; the id makes unsubscribe exact.
    subs: Arc<Mutex<HashMap<String, Vec<(u64, QuoteHandler)>>>>,
    next_id: Mutex<u64>,
}

impl InMemoryMarketDataFeed {
    /// Creates an empty feed.
    pub fn new() -> Self {
        Self {
            quotes: Mutex::new(HashMap::new()),
            subs: Arc::new(Mutex::new(HashMap::new())),
            next_id: Mutex::new(0),
        }
    }

    /// Publishes `q`: stores it as the latest for its symbol and fires every
    /// current subscriber's callback (outside the lock).
    pub fn publish(&self, q: Quote) {
        let key = q.symbol.to_lowercase();
        self.quotes.lock().unwrap().insert(key.clone(), q.clone());
        let snap: Vec<QuoteHandler> = {
            let subs = self.subs.lock().unwrap();
            match subs.get(&key) {
                Some(list) => list.iter().map(|(_, h)| Arc::clone(h)).collect(),
                None => Vec::new(),
            }
        };
        for h in snap {
            // Mirror the C# try/catch that swallows subscriber exceptions.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(&q)));
        }
    }
}

impl Default for InMemoryMarketDataFeed {
    fn default() -> Self {
        Self::new()
    }
}

impl IMarketDataFeed for InMemoryMarketDataFeed {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn get_quote(&self, symbol: &str) -> Option<Quote> {
        if symbol.trim().is_empty() {
            panic!("symbol required");
        }
        self.quotes.lock().unwrap().get(&symbol.to_lowercase()).cloned()
    }

    fn subscribe_quotes(&self, symbol: &str, handler: QuoteHandler) -> QuoteSubscription {
        if symbol.trim().is_empty() {
            panic!("symbol required");
        }
        let key = symbol.to_lowercase();
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.subs
            .lock()
            .unwrap()
            .entry(key.clone())
            .or_default()
            .push((id, handler));
        let subs = Arc::clone(&self.subs);
        QuoteSubscription::new(move || {
            if let Some(list) = subs.lock().unwrap().get_mut(&key) {
                list.retain(|(hid, _)| *hid != id);
            }
        })
    }
}

/// (3.3.0) In-memory [`IOrderRouter`].
///
/// Mirrors `sealed class InMemoryOrderRouter`. Validates against an injected
/// [`IInstrumentCatalog`] (positive quantity, known instrument, valid limit
/// price for limit orders).
pub struct InMemoryOrderRouter {
    catalog: Arc<dyn IInstrumentCatalog + Send + Sync>,
    seq: AtomicI64,
}

impl InMemoryOrderRouter {
    /// Wraps an instrument catalog used for symbol validation.
    pub fn new(catalog: Arc<dyn IInstrumentCatalog + Send + Sync>) -> Self {
        Self {
            catalog,
            seq: AtomicI64::new(0),
        }
    }

    fn next_id(&self) -> String {
        let n = self.seq.fetch_add(1, AtomicOrdering::SeqCst) + 1;
        format!("ord-{n}")
    }
}

impl IOrderRouter for InMemoryOrderRouter {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn submit(&self, req: OrderRequest) -> OrderResult {
        if req.quantity <= 0.0 {
            return OrderResult::new(self.next_id(), false, Some("Quantity must be positive".into()));
        }
        if req.order_type == OrderType::Limit
            && req.limit_price.is_none_or(|p| p <= 0.0)
        {
            return OrderResult::new(
                self.next_id(),
                false,
                Some("Limit order requires positive LimitPrice".into()),
            );
        }
        if self.catalog.get(&req.symbol).is_none() {
            return OrderResult::new(self.next_id(), false, Some("Unknown symbol".into()));
        }
        OrderResult::new(self.next_id(), true, None)
    }
}
