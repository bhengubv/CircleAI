//! anomaly_signal.rs
//!
//! Carries the details of a locally-detected runtime anomaly from the
//! detection site to a security watchdog handler.
//!
//! The signal is conceptually IMMUTABLE — detection sites create it and hand
//! it off. The watchdog (and any ops-security agent) reads it and decides the
//! response. Confidence is clamped into `[0.0, 1.0]` at construction.

use std::collections::HashMap;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use super::threat_vector::ThreatVector;

/// An immutable record describing a locally-detected runtime anomaly.
///
/// Created at the detection site (e.g. the companion pipeline, the biometric
/// verifier, or an agent patch gate) and consumed by the host-side security
/// watchdog.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AnomalySignal {
    /// Unique identifier for this signal instance.
    pub id: Uuid,

    /// Classification of the detected threat.
    pub vector: ThreatVector,

    /// Confidence that this is a genuine threat, in `[0.0, 1.0]`.
    /// `1.0` = definitive; `0.0` = speculative.
    pub confidence: f32,

    /// The module or subsystem where the anomaly was detected
    /// (e.g. `"Circle.AI.Companion"`, `"Circle.AI.Identity"`).
    pub affected_module: String,

    /// Human-readable description of the anomaly.
    pub description: String,

    /// Optional structured evidence attached by the detection site.
    /// Keys are evidence labels; values are serialised data or hashes.
    pub evidence: HashMap<String, String>,

    /// UTC timestamp of detection.
    pub detected_at: DateTime<Utc>,
}

impl AnomalySignal {
    /// Creates an `AnomalySignal` with a new UUID v4 and the current UTC time.
    /// `confidence` is clamped to `[0.0, 1.0]`.
    pub fn create(
        vector: ThreatVector,
        confidence: f32,
        affected_module: impl Into<String>,
        description: impl Into<String>,
        evidence: Option<HashMap<String, String>>,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            vector,
            confidence: confidence.clamp(0.0, 1.0),
            affected_module: affected_module.into(),
            description: description.into(),
            evidence: evidence.unwrap_or_default(),
            detected_at: Utc::now(),
        }
    }
}
