//! null_implementations.rs
//!
//! (2.8.0) Fail-closed markets defaults — Rust port of
//! `src/CircleAI.Markets/NullImplementations.cs`.
//!
//! Each null backend reports `backend_id() == "null"`. The feed returns no
//! quote and a no-op subscription; the catalog knows nothing; the order router
//! refuses every order with the fixed reason `"NullOrderRouter — fail-closed."`
//! carrying the empty guid (`Guid.Empty.ToString()`).

use super::contracts::{
    IInstrumentCatalog, IMarketDataFeed, IOrderRouter, Instrument, OrderRequest, OrderResult,
    Quote, QuoteHandler, QuoteSubscription,
};

/// C# `Guid.Empty.ToString()` — the all-zero guid in the default hyphenated form.
pub const EMPTY_GUID: &str = "00000000-0000-0000-0000-000000000000";

/// (Markets) Fail-closed [`IMarketDataFeed`].
///
/// Mirrors `sealed class NullMarketDataFeed`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullMarketDataFeed;

impl NullMarketDataFeed {
    /// The shared instance (mirrors the C# `static readonly Instance`).
    pub const INSTANCE: NullMarketDataFeed = NullMarketDataFeed;
}

impl IMarketDataFeed for NullMarketDataFeed {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn get_quote(&self, _symbol: &str) -> Option<Quote> {
        None
    }
    fn subscribe_quotes(&self, _symbol: &str, _handler: QuoteHandler) -> QuoteSubscription {
        QuoteSubscription::noop()
    }
}

/// (Markets) Fail-closed [`IInstrumentCatalog`].
///
/// Mirrors `sealed class NullInstrumentCatalog`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullInstrumentCatalog;

impl NullInstrumentCatalog {
    /// The shared instance.
    pub const INSTANCE: NullInstrumentCatalog = NullInstrumentCatalog;
}

impl IInstrumentCatalog for NullInstrumentCatalog {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn get(&self, _symbol: &str) -> Option<Instrument> {
        None
    }
    fn search(&self, _query: &str, _top_k: usize) -> Vec<Instrument> {
        Vec::new()
    }
}

/// (Markets) Fail-closed [`IOrderRouter`].
///
/// Mirrors `sealed class NullOrderRouter`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullOrderRouter;

impl NullOrderRouter {
    /// The shared instance.
    pub const INSTANCE: NullOrderRouter = NullOrderRouter;
}

impl IOrderRouter for NullOrderRouter {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn submit(&self, _req: OrderRequest) -> OrderResult {
        OrderResult::new(EMPTY_GUID, false, Some("NullOrderRouter — fail-closed.".into()))
    }
}
