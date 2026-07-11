//! legal — CircleAI legal knowledge + compliance domain pack.
//!
//! Full Rust port of `src/CircleAI.Legal/*.cs`:
//!
//! - Records ([`Matter`], [`Contract`], [`LegalDeadline`], [`Clause`]) +
//!   [`ILegalBoard`] with the deterministic in-memory [`InMemoryLegalBoard`]
//!   (matter lifecycle, contract-expiry queries, deadline calendar, clause
//!   library by tag).
//! - [`LegalDomainContext`] — the static domain descriptor.
//! - [`LegalCompanionAdapter`] — an [`crate::companion::ICompanionSession`]
//!   decorator that injects the domain snippet and adds legal agent helpers.

pub mod legal_companion_adapter;
pub mod legal_domain_context;
pub mod legal_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use legal_companion_adapter::LegalCompanionAdapter;
pub use legal_domain_context::LegalDomainContext;
pub use legal_primitives::{
    Clause, Contract, ILegalBoard, InMemoryLegalBoard, LegalDeadline, Matter,
};
