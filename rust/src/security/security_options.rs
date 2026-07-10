//! security_options.rs
//!
//! Configuration model for the AI Security Layer — Rust port of
//! `SecurityOptions.cs`.
//!
//! All threshold values are trust scores in the `[0, 1]` range.
//! Lower score = more compromised. Thresholds must satisfy:
//!   `QuarantineThreshold < AvoidNodeThreshold < ElevateMonitoringThreshold`

use serde::{Deserialize, Serialize};

/// Configures thresholds, decay rates, and event retention for the AI Security
/// Layer. Pass to [`crate::security::NodeTrustRegistry`] and
/// [`crate::security::SecurityLayerService`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SecurityOptions {
    /// Trust score below which monitoring is elevated for the node.
    /// Default: `0.75` — a 25 % trust loss triggers closer observation.
    pub elevate_monitoring_threshold: f64,

    /// Trust score below which the node is excluded from routing.
    /// Default: `0.50` — half trust lost → route around the node.
    pub avoid_node_threshold: f64,

    /// Trust score at or below which the node is hard-blocked (quarantined).
    /// Default: `0.25` — severe compromise → no traffic to or from the node.
    pub quarantine_threshold: f64,

    /// Passive trust recovery per second when no adverse events occur.
    /// Default: `0.001` ≈ full recovery from zero in ~16 minutes of clean
    /// behaviour.
    pub recovery_rate_per_second: f64,

    /// Sliding window used for pattern-based indicator detection (e.g. repeated
    /// auth attempts). Events outside this window are ignored for pattern
    /// analysis. Default: 5 minutes.
    pub event_window: chrono::Duration,

    /// Maximum security events retained per node. Oldest are dropped first.
    /// Default: `100`.
    pub max_events_per_node: usize,

    /// Trust score assigned to nodes on first observation.
    /// Default: `1.0` (full trust until evidence says otherwise).
    pub initial_trust_score: f64,
}

impl Default for SecurityOptions {
    fn default() -> Self {
        Self {
            elevate_monitoring_threshold: 0.75,
            avoid_node_threshold: 0.50,
            quarantine_threshold: 0.25,
            recovery_rate_per_second: 0.001,
            event_window: chrono::Duration::minutes(5),
            max_events_per_node: 100,
            initial_trust_score: 1.0,
        }
    }
}

impl SecurityOptions {
    /// Returns the default options (identical to [`SecurityOptions::default`]).
    pub fn new() -> Self {
        Self::default()
    }
}
