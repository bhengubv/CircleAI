//! contracts.rs
//!
//! (2.8.0) Markets contracts — Rust port of
//! `src/CircleAI.Markets/Contracts.cs`.
//!
//! The C# `ValueTask`-returning, `CancellationToken`-parameterised interfaces
//! collapse to synchronous traits here (the workspace Rust port is sync-only).
//! `decimal` price/quantity values become [`f64`]. The `Func<Quote, ValueTask>`
//! quote handler becomes a synchronous `Arc<dyn Fn(&Quote) + Send + Sync>`, and
//! the `IDisposable` returned by `SubscribeQuotes` becomes the drop-based
//! [`QuoteSubscription`].

use std::sync::Arc;

use chrono::{DateTime, Utc};

/// (Markets) Order side. Mirrors `enum OrderSide { Buy, Sell }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OrderSide {
    Buy,
    Sell,
}

/// (Markets) Order type. Mirrors `enum OrderType { Market, Limit }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OrderType {
    Market,
    Limit,
}

/// (Markets) A tradeable instrument.
///
/// Mirrors `sealed record Instrument(string Symbol, string Exchange,
/// string Currency, string AssetClass)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Instrument {
    pub symbol: String,
    pub exchange: String,
    pub currency: String,
    pub asset_class: String,
}

impl Instrument {
    /// Constructs an instrument, mirroring the positional C# record constructor.
    pub fn new(
        symbol: impl Into<String>,
        exchange: impl Into<String>,
        currency: impl Into<String>,
        asset_class: impl Into<String>,
    ) -> Self {
        Self {
            symbol: symbol.into(),
            exchange: exchange.into(),
            currency: currency.into(),
            asset_class: asset_class.into(),
        }
    }
}

/// (Markets) A price quote.
///
/// Mirrors `sealed record Quote(string Symbol, decimal Bid, decimal Ask,
/// decimal Last, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Quote {
    pub symbol: String,
    pub bid: f64,
    pub ask: f64,
    pub last: f64,
    pub at_utc: DateTime<Utc>,
}

impl Quote {
    /// Constructs a quote, mirroring the positional C# record constructor.
    pub fn new(
        symbol: impl Into<String>,
        bid: f64,
        ask: f64,
        last: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            symbol: symbol.into(),
            bid,
            ask,
            last,
            at_utc,
        }
    }
}

/// (Markets) An order request.
///
/// Mirrors `sealed record OrderRequest(string Symbol, OrderSide Side,
/// OrderType Type, decimal Quantity, decimal? LimitPrice)`.
#[derive(Debug, Clone, PartialEq)]
pub struct OrderRequest {
    pub symbol: String,
    pub side: OrderSide,
    pub order_type: OrderType,
    pub quantity: f64,
    pub limit_price: Option<f64>,
}

impl OrderRequest {
    /// Constructs an order request, mirroring the positional C# record constructor.
    pub fn new(
        symbol: impl Into<String>,
        side: OrderSide,
        order_type: OrderType,
        quantity: f64,
        limit_price: Option<f64>,
    ) -> Self {
        Self {
            symbol: symbol.into(),
            side,
            order_type,
            quantity,
            limit_price,
        }
    }
}

/// (Markets) The outcome of an order submission.
///
/// Mirrors `sealed record OrderResult(string OrderId, bool Accepted,
/// string? FailureReason)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OrderResult {
    pub order_id: String,
    pub accepted: bool,
    pub failure_reason: Option<String>,
}

impl OrderResult {
    /// Constructs an order result, mirroring the positional C# record constructor.
    pub fn new(
        order_id: impl Into<String>,
        accepted: bool,
        failure_reason: Option<String>,
    ) -> Self {
        Self {
            order_id: order_id.into(),
            accepted,
            failure_reason,
        }
    }
}

/// A synchronous quote handler — the sync-only analogue of the C#
/// `Func<Quote, ValueTask>`.
pub type QuoteHandler = Arc<dyn Fn(&Quote) + Send + Sync>;

/// Unsubscribe handle. Dropping it (or calling [`QuoteSubscription::unsubscribe`])
/// removes the associated handler from its feed. Mirrors the C# `IDisposable`
/// returned by `SubscribeQuotes`.
pub struct QuoteSubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl QuoteSubscription {
    pub(crate) fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// A subscription that does nothing on drop (used by [`super::NullMarketDataFeed`]).
    pub fn noop() -> Self {
        Self { remover: None }
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for QuoteSubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

/// (Markets) Live market-data feed.
///
/// Mirrors `interface IMarketDataFeed`.
pub trait IMarketDataFeed {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// The latest quote for `symbol`, if any.
    fn get_quote(&self, symbol: &str) -> Option<Quote>;
    /// Subscribes `handler` to future quotes for `symbol`; returns a drop-based
    /// unsubscribe handle.
    fn subscribe_quotes(&self, symbol: &str, handler: QuoteHandler) -> QuoteSubscription;
}

/// (Markets) Instrument reference-data catalog.
///
/// Mirrors `interface IInstrumentCatalog`.
pub trait IInstrumentCatalog {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Looks up an instrument by symbol.
    fn get(&self, symbol: &str) -> Option<Instrument>;
    /// Up to `top_k` instruments whose symbol contains `query`
    /// (case-insensitive), ordered by symbol ascending.
    fn search(&self, query: &str, top_k: usize) -> Vec<Instrument>;
}

/// (Markets) Order router.
///
/// Mirrors `interface IOrderRouter`.
pub trait IOrderRouter {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Validates and submits `req`, returning the outcome.
    fn submit(&self, req: OrderRequest) -> OrderResult;
}

/// The default `top_k` in the C# `SearchAsync(..., int topK = 20, ...)`.
pub const DEFAULT_TOP_K: usize = 20;
