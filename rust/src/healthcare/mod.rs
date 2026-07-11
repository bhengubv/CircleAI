//! healthcare — CircleAI healthcare operations + clinical knowledge domain pack.
//!
//! Full Rust port of `src/CircleAI.Healthcare/*.cs`:
//!
//! - Records ([`Patient`], [`HealthAppointment`], [`Prescription`]) +
//!   [`IHealthcareBoard`] with the deterministic in-memory
//!   [`InMemoryHealthcareBoard`] (patient registry, appointment scheduling +
//!   status, prescription ledger).
//! - [`HealthcareDomainContext`] — the static domain descriptor.
//! - [`HealthcareCompanionAdapter`] — an [`crate::companion::ICompanionSession`]
//!   decorator that injects the domain snippet and adds clinical agent helpers.

pub mod healthcare_companion_adapter;
pub mod healthcare_domain_context;
pub mod healthcare_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use healthcare_companion_adapter::HealthcareCompanionAdapter;
pub use healthcare_domain_context::HealthcareDomainContext;
pub use healthcare_primitives::{
    HealthAppointment, IHealthcareBoard, InMemoryHealthcareBoard, Patient, Prescription,
};
