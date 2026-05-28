//! sync.rs
//!
//! SyncDeliveryMode, SyncDomainKeys, SyncDelta, and ISyncChannel trait.
//!
//! This is the primitive that makes Circle AI cross-device continuous —
//! HER + JARVIS memory following the person, not the device.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// SyncDeliveryMode
// ─────────────────────────────────────────────────────────────────────────────

/// How hard the sync channel should try to deliver a delta.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncDeliveryMode {
    /// Fire-and-forget; may be lost.
    BestEffort,
    /// Retried until acknowledged or TTL expires.
    Guaranteed,
    /// Highest priority, interrupts current transfer.
    Urgent,
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncDomainKeys
// ─────────────────────────────────────────────────────────────────────────────

/// Canonical domain key constants for [`SyncDelta::domain_key`].
pub struct SyncDomainKeys;

impl SyncDomainKeys {
    pub const MEMORY_EPISODIC: &'static str = "memory.episodic";
    pub const AFFECT_STATE: &'static str = "affect.state";
    pub const PERSONA: &'static str = "persona";
    pub const GOALS: &'static str = "goals";
    pub const FEEDBACK: &'static str = "feedback";
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncDelta
// ─────────────────────────────────────────────────────────────────────────────

/// An incremental state change that must reach every device owned by `owner_id`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncDelta {
    /// Identity whose state this belongs to.
    pub owner_id: String,
    /// Origin device.
    pub source_device_id: String,
    /// `""` = broadcast to all owned devices.
    pub target_device_id: String,
    /// Well-known domain key string, e.g. `"memory.episodic"` or `"affect.state"`.
    pub domain_key: String,
    /// Serialised state fragment.
    pub payload: Vec<u8>,
    /// Monotonic sequence number per owner + domain.
    pub sequence: i64,
    pub delivery_mode: SyncDeliveryMode,
    /// Optional time-to-live in seconds. `None` = no expiry.
    pub ttl_secs: Option<i64>,
    pub created_at: DateTime<Utc>,
}

impl SyncDelta {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        owner_id: impl Into<String>,
        source_device_id: impl Into<String>,
        target_device_id: impl Into<String>,
        domain_key: impl Into<String>,
        payload: Vec<u8>,
        sequence: i64,
        delivery_mode: SyncDeliveryMode,
        ttl_secs: Option<i64>,
    ) -> Self {
        Self {
            owner_id: owner_id.into(),
            source_device_id: source_device_id.into(),
            target_device_id: target_device_id.into(),
            domain_key: domain_key.into(),
            payload,
            sequence,
            delivery_mode,
            ttl_secs,
            created_at: Utc::now(),
        }
    }

    /// Returns `true` if this is a broadcast delta (no specific target device).
    pub fn is_broadcast(&self) -> bool {
        self.target_device_id.is_empty()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISyncChannel trait
// ─────────────────────────────────────────────────────────────────────────────

/// The cross-device continuity primitive.
pub trait ISyncChannel {
    type Error: std::error::Error;

    fn push_delta(&mut self, delta: &SyncDelta) -> Result<(), Self::Error>;
    fn receive_deltas(
        &self,
        owner_id: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<SyncDelta, Self::Error>>>, Self::Error>;
    fn get_last_sequence(&self, owner_id: &str, domain_key: &str) -> Result<i64, Self::Error>;
}
