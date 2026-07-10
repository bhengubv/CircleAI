//! security_watchdog.rs
//!
//! The central contract for the CircleAI local runtime immune system — Rust
//! port of `ISecurityWatchdog.cs`.
//!
//! Detection sites (companion pipeline, biometric verifier, agent patch gate)
//! call `on_anomaly_detected` when they observe something suspicious. The
//! watchdog implementation decides the response: key rotation, session
//! revocation, mesh isolation, or state rollback.
//!
//! The SDK ships [`DefaultSecurityWatchdog`] as the out-of-box implementation.
//! Host applications can substitute their own.

use std::collections::VecDeque;
use std::sync::Mutex;

use super::anomaly_signal::AnomalySignal;
use super::security_checkpoint::SecurityCheckpoint;
use super::security_response::{SecurityResponse, SecurityResponseKind};
use super::threat_vector::ThreatVector;

/// Central contract for the CircleAI local runtime immune system. Receives
/// [`AnomalySignal`] instances from detection sites and returns the
/// [`SecurityResponse`] describing protective action taken.
pub trait ISecurityWatchdog: Send + Sync {
    /// Called by any detection site when a local runtime anomaly is observed.
    /// The watchdog evaluates `signal` and applies the appropriate protective
    /// response.
    ///
    /// `checkpoint` is the most recent [`SecurityCheckpoint`] for the affected
    /// module, if one is available — passed so the watchdog can roll back state
    /// without holding a reference to it itself.
    fn on_anomaly_detected(
        &self,
        signal: &AnomalySignal,
        checkpoint: Option<&SecurityCheckpoint>,
    ) -> SecurityResponse;

    /// Drains every [`AnomalySignal`] observed since the last drain. The C#
    /// reference exposes an unbounded `IAsyncEnumerable`; the sync port drains
    /// the unbounded backlog buffer.
    fn stream_signals(&self) -> Vec<AnomalySignal>;
}

/// Confidence band label for an anomaly signal — mirrors the C# diagnostics
/// tag (`< 0.30` low, `< 0.60` mid, otherwise high).
pub fn confidence_band(confidence: f32) -> &'static str {
    if confidence < 0.30 {
        "low"
    } else if confidence < 0.60 {
        "mid"
    } else {
        "high"
    }
}

/// Default in-process watchdog. Applies graduated responses based on
/// [`ThreatVector`] and confidence level:
/// - Confidence < 0.30 → [`SecurityResponseKind::NoAction`]
/// - Confidence 0.30–0.60 → [`SecurityResponseKind::KeyRotation`]
/// - Confidence > 0.60 → [`SecurityResponseKind::Composite`] (rotation + mesh
///   signal), plus [`SecurityResponseKind::StateRollback`] when a verified
///   checkpoint is available for a high-severity vector.
///
/// The signal stream is an in-process unbounded backlog. Single-process
/// correct; not multi-replica safe — signals emitted on replica A do not reach
/// stream drainers on replica B.
pub struct DefaultSecurityWatchdog {
    signals: Mutex<VecDeque<AnomalySignal>>,
}

impl DefaultSecurityWatchdog {
    const ROTATION_THRESHOLD: f32 = 0.30;
    const COMPOSITE_THRESHOLD: f32 = 0.60;

    /// Construct the default watchdog.
    pub fn new() -> Self {
        Self {
            signals: Mutex::new(VecDeque::new()),
        }
    }

    /// The component name, mirroring the C# `ComponentName`.
    pub fn component_name(&self) -> &'static str {
        "DefaultSecurityWatchdog"
    }

    fn format_percent(confidence: f32) -> String {
        // Mirrors C# `:P0` — percentage, no decimals, e.g. 0.923 -> "92%".
        format!("{}%", (confidence * 100.0).round() as i64)
    }
}

impl Default for DefaultSecurityWatchdog {
    fn default() -> Self {
        Self::new()
    }
}

impl ISecurityWatchdog for DefaultSecurityWatchdog {
    fn on_anomaly_detected(
        &self,
        signal: &AnomalySignal,
        checkpoint: Option<&SecurityCheckpoint>,
    ) -> SecurityResponse {
        // Broadcast to any stream drainers.
        self.signals.lock().unwrap().push_back(signal.clone());

        // ── Graduated response policy ────────────────────────────────────────

        if signal.confidence < Self::ROTATION_THRESHOLD {
            return SecurityResponse::no_action(
                signal.id,
                format!(
                    "Confidence {} below rotation threshold — monitoring only.",
                    Self::format_percent(signal.confidence)
                ),
            );
        }

        // High-severity vectors always warrant rollback if we have a checkpoint.
        let is_high_severity = matches!(
            signal.vector,
            ThreatVector::ControlFlowDrift
                | ThreatVector::PrivilegeEscalation
                | ThreatVector::NetworkPivot
                | ThreatVector::StateCorruption
        );

        if signal.confidence > Self::COMPOSITE_THRESHOLD {
            let mut actions = vec![
                SecurityResponseKind::KeyRotation,
                SecurityResponseKind::MeshIsolationSignal,
            ];

            let mut restored: Option<SecurityCheckpoint> = None;
            if let Some(cp) = checkpoint {
                if is_high_severity && cp.verify() {
                    actions.push(SecurityResponseKind::StateRollback);
                    restored = Some(cp.clone());
                }
            }

            return SecurityResponse::composite(
                signal.id,
                actions,
                format!(
                    "Composite response for {:?} (confidence {}) in {}.",
                    signal.vector,
                    Self::format_percent(signal.confidence),
                    signal.affected_module
                ),
                restored,
            );
        }

        // Mid-range confidence: rotate keys only.
        SecurityResponse::for_key_rotation(
            signal.id,
            format!(
                "Key rotation triggered for {:?} (confidence {}) in {}.",
                signal.vector,
                Self::format_percent(signal.confidence),
                signal.affected_module
            ),
        )
    }

    fn stream_signals(&self) -> Vec<AnomalySignal> {
        self.signals.lock().unwrap().drain(..).collect()
    }
}
