//! face_affect_mapper.rs
//!
//! Maps facial expression observations to AffectState deltas.
//!
//! CRITICAL: deltas must match fixtures/facex_biometric_vectors.json affect_mapper_vectors
//! within tolerance 1e-5. Expressions not listed below produce no change.

use crate::memory::affect_state::AffectState;
use crate::tools::facial_metric::{FaceExpressionClassification, FacialMetricMatrix};

/// Minimum confidence score below which an expression observation is discarded.
const MIN_CONFIDENCE: f32 = 0.5;

/// Applies affect deltas driven by `matrix.expression` to `affect`.
///
/// No-op if `matrix.confidence_score < MIN_CONFIDENCE` or expression is
/// `Neutral`, `Sad`, or `Unknown`.
///
/// Delta table (all clamped to [0.0, 1.0] after application):
/// - `Happy`:    engagement += 0.03, energy += 0.02
/// - `Surprised`: curiosity += 0.04
/// - `Confused`:  uncertainty += 0.05
/// - `Stressed`:  uncertainty += 0.08, energy -= 0.05
/// - `Angry`:     engagement -= 0.04, rapport -= 0.02
pub fn apply(matrix: &FacialMetricMatrix, affect: &mut AffectState) {
    if matrix.confidence_score < MIN_CONFIDENCE {
        return;
    }

    match matrix.expression {
        FaceExpressionClassification::Happy => {
            affect.engagement = (affect.engagement + 0.03).clamp(0.0, 1.0);
            affect.energy     = (affect.energy     + 0.02).clamp(0.0, 1.0);
        }
        FaceExpressionClassification::Surprised => {
            affect.curiosity = (affect.curiosity + 0.04).clamp(0.0, 1.0);
        }
        FaceExpressionClassification::Confused => {
            affect.uncertainty = (affect.uncertainty + 0.05).clamp(0.0, 1.0);
        }
        FaceExpressionClassification::Stressed => {
            affect.uncertainty = (affect.uncertainty + 0.08).clamp(0.0, 1.0);
            affect.energy      = (affect.energy      - 0.05).clamp(0.0, 1.0);
        }
        FaceExpressionClassification::Angry => {
            affect.engagement = (affect.engagement - 0.04).clamp(0.0, 1.0);
            affect.rapport    = (affect.rapport    - 0.02).clamp(0.0, 1.0);
        }
        // Neutral, Sad, Unknown — no affect change
        _ => return,
    }

    affect.last_updated_at = chrono::Utc::now();
}
