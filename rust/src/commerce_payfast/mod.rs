//! commerce_payfast — CircleAI PayFast gateway integration domain pack.
//!
//! Full Rust port of `src/CircleAI.Commerce.Integration.PayFast/*.cs`:
//!
//! - Records ([`PayFastConfig`], [`PayFastItnPayload`]) + [`IPayFastBoard`] with
//!   the deterministic in-memory [`InMemoryPayFastBoard`] (real MD5 signature
//!   builder over URL-encoded ordered fields + passphrase, ITN verification,
//!   webhook recorder).
//! - [`CommerceIntegrationPayFastDomainContext`] — the static domain descriptor.
//! - [`CommerceIntegrationPayFastCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus PayFast agent helpers.
//!
//! The MD5 primitive is hand-rolled (`md5`) to keep the port dependency-free,
//! matching the workspace's self-contained SHA-256.

pub mod commerce_payfast_companion_adapter;
pub mod commerce_payfast_domain_context;
pub mod md5;
pub mod payfast_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use commerce_payfast_companion_adapter::CommerceIntegrationPayFastCompanionAdapter;
pub use commerce_payfast_domain_context::CommerceIntegrationPayFastDomainContext;
pub use payfast_primitives::{
    IPayFastBoard, InMemoryPayFastBoard, PayFastConfig, PayFastItnPayload, DEFAULT_WEBHOOK_LIMIT,
};
