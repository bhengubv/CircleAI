//! safety — CircleAI personal-safety domain pack.
//!
//! Full Rust port of `src/CircleAI.Safety/*.cs`. The personal-safety vertical:
//!
//! - [`IncidentSeverity`] + records ([`Incident`], [`Hazard`],
//!   [`EmergencyContact`]) + [`ISafetyBoard`] with the deterministic in-memory
//!   [`InMemorySafetyBoard`] (severity-routing, hazard ledger, contact ring).
//! - [`SafetyDomainContext`] — the static domain descriptor.
//! - [`SafetyCompanionAdapter`] — an [`crate::companion::ICompanionSession`]
//!   decorator that injects the domain snippet and adds safety agent helpers.

pub mod safety_companion_adapter;
pub mod safety_domain_context;
pub mod safety_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use safety_companion_adapter::SafetyCompanionAdapter;
pub use safety_domain_context::SafetyDomainContext;
pub use safety_primitives::{
    EmergencyContact, Hazard, ISafetyBoard, Incident, IncidentSeverity, InMemorySafetyBoard,
};
