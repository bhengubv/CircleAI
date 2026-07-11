//! banking — CircleAI banking primitives.
//!
//! Full Rust port of `src/CircleAI.Banking/*.cs`:
//!
//! - Records ([`Account`], [`LedgerEntry`], [`PaymentRequest`],
//!   [`PaymentResult`]) + the three contracts ([`IAccountReader`],
//!   [`ILedgerWriter`], [`IPaymentProcessor`]).
//! - [`InMemoryBank`] + its reader/ledger/payment adapters
//!   ([`InMemoryAccountReader`], [`InMemoryLedgerWriter`],
//!   [`InMemoryPaymentProcessor`]) — balance-checked double-entry bookkeeping.
//! - Fail-closed [`NullAccountReader`] / [`NullLedgerWriter`] /
//!   [`NullPaymentProcessor`].
//!
//! Sync-only (the C# `ValueTask` + `CancellationToken` are dropped); `decimal`
//! money maps to [`f64`].

pub mod contracts;
pub mod in_memory_banking;
pub mod null_implementations;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use contracts::{
    Account, IAccountReader, ILedgerWriter, IPaymentProcessor, LedgerEntry, PaymentRequest,
    PaymentResult, DEFAULT_READ_LIMIT,
};
pub use in_memory_banking::{
    InMemoryAccountReader, InMemoryBank, InMemoryLedgerWriter, InMemoryPaymentProcessor,
};
pub use null_implementations::{
    NullAccountReader, NullLedgerWriter, NullPaymentProcessor, EMPTY_GUID,
};
