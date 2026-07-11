//! personal_finance — CircleAI personal-finance coach domain pack.
//!
//! Full Rust port of `src/CircleAI.Personal.Finance/*.cs`:
//!
//! - Records ([`Account`], [`FinanceTransaction`], [`BudgetLine`],
//!   [`MonthSummary`]) + [`IPersonalFinanceBoard`] with the deterministic
//!   in-memory [`InMemoryPersonalFinanceBoard`] (accounts, balance-applying
//!   transactions, case-insensitive budgets, monthly rollup).
//! - [`PersonalFinanceDomainContext`] — the static domain descriptor.
//! - [`PersonalFinanceCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus coaching agent helpers.

pub mod personal_finance_companion_adapter;
pub mod personal_finance_domain_context;
pub mod personal_finance_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use personal_finance_companion_adapter::PersonalFinanceCompanionAdapter;
pub use personal_finance_domain_context::PersonalFinanceDomainContext;
pub use personal_finance_primitives::{
    Account, BudgetLine, FinanceTransaction, IPersonalFinanceBoard, InMemoryPersonalFinanceBoard,
    MonthSummary,
};
