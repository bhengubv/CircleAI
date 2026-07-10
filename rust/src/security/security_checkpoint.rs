//! security_checkpoint.rs
//!
//! A cryptographically-bound snapshot of trusted local state — Rust port of
//! `SecurityCheckpoint.cs`.
//!
//! When CircleAI detects an anomaly, the watchdog may roll back to the last
//! verified checkpoint. A checkpoint is:
//!   - IMMUTABLE once created
//!   - SELF-VERIFYING (SHA-256 of payload, verified on restore)
//!   - TAGGED with the UHID that created it (identity binding)
//!
//! The payload is deliberately opaque (`Vec<u8>`) so any module can checkpoint
//! its own serialised state without this module taking a dependency on it.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use super::hashing::{fixed_time_equals, sha256_bytes, to_hex_upper};

/// An immutable, self-verifying snapshot of trusted local state.
/// Created before a risky operation; used for rollback if an
/// [`crate::security::AnomalySignal`] is confirmed.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SecurityCheckpoint {
    /// Unique checkpoint identifier.
    pub id: Uuid,
    /// The UHID of the local user whose state is captured. Binds the checkpoint
    /// to a specific identity.
    pub uhid_identity_id: String,
    /// Label for the module or subsystem that created this checkpoint
    /// (e.g. `"CircleAI.Companion"`, `"CircleAI.Memory"`).
    pub module_label: String,
    /// Opaque serialised state payload.
    pub payload: Vec<u8>,
    /// SHA-256 hash of `payload`, computed at creation time. Verified by
    /// [`SecurityCheckpoint::verify`] before restoring.
    pub payload_hash: Vec<u8>,
    /// UTC timestamp of checkpoint creation.
    pub created_at: DateTime<Utc>,
}

impl SecurityCheckpoint {
    /// Creates a new checkpoint, computing `payload_hash` automatically.
    ///
    /// # Panics
    /// Panics if `uhid_identity_id` or `module_label` is blank (mirrors the C#
    /// `ArgumentException.ThrowIfNullOrWhiteSpace` guards).
    pub fn create(
        uhid_identity_id: impl Into<String>,
        module_label: impl Into<String>,
        payload: Vec<u8>,
    ) -> Self {
        let uhid_identity_id = uhid_identity_id.into();
        let module_label = module_label.into();
        assert!(
            !uhid_identity_id.trim().is_empty(),
            "uhidIdentityId required"
        );
        assert!(!module_label.trim().is_empty(), "moduleLabel required");

        let hash = sha256_bytes(&payload).to_vec();
        Self {
            id: Uuid::new_v4(),
            uhid_identity_id,
            module_label,
            payload,
            payload_hash: hash,
            created_at: Utc::now(),
        }
    }

    /// Verifies that `payload` has not been tampered with since the checkpoint
    /// was created.
    ///
    /// Returns `true` if the current SHA-256 of `payload` matches
    /// `payload_hash`; `false` if the payload was modified.
    pub fn verify(&self) -> bool {
        let current = sha256_bytes(&self.payload);
        fixed_time_equals(&current, &self.payload_hash)
    }

    /// Returns a non-sensitive textual representation of this checkpoint — the
    /// payload bytes are NEVER included in clear. Only the first 16 hex chars
    /// (8 bytes) of `payload_hash` are emitted, sufficient for correlation
    /// across logs without leaking content. Mirrors the C# `ToString` override.
    pub fn display_string(&self) -> String {
        let hash_prefix = if self.payload_hash.len() >= 8 {
            to_hex_upper(&self.payload_hash[..8])
        } else {
            "(empty)".to_string()
        };
        format!(
            "SecurityCheckpoint(Id={}, Module={}, Uhid={}, PayloadSha256={}\u{2026}, PayloadBytes={}, CreatedAt={})",
            self.id,
            self.module_label,
            self.uhid_identity_id,
            hash_prefix,
            self.payload.len(),
            self.created_at.to_rfc3339_opts(chrono::SecondsFormat::AutoSi, true)
        )
    }
}

impl std::fmt::Display for SecurityCheckpoint {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.display_string())
    }
}
