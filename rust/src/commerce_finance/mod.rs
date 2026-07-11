//! commerce_finance — CircleAI commercial-finance / invoicing domain pack.
//!
//! Full Rust port of `src/CircleAI.Commerce.Finance/*.cs`:
//!
//! - Records ([`InvoiceLine`], [`Invoice`], [`FinancePayment`]) +
//!   [`IInvoiceBoard`] with the deterministic in-memory
//!   [`InMemoryInvoiceBoard`] (issue/pay, overdue flip, tax-inclusive
//!   remaining/outstanding).
//! - [`CommerceFinanceDomainContext`] — the static domain descriptor.
//! - [`CommerceFinanceCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus finance agent helpers (both `ForecastCashFlow` overloads).

pub mod commerce_finance_companion_adapter;
pub mod commerce_finance_domain_context;
pub mod finance_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use commerce_finance_companion_adapter::CommerceFinanceCompanionAdapter;
pub use commerce_finance_domain_context::CommerceFinanceDomainContext;
pub use finance_primitives::{
    FinancePayment, IInvoiceBoard, InMemoryInvoiceBoard, Invoice, InvoiceLine,
};
