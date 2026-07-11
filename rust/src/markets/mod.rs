//! markets — CircleAI markets primitives.
//!
//! Full Rust port of `src/CircleAI.Markets/*.cs`:
//!
//! - Enums ([`OrderSide`], [`OrderType`]) + records ([`Instrument`], [`Quote`],
//!   [`OrderRequest`], [`OrderResult`]) + the three contracts
//!   ([`IMarketDataFeed`], [`IInstrumentCatalog`], [`IOrderRouter`]).
//! - [`InMemoryInstrumentCatalog`], [`InMemoryMarketDataFeed`] (subscribe /
//!   broadcast quote pushes), [`InMemoryOrderRouter`] (validating against a
//!   catalog).
//! - Fail-closed [`NullMarketDataFeed`] / [`NullInstrumentCatalog`] /
//!   [`NullOrderRouter`].
//!
//! Sync-only (the C# `ValueTask` + `CancellationToken` are dropped); `decimal`
//! money maps to [`f64`]; the `Func<Quote, ValueTask>` handler + `IDisposable`
//! become a synchronous [`QuoteHandler`] + drop-based [`QuoteSubscription`].

pub mod contracts;
pub mod in_memory_markets;
pub mod null_implementations;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use contracts::{
    IInstrumentCatalog, IMarketDataFeed, IOrderRouter, Instrument, OrderRequest, OrderResult,
    OrderSide, OrderType, Quote, QuoteHandler, QuoteSubscription, DEFAULT_TOP_K,
};
pub use in_memory_markets::{
    InMemoryInstrumentCatalog, InMemoryMarketDataFeed, InMemoryOrderRouter,
};
pub use null_implementations::{
    NullInstrumentCatalog, NullMarketDataFeed, NullOrderRouter, EMPTY_GUID,
};
