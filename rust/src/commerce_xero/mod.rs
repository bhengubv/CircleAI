//! commerce_xero — CircleAI Xero platform integration domain pack.
//!
//! Full Rust port of `src/CircleAI.Commerce.Integration.Xero/*.cs`:
//!
//! - Records ([`XeroTokens`], [`XeroTenant`], [`XeroWebhookEvent`]) +
//!   [`IXeroBoard`] with the deterministic in-memory [`InMemoryXeroBoard`]
//!   (per-user token store, deduplicated tenants, event log newest-first).
//! - [`CommerceIntegrationXeroDomainContext`] — the static domain descriptor.
//! - [`CommerceIntegrationXeroCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus Xero agent helpers.

pub mod commerce_xero_companion_adapter;
pub mod commerce_xero_domain_context;
pub mod xero_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use commerce_xero_companion_adapter::CommerceIntegrationXeroCompanionAdapter;
pub use commerce_xero_domain_context::CommerceIntegrationXeroDomainContext;
pub use xero_primitives::{
    IXeroBoard, InMemoryXeroBoard, XeroTenant, XeroTokens, XeroWebhookEvent, DEFAULT_EVENT_LIMIT,
};
