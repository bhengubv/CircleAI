//! biometric.rs
//!
//! BiometricProfile and BiometricMatcher.
//!
//! The cosine similarity implementation uses f64 accumulators for
//! cross-platform reproducibility. SIMD intrinsics are intentionally
//! absent — see safety note below.

use chrono::{DateTime, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// BiometricProfile
// ─────────────────────────────────────────────────────────────────────────────

/// A stored biometric identity embedding.
#[derive(Debug, Clone)]
pub struct BiometricProfile {
    /// The identity this profile belongs to.
    pub identity_id: String,

    /// L2-normalised embedding vector.
    pub embedding_vector: Vec<f32>,

    /// Cosine-similarity threshold above which a candidate is accepted.
    /// Default: 0.85.
    pub match_threshold: f32,

    /// When this profile was enrolled.
    pub enrolled_at: DateTime<Utc>,

    /// When the profile last produced a successful match, if ever.
    pub last_match_at: Option<DateTime<Utc>>,
}

impl BiometricProfile {
    /// Create a new profile with the default match threshold (0.85).
    pub fn new(identity_id: impl Into<String>, embedding_vector: Vec<f32>) -> Self {
        Self {
            identity_id: identity_id.into(),
            embedding_vector,
            match_threshold: 0.85,
            enrolled_at: Utc::now(),
            last_match_at: None,
        }
    }

    /// Dimension of the stored embedding vector.
    pub fn embedding_dimension(&self) -> usize {
        self.embedding_vector.len()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BiometricMatcher
// ─────────────────────────────────────────────────────────────────────────────

/// Stateless biometric matching utilities.
///
/// # Cross-platform reproducibility
/// Uses f64 accumulators throughout. Do NOT use SIMD (`packed_simd`, AVX, NEON,
/// etc.) here — different hardware lanes produce different rounding, breaking the
/// cross-language fixture comparisons.
pub struct BiometricMatcher;

impl BiometricMatcher {
    /// Computes the cosine similarity between two equal-length f32 vectors.
    ///
    /// Returns a value in [-1.0, 1.0]. Returns 0.0 when either vector has
    /// near-zero magnitude (< 1e-10 after accumulation).
    ///
    /// # Panics
    /// Panics if `a` and `b` have different lengths or are empty.
    pub fn cosine_similarity(a: &[f32], b: &[f32]) -> f64 {
        assert_eq!(a.len(), b.len(), "cosine_similarity: vectors must have equal length");
        assert!(!a.is_empty(), "cosine_similarity: vectors must not be empty");

        let mut dot = 0.0_f64;
        let mut mag_a = 0.0_f64;
        let mut mag_b = 0.0_f64;

        for (&ai, &bi) in a.iter().zip(b.iter()) {
            let (af, bf) = (ai as f64, bi as f64);
            dot   += af * bf;
            mag_a += af * af;
            mag_b += bf * bf;
        }

        let mag_a = mag_a.sqrt();
        let mag_b = mag_b.sqrt();

        if mag_a < 1e-10 || mag_b < 1e-10 {
            return 0.0;
        }

        (dot / (mag_a * mag_b)).clamp(-1.0, 1.0)
    }

    /// Returns `true` if `candidate` matches the stored profile.
    ///
    /// Comparison: `cosine_similarity(candidate, stored.embedding_vector) >= stored.match_threshold`.
    pub fn is_match(candidate: &[f32], stored: &BiometricProfile) -> bool {
        Self::cosine_similarity(candidate, &stored.embedding_vector)
            >= stored.match_threshold as f64
    }
}
