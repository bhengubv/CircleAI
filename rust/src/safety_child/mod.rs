//! safety_child — CircleAI child-safety / safeguarding domain pack.
//!
//! Full Rust port of `src/CircleAI.Safety.Child/*.cs`. The child-safeguarding
//! vertical:
//!
//! - Records ([`TrustedAdult`], [`Geofence`], [`CheckIn`]) + [`IChildSafetyBoard`]
//!   with the deterministic in-memory [`InMemoryChildSafetyBoard`] (trusted-adult
//!   ring, geofence containment via Haversine, per-child check-in history).
//! - [`SafetyChildDomainContext`] — the static domain descriptor.
//! - [`SafetyChildCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator that injects the domain
//!   snippet and adds safeguarding agent helpers.

pub mod child_safety_primitives;
pub mod safety_child_companion_adapter;
pub mod safety_child_domain_context;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use child_safety_primitives::{
    haversine_meters, CheckIn, Geofence, IChildSafetyBoard, InMemoryChildSafetyBoard, TrustedAdult,
};
pub use safety_child_companion_adapter::SafetyChildCompanionAdapter;
pub use safety_child_domain_context::SafetyChildDomainContext;
