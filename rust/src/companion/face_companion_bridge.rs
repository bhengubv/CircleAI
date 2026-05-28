//! face_companion_bridge.rs
//!
//! Bridges facial metric observations to Companion proactive events.
//! When the user appears confused/stressed AND uncertainty exceeds
//! CONFUSION_THRESHOLD, the Companion proactively offers help.

use crate::memory::affect_state::AffectState;
use crate::tools::facial_metric::{FaceExpressionClassification, FacialMetricMatrix};
use super::face_affect_mapper::apply;
use super::types::{CompanionProactiveEvent, InterfaceKind};

/// Uncertainty threshold above which a confusion/stress expression triggers
/// a proactive help offer.
pub const CONFUSION_THRESHOLD: f32 = 0.70;

/// Observe a facial metric matrix, update affect state, and optionally emit
/// a proactive help event.
///
/// Steps:
/// 1. Apply affect deltas via `face_affect_mapper::apply`.
/// 2. If `affect.uncertainty >= CONFUSION_THRESHOLD` **and** the expression is
///    `Confused` or `Stressed`, return `Some(CompanionProactiveEvent)`.
/// 3. Otherwise return `None`.
pub fn observe(
    matrix: &FacialMetricMatrix,
    affect: &mut AffectState,
    session_id: &str,
    identity_id: &str,
    surface: InterfaceKind,
) -> Option<CompanionProactiveEvent> {
    apply(matrix, affect);

    let crossed = affect.uncertainty >= CONFUSION_THRESHOLD
        && matches!(
            matrix.expression,
            FaceExpressionClassification::Confused | FaceExpressionClassification::Stressed
        );

    if !crossed {
        return None;
    }

    Some(CompanionProactiveEvent {
        session_id:   session_id.to_owned(),
        identity_id:  identity_id.to_owned(),
        interface:    surface,
        message: "I notice you might be finding this a bit tricky. Would you like me to slow down or explain it differently?".to_owned(),
        trigger_name: "face.confusion_detected".to_owned(),
        generated_at: chrono::Utc::now(),
    })
}
