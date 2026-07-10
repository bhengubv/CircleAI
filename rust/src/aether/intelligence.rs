//! aether::intelligence — Rust port of `CircleAI.Aether/IAetherIntelligence.cs`.
//!
//! Contract 3 — Intelligence Output. What BhenguAI produces after reasoning over
//! Aether telemetry. Aether never sees this interface — it flows upward only,
//! consumed by apps and the Security Layer.
//!
//! The C# surface is `Task`-based, with a streaming `IAsyncEnumerable`. The Rust
//! port is sync; the stream becomes a drain
//! ([`IAetherIntelligence::stream_trust_scores`] returns every update observed
//! since the last drain), matching the crate's unbounded-backlog convention.
//! [`InMemoryAetherIntelligence`] is the self-contained working implementation
//! backed by a per-node trust map.

use std::collections::HashMap;
use std::collections::VecDeque;
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use super::events::AetherThreatLevel;

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/// Aggregate health of the mesh as assessed by BhenguAI.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct NetworkHealthReport {
    pub overall_score: f64,
    pub trusted_node_count: i32,
    pub suspicious_node_count: i32,
    pub summary: String,
    pub generated_at: DateTime<Utc>,
}

impl NetworkHealthReport {
    pub fn new(
        overall_score: f64,
        trusted_node_count: i32,
        suspicious_node_count: i32,
        summary: impl Into<String>,
        generated_at: DateTime<Utc>,
    ) -> Self {
        Self {
            overall_score,
            trusted_node_count,
            suspicious_node_count,
            summary: summary.into(),
            generated_at,
        }
    }

    /// True when `overall_score` is within the valid 0–1 range.
    pub fn is_valid(&self) -> bool {
        (0.0..=1.0).contains(&self.overall_score)
    }
}

/// BhenguAI's assessment of the threat posed by a specific node.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ThreatAssessment {
    pub node_id: String,
    pub threat_confidence: f64,
    pub level: AetherThreatLevel,
    pub indicators: Vec<String>,
    pub assessed_at: DateTime<Utc>,
}

impl ThreatAssessment {
    pub fn new(
        node_id: impl Into<String>,
        threat_confidence: f64,
        level: AetherThreatLevel,
        indicators: Vec<String>,
        assessed_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            threat_confidence,
            level,
            indicators,
            assessed_at,
        }
    }

    /// True when `threat_confidence` is within the valid 0–1 range.
    pub fn is_valid(&self) -> bool {
        (0.0..=1.0).contains(&self.threat_confidence)
    }
}

/// BhenguAI's recommendation for routing to a destination node.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RoutingAdvice {
    pub destination_node_id: String,
    pub recommended_path: Vec<String>,
    pub avoid_nodes: Vec<String>,
    pub confidence: f64,
    pub reasoning: String,
    pub generated_at: DateTime<Utc>,
}

impl RoutingAdvice {
    pub fn new(
        destination_node_id: impl Into<String>,
        recommended_path: Vec<String>,
        avoid_nodes: Vec<String>,
        confidence: f64,
        reasoning: impl Into<String>,
        generated_at: DateTime<Utc>,
    ) -> Self {
        Self {
            destination_node_id: destination_node_id.into(),
            recommended_path,
            avoid_nodes,
            confidence,
            reasoning: reasoning.into(),
            generated_at,
        }
    }
}

/// Emitted when BhenguAI revises the trust score for a node.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TrustScoreUpdate {
    pub node_id: String,
    pub previous_score: f64,
    pub current_score: f64,
    pub reason: String,
    pub updated_at: DateTime<Utc>,
}

impl TrustScoreUpdate {
    pub fn new(
        node_id: impl Into<String>,
        previous_score: f64,
        current_score: f64,
        reason: impl Into<String>,
        updated_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            previous_score,
            current_score,
            reason: reason.into(),
            updated_at,
        }
    }

    /// True when the score moved in either direction.
    pub fn has_changed(&self) -> bool {
        (self.current_score - self.previous_score).abs() > 0.001
    }

    /// True when the score decreased.
    pub fn is_degraded(&self) -> bool {
        self.current_score < self.previous_score
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAetherIntelligence trait
// ─────────────────────────────────────────────────────────────────────────────

/// The intelligence output surface produced by BhenguAI from Aether telemetry.
/// Consumed by apps and the Security Layer; never by Aether.
pub trait IAetherIntelligence: Send + Sync {
    /// Returns an aggregate health report for the current mesh state.
    fn get_network_health(&self) -> NetworkHealthReport;

    /// Assesses the current threat level of a specific node. Returns a
    /// zero-confidence assessment when the node is unknown.
    fn assess_threat(&self, node_id: &str) -> ThreatAssessment;

    /// Returns a routing recommendation for reaching the given destination,
    /// factoring out nodes with low trust scores.
    fn get_routing_advice(&self, destination_node_id: &str) -> RoutingAdvice;

    /// Drains every trust score update observed since the last drain. The C#
    /// reference streams via an unbounded `IAsyncEnumerable`; the sync port
    /// drains the unbounded backlog (updates emitted before this call are
    /// retained).
    fn stream_trust_scores(&self) -> Vec<TrustScoreUpdate>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryAetherIntelligence — self-contained working implementation
// ─────────────────────────────────────────────────────────────────────────────

/// Trust-score thresholds mirroring the transport-agnostic security layer.
const AVOID_THRESHOLD: f64 = 0.50;
const MONITOR_THRESHOLD: f64 = 0.75;

/// A complete, self-contained [`IAetherIntelligence`] backed by a per-node trust
/// map. Hosts feed observations via [`InMemoryAetherIntelligence::set_trust`];
/// every change is recorded on an unbounded backlog drained by
/// [`IAetherIntelligence::stream_trust_scores`]. All four outputs are computed
/// deterministically from the current map.
pub struct InMemoryAetherIntelligence {
    scores: Mutex<HashMap<String, f64>>,
    backlog: Mutex<VecDeque<TrustScoreUpdate>>,
}

impl InMemoryAetherIntelligence {
    /// Creates an empty intelligence surface (no observed nodes).
    pub fn new() -> Self {
        Self {
            scores: Mutex::new(HashMap::new()),
            backlog: Mutex::new(VecDeque::new()),
        }
    }

    /// Records or updates a node's trust score (clamped to `[0, 1]`), publishing
    /// a [`TrustScoreUpdate`] on the backlog when the value actually changes.
    pub fn set_trust(&self, node_id: &str, score: f64, reason: &str) {
        let clamped = score.clamp(0.0, 1.0);
        let previous = {
            let mut map = self.scores.lock().unwrap();
            let prev = map.get(node_id).copied().unwrap_or(1.0);
            map.insert(node_id.to_string(), clamped);
            prev
        };
        if (clamped - previous).abs() > 0.0001 {
            self.backlog.lock().unwrap().push_back(TrustScoreUpdate::new(
                node_id,
                previous,
                clamped,
                reason,
                Utc::now(),
            ));
        }
    }

    fn level_for(score: f64) -> AetherThreatLevel {
        if score <= 0.25 {
            AetherThreatLevel::Critical
        } else if score <= 0.50 {
            AetherThreatLevel::High
        } else if score <= 0.75 {
            AetherThreatLevel::Medium
        } else if score <= 0.90 {
            AetherThreatLevel::Low
        } else {
            AetherThreatLevel::None
        }
    }
}

impl Default for InMemoryAetherIntelligence {
    fn default() -> Self {
        Self::new()
    }
}

impl IAetherIntelligence for InMemoryAetherIntelligence {
    fn get_network_health(&self) -> NetworkHealthReport {
        let map = self.scores.lock().unwrap();
        if map.is_empty() {
            return NetworkHealthReport::new(1.0, 0, 0, "No nodes observed.", Utc::now());
        }
        let scores: Vec<f64> = map.values().copied().collect();
        let overall = scores.iter().sum::<f64>() / scores.len() as f64;
        let trusted = scores.iter().filter(|s| **s > AVOID_THRESHOLD).count() as i32;
        let suspicious = scores.iter().filter(|s| **s <= MONITOR_THRESHOLD).count() as i32;

        let summary = if overall > 0.90 {
            "Network health is excellent."
        } else if overall > 0.75 {
            "Network health is good; minor anomalies detected."
        } else if overall > 0.50 {
            "Network health is degraded; elevated monitoring active."
        } else if overall > 0.25 {
            "Network health is poor; routing around compromised nodes."
        } else {
            "Network health is critical; quarantine directives in effect."
        };

        NetworkHealthReport::new(overall, trusted, suspicious, summary, Utc::now())
    }

    fn assess_threat(&self, node_id: &str) -> ThreatAssessment {
        let map = self.scores.lock().unwrap();
        match map.get(node_id) {
            None => {
                // Unknown node → zero-confidence assessment.
                ThreatAssessment::new(node_id, 0.0, AetherThreatLevel::None, Vec::new(), Utc::now())
            }
            Some(&score) => {
                let level = Self::level_for(score);
                let mut indicators = Vec::new();
                if score <= AVOID_THRESHOLD {
                    indicators.push("trust-below-avoid-threshold".to_string());
                }
                if score <= 0.25 {
                    indicators.push("trust-critical".to_string());
                }
                let confidence = (1.0 - score + indicators.len() as f64 * 0.1).min(1.0);
                ThreatAssessment::new(node_id, confidence, level, indicators, Utc::now())
            }
        }
    }

    fn get_routing_advice(&self, destination_node_id: &str) -> RoutingAdvice {
        let map = self.scores.lock().unwrap();
        let avoid: Vec<String> = map
            .iter()
            .filter(|(_, s)| **s <= AVOID_THRESHOLD)
            .map(|(id, _)| id.clone())
            .collect();
        let dest_score = map.get(destination_node_id).copied().unwrap_or(1.0);
        let recommended = if dest_score > AVOID_THRESHOLD {
            vec![destination_node_id.to_string()]
        } else {
            Vec::new()
        };
        let reasoning = if dest_score > 0.75 {
            format!("Direct path to {destination_node_id} is trusted (score {dest_score:.2}).")
        } else if dest_score > 0.50 {
            format!("Destination {destination_node_id} is under monitoring; routing with caution.")
        } else if dest_score > 0.25 {
            format!("Destination {destination_node_id} has degraded trust; avoid recommended.")
        } else {
            format!("Destination {destination_node_id} is quarantined; no safe path available.")
        };
        RoutingAdvice::new(
            destination_node_id,
            recommended,
            avoid,
            dest_score,
            reasoning,
            Utc::now(),
        )
    }

    fn stream_trust_scores(&self) -> Vec<TrustScoreUpdate> {
        let mut backlog = self.backlog.lock().unwrap();
        backlog.drain(..).collect()
    }
}
