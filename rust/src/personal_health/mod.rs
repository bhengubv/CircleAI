//! personal_health — CircleAI personal-health / wellness domain pack.
//!
//! Full Rust port of `src/CircleAI.Personal.Health/*.cs`:
//!
//! - [`VitalKind`] + records ([`VitalReading`], [`Allergy`], [`Medication`]) +
//!   [`IPersonalHealthBoard`] with the deterministic in-memory
//!   [`InMemoryPersonalHealthBoard`] (vitals log, latest/read-since helpers,
//!   allergy list, medication start/end).
//! - [`PersonalHealthDomainContext`] — the static domain descriptor.
//! - [`PersonalHealthCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus health agent helpers.

pub mod personal_health_companion_adapter;
pub mod personal_health_domain_context;
pub mod personal_health_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use personal_health_companion_adapter::PersonalHealthCompanionAdapter;
pub use personal_health_domain_context::PersonalHealthDomainContext;
pub use personal_health_primitives::{
    Allergy, IPersonalHealthBoard, InMemoryPersonalHealthBoard, Medication, VitalKind, VitalReading,
};
