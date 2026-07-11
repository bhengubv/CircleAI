//! education — CircleAI education / curriculum domain pack.
//!
//! Full Rust port of `src/CircleAI.Education/*.cs`:
//!
//! - Records ([`Course`], [`Lesson`], [`StudentRecord`]) + [`IEducationBoard`]
//!   with the deterministic in-memory [`InMemoryEducationBoard`] (course
//!   catalogue, ordered lessons, enrolment + progress, cohort average).
//! - [`EducationDomainContext`] — the static domain descriptor.
//! - [`EducationCompanionAdapter`] — an [`crate::companion::ICompanionSession`]
//!   decorator that injects the domain snippet and adds education agent helpers.

pub mod education_companion_adapter;
pub mod education_domain_context;
pub mod education_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use education_companion_adapter::EducationCompanionAdapter;
pub use education_domain_context::EducationDomainContext;
pub use education_primitives::{
    Course, IEducationBoard, InMemoryEducationBoard, Lesson, StudentRecord,
};
