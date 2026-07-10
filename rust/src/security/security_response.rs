//! security_response.rs
//!
//! Describes the action taken by `ISecurityWatchdog` in response to an
//! `AnomalySignal` — Rust port of `SecurityResponse.cs`. Returned from
//! `on_anomaly_detected` so calling code (ops-security agent, host application)
//! knows what was done.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use super::security_checkpoint::SecurityCheckpoint;

/// The type of protective action taken in response to an
/// [`crate::security::AnomalySignal`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum SecurityResponseKind {
    /// No action — confidence below threshold or vector is informational.
    NoAction = 0,
    /// The session's ephemeral UHID key ring was regenerated; prior session
    /// keys are revoked and all in-flight requests using old keys will fail.
    KeyRotation = 1,
    /// The affected session or execution sandbox was marked untrusted and
    /// isolated from the rest of the runtime.
    SessionRevocation = 2,
    /// A [`crate::security::PeerDirective`] was issued to surrounding mesh nodes
    /// to isolate the suspected attack origin.
    MeshIsolationSignal = 3,
    /// State was rolled back to the most recent verified
    /// [`SecurityCheckpoint`].
    StateRollback = 4,
    /// A combination of responses was applied (e.g. key rotation + mesh
    /// isolation). See [`SecurityResponse::applied_actions`] for the full list.
    Composite = 5,
}

/// Describes the protective action taken by `ISecurityWatchdog` in response to
/// an [`crate::security::AnomalySignal`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SecurityResponse {
    /// Identifier of the anomaly signal that triggered this response.
    pub signal_id: Uuid,
    /// Primary response kind.
    pub kind: SecurityResponseKind,
    /// When `kind` is [`SecurityResponseKind::Composite`], lists each individual
    /// action applied. Empty for single-action responses.
    pub applied_actions: Vec<SecurityResponseKind>,
    /// Human-readable description of what was done and why.
    pub description: String,
    /// The [`SecurityCheckpoint`] that was restored, if any. `None` when `kind`
    /// is not [`SecurityResponseKind::StateRollback`].
    pub restored_checkpoint: Option<SecurityCheckpoint>,
    /// UTC timestamp of the response.
    pub responded_at: DateTime<Utc>,
}

impl SecurityResponse {
    /// Creates a no-action response for low-confidence or informational signals.
    pub fn no_action(signal_id: Uuid, reason: impl Into<String>) -> Self {
        Self {
            signal_id,
            kind: SecurityResponseKind::NoAction,
            applied_actions: Vec::new(),
            description: reason.into(),
            restored_checkpoint: None,
            responded_at: Utc::now(),
        }
    }

    /// Creates a key-rotation response.
    pub fn for_key_rotation(signal_id: Uuid, description: impl Into<String>) -> Self {
        Self {
            signal_id,
            kind: SecurityResponseKind::KeyRotation,
            applied_actions: Vec::new(),
            description: description.into(),
            restored_checkpoint: None,
            responded_at: Utc::now(),
        }
    }

    /// Creates a state-rollback response, recording the restored checkpoint.
    pub fn for_rollback(signal_id: Uuid, restored: SecurityCheckpoint) -> Self {
        let description = format!(
            "State rolled back to checkpoint {} ({}).",
            restored.id, restored.module_label
        );
        Self {
            signal_id,
            kind: SecurityResponseKind::StateRollback,
            applied_actions: Vec::new(),
            description,
            restored_checkpoint: Some(restored),
            responded_at: Utc::now(),
        }
    }

    /// Creates a composite response from multiple individual actions.
    pub fn composite(
        signal_id: Uuid,
        actions: Vec<SecurityResponseKind>,
        description: impl Into<String>,
        restored_checkpoint: Option<SecurityCheckpoint>,
    ) -> Self {
        Self {
            signal_id,
            kind: SecurityResponseKind::Composite,
            applied_actions: actions,
            description: description.into(),
            restored_checkpoint,
            responded_at: Utc::now(),
        }
    }
}
