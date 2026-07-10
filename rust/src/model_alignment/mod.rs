//! model_alignment — CircleAI targeted-abliteration surface.
//!
//! Full Rust port of `src/CircleAI.ModelAlignment/*.cs`. A pattern-port of
//! OBLITERATUS: alignment (abliteration) profiles are applied / reverted behind
//! contracts so a host does it deliberately, and an auditor can refuse to publish
//! weights that carry alignment deltas.
//!
//! - [`IAlignmentToolkit`] — apply / revert / list profiles. Real:
//!   [`InMemoryAlignmentToolkit`] (reversible-only); fail-closed:
//!   [`NullAlignmentToolkit`].
//! - [`IAlignmentAuditor`] — gate on publishing. Real:
//!   [`RefuseAlignedPublishAuditor`] (refuses if any profile applied); fail-open:
//!   [`NullAlignmentAuditor`].
//!
//! The C# `ArgumentException` / `InvalidOperationException` throws surface as
//! [`AlignmentError`].

pub mod contracts;
pub mod in_memory_model_alignment;
pub mod null_implementations;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use contracts::{
    AlignmentError, AlignmentProfile, AlignmentResult, IAlignmentAuditor, IAlignmentToolkit,
};
pub use in_memory_model_alignment::{InMemoryAlignmentToolkit, RefuseAlignedPublishAuditor};
pub use null_implementations::{NullAlignmentAuditor, NullAlignmentToolkit};
