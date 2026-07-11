//! commerce — CircleAI e-commerce / trading domain pack.
//!
//! Full Rust port of `src/CircleAI.Commerce/*.cs`:
//!
//! - Records ([`CommerceCustomer`], [`CommerceOrder`], [`CommerceLineItem`]) +
//!   [`ICommerceBoard`] with the deterministic in-memory
//!   [`InMemoryCommerceBoard`] (customer registry, orders newest-first, line
//!   items, lifetime value).
//! - [`CommerceDomainContext`] — the static domain descriptor.
//! - [`CommerceCompanionAdapter`] — an [`crate::companion::ICompanionSession`]
//!   decorator that injects the domain snippet and adds commerce agent helpers.

pub mod commerce_companion_adapter;
pub mod commerce_domain_context;
pub mod commerce_primitives;
pub(crate) mod money;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use commerce_companion_adapter::CommerceCompanionAdapter;
pub use commerce_domain_context::CommerceDomainContext;
pub use commerce_primitives::{
    CommerceCustomer, CommerceLineItem, CommerceOrder, ICommerceBoard, InMemoryCommerceBoard,
};
