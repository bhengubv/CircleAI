//! affect_vad.rs
//!
//! Derived Valence / Arousal / Dominance view of `AffectState`.
//!
//! The Circle AI SDK uses a 5-dimensional affect model (curiosity, engagement,
//! uncertainty, rapport, energy). Some downstream systems — including external
//! affective-computing research tooling and HR/health analytics pipelines —
//! expect Russell's PAD/VAD model. `AffectVad` is the DERIVED 3-dimensional
//! view of the same underlying state; it does not replace `AffectState`.
//!
//! Derivation (all results clamped to `[0.0, 1.0]`):
//! ```text
//!   valence   = (engagement + rapport + (1 - uncertainty)) / 3
//!   arousal   = (energy * 2 + curiosity + uncertainty) / 4
//!   dominance = (engagement + (1 - uncertainty)) / 2
//! ```
//!
//! These formulas are the cross-language fixture contract — see
//! `fixtures/affect_vad_derivation.json`. Any change to the math must update
//! every port and every fixture vector.

use serde::{Deserialize, Serialize};

use super::affect_state::AffectState;

/// Derived Russell-PAD view of an [`AffectState`].
///
/// All three dimensions are in `[0.0, 1.0]`.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct AffectVad {
    /// Pleasure ↔ displeasure axis. `1.0` = maximally pleasant,
    /// `0.0` = maximally unpleasant.
    pub valence: f32,

    /// Activation ↔ deactivation axis. `1.0` = maximally aroused/alert,
    /// `0.0` = maximally calm/dormant.
    pub arousal: f32,

    /// In-control ↔ submissive axis. `1.0` = maximally in control,
    /// `0.0` = maximally submissive/overwhelmed.
    pub dominance: f32,
}

impl AffectVad {
    /// Computes the VAD projection of an [`AffectState`] using the canonical
    /// fixture derivation. Output components are clamped to `[0.0, 1.0]`.
    pub fn from_state(state: &AffectState) -> Self {
        let v = (state.engagement + state.rapport + (1.0 - state.uncertainty)) / 3.0;
        let a = (state.energy * 2.0 + state.curiosity + state.uncertainty) / 4.0;
        let d = (state.engagement + (1.0 - state.uncertainty)) / 2.0;
        Self {
            valence: v.clamp(0.0, 1.0),
            arousal: a.clamp(0.0, 1.0),
            dominance: d.clamp(0.0, 1.0),
        }
    }
}

impl AffectState {
    /// Projects this `AffectState` into the derived VAD view.
    pub fn to_vad(&self) -> AffectVad {
        AffectVad::from_state(self)
    }
}
