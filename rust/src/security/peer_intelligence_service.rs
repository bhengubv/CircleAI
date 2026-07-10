//! peer_intelligence_service.rs
//!
//! Transport-agnostic intelligence output — Rust port of the
//! `PeerIntelligenceService` in `AetherIntelligenceService.cs`. Full
//! implementation of [`IPeerIntelligence`].
//!
//! Reads trust scores and event history from [`NodeTrustRegistry`] and packages
//! them as the four intelligence outputs consumed by apps and the security
//! layer:
//!   `PeerNetworkHealthReport`   aggregate health (overall score, counts)
//!   `PeerThreatAssessment`      per-peer confidence + level + indicators
//!   `PeerRoutingAdvice`         trust-aware path with avoid-list
//!   `PeerTrustScoreUpdate`      backlog of every score change

use std::sync::Arc;

use chrono::Utc;

use super::node_trust_registry::NodeTrustRegistry;
use super::peer_security_types::{
    IPeerIntelligence, PeerNetworkHealthReport, PeerRoutingAdvice, PeerThreatAssessment,
    PeerThreatLevel, PeerTrustScoreUpdate,
};
use super::security_options::SecurityOptions;
use super::threat_detector::ThreatDetector;

/// Reads [`NodeTrustRegistry`] state to produce transport-agnostic intelligence
/// outputs. Wires directly to the registry's trust-score backlog for the
/// streaming API.
pub struct PeerIntelligenceService {
    registry: Arc<NodeTrustRegistry>,
    options: SecurityOptions,
}

impl PeerIntelligenceService {
    /// Creates the intelligence service over a shared trust registry.
    pub fn new(registry: Arc<NodeTrustRegistry>, options: SecurityOptions) -> Self {
        Self { registry, options }
    }

    fn score_to_threat_level(score: f64) -> PeerThreatLevel {
        if score <= 0.25 {
            PeerThreatLevel::Critical
        } else if score <= 0.50 {
            PeerThreatLevel::High
        } else if score <= 0.75 {
            PeerThreatLevel::Medium
        } else if score <= 0.90 {
            PeerThreatLevel::Low
        } else {
            PeerThreatLevel::None
        }
    }
}

impl IPeerIntelligence for PeerIntelligenceService {
    fn get_network_health(&self) -> PeerNetworkHealthReport {
        let node_ids = self.registry.all_node_ids();

        if node_ids.is_empty() {
            return PeerNetworkHealthReport {
                overall_score: 1.0,
                trusted_peer_count: 0,
                suspicious_peer_count: 0,
                summary: "No peers observed.".to_string(),
                generated_at: Utc::now(),
            };
        }

        let scores: Vec<f64> = node_ids
            .iter()
            .map(|id| self.registry.get_trust_score(id))
            .collect();
        let overall = scores.iter().sum::<f64>() / scores.len() as f64;
        let trusted = scores
            .iter()
            .filter(|s| **s > self.options.avoid_node_threshold)
            .count() as i32;
        let suspicious = scores
            .iter()
            .filter(|s| **s <= self.options.elevate_monitoring_threshold)
            .count() as i32;

        let summary = if overall > 0.90 {
            "Network health is excellent."
        } else if overall > 0.75 {
            "Network health is good; minor anomalies detected."
        } else if overall > 0.50 {
            "Network health is degraded; elevated monitoring active."
        } else if overall > 0.25 {
            "Network health is poor; routing around compromised peers."
        } else {
            "Network health is critical; quarantine directives in effect."
        }
        .to_string();

        PeerNetworkHealthReport {
            overall_score: overall,
            trusted_peer_count: trusted,
            suspicious_peer_count: suspicious,
            summary,
            generated_at: Utc::now(),
        }
    }

    fn assess_threat(&self, node_id: &str) -> PeerThreatAssessment {
        let score = self.registry.get_trust_score(node_id);
        let deficit = 1.0 - score; // 0 = fully trusted, 1 = fully lost

        let recent = self.registry.get_recent_events(node_id);
        let indicators = ThreatDetector::detect_indicators(recent.iter(), self.options.event_window);

        let level = Self::score_to_threat_level(score);

        // Confidence is proportional to trust deficit, boosted by each indicator.
        let confidence = (deficit + indicators.len() as f64 * 0.1).min(1.0);

        PeerThreatAssessment {
            node_id: node_id.to_string(),
            confidence,
            threat_level: level,
            indicators,
            assessed_at: Utc::now(),
        }
    }

    fn get_routing_advice(&self, destination_node_id: &str) -> PeerRoutingAdvice {
        let all_nodes = self.registry.all_node_ids();
        let avoid_nodes: Vec<String> = all_nodes
            .iter()
            .filter(|id| self.registry.get_trust_score(id) <= self.options.avoid_node_threshold)
            .cloned()
            .collect();

        let dest_score = self.registry.get_trust_score(destination_node_id);

        // Recommended path is direct only when destination is above avoid-threshold.
        let recommended: Vec<String> = if dest_score > self.options.avoid_node_threshold {
            vec![destination_node_id.to_string()]
        } else {
            Vec::new()
        };

        let reasoning = if dest_score > 0.75 {
            format!(
                "Direct path to {destination_node_id} is trusted (score {dest_score:.2})."
            )
        } else if dest_score > 0.50 {
            format!("Destination {destination_node_id} is under monitoring; routing with caution.")
        } else if dest_score > 0.25 {
            format!("Destination {destination_node_id} has degraded trust; avoid recommended.")
        } else {
            format!("Destination {destination_node_id} is quarantined; no safe path available.")
        };

        PeerRoutingAdvice {
            destination_node_id: destination_node_id.to_string(),
            recommended_path: recommended,
            avoid_node_ids: avoid_nodes,
            confidence: dest_score,
            reasoning,
            generated_at: Utc::now(),
        }
    }

    fn stream_trust_scores(&self) -> Vec<PeerTrustScoreUpdate> {
        self.registry.trust_score_updates()
    }
}
