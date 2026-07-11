//! crm — CircleAI CRM primitives.
//!
//! Full Rust port of `src/CircleAI.CRM/*.cs`:
//!
//! - Records ([`Contact`], [`Company`], [`Deal`], [`Activity`]) + the three
//!   contracts ([`IContactStore`], [`IDealPipeline`], [`IActivityLog`]).
//! - [`InMemoryContactStore`] (name/email substring search), [`InMemoryDealPipeline`]
//!   (indexed by stage), [`InMemoryActivityLog`] (per-contact, newest-first).
//! - Fail-closed [`NullContactStore`] / [`NullDealPipeline`] / [`NullActivityLog`].
//!
//! Sync-only (the C# `ValueTask` + `CancellationToken` are dropped); `decimal`
//! money maps to [`f64`].

pub mod contracts;
pub mod in_memory_crm;
pub mod null_implementations;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use contracts::{
    Activity, Company, Contact, Deal, IActivityLog, IContactStore, IDealPipeline,
    DEFAULT_READ_LIMIT, DEFAULT_TOP_K,
};
pub use in_memory_crm::{InMemoryActivityLog, InMemoryContactStore, InMemoryDealPipeline};
pub use null_implementations::{NullActivityLog, NullContactStore, NullDealPipeline};
