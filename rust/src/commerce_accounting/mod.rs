//! commerce_accounting — CircleAI accounting domain pack.
//!
//! Full Rust port of `src/CircleAI.Commerce.Accounting/*.cs`:
//!
//! - Records ([`AccountingEntry`], [`TaxRate`], [`Period`]) +
//!   [`IAccountingBoard`] with the deterministic in-memory
//!   [`InMemoryAccountingBoard`] (journal ledger, tax registry, period sums,
//!   balances, net profit).
//! - [`CommerceAccountingDomainContext`] — the static domain descriptor.
//! - [`CommerceAccountingCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus accounting agent helpers.

pub mod accounting_primitives;
pub mod commerce_accounting_companion_adapter;
pub mod commerce_accounting_domain_context;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use accounting_primitives::{
    AccountingEntry, IAccountingBoard, InMemoryAccountingBoard, Period, TaxRate,
};
pub use commerce_accounting_companion_adapter::CommerceAccountingCompanionAdapter;
pub use commerce_accounting_domain_context::CommerceAccountingDomainContext;
