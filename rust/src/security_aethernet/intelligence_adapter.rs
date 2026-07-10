//! security_aethernet::intelligence_adapter — Rust port of
//! `CircleAI.Security.AetherNet/AetherIntelligenceAdapter.cs`.
//!
//! Implements the Aether [`IAetherIntelligence`] contract by delegating to the
//! transport-agnostic [`PeerIntelligenceService`] and mapping the four result
//! types:
//!
//!   `PeerNetworkHealthReport` → `NetworkHealthReport`
//!   `PeerThreatAssessment`    → `ThreatAssessment`
//!   `PeerRoutingAdvice`       → `RoutingAdvice`
//!   `PeerTrustScoreUpdate`    → `TrustScoreUpdate` (streaming drain)
//!
//! Callers that only need transport-agnostic intelligence should use
//! [`PeerIntelligenceService`] directly.

use std::sync::Arc;

use crate::aether::intelligence::{
    IAetherIntelligence, NetworkHealthReport, RoutingAdvice, ThreatAssessment, TrustScoreUpdate,
};
use crate::security::{IPeerIntelligence, PeerIntelligenceService};

use super::mapper::to_aether_threat_level;

/// Implements [`IAetherIntelligence`] by wrapping [`PeerIntelligenceService`] and
/// mapping transport-agnostic result types to their Aether equivalents.
pub struct AetherIntelligenceAdapter {
    inner: Arc<PeerIntelligenceService>,
}

impl AetherIntelligenceAdapter {
    pub fn new(inner: Arc<PeerIntelligenceService>) -> Self {
        Self { inner }
    }
}

impl IAetherIntelligence for AetherIntelligenceAdapter {
    fn get_network_health(&self) -> NetworkHealthReport {
        let r = self.inner.get_network_health();
        NetworkHealthReport::new(
            r.overall_score,
            r.trusted_peer_count,
            r.suspicious_peer_count,
            r.summary,
            r.generated_at,
        )
    }

    fn assess_threat(&self, node_id: &str) -> ThreatAssessment {
        let a = self.inner.assess_threat(node_id);
        ThreatAssessment::new(
            a.node_id,
            a.confidence,
            to_aether_threat_level(a.threat_level),
            a.indicators,
            a.assessed_at,
        )
    }

    fn get_routing_advice(&self, destination_node_id: &str) -> RoutingAdvice {
        let r = self.inner.get_routing_advice(destination_node_id);
        RoutingAdvice::new(
            r.destination_node_id,
            r.recommended_path,
            r.avoid_node_ids,
            r.confidence,
            r.reasoning,
            r.generated_at,
        )
    }

    fn stream_trust_scores(&self) -> Vec<TrustScoreUpdate> {
        self.inner
            .stream_trust_scores()
            .into_iter()
            .map(|u| {
                TrustScoreUpdate::new(u.node_id, u.previous_score, u.new_score, u.reason, u.changed_at)
            })
            .collect()
    }
}
