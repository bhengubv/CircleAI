//! personal_mental — CircleAI mental-wellness domain pack.
//!
//! Full Rust port of `src/CircleAI.Personal.Mental/*.cs`:
//!
//! - [`Mood`] + records ([`MoodLog`], [`JournalEntry`], [`CopingStrategy`]) +
//!   [`IMentalHealthBoard`] with the deterministic in-memory
//!   [`InMemoryMentalHealthBoard`] (mood log + 7-day window/average, journal,
//!   coping-strategy library by tag).
//! - [`PersonalMentalDomainContext`] — the static domain descriptor.
//! - [`PersonalMentalCompanionAdapter`] — an
//!   [`crate::companion::ICompanionSession`] decorator injecting the domain
//!   snippet plus wellness agent helpers.

pub mod personal_mental_companion_adapter;
pub mod personal_mental_domain_context;
pub mod personal_mental_primitives;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use personal_mental_companion_adapter::PersonalMentalCompanionAdapter;
pub use personal_mental_domain_context::PersonalMentalDomainContext;
pub use personal_mental_primitives::{
    CopingStrategy, IMentalHealthBoard, InMemoryMentalHealthBoard, JournalEntry, Mood, MoodLog,
};
